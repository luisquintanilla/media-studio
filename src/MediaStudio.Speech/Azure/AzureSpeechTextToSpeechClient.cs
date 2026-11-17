using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using MediaStudio.Configuration;
using MediaStudio.Speech.Audio;
using Microsoft.Extensions.AI;

namespace MediaStudio.Speech.Azure;

public sealed record AzureSpeechTextToSpeechOptions
{
    public const string DefaultVoice = "en-US-Harper:MAI-Voice-2.1-Flash";
    public const string DefaultStyle = "narrator";
    public const string OutputFormat = "riff-24khz-16bit-mono-pcm";
    public const int MaxCharactersPerRequest = 5000;

    public string Voice { get; init; } = DefaultVoice;
    /// <summary>mstts:express-as style; dropped (with a warning) when the voice catalog says the voice does not support it.</summary>
    public string? Style { get; init; } = DefaultStyle;
    public string Language { get; init; } = "en-US";
    public SpeechTtsMode Mode { get; init; } = FoundryEndpoints.DefaultSpeechTtsMode;
    /// <summary>Voice catalog (from <c>model-manifest.json</c> cloudVoices) used to validate styles; empty = no validation.</summary>
    public IReadOnlyList<CloudVoice> Voices { get; init; } = [];
    public TimeSpan CrossFade { get; init; } = TimeSpan.FromMilliseconds(10);

    public static AzureSpeechTextToSpeechOptions FromManifest(ModelManifest manifest, string? voice = null, string? style = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var chosen = voice is null ? manifest.DefaultCloudVoice : manifest.CloudVoices.FirstOrDefault(v => string.Equals(v.Voice, voice, StringComparison.OrdinalIgnoreCase));
        return new() {
            Voice = voice ?? chosen?.Voice ?? DefaultVoice,
            Style = style ?? chosen?.DefaultStyle,
            Voices = manifest.CloudVoices
        };
    }
}

/// <summary>
/// Azure Speech (MAI-Voice) text-to-speech over the SSML REST API: <c>POST {root}tts/cognitiveservices/v1</c> (or the regional
/// host) with <c>X-Microsoft-OutputFormat: riff-24khz-16bit-mono-pcm</c>. Text over 5,000 characters is split at sentence
/// boundaries and the WAVs are joined with a short cross-fade. Credentials come from <see cref="SpeechAuthHandler"/>.
/// </summary>
public sealed class AzureSpeechTextToSpeechClient : ITextToSpeechClient
{
    public const string ProviderName = "azure-speech";

    private readonly HttpClient http;
    private readonly Uri endpoint;
    private readonly bool ownsHttp;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly List<string> warnings = [];

    public AzureSpeechTextToSpeechClient(HttpClient httpClient, FoundryEndpoints endpoints, AzureSpeechTextToSpeechOptions? options = null,
        bool ownsHttpClient = false, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        ArgumentNullException.ThrowIfNull(endpoints);
        Options = options ?? new();
        endpoint = endpoints.SpeechTtsFor(Options.Mode);
        ownsHttp = ownsHttpClient;
        this.delay = delay ?? Task.Delay;
    }

    public AzureSpeechTextToSpeechOptions Options { get; }
    public Uri Endpoint => endpoint;
    /// <summary>Non-fatal issues, e.g. a style the voice does not support was dropped.</summary>
    public IReadOnlyList<string> Warnings => warnings;

