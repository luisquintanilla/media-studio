using System.Text.Json;
using System.Text.Json.Nodes;
using MediaStudio.Configuration;
using MediaStudio.Speech;
using MediaStudio.Speech.Azure;
using MediaStudio.Speech.Kokoro;
using Microsoft.Extensions.AI;

#pragma warning disable MEAI001 // MEAI speech API is experimental; isolate its use here.
namespace VideoProduction;

/// <summary>Narration speech selection. <c>Provider</c> is <c>kokoro</c> (local, default) or <c>azure-speech</c> (cloud, opt-in).</summary>
public sealed record SpeechConfig(string Provider, string Voice, string Language, float Speed = 1,
    string? Quality = null, int EndGuardMilliseconds = 0);
public sealed record SpeechRequest(string Voice, string Language, float Speed = 1);
public sealed record SpeechClientIdentity(string Provider, string ClientType, ArtifactHash[] Artifacts);

public sealed class MeaiNarrationBackend(ITextToSpeechClient client, SpeechRequest request,
    SpeechClientIdentity identity, Func<object?, JsonObject>? providerEvidence = null,
    Func<string, string>? normalizeSpokenText = null) : INarrationBackend
{
    public async Task<NarrationTake> Synthesize(string displayText, string outputDirectory, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Guard.Require(!Directory.Exists(outputDirectory) && !File.Exists(outputDirectory),
            "narration.immutable", "Choose a new take directory; no existing evidence is overwritten.");
        Guard.Require(!string.IsNullOrWhiteSpace(displayText) && !string.IsNullOrWhiteSpace(request.Voice)
            && !string.IsNullOrWhiteSpace(request.Language) && float.IsFinite(request.Speed) && request.Speed is >= 0.5f and <= 2,
            "speech.options", "Explicit text/voice/language and speed0.5..2 required.");
        Guard.Require(!string.IsNullOrWhiteSpace(identity.Provider) && identity.ClientType == client.GetType().FullName,
            "speech.identity", "Evidence must name the actual configured MEAI client type.");
        var expectedSpokenText = normalizeSpokenText?.Invoke(displayText) ?? displayText;
        TextToSpeechResponse response;
        try
        {
            response = await client.GetAudioAsync(displayText, new TextToSpeechOptions {
                VoiceId = request.Voice, Language = request.Language, Speed = request.Speed, Volume = 1, AudioFormat = "wav"
            }, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            throw new VideoException("speech.client", $"MEAI client failed; no fallback: {error.GetType().Name}: {error.Message}");
        }
        ct.ThrowIfCancellationRequested();
        Guard.Require(response is not null && response.Contents.Count == 1 && response.Contents[0] is DataContent { MediaType: "audio/wav" },
            "speech.audio", "Client must return exactly one explicit audio/wav DataContent; no URI/download fallback.");
        var data = (DataContent)response!.Contents[0];
        Guard.Require(data.Data.Length is > 44 and <= 100_000_000 && !string.IsNullOrWhiteSpace(response.ModelId),
            "speech.audio", "Missing model identity or invalid/bounded WAV response.");
        var evidence = providerEvidence?.Invoke(response.RawRepresentation);
        Directory.CreateDirectory(outputDirectory);
        var wav = Path.Combine(outputDirectory, "narration.wav");
        await using (var stream = new FileStream(wav, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await stream.WriteAsync(data.Data, ct);
        var info = Wav.Inspect(wav);
        Guard.Require(info.Channels == 1 && info.SampleRate is >= 8000 and <= 192000 && info.Samples > 0,
            "speech.clock", "Client WAV must be mono PCM16 with an actual nonempty supported sample clock.");
        var hash = Files.Hash(wav);
        if (evidence is not null)
        {
            try { ValidateKokoro(evidence, displayText, expectedSpokenText, response.ModelId!, info, request); }
            catch (VideoException error)
            {
                Json.Write(Path.Combine(outputDirectory, "speech-validation-failure.json"), new
                {
                    schemaVersion = 1, ok = false, error.Code, error.Message,
                    expected = new { displayText, spokenText = expectedSpokenText, modelId = response.ModelId, request },
                    actualWav = new { wavPath = "narration.wav", wavSha256 = hash, info.SampleRate, sampleCount = info.Samples },
                    client = identity, providerEvidence = evidence
                });
                throw;
            }
        }
        var metadata = Path.Combine(outputDirectory, "speech.json");
        ct.ThrowIfCancellationRequested();
        var abstractionAssembly = new
        {
            version = typeof(ITextToSpeechClient).Assembly.GetName().Version!.ToString(),
            sha256 = Files.Hash(typeof(ITextToSpeechClient).Assembly.Location)
        };
        var execution = new
        {
            path = "meai-in-process-v1", abstraction = "Microsoft.Extensions.AI.ITextToSpeechClient",
            package = "Microsoft.Extensions.AI.Abstractions", packageVersion = "10.11.0",
            apiStatus = "experimental MEAI001", abstractionAssembly, client = identity, request,
            cacheIdentity = Files.Fingerprint(new
            {
                path = "meai-in-process-v1", packageVersion = "10.11.0",
                abstractionAssembly, identity, request, response.ModelId, displayText, evidence
            })
        };
        Json.Write(metadata, new
        {
            schemaVersion = 1, ok = true, wavPath = "narration.wav", wavSha256 = hash,
            sampleRate = info.SampleRate, sampleCount = info.Samples, channels = 1, encoding = "pcm_s16le",
            displayText, modelId = response.ModelId, execution,
            providerEvidence = evidence,
            timingEvidence = evidence is null ? "whole-returned-take-sample-range-only" : "synthesis-chunk-sample-ranges-only"
        });
        return new NarrationTake(wav, hash, info.SampleRate, info.Samples, displayText, metadata);
    }

    private static void ValidateKokoro(JsonObject evidence, string displayText, string spokenText, string model,
        WavInfo info, SpeechRequest request)
    {
        void Match(bool condition, string field) => Guard.Require(condition, "speech.evidence",
            $"Actual provider field '{field}' differs from the requested contract or returned WAV; see speech-validation-failure.json.");
        var provenance = evidence["provenance"]!.AsObject();
        Match(evidence["displayText"]!.GetValue<string>() == displayText, "displayText");
        Match(evidence["spokenText"]!.GetValue<string>() == spokenText, "spokenText");
        Match(info.SampleRate == 24000 && evidence["sampleRate"]!.GetValue<int>() == info.SampleRate, "sampleRate");
        Match(evidence["sampleCount"]!.GetValue<long>() == info.Samples, "sampleCount");
        Match(provenance["modelId"]!.GetValue<string>() == model, "provenance.modelId");
        Match(provenance["voiceId"]!.GetValue<string>() == request.Voice, "provenance.voiceId");
        Match(string.Equals(provenance["language"]!.GetValue<string>(), request.Language, StringComparison.OrdinalIgnoreCase), "provenance.language");
        Match(provenance["speed"]!.GetValue<float>() == request.Speed, "provenance.speed");
        Match(provenance["gain"]!.GetValue<float>() == 1, "provenance.gain");
        foreach (var key in new[] { "modelSha256", "voiceSha256", "frontendSha256" })
            Match(Files.ValidHash(provenance[key]!.GetValue<string>()), "provenance." + key);
        long cursor = 0;
        foreach (var node in evidence["chunks"]!.AsArray())
        {
            var chunk = node!.AsObject();
            var length = chunk["sampleCount"]!.GetValue<long>();
            Guard.Require(chunk["startSample"]!.GetValue<long>() == cursor && length > 0,
                "speech.chunks", "Provider chunk samples must be actual contiguous nonempty ranges.");
            cursor = checked(cursor + length);
        }
        Guard.Require(cursor == info.Samples, "speech.chunks", "Actual chunk ledger must cover the complete returned WAV.");
    }
}

/// <summary>
/// Owns the narration <see cref="ITextToSpeechClient"/> built by <see cref="SpeechClientFactory"/> from a <see cref="SpeechConfig"/>
/// layered over the user's provider settings (local Kokoro by default; Azure Speech only when <c>privacy.allowCloud</c>).
/// </summary>
public sealed class NarrationSpeechClient : IDisposable
{
    public const string Kokoro = "kokoro";
    public const string AzureSpeech = "azure-speech";

    private NarrationSpeechClient(ITextToSpeechClient client, SpeechClientIdentity identity) => (Client, Identity) = (client, identity);

    public ITextToSpeechClient Client { get; }
    public SpeechClientIdentity Identity { get; }

    public INarrationBackend CreateBackend(SpeechRequest request) =>
        new MeaiNarrationBackend(Client, request, Identity, Client is KokoroTextToSpeechClient ? KokoroEvidence : null);

    /// <param name="settings">Provider settings; defaults to the saved per-user settings (or built-in defaults).</param>
    /// <param name="assets">Model assets; defaults to the per-user model store and bundled manifest.</param>
    public static NarrationSpeechClient Create(SpeechConfig config, ProviderSettings? settings = null, ModelAssets? assets = null,
        HttpMessageHandler? azureInnerHandler = null)
    {
        Validate(config);
        settings ??= new ProviderSettingsStore().TryLoad() ?? new ProviderSettings();
        assets ??= new ModelAssets(ModelManifest.LoadDefault());
        var tts = settings.Speech.Tts with {
            Provider = config.Provider == AzureSpeech ? TtsProvider.AzureSpeech : TtsProvider.Kokoro,
            Voice = config.Voice,
            Speed = config.Speed,
            Quality = config.Quality ?? settings.Speech.Tts.Quality
        };
        ITextToSpeechClient client;
        try { client = new SpeechClientFactory(settings with { Speech = settings.Speech with { Tts = tts } }, assets, azureInnerHandler).CreateTextToSpeech(); }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException or FileNotFoundException or ArgumentException)
        {
            throw new VideoException("speech.load", $"Speech provider '{config.Provider}' could not be created; no fallback: {error.Message}");
        }
        return new NarrationSpeechClient(client, IdentityFor(config, assets.Manifest));
    }

    /// <summary>The identity an approval pins: provider, client type and (for Kokoro) the manifest hashes of model, tokenizer and voice.</summary>
    public static SpeechClientIdentity IdentityFor(SpeechConfig config, ModelManifest manifest)
    {
        Validate(config);
        if (config.Provider == AzureSpeech)
            return new(AzureSpeech, typeof(AzureSpeechTextToSpeechClient).FullName!, []);
        var fp32 = string.Equals(config.Quality, "fp32", StringComparison.OrdinalIgnoreCase);
        var asset = manifest.GetAsset(fp32 ? KokoroAssetPaths.Fp32AssetId : KokoroAssetPaths.QuantizedAssetId);
        var model = fp32 ? "onnx/model.onnx" : "onnx/model_quantized.onnx";
        var files = asset.Files.Where(f => f.Path == model || f.Path == "tokenizer.json" || f.Path == $"voices/{config.Voice}.bin")
            .Select(f => new ArtifactHash(f.Path, f.Sha256)).ToArray();
        return new(Kokoro, typeof(KokoroTextToSpeechClient).FullName!, files);
    }

    public static JsonObject KokoroEvidence(object? raw)
    {
        Guard.Require(raw is KokoroSynthesisResult, "speech.evidence",
            "Kokoro MEAI response must retain its actual synthesis RawRepresentation; WAV alone cannot reconstruct metadata.");
        return JsonSerializer.SerializeToNode((KokoroSynthesisResult)raw!, Json.Options)!.AsObject();
    }

    public void Dispose() => Client.Dispose();

    private static void Validate(SpeechConfig config) =>
        Guard.Require(config.Provider is Kokoro or AzureSpeech && !string.IsNullOrWhiteSpace(config.Voice)
            && !string.IsNullOrWhiteSpace(config.Language), "speech.provider",
            "Speech provider must be 'kokoro' or 'azure-speech' with an explicit voice and language; no provider fallback.");
}
#pragma warning restore MEAI001
