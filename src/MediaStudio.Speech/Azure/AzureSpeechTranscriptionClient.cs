using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaStudio.Configuration;
using Microsoft.Extensions.AI;

namespace MediaStudio.Speech.Azure;

/// <summary>Fast-transcription <c>definition</c> part. Names mirror <c>Azure.AI.Speech.Transcription</c> so moving to the SDK later is internal.</summary>
public sealed record TranscriptionDefinition
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string[]? Locales { get; init; }
    public required EnhancedModeDefinition EnhancedMode { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public DiarizationDefinition? Diarization { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public PhraseListDefinition? PhraseList { get; init; }
}

public sealed record EnhancedModeDefinition
{
    public bool Enabled { get; init; } = true;
    public required string Model { get; init; }
    public required EnhancedModeOptions ModelOptions { get; init; }
}

public sealed record EnhancedModeOptions
{
    public string Timestamps { get; init; } = "word";
    public string TranscribeStyle { get; init; } = "clean";
}

public sealed record DiarizationDefinition(bool Enabled);

public sealed record PhraseListDefinition(string[] Phrases);

internal sealed record TranscriptionResult
{
    public long DurationMilliseconds { get; init; }
    public CombinedPhrase[] CombinedPhrases { get; init; } = [];
    public TranscribedPhrase[] Phrases { get; init; } = [];
}

internal sealed record CombinedPhrase(string? Text);

internal sealed record TranscribedPhrase
{
    public long OffsetMilliseconds { get; init; }
    public long DurationMilliseconds { get; init; }
    public string? Text { get; init; }
    public string? Locale { get; init; }
    public int? Speaker { get; init; }
    public TranscribedWord[] Words { get; init; } = [];
}

internal sealed record TranscribedWord(string? Text, long OffsetMilliseconds, long DurationMilliseconds);

public sealed record AzureSpeechTranscriptionOptions
{
    public const string DefaultModel = "MAI-Transcribe-2";
    /// <summary>Default request-size guard (the fast-transcription API rejects very large uploads).</summary>
    public const long DefaultMaxAudioBytes = 300L * 1024 * 1024;

    public string Model { get; init; } = DefaultModel;
    /// <summary><c>clean</c> (captions; drops fillers) or <c>verbatim</c> (dictation).</summary>
    public string TranscribeStyle { get; init; } = "clean";
    public bool Diarization { get; init; }
    public long MaxAudioBytes { get; init; } = DefaultMaxAudioBytes;
}

/// <summary>
/// MAI-Transcribe-2 over the fast-transcription REST API:
/// <c>POST {root}speechtotext/transcriptions:transcribe?api-version=2025-10-15</c> with multipart parts <c>audio</c> and <c>definition</c>.
/// Word timestamps are returned in <c>AdditionalProperties["words"]</c> as <see cref="TimedWord"/> (same key as Whisper).
/// </summary>
public sealed class AzureSpeechTranscriptionClient : ISpeechToTextClient
{
    public const string ProviderName = "azure-speech";

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient http;
    private readonly bool ownsHttp;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;

    public AzureSpeechTranscriptionClient(HttpClient httpClient, FoundryEndpoints endpoints, AzureSpeechTranscriptionOptions? options = null,
        bool ownsHttpClient = false, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        ArgumentNullException.ThrowIfNull(endpoints);
        Endpoint = endpoints.SpeechTranscribe;
        Options = options ?? new();
        ownsHttp = ownsHttpClient;
        this.delay = delay ?? Task.Delay;
    }

    public Uri Endpoint { get; }
    public AzureSpeechTranscriptionOptions Options { get; }

    public static AzureSpeechTranscriptionClient Create(ConnectionSettings connection, AzureSpeechTranscriptionOptions? options = null,
        HttpMessageHandler? innerHandler = null)
    {
        var handler = SpeechAuthHandler.For(connection, SpeechTtsMode.CustomDomain, innerHandler);
        return new(new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) }, FoundryEndpoints.From(connection), options, ownsHttpClient: true);
    }

    /// <summary>Builds the definition JSON from MEAI options (<c>SpeechLanguage</c> → single locale, <c>ModelId</c> → model, extras → diarization/phrases/style).</summary>
    public TranscriptionDefinition BuildDefinition(SpeechToTextOptions? options)
    {
        var extra = options?.AdditionalProperties;
        var language = options?.SpeechLanguage;
        var diarization = extra?.TryGetValue("diarization", out var d) == true ? d is bool b ? b : bool.TryParse(d?.ToString(), out var parsed) && parsed : Options.Diarization;
        var phrases = extra?.TryGetValue("phrases", out var p) == true ? p switch {
            IEnumerable<string> list => list.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray(),
            string single => [single],
            _ => null
        } : null;
        var style = extra?.TryGetValue("style", out var s) == true ? s?.ToString() : null;
        return new() {
            Locales = string.IsNullOrWhiteSpace(language) || language.Equals("auto", StringComparison.OrdinalIgnoreCase) ? null : [language],
            EnhancedMode = new() {
                Model = options?.ModelId ?? Options.Model,
                ModelOptions = new() { Timestamps = "word", TranscribeStyle = style ?? Options.TranscribeStyle }
            },
            Diarization = new(diarization),
            PhraseList = phrases is { Length: > 0 } ? new(phrases) : null
        };
    }

    public async Task<SpeechToTextResponse> GetTextAsync(Stream audioSpeechStream, SpeechToTextOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audioSpeechStream);
        var audio = await ReadAllAsync(audioSpeechStream, Options.MaxAudioBytes, cancellationToken).ConfigureAwait(false);
        var definition = JsonSerializer.Serialize(BuildDefinition(options), Json);

        using var response = await SpeechHttp.SendWithRetryAsync(http, () =>
        {
            var form = new MultipartFormDataContent();
            var audioPart = new ByteArrayContent(audio);
            audioPart.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            form.Add(audioPart, "audio", "audio.wav");
            var definitionPart = new StringContent(definition);
            definitionPart.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            form.Add(definitionPart, "definition");
            var request = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = form };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            SpeechHttp.AddCommonHeaders(request);
            return request;
        }, delay, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.RequestEntityTooLarge)
            throw new AzureSpeechException("The recording is too large for Azure fast transcription. Split it, or use local Whisper (speech.stt.provider = whisper).", response.StatusCode);
        if (!response.IsSuccessStatusCode)
            throw await AzureSpeechException.FromResponseAsync(response, "transcription", null, cancellationToken).ConfigureAwait(false);

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var result = JsonSerializer.Deserialize<TranscriptionResult>(json, Json)
            ?? throw new AzureSpeechException("Azure Speech transcription returned an empty response.", response.StatusCode);
        return ToResponse(result, options?.ModelId ?? Options.Model);
    }

    public async IAsyncEnumerable<SpeechToTextResponseUpdate> GetStreamingTextAsync(Stream audioSpeechStream, SpeechToTextOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetTextAsync(audioSpeechStream, options, cancellationToken).ConfigureAwait(false);
        yield return new SpeechToTextResponseUpdate(response.Contents) {
            Kind = SpeechToTextResponseUpdateKind.TextUpdated,
            StartTime = response.StartTime,
            EndTime = response.EndTime,
            ModelId = response.ModelId,
            AdditionalProperties = response.AdditionalProperties
        };
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is not null ? null
        : serviceType == typeof(SpeechToTextClientMetadata) ? new SpeechToTextClientMetadata(ProviderName, Endpoint, Options.Model)
        : serviceType.IsInstanceOfType(this) ? this
        : null;

    public void Dispose()
    {
        if (ownsHttp) http.Dispose();
    }

    internal static SpeechToTextResponse ToResponse(TranscriptionResult result, string model)
    {
        var text = result.CombinedPhrases.FirstOrDefault()?.Text
            ?? string.Join(' ', result.Phrases.Select(p => p.Text).Where(t => !string.IsNullOrWhiteSpace(t)));
        var words = new List<TimedWord>();
        foreach (var phrase in result.Phrases.OrderBy(p => p.OffsetMilliseconds))
        {
            if (phrase.Words.Length == 0)
            {
                if (!string.IsNullOrWhiteSpace(phrase.Text))
                    words.Add(new(phrase.Text.Trim(), TimeSpan.FromMilliseconds(phrase.OffsetMilliseconds), TimeSpan.FromMilliseconds(phrase.DurationMilliseconds), phrase.Speaker));
                continue;
            }
            foreach (var word in phrase.Words)
                if (!string.IsNullOrWhiteSpace(word.Text))
                    words.Add(new(word.Text.Trim(), TimeSpan.FromMilliseconds(word.OffsetMilliseconds), TimeSpan.FromMilliseconds(word.DurationMilliseconds), phrase.Speaker));
        }
        return new SpeechToTextResponse(text ?? "") {
            ModelId = model,
            StartTime = TimeSpan.Zero,
            EndTime = TimeSpan.FromMilliseconds(result.DurationMilliseconds),
            RawRepresentation = result,
            AdditionalProperties = new() {
                [TimedWord.WordsKey] = (IReadOnlyList<TimedWord>)words,
                ["language"] = result.Phrases.Select(p => p.Locale).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))
            }
        };
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream, long max, CancellationToken cancellationToken)
    {
        if (stream.CanSeek && stream.Length - stream.Position > max) throw TooLarge(max);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > max) throw TooLarge(max);
        }
        if (buffer.Length == 0) throw new ArgumentException("The audio stream is empty.", nameof(stream));
        return buffer.ToArray();
    }

    private static AzureSpeechException TooLarge(long max) =>
        new($"The recording exceeds {max / (1024 * 1024)} MB, the Azure fast-transcription limit configured here. Split it, or use local Whisper (speech.stt.provider = whisper).");
}
