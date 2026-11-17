using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;
using MediaStudio.Configuration;
using MediaStudio.Speech;
using MediaStudio.Speech.Audio;
using MediaStudio.Speech.Azure;
using MediaStudio.Speech.Whisper;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using VideoProduction;
using Whisper.net;

namespace MediaStudio.Tests.Speech;

public sealed class AzureSpeechTests
{
    private const string Root = "https://contoso-ai.services.ai.azure.com/";
    private const string ResourceId = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg/providers/Microsoft.CognitiveServices/accounts/contoso-ai";
    private static readonly FoundryEndpoints Endpoints = new(Root, "eastus", ResourceId);

    [Fact]
    public void SsmlEscapesTextAndOmitsOptionalElements()
    {
        var ssml = SsmlBuilder.Build("a < b & c > d \"e\" 'f'", "en-US-Harper:MAI-Voice-2.1-Flash", "en-US");
        Assert.Equal(
            "<speak version=\"1.0\" xmlns:mstts=\"https://www.w3.org/2001/mstts\" xml:lang=\"en-US\" xmlns=\"http://www.w3.org/2001/10/synthesis\">" +
            "<voice name=\"en-US-Harper:MAI-Voice-2.1-Flash\">a &lt; b &amp; c &gt; d \"e\" 'f'</voice></speak>", ssml);
    }

    [Fact]
    public void SsmlNestsStyleAndProsody()
    {
        var ssml = SsmlBuilder.Build("Hi.", "v", "en-US", "narrator", "+20%");
        Assert.Equal(
            "<speak version=\"1.0\" xmlns:mstts=\"https://www.w3.org/2001/mstts\" xml:lang=\"en-US\" xmlns=\"http://www.w3.org/2001/10/synthesis\">" +
            "<voice name=\"v\"><mstts:express-as style=\"narrator\"><prosody rate=\"+20%\">Hi.</prosody></mstts:express-as></voice></speak>", ssml);
        Assert.Contains("<voice name=\"v\"><prosody rate=\"-10%\">", SsmlBuilder.Build("Hi.", "v", "en-US", rate: "-10%"));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(1f, null)]
    [InlineData(1.2f, "+20%")]
    [InlineData(0.9f, "-10%")]
    public void SpeedMapsToProsodyRate(float? speed, string? rate) => Assert.Equal(rate, SsmlBuilder.RateFor(speed));

    [Fact]
    public async Task TextToSpeechPostsSsmlWithRequiredHeaders()
    {
        var handler = new RecordingHandler(_ => Wav(2400));
        using var client = new AzureSpeechTextToSpeechClient(new HttpClient(handler), Endpoints, Options());

        var response = await client.GetAudioAsync("Hello <world> & friends.");

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(Root + "tts/cognitiveservices/v1", request.Uri.ToString());
        Assert.Equal("application/ssml+xml", request.ContentType?.MediaType);
        Assert.Equal("riff-24khz-16bit-mono-pcm", request.Header("X-Microsoft-OutputFormat"));
        Assert.Contains("MediaStudio", request.Header("User-Agent"));
        Assert.Contains("<voice name=\"en-US-Harper:MAI-Voice-2.1-Flash\"><mstts:express-as style=\"narrator\">Hello &lt;world&gt; &amp; friends.</mstts:express-as>", request.Body);
        var audio = Assert.IsType<DataContent>(Assert.Single(response.Contents));
        Assert.Equal("audio/wav", audio.MediaType);
        Assert.Equal("en-US-Harper:MAI-Voice-2.1-Flash", response.ModelId);
        var metadata = client.GetService<TextToSpeechClientMetadata>();
        Assert.Equal(("azure-speech", "en-US-Harper:MAI-Voice-2.1-Flash"), (metadata?.ProviderName, metadata?.DefaultModelId));
    }

    [Fact]
    public async Task TextToSpeechUsesOptionsAndRegionalEndpoint()
    {
        var handler = new RecordingHandler(_ => Wav(10));
        using var client = new AzureSpeechTextToSpeechClient(new HttpClient(handler), Endpoints, Options() with { Mode = SpeechTtsMode.RegionalAad });
        await client.GetAudioAsync("Hi.", new TextToSpeechOptions {
            VoiceId = "en-US-Grant:MAI-Voice-2.1-Flash", Language = "en-GB", Speed = 1.1f,
            AdditionalProperties = new() { ["style"] = "audiobook" }
        });
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://eastus.tts.speech.microsoft.com/cognitiveservices/v1", request.Uri.ToString());
        Assert.Contains("xml:lang=\"en-GB\"", request.Body);
        Assert.Contains("<voice name=\"en-US-Grant:MAI-Voice-2.1-Flash\"><mstts:express-as style=\"audiobook\"><prosody rate=\"+10%\">Hi.</prosody>", request.Body);
    }

