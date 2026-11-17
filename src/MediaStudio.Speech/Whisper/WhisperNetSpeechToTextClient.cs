using System.Runtime.CompilerServices;
using MediaStudio.Configuration;
using MediaStudio.Speech.Audio;
using Microsoft.Extensions.AI;
using Whisper.net;

namespace MediaStudio.Speech.Whisper;

public sealed record WhisperClientOptions
{
    /// <summary>Default spoken language when the request does not set one; <c>auto</c> lets Whisper detect it.</summary>
    public string DefaultLanguage { get; init; } = "auto";
    public int? Threads { get; init; }
}

/// <summary>
/// Local speech-to-text over Whisper.net (whisper.cpp). Accepts PCM16/float32 WAV at any rate/channel count (resampled to 16 kHz
/// mono) and returns per-word <see cref="TimedWord"/>s under <see cref="TimedWord.WordsKey"/> and segments under <see cref="SegmentsKey"/>.
/// </summary>
public sealed class WhisperNetSpeechToTextClient : ISpeechToTextClient
{
    public const string ProviderName = "whisper.net";
    public const string SegmentsKey = "segments";
    public const int SampleRate = 16000;
    public const string BaseAssetId = "whisper-base";
    public const string TurboAssetId = "whisper-large-v3-turbo-q5";

    private readonly Lazy<WhisperFactory> factory;
    private readonly WhisperClientOptions options;
    private readonly string modelId;

    public WhisperNetSpeechToTextClient(string modelPath, string? modelId = null, WhisperClientOptions? options = null)
    {
        if (!File.Exists(modelPath)) throw new FileNotFoundException("Whisper ggml model not found.", modelPath);
        ModelPath = modelPath;
        this.modelId = modelId ?? Path.GetFileNameWithoutExtension(modelPath);
        this.options = options ?? new();
        factory = new Lazy<WhisperFactory>(() => WhisperFactory.FromPath(modelPath));
        Metadata = new SpeechToTextClientMetadata(ProviderName, null, this.modelId);
    }

    public string ModelPath { get; }
    public SpeechToTextClientMetadata Metadata { get; }

    /// <summary>Maps the settings model name (<c>base</c>, <c>large-v3-turbo</c>) to a manifest asset id.</summary>
    public static string AssetIdFor(string? model) => model?.Trim().ToLowerInvariant() switch {
        "base" or BaseAssetId => BaseAssetId,
        _ => TurboAssetId
    };

    public static WhisperNetSpeechToTextClient FromModelAssets(ModelAssets assets, string assetId, WhisperClientOptions? options = null)
    {
        var directory = assets.RequirePath(assetId);
        var asset = assets.Manifest.GetAsset(assetId);
        var file = asset.Files.FirstOrDefault(f => f.Path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"Asset '{assetId}' has no ggml .bin file.");
        return new WhisperNetSpeechToTextClient(Path.Combine(directory, file.Path.Replace('/', Path.DirectorySeparatorChar)), assetId, options);
    }

    public async Task<SpeechToTextResponse> GetTextAsync(Stream audioSpeechStream, SpeechToTextOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var segments = new List<SegmentData>();
        await foreach (var segment in ProcessAsync(audioSpeechStream, options, cancellationToken).ConfigureAwait(false))
            segments.Add(segment);
        var text = string.Join(" ", segments.Select(s => s.Text.Trim()).Where(t => t.Length > 0));
        var response = new SpeechToTextResponse(text) {
            ModelId = options?.ModelId ?? modelId,
            StartTime = segments.Count > 0 ? segments[0].Start : TimeSpan.Zero,
            EndTime = segments.Count > 0 ? segments[^1].End : TimeSpan.Zero,
            RawRepresentation = segments,
            AdditionalProperties = new() {
                [TimedWord.WordsKey] = Words(segments),
                [SegmentsKey] = segments.Select(s => new TimedWord(s.Text.Trim(), s.Start, s.End - s.Start)).ToArray(),
                ["language"] = segments.FirstOrDefault()?.Language
            }
        };
        return response;
    }

