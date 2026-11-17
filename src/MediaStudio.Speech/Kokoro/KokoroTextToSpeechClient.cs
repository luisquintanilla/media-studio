using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using MediaStudio.Configuration;
using MediaStudio.Speech.Audio;
using Microsoft.Extensions.AI;

namespace MediaStudio.Speech.Kokoro;

/// <summary>Locations of the Kokoro model, tokenizer and voice packs.</summary>
public sealed record KokoroAssetPaths(string ModelFile, string TokenizerFile, string VoicesDirectory, string ModelId)
{
    public const string QuantizedAssetId = "kokoro-v1.0-q8";
    public const string Fp32AssetId = "kokoro-v1.0-fp32";

    /// <summary>Resolves the installed asset through <see cref="ModelAssets"/> (throws with the <c>assets ensure</c> command when missing).</summary>
    public static KokoroAssetPaths FromModelAssets(ModelAssets assets, bool fp32 = false)
    {
        var id = fp32 ? Fp32AssetId : QuantizedAssetId;
        return FromDirectory(assets.RequirePath(id), fp32, id);
    }

    /// <summary>
    /// Uses a directory laid out like the Hugging Face repo (<c>onnx/model_quantized.onnx</c>, <c>voices/af_heart.bin</c>) or
    /// with the same names flattened (<c>onnx_model_quantized.onnx</c>, <c>voices_af_heart.bin</c>).
    /// </summary>
    public static KokoroAssetPaths FromDirectory(string directory, bool fp32 = false, string? modelId = null)
    {
        var file = fp32 ? "model.onnx" : "model_quantized.onnx";
        var model = new[] { Path.Combine(directory, "onnx", file), Path.Combine(directory, "onnx_" + file) }.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException($"Kokoro model '{file}' not found under the asset directory.", Path.Combine(directory, "onnx", file));
        return new(model, Path.Combine(directory, "tokenizer.json"), directory, modelId ?? (fp32 ? Fp32AssetId : QuantizedAssetId));
    }
}

public sealed record KokoroClientOptions
{
    public string DefaultVoice { get; init; } = "af_heart";
    public float DefaultSpeed { get; init; } = 1f;
    /// <summary>Linear cross-fade between sentence chunks.</summary>
    public TimeSpan CrossFade { get; init; } = TimeSpan.FromMilliseconds(10);
    public float PeakTarget { get; init; } = 0.95f;
    public bool UseDirectML { get; init; }
}

/// <summary>One synthesized chunk in the returned WAV, as a contiguous sample range.</summary>
public sealed record KokoroChunk(string Text, string Phonemes, int TokenCount, int UnknownSymbols, long StartSample, long SampleCount);

public sealed record KokoroProvenance(string ModelId, string VoiceId, string Language, float Speed, float Gain,
    string ModelSha256, string VoiceSha256, string FrontendSha256);

/// <summary>Synthesis evidence returned as <see cref="TextToSpeechResponse.RawRepresentation"/>.</summary>
public sealed record KokoroSynthesisResult(string DisplayText, string SpokenText, int SampleRate, long SampleCount,
    KokoroChunk[] Chunks, KokoroProvenance Provenance);

/// <summary>
/// Local Kokoro-82M text-to-speech: sentence split → espeak-ng IPA → <see cref="KokoroPhonemeTokenizer"/> (≤ 510 ids per chunk)
/// → ONNX Runtime → cross-faded, peak-normalized 24 kHz mono PCM16 WAV.
/// </summary>
public sealed class KokoroTextToSpeechClient : ITextToSpeechClient
{
    public const string ProviderName = "kokoro";
    private const string SentenceEnds = ".!?";

    private readonly KokoroAssetPaths? paths;
    private readonly KokoroClientOptions options;
    private readonly IPhonemizer phonemizer;
    private readonly KokoroVoices? voices;
    private readonly Func<string, KokoroVoice> voiceLookup;
    private readonly Func<long[], ReadOnlyMemory<float>, float, float[]> run;
    private readonly Lazy<KokoroModel>? model;
    private readonly Dictionary<string, string> hashes = new(StringComparer.OrdinalIgnoreCase);
    private readonly string modelId;