    [Fact]
    public async Task UnsupportedStyleIsDroppedWithWarning()
    {
        var handler = new RecordingHandler(_ => Wav(10));
        using var client = new AzureSpeechTextToSpeechClient(new HttpClient(handler), Endpoints, Options());
        await client.GetAudioAsync("Hi.", new TextToSpeechOptions { VoiceId = "en-US-Sage:MAI-Voice-2.1-Flash" });
        Assert.DoesNotContain("express-as", Assert.Single(handler.Requests).Body);
        Assert.Contains("does not support style 'narrator'", Assert.Single(client.Warnings));
    }

    [Fact]
    public async Task LongTextIsSplitAt5000CharactersAndWavsAreJoined()
    {
        var handler = new RecordingHandler(_ => Wav(24000));
        using var client = new AzureSpeechTextToSpeechClient(new HttpClient(handler), Endpoints, Options());
        var text = string.Join(" ", Enumerable.Range(0, 300).Select(i => $"This is sentence number {i} of the narration."));
        Assert.True(text.Length > 10000);

        var response = await client.GetAudioAsync(text);

        Assert.Equal(3, handler.Requests.Count);
        Assert.All(handler.Requests, r => Assert.True(InnerText(r.Body).Length <= 5000));
        Assert.Equal(text, string.Join(" ", handler.Requests.Select(r => InnerText(r.Body))));
        var wav = Assert.IsType<DataContent>(Assert.Single(response.Contents)).Data.ToArray();
        var samples = 3 * 24000 - 2 * 240;
        Assert.Equal(44 + samples * 2, wav.Length);
        Assert.Equal(wav.Length - 8, BitConverter.ToInt32(wav, 4));
        Assert.Equal(samples * 2, BitConverter.ToInt32(wav, 40));

        var updates = new List<TextToSpeechResponseUpdate>();
        await foreach (var update in client.GetStreamingAudioAsync(text)) updates.Add(update);
        Assert.Equal(3, updates.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "troubleshooting.md#speech-auth")]
    [InlineData(HttpStatusCode.Forbidden, "Cognitive Services User")]
    [InlineData(HttpStatusCode.BadRequest, "not available in region")]
    [InlineData(HttpStatusCode.InternalServerError, "HTTP 500")]
    public async Task TextToSpeechErrorsAreActionable(HttpStatusCode status, string expected)
    {
        using var client = new AzureSpeechTextToSpeechClient(new HttpClient(new RecordingHandler(_ => new HttpResponseMessage(status))), Endpoints, Options());
        var error = await Assert.ThrowsAsync<AzureSpeechException>(() => client.GetAudioAsync("Hi."));
        Assert.Contains(expected, error.Message);
        Assert.Equal(status, error.StatusCode);
    }