    public async IAsyncEnumerable<SpeechToTextResponseUpdate> GetStreamingTextAsync(Stream audioSpeechStream, SpeechToTextOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var segment in ProcessAsync(audioSpeechStream, options, cancellationToken).ConfigureAwait(false))
        {
            yield return new SpeechToTextResponseUpdate(segment.Text.Trim()) {
                Kind = SpeechToTextResponseUpdateKind.TextUpdated,
                StartTime = segment.Start,
                EndTime = segment.End,
                ModelId = options?.ModelId ?? modelId,
                AdditionalProperties = new() { [TimedWord.WordsKey] = Words([segment]) }
            };
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is not null ? null
        : serviceType == typeof(SpeechToTextClientMetadata) ? Metadata
        : serviceType.IsInstanceOfType(this) ? this
        : null;

    public void Dispose()
    {
        if (factory.IsValueCreated) factory.Value.Dispose();
    }

    /// <summary>Reads WAV bytes and converts them to 16 kHz mono floats.</summary>
    public static async Task<float[]> ReadSamplesAsync(Stream audio, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await audio.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        var decoded = WavFile.Read(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
        return AudioMath.Resample(decoded.Samples, decoded.SampleRate, SampleRate);
    }

    /// <summary>
    /// Builds words from whisper.cpp tokens: special tokens (<c>[_…]</c>, <c>&lt;|…|&gt;</c>) are skipped and a token with a leading
    /// space starts a new word. Token times are in 10 ms units. Words fall back to an even split of the segment when tokens carry no times.
    /// </summary>
    internal static IReadOnlyList<TimedWord> Words(IEnumerable<SegmentData> segments)
    {
        var words = new List<TimedWord>();
        foreach (var segment in segments)
        {
            var pieces = new List<(string Text, long Start, long End)>();
            foreach (var token in segment.Tokens ?? [])
            {
                var text = token.Text ?? "";
                if (text.Length == 0 || text.StartsWith("[_", StringComparison.Ordinal) || text.StartsWith("<|", StringComparison.Ordinal)) continue;
                if (pieces.Count == 0 || text.StartsWith(' '))
                    pieces.Add((text.Trim(), token.Start, token.End));
                else
                    pieces[^1] = (pieces[^1].Text + text.Trim(), pieces[^1].Start, Math.Max(pieces[^1].End, token.End));
            }
            pieces.RemoveAll(p => p.Text.Length == 0);
            var timed = pieces.Count > 0 && pieces.All(p => p.End >= p.Start) && pieces.Any(p => p.End > 0);
            for (var i = 0; i < pieces.Count; i++)
            {
                TimeSpan offset, duration;
                if (timed)
                {
                    offset = TimeSpan.FromMilliseconds(pieces[i].Start * 10);
                    duration = TimeSpan.FromMilliseconds(Math.Max(0, pieces[i].End - pieces[i].Start) * 10);
                }
                else
                {
                    var span = (segment.End - segment.Start) / pieces.Count;
                    offset = segment.Start + span * i;
                    duration = span;
                }
                words.Add(new TimedWord(pieces[i].Text, offset, duration));
            }
        }
        return words;
    }

    private async IAsyncEnumerable<SegmentData> ProcessAsync(Stream audio, SpeechToTextOptions? requested,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(audio);
        var samples = await ReadSamplesAsync(audio, cancellationToken).ConfigureAwait(false);
        var builder = factory.Value.CreateBuilder()
            .WithLanguage(WhisperLanguage(requested?.SpeechLanguage ?? options.DefaultLanguage))
            .WithTokenTimestamps();
        if (options.Threads is { } threads) builder = builder.WithThreads(threads);
        await using var processor = builder.Build();
        await foreach (var segment in processor.ProcessAsync(samples, cancellationToken).ConfigureAwait(false))
            yield return segment;
    }

    /// <summary>Whisper expects ISO 639-1 codes (<c>en</c>), not BCP-47 tags (<c>en-US</c>).</summary>
    public static string WhisperLanguage(string? language) =>
        string.IsNullOrWhiteSpace(language) ? "auto" : language.Split('-', '_')[0].ToLowerInvariant();
}