    public KokoroTextToSpeechClient(KokoroAssetPaths paths, IPhonemizer? phonemizer = null, KokoroClientOptions? options = null)
    {
        this.paths = paths ?? throw new ArgumentNullException(nameof(paths));
        this.options = options ?? new();
        this.phonemizer = phonemizer ?? new EspeakPhonemizer();
        Tokenizer = KokoroPhonemeTokenizer.FromFile(paths.TokenizerFile);
        voices = new KokoroVoices(paths.VoicesDirectory);
        voiceLookup = voices.Get;
        model = new Lazy<KokoroModel>(() => new KokoroModel(paths.ModelFile, this.options.UseDirectML));
        run = (ids, style, speed) => model.Value.Run(ids, style.Span, speed);
        modelId = paths.ModelId;
        Metadata = new TextToSpeechClientMetadata(ProviderName, null, modelId);
    }

    /// <summary>Test seam: runs the full text pipeline against a supplied inference function.</summary>
    internal KokoroTextToSpeechClient(KokoroPhonemeTokenizer tokenizer, IPhonemizer phonemizer, Func<string, KokoroVoice> voiceLookup,
        Func<long[], ReadOnlyMemory<float>, float, float[]> run, string modelId = "kokoro-test", KokoroClientOptions? options = null)
    {
        Tokenizer = tokenizer;
        this.phonemizer = phonemizer;
        this.voiceLookup = voiceLookup;
        this.run = run;
        this.modelId = modelId;
        this.options = options ?? new();
        Metadata = new TextToSpeechClientMetadata(ProviderName, null, modelId);
    }

    public KokoroPhonemeTokenizer Tokenizer { get; }
    public TextToSpeechClientMetadata Metadata { get; }
    public IReadOnlyList<string> ListVoices() => voices?.ListVoices() ?? [];

    public async Task<TextToSpeechResponse> GetAudioAsync(string text, TextToSpeechOptions? options = null, CancellationToken cancellationToken = default)
    {
        var request = Resolve(options);
        var segments = new List<(KokoroChunkDraft Draft, float[] Audio)>();
        await foreach (var segment in SynthesizeSegmentsAsync(text, request, cancellationToken).ConfigureAwait(false))
            segments.Add(segment);
        if (segments.Count == 0) throw new ArgumentException("There is no speakable text to synthesize.", nameof(text));
        var fade = (int)(KokoroModel.SampleRate * this.options.CrossFade.TotalSeconds);
        var joined = AudioMath.Concatenate(segments.Select(s => s.Audio).ToArray(), fade);
        AudioMath.PeakNormalize(joined, this.options.PeakTarget * request.Gain);
        var chunks = new KokoroChunk[segments.Count];
        long cursor = 0;
        for (var i = 0; i < segments.Count; i++)
        {
            var overlap = i == 0 ? 0 : Math.Min(fade, Math.Min(segments[i - 1].Audio.Length, segments[i].Audio.Length));
            var length = segments[i].Audio.Length - overlap;
            var d = segments[i].Draft;
            chunks[i] = new KokoroChunk(d.Text, d.Phonemes, d.TokenCount, d.Unknown, cursor, length);
            cursor += length;
        }
        var wav = WavFile.ToPcm16Wav(joined, KokoroModel.SampleRate);
        var raw = new KokoroSynthesisResult(text, text, KokoroModel.SampleRate, joined.Length, chunks, Provenance(request));
        return new TextToSpeechResponse([new DataContent(wav, "audio/wav")]) {
            ModelId = modelId,
            RawRepresentation = raw,
            AdditionalProperties = new() { ["sampleRate"] = KokoroModel.SampleRate, ["voice"] = request.Voice }
        };
    }

    /// <summary>Yields one complete WAV per sentence chunk as soon as it is synthesized.</summary>
    public async IAsyncEnumerable<TextToSpeechResponseUpdate> GetStreamingAudioAsync(string text, TextToSpeechOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var request = Resolve(options);
        await foreach (var (draft, audio) in SynthesizeSegmentsAsync(text, request, cancellationToken).ConfigureAwait(false))
        {
            AudioMath.PeakLimit(audio, this.options.PeakTarget);
            yield return new TextToSpeechResponseUpdate([new DataContent(WavFile.ToPcm16Wav(audio, KokoroModel.SampleRate), "audio/wav")]) {
                Kind = TextToSpeechResponseUpdateKind.AudioUpdating,
                ModelId = modelId,
                AdditionalProperties = new() { ["text"] = draft.Text }
            };
        }
        yield return new TextToSpeechResponseUpdate([]) { Kind = TextToSpeechResponseUpdateKind.AudioUpdated, ModelId = modelId };
    }