    public static AzureSpeechTextToSpeechClient Create(ConnectionSettings connection, AzureSpeechTextToSpeechOptions? options = null,
        HttpMessageHandler? innerHandler = null)
    {
        options ??= new();
        var handler = SpeechAuthHandler.For(connection, options.Mode, innerHandler);
        return new(new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(2) }, FoundryEndpoints.From(connection), options, ownsHttpClient: true);
    }

    public async Task<TextToSpeechResponse> GetAudioAsync(string text, TextToSpeechOptions? options = null, CancellationToken cancellationToken = default)
    {
        var request = Prepare(text, options);
        var parts = new List<byte[]>(request.Chunks.Count);
        foreach (var chunk in request.Chunks)
            parts.Add(await SynthesizeChunkAsync(request.Ssml(chunk), request.Voice, cancellationToken).ConfigureAwait(false));
        var wav = parts.Count == 1 ? parts[0] : WavFile.Concatenate(parts, Options.CrossFade);
        return new TextToSpeechResponse([new DataContent(wav, "audio/wav")]) {
            ModelId = request.Voice,
            AdditionalProperties = new() {
                ["sampleRate"] = 24000,
                ["voice"] = request.Voice,
                ["style"] = request.Style,
                ["requests"] = request.Chunks.Count
            }
        };
    }

    /// <summary>One <see cref="TextToSpeechResponseUpdateKind.AudioUpdating"/> update per ≤ 5,000-character SSML request.</summary>
    public async IAsyncEnumerable<TextToSpeechResponseUpdate> GetStreamingAudioAsync(string text, TextToSpeechOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var request = Prepare(text, options);
        foreach (var chunk in request.Chunks)
        {
            var wav = await SynthesizeChunkAsync(request.Ssml(chunk), request.Voice, cancellationToken).ConfigureAwait(false);
            yield return new TextToSpeechResponseUpdate([new DataContent(wav, "audio/wav")]) {
                Kind = TextToSpeechResponseUpdateKind.AudioUpdating,
                ModelId = request.Voice
            };
        }
    }
    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is not null ? null
        : serviceType == typeof(TextToSpeechClientMetadata) ? new TextToSpeechClientMetadata(ProviderName, endpoint, Options.Voice)
        : serviceType.IsInstanceOfType(this) ? this
        : null;

    public void Dispose()
    {
        if (ownsHttp) http.Dispose();
    }

    private sealed record PreparedRequest(IReadOnlyList<string> Chunks, string Voice, string Language, string? Style, string? Rate)
    {
        public string Ssml(string chunk) => SsmlBuilder.Build(chunk, Voice, Language, Style, Rate);
    }

    private PreparedRequest Prepare(string text, TextToSpeechOptions? options)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (options?.AudioFormat is { } format && !format.Contains("wav", StringComparison.OrdinalIgnoreCase) && !format.Contains("riff", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"Azure Speech client returns WAV ({AzureSpeechTextToSpeechOptions.OutputFormat}); '{format}' is not supported.");
        var voice = options?.VoiceId ?? Options.Voice;
        var requestedStyle = options?.AdditionalProperties?.TryGetValue("style", out var value) == true ? value?.ToString() : Options.Style;
        var chunks = TextChunker.Pack(TextChunker.SplitSentences(text), AzureSpeechTextToSpeechOptions.MaxCharactersPerRequest);
        if (chunks.Count == 0) throw new ArgumentException("Text to synthesize is empty.", nameof(text));
        return new(chunks, voice, options?.Language ?? Options.Language, ResolveStyle(voice, requestedStyle), SsmlBuilder.RateFor(options?.Speed));
    }

    internal string? ResolveStyle(string voice, string? style)
    {
        if (string.IsNullOrWhiteSpace(style) || Options.Voices.Count == 0) return style;
        var entry = Options.Voices.FirstOrDefault(v => string.Equals(v.Voice, voice, StringComparison.OrdinalIgnoreCase));
        if (entry is null || entry.Styles.Contains(style, StringComparer.OrdinalIgnoreCase)) return style;
        var warning = $"Voice '{voice}' does not support style '{style}'; synthesizing without a style.";
        lock (warnings) warnings.Add(warning);
        return null;
    }

    private async Task<byte[]> SynthesizeChunkAsync(string ssml, string voice, CancellationToken cancellationToken)
    {
        using var response = await SpeechHttp.SendWithRetryAsync(http, () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, endpoint) {
                Content = new StringContent(ssml, Encoding.UTF8)
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/ssml+xml") { CharSet = "utf-8" };
            request.Headers.TryAddWithoutValidation("X-Microsoft-OutputFormat", AzureSpeechTextToSpeechOptions.OutputFormat);
            SpeechHttp.AddCommonHeaders(request);
            return request;
        }, delay, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw await AzureSpeechException.FromResponseAsync(response, "text-to-speech", voice, cancellationToken).ConfigureAwait(false);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (bytes.Length < 44 || bytes[0] != (byte)'R' || bytes[1] != (byte)'I' || bytes[2] != (byte)'F' || bytes[3] != (byte)'F')
            throw new AzureSpeechException("Azure Speech text-to-speech returned a response that is not a RIFF/WAV file.", response.StatusCode);
        return bytes;
    }
}