    [Fact]
    public async Task ThrottlingHonorsRetryAfterOnce()
    {
        var delays = new List<TimeSpan>();
        var handler = new RecordingHandler(n =>
        {
            var throttled = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            throttled.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(2));
            return n == 0 ? throttled : Wav(10);
        });
        using var client = new AzureSpeechTextToSpeechClient(new HttpClient(handler), Endpoints, Options(),
            delay: (d, _) => { delays.Add(d); return Task.CompletedTask; });
        await client.GetAudioAsync("Hi.");
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(2)], delays);

        var always = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        using var throttledClient = new AzureSpeechTextToSpeechClient(new HttpClient(always), Endpoints, Options(), delay: (_, _) => Task.CompletedTask);
        var error = await Assert.ThrowsAsync<AzureSpeechException>(() => throttledClient.GetAudioAsync("Hi."));
        Assert.Contains("429", error.Message);
        Assert.Equal(2, always.Requests.Count);
    }

    [Fact]
    public void ManifestDefaultsDriveVoiceAndStyle()
    {
        var options = AzureSpeechTextToSpeechOptions.FromManifest(Manifest());
        Assert.Equal("en-US-Harper:MAI-Voice-2.1-Flash", options.Voice);
        Assert.Equal("narrator", options.Style);
        Assert.Null(AzureSpeechTextToSpeechOptions.FromManifest(Manifest(), "en-US-Sage:MAI-Voice-2.1-Flash").Style);
    }

    [Fact]
    public async Task AuthHandlerSendsBearerOnCustomDomainAndCachesToken()
    {
        var credential = new CountingCredential(TimeSpan.FromHours(1));
        var time = new ManualTime();
        var inner = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var http = new HttpClient(new SpeechAuthHandler(Endpoints, SpeechAuthMode.CustomDomain, credential, timeProvider: time, innerHandler: inner));

        await http.GetAsync(Endpoints.SpeechTts);
        await http.GetAsync(Endpoints.SpeechTranscribe);
        Assert.Equal(1, credential.Calls);
        Assert.Equal("Bearer token-1", inner.Requests[0].Header("Authorization"));
        Assert.Equal("Bearer token-1", inner.Requests[1].Header("Authorization"));
        Assert.Equal([FoundryEndpoints.CognitiveServicesScope], credential.Scopes);

        time.Now += TimeSpan.FromMinutes(56); // inside the 5-minute refresh margin
        await http.GetAsync(Endpoints.SpeechTts);
        Assert.Equal(2, credential.Calls);
        Assert.Equal("Bearer token-2", inner.Requests[2].Header("Authorization"));
    }

    [Fact]
    public async Task AuthHandlerUsesAadFormatOnRegionalHost()
    {
        var inner = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var http = new HttpClient(new SpeechAuthHandler(Endpoints, SpeechAuthMode.RegionalAad, new CountingCredential(TimeSpan.FromHours(1)), innerHandler: inner));
        await http.GetAsync(Endpoints.SpeechTtsRegional);
        Assert.Equal($"Bearer aad#{ResourceId}#token-1", Assert.Single(inner.Requests).Header("Authorization"));
    }

    [Fact]
    public async Task AuthHandlerSendsSubscriptionKeyInKeyMode()
    {
        var inner = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var http = new HttpClient(new SpeechAuthHandler(Endpoints, SpeechAuthMode.ApiKey, apiKey: "test-key", innerHandler: inner));
        await http.GetAsync(Endpoints.SpeechTts);
        var request = Assert.Single(inner.Requests);
        Assert.Equal("test-key", request.Header("Ocp-Apim-Subscription-Key"));
        Assert.Null(request.Header("Authorization"));
    }

    [Theory]
    [InlineData("https://attacker.example.com/tts")]
    [InlineData("http://contoso-ai.services.ai.azure.com/tts")]
    [InlineData("https://westus.tts.speech.microsoft.com/cognitiveservices/v1")]
    public async Task AuthHandlerRefusesUntrustedHosts(string url)
    {
        var credential = new CountingCredential(TimeSpan.FromHours(1));
        var inner = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var http = new HttpClient(new SpeechAuthHandler(Endpoints, SpeechAuthMode.CustomDomain, credential, innerHandler: inner));
        await Assert.ThrowsAsync<InvalidOperationException>(() => http.GetAsync(url));
        Assert.Empty(inner.Requests);
        Assert.Equal(0, credential.Calls);
    }

    [Fact]
    public void RegionalAuthRequiresResourceId() =>
        Assert.Throws<InvalidOperationException>(() => new SpeechAuthHandler(new FoundryEndpoints(Root, "eastus"), SpeechAuthMode.RegionalAad, new CountingCredential(TimeSpan.FromHours(1))));

    [Fact]
    public async Task TranscriptionPostsMultipartWithGoldenDefinition()
    {
        var handler = new RecordingHandler(_ => Json(Fixture()));
        using var client = new AzureSpeechTranscriptionClient(new HttpClient(handler), Endpoints);
        var wav = WavFile.ToPcm16Wav(new float[1600], 16000);

        await client.GetTextAsync(new MemoryStream(wav));

        var request = Assert.Single(handler.Requests);
        Assert.Equal(Root + "speechtotext/transcriptions:transcribe?api-version=2025-10-15", request.Uri.ToString());
        Assert.Equal("multipart/form-data", request.ContentType?.MediaType);
        Assert.Equal(2, request.Parts.Count);
        var audio = request.Parts[0];
        Assert.Equal("audio", audio.Name);
        Assert.Equal("audio.wav", audio.FileName);
        Assert.Equal(wav, audio.Bytes);
        var definition = request.Parts[1];
        Assert.Equal("definition", definition.Name);
        Assert.Equal(
            """{"enhancedMode":{"enabled":true,"model":"MAI-Transcribe-2","modelOptions":{"timestamps":"word","transcribeStyle":"clean"}},"diarization":{"enabled":false}}""",
            Encoding.UTF8.GetString(definition.Bytes));
    }

    [Fact]
    public void TranscriptionDefinitionMapsMeaiOptions()
    {
        using var client = new AzureSpeechTranscriptionClient(new HttpClient(), Endpoints);
        var definition = client.BuildDefinition(new SpeechToTextOptions {
            SpeechLanguage = "en-US",
            ModelId = "MAI-Transcribe-3",
            AdditionalProperties = new() { ["diarization"] = true, ["phrases"] = new[] { "Kokoro", "Whisper" }, ["style"] = "verbatim" }
        });
        Assert.Equal(
            """{"locales":["en-US"],"enhancedMode":{"enabled":true,"model":"MAI-Transcribe-3","modelOptions":{"timestamps":"word","transcribeStyle":"verbatim"}},"diarization":{"enabled":true},"phraseList":{"phrases":["Kokoro","Whisper"]}}""",
            JsonSerializer.Serialize(definition, AzureSpeechTranscriptionClient.Json));
        Assert.Null(client.BuildDefinition(new SpeechToTextOptions { SpeechLanguage = "auto" }).Locales);
    }

    [Fact]
    public async Task TranscriptionMapsRecordedResponseToTextAndWords()
    {
        using var client = new AzureSpeechTranscriptionClient(new HttpClient(new RecordingHandler(_ => Json(Fixture()))), Endpoints);
        var response = await client.GetTextAsync(new MemoryStream(WavFile.ToPcm16Wav(new float[16], 16000)), new SpeechToTextOptions { ModelId = "custom-model" });

        Assert.Equal("Hello world. This is Media Studio.", response.Text);
        Assert.Equal(TimeSpan.FromMilliseconds(2480), response.EndTime);
        Assert.Equal("custom-model", response.ModelId);
        var words = TimedWord.From(response);
        Assert.Equal(["Hello", "world.", "This", "is", "Media", "Studio."], words.Select(w => w.Text));
        Assert.Equal(TimeSpan.FromMilliseconds(80), words[0].Offset);
        Assert.Equal(TimeSpan.FromMilliseconds(400), words[0].Duration);
        Assert.Equal(TimeSpan.FromMilliseconds(2360), words[^1].End);
        Assert.Equal("en-US", response.AdditionalProperties?["language"]);
        var metadata = client.GetService<SpeechToTextClientMetadata>();
        Assert.Equal(("azure-speech", "MAI-Transcribe-2"), (metadata?.ProviderName, metadata?.DefaultModelId));
    }

    [Fact]
    public async Task TranscriptionTooLargeSuggestsWhisper()
    {
        using var client = new AzureSpeechTranscriptionClient(new HttpClient(new RecordingHandler(_ => Json(Fixture()))), Endpoints, new() { MaxAudioBytes = 100 });
        var error = await Assert.ThrowsAsync<AzureSpeechException>(() => client.GetTextAsync(new MemoryStream(new byte[101])));
        Assert.Contains("Whisper", error.Message);

        using var rejected = new AzureSpeechTranscriptionClient(new HttpClient(new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.RequestEntityTooLarge))), Endpoints);
        error = await Assert.ThrowsAsync<AzureSpeechException>(() => rejected.GetTextAsync(new MemoryStream(new byte[10])));
        Assert.Contains("Whisper", error.Message);
    }

    public static TheoryData<string> WordSources => ["whisper", "azure"];

    [Theory]
    [MemberData(nameof(WordSources))]
    public async Task EverySpeechToTextClientReturnsOrderedTimedWords(string source)
    {
        SpeechToTextResponse response;
        if (source == "whisper")
        {
            response = new SpeechToTextResponse("Hello world.") { AdditionalProperties = new() { [TimedWord.WordsKey] = WhisperNetSpeechToTextClient.Words([Segment()]) } };
        }
        else
        {
            using var client = new AzureSpeechTranscriptionClient(new HttpClient(new RecordingHandler(_ => Json(Fixture()))), Endpoints);
            response = await client.GetTextAsync(new MemoryStream(new byte[10]));
        }
        Assert.True(response.AdditionalProperties?.ContainsKey(TimedWord.WordsKey));
        var words = TimedWord.From(response);
        Assert.NotEmpty(words);
        Assert.Equal(words.OrderBy(w => w.Offset).Select(w => w.Text), words.Select(w => w.Text));
        Assert.All(words, w => Assert.True(w.Duration >= TimeSpan.Zero && w.Text.Trim().Length > 0));
    }

    [Fact]
    public void WhisperTokensBecomeWordsWithCentisecondTimes()
    {
        var words = WhisperNetSpeechToTextClient.Words([Segment()]);
        Assert.Equal(["Hello", "world."], words.Select(w => w.Text));
        Assert.Equal(TimeSpan.FromMilliseconds(100), words[0].Offset);
        Assert.Equal(TimeSpan.FromMilliseconds(400), words[0].Duration);
        Assert.Equal(TimeSpan.FromMilliseconds(500), words[1].Offset);
        Assert.Equal(TimeSpan.FromMilliseconds(1000), words[1].End);
    }

    [Fact]
    public void WhisperWithoutTokenTimesSplitsSegmentEvenly()
    {
        var segment = new SegmentData(" a b", TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), 0, 0, 0, 0, "en",
            [new WhisperToken { Text = " a" }, new WhisperToken { Text = " b" }]);
        var words = WhisperNetSpeechToTextClient.Words([segment]);
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3)], words.Select(w => w.Offset));
    }

    [Theory]
    [InlineData("en-US", "en")]
    [InlineData(null, "auto")]
    [InlineData("de_DE", "de")]
    public void WhisperLanguageUsesIso639(string? language, string expected) => Assert.Equal(expected, WhisperNetSpeechToTextClient.WhisperLanguage(language));

    [Fact]
    public void FactoryRefusesCloudSpeechWhenCloudIsDisabled()
    {
        var settings = new ProviderSettings {
            Connections = [new ConnectionSettings { Id = "foundry", Kind = ConnectionKind.Foundry, Endpoint = Root, Auth = AuthMode.Entra }],
            Speech = new SpeechSettings { Tts = new TtsSettings { Provider = TtsProvider.AzureSpeech, Connection = "foundry" } }
        };
        var factory = new SpeechClientFactory(settings, new ModelAssets(Manifest(), Path.GetTempPath()));
        Assert.True(factory.IsTextToSpeechCloud);
        Assert.False(factory.IsSpeechToTextCloud);
        var error = Assert.Throws<InvalidOperationException>(() => factory.CreateTextToSpeech());
        Assert.Contains("allowCloud", error.Message);
    }

    [Fact]
    public void FactoryCreatesAzureClientsWithManifestDefaultsWhenCloudIsAllowed()
    {
        var settings = new ProviderSettings {
            Privacy = new PrivacySettings { AllowCloud = true },
            Connections = [new ConnectionSettings { Id = "foundry", Kind = ConnectionKind.Foundry, Endpoint = Root, Auth = AuthMode.Entra }],
            Speech = new SpeechSettings {
                Tts = new TtsSettings { Provider = TtsProvider.AzureSpeech, Connection = "foundry" },
                Stt = new SttSettings { Provider = SttProvider.AzureSpeech, Connection = "foundry" }
            }
        };
        var factory = new SpeechClientFactory(settings, new ModelAssets(Manifest(), Path.GetTempPath()));
        using var tts = Assert.IsType<AzureSpeechTextToSpeechClient>(factory.CreateTextToSpeech());
        Assert.Equal("en-US-Harper:MAI-Voice-2.1-Flash", tts.Options.Voice); // Kokoro's af_heart is not an Azure voice
        Assert.Equal("narrator", tts.Options.Style);
        using var stt = Assert.IsType<AzureSpeechTranscriptionClient>(factory.CreateSpeechToText());
        Assert.Equal("MAI-Transcribe-2", stt.Options.Model); // the Whisper default model name is not sent to Azure
    }

    [Fact]
    public void ServiceRegistrationResolvesKeyedSettingsSelectedClientsLazily()
    {
        var settings = new ProviderSettings {
            Privacy = new PrivacySettings { AllowCloud = true },
            Connections = [new ConnectionSettings { Id = "foundry", Kind = ConnectionKind.Foundry, Endpoint = Root, Auth = AuthMode.Entra }],
            Speech = new SpeechSettings { Tts = new TtsSettings { Provider = TtsProvider.AzureSpeech, Connection = "foundry" } }
        };
        var services = new ServiceCollection()
            .AddMediaStudioSpeech(settings, new ModelAssets(ModelManifest.LoadDefault(), Path.Combine(Path.GetTempPath(), "no-models-" + Guid.NewGuid().ToString("N"))));
        using var provider = services.BuildServiceProvider();
        Assert.IsType<AzureSpeechTextToSpeechClient>(
            provider.GetRequiredKeyedService<ITextToSpeechClient>(SpeechClientFactory.NarrationKey));
        var error = Assert.Throws<InvalidOperationException>(() =>
            provider.GetRequiredKeyedService<ISpeechToTextClient>(SpeechClientFactory.TranscriptionKey));
        Assert.Contains("assets ensure", error.Message);
    }

    private static SegmentData Segment() => new(" Hello world.", TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(1000), 0, 0, 0, 0, "en",
    [
        new WhisperToken { Text = "[_BEG_]", Start = 0, End = 0 },
        new WhisperToken { Text = " Hello", Start = 10, End = 50 },
        new WhisperToken { Text = " world", Start = 50, End = 90 },
        new WhisperToken { Text = ".", Start = 90, End = 100 },
        new WhisperToken { Text = "<|endoftext|>", Start = 100, End = 100 }
    ]);

    private static AzureSpeechTextToSpeechOptions Options() => AzureSpeechTextToSpeechOptions.FromManifest(Manifest());

    private static ModelManifest Manifest() => ModelManifest.Parse("""
        {
          "schemaVersion": 1,
          "assets": [],
          "cloudVoices": [
            { "voice": "en-US-Harper:MAI-Voice-2.1-Flash", "styles": [ "narrator", "educational" ], "defaultStyle": "narrator", "default": true },
            { "voice": "en-US-Grant:MAI-Voice-2.1-Flash", "styles": [ "narrator", "educational", "audiobook" ], "defaultStyle": "narrator" },
            { "voice": "en-US-Sage:MAI-Voice-2.1-Flash", "styles": [], "defaultStyle": null }
          ]
        }
        """);

    private static string Fixture() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Speech", "fixtures", "mai-transcribe-response.json"));

    private static string InnerText(string ssml)
    {
        var start = ssml.IndexOf("style=\"narrator\">", StringComparison.Ordinal) + "style=\"narrator\">".Length;
        var end = ssml.IndexOf("</mstts:express-as>", StringComparison.Ordinal);
        return ssml[start..end];
    }

    private static HttpResponseMessage Wav(int samples) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(WavFile.ToPcm16Wav(Enumerable.Repeat(0.1f, samples).ToArray(), 24000)) };

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    internal sealed record Part(string? Name, string? FileName, byte[] Bytes);

    internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, Dictionary<string, string> Headers, MediaTypeHeaderValue? ContentType, string Body, List<Part> Parts)
    {
        public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
    }

    internal sealed class RecordingHandler(Func<int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(" ", h.Value), StringComparer.OrdinalIgnoreCase);
            var parts = new List<Part>();
            var body = "";
            if (request.Content is MultipartFormDataContent multipart)
                foreach (var part in multipart)
                    parts.Add(new(part.Headers.ContentDisposition?.Name?.Trim('"'), part.Headers.ContentDisposition?.FileName?.Trim('"'),
                        await part.ReadAsByteArrayAsync(cancellationToken)));
            else if (request.Content is not null)
                body = await request.Content.ReadAsStringAsync(cancellationToken);
            var index = Requests.Count;
            Requests.Add(new(request.Method, request.RequestUri!, headers, request.Content?.Headers.ContentType, body, parts));
            return respond(index);
        }
    }

    private sealed class CountingCredential(TimeSpan lifetime) : TokenCredential
    {
        public int Calls { get; private set; }
        public string[] Scopes { get; private set; } = [];

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            GetTokenAsync(requestContext, cancellationToken).AsTask().GetAwaiter().GetResult();

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Calls++;
            Scopes = requestContext.Scopes;
            return ValueTask.FromResult(new AccessToken($"token-{Calls}", ManualTime.Start + lifetime));
        }
    }

    private sealed class ManualTime : TimeProvider
    {
        public static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public DateTimeOffset Now { get; set; } = Start;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