    /// <summary>Phonemizes and tokenizes without running the model; useful for diagnostics and tests.</summary>
    public async Task<IReadOnlyList<(string Text, string Phonemes, int[] Ids)>> PrepareAsync(string text, string? language = null,
        CancellationToken cancellationToken = default)
    {
        var lang = language ?? LanguageForVoice(options.DefaultVoice);
        var prepared = new List<(string, string, int[])>();
        foreach (var sentence in TextChunker.SplitSentences(text))
        {
            var phonemes = await phonemizer.PhonemizeAsync(sentence, lang, cancellationToken).ConfigureAwait(false);
            if (SentenceEnds.Contains(sentence[^1]) && phonemes.Length > 0 && !SentenceEnds.Contains(phonemes[^1])) phonemes += sentence[^1];
            var ids = Tokenizer.EncodeToIds(phonemes);
            foreach (var chunk in Tokenizer.Chunk(ids)) prepared.Add((sentence, phonemes, chunk));
        }
        return prepared;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is not null ? null
        : serviceType == typeof(TextToSpeechClientMetadata) ? Metadata
        : serviceType == typeof(KokoroPhonemeTokenizer) ? Tokenizer
        : serviceType.IsInstanceOfType(this) ? this
        : null;

    public void Dispose()
    {
        if (model?.IsValueCreated == true) model.Value.Dispose();
    }

    private async IAsyncEnumerable<(KokoroChunkDraft, float[])> SynthesizeSegmentsAsync(string text, Request request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var voice = voiceLookup(request.Voice);
        foreach (var sentence in TextChunker.SplitSentences(text))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var phonemes = await phonemizer.PhonemizeAsync(sentence, request.Language, cancellationToken).ConfigureAwait(false);
            if (SentenceEnds.Contains(sentence[^1]) && phonemes.Length > 0 && !SentenceEnds.Contains(phonemes[^1])) phonemes += sentence[^1];
            var ids = Tokenizer.EncodeToIds(phonemes);
            var unknown = Tokenizer.LastUnknownCount;
            foreach (var chunk in Tokenizer.Chunk(ids))
            {
                if (chunk.Length == 0) continue;
                var input = new long[chunk.Length + 2];
                for (var i = 0; i < chunk.Length; i++) input[i + 1] = chunk[i];
                var style = voice.GetStyle(chunk.Length).ToArray();
                var audio = await Task.Run(() => run(input, style, request.Speed), cancellationToken).ConfigureAwait(false);
                if (audio.Length == 0) continue;
                yield return (new KokoroChunkDraft(sentence, Tokenizer.Decode(chunk) ?? "", chunk.Length, unknown), audio);
                unknown = 0;
            }
        }
    }

    private Request Resolve(TextToSpeechOptions? requested)
    {
        var voice = string.IsNullOrWhiteSpace(requested?.VoiceId) ? options.DefaultVoice : requested!.VoiceId!;
        var speed = requested?.Speed ?? options.DefaultSpeed;
        if (!float.IsFinite(speed) || speed is < 0.5f or > 2f) throw new ArgumentOutOfRangeException(nameof(requested), "Kokoro speed must be between 0.5 and 2.");
        var language = string.IsNullOrWhiteSpace(requested?.Language) ? LanguageForVoice(voice) : EspeakPhonemizer.VoiceFor(requested!.Language);
        var gain = requested?.Volume is { } volume && float.IsFinite(volume) && volume > 0 ? Math.Min(volume, 1f) : 1f;
        if (requested?.AudioFormat is { } format && !format.Contains("wav", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"Kokoro returns audio/wav only (requested '{format}').");
        return new Request(voice, language, speed, gain);
    }

    /// <summary>Kokoro voice names start with a language letter: <c>a</c> = American, <c>b</c> = British English.</summary>
    public static string LanguageForVoice(string voice) => voice.StartsWith('b') ? "en-gb" : "en-us";

    private KokoroProvenance Provenance(Request request) => new(modelId, request.Voice, request.Language, request.Speed, request.Gain,
        Hash(paths?.ModelFile, "model:" + modelId),
        Hash(voices?.FindFile(request.Voice), "voice:" + request.Voice),
        Hash((phonemizer as EspeakPhonemizer)?.ExecutablePath, "frontend:" + phonemizer.GetType().FullName));

    private string Hash(string? file, string fallback)
    {
        lock (hashes)
        {
            var key = file ?? fallback;
            if (hashes.TryGetValue(key, out var cached)) return cached;
            string value;
            if (file is not null && File.Exists(file))
            {
                using var stream = File.OpenRead(file);
                value = Convert.ToHexStringLower(SHA256.HashData(stream));
            }
            else value = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(fallback)));
            return hashes[key] = value;
        }
    }

    private sealed record Request(string Voice, string Language, float Speed, float Gain);
    private sealed record KokoroChunkDraft(string Text, string Phonemes, int TokenCount, int Unknown);
}
