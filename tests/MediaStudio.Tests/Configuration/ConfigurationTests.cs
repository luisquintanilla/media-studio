using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MediaStudio.Configuration;
using Microsoft.Extensions.Configuration;

namespace MediaStudio.Tests.Configuration;

public sealed class ConfigurationTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "configuration-tests", Guid.NewGuid().ToString("N")[..8]);

    public ConfigurationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static ModelManifest Manifest => ModelManifest.Load(Path.Combine(TestPaths.RepoRoot, "assets", ModelManifest.FileName));

    private static ProviderSettings LocalDefaults => ProviderSettingsDefaults.Local(HardwareTier.M, Manifest);

    private static readonly ConnectionSettings Azure = new() {
        Id = "azure", Kind = ConnectionKind.Foundry, Endpoint = "https://example-account.services.ai.azure.com/", Auth = AuthMode.Entra
    };

    [Fact]
    public void LocalDefaultsAreLocalFirstAndValid()
    {
        var settings = LocalDefaults;
        Assert.Empty(ProviderSettingsValidator.Validate(settings));
        Assert.False(settings.Privacy.AllowCloud);
        Assert.All(settings.Connections, c => Assert.False(c.IsCloud));
        Assert.Equal([ChatTierNames.Fast, ChatTierNames.Balanced], settings.Chat.Tiers.Select(t => t.Name));
        Assert.Equal(TtsProvider.Kokoro, settings.Speech.Tts.Provider);
        Assert.Equal(SttProvider.Whisper, settings.Speech.Stt.Provider);
    }

    public static TheoryData<string, string> InvalidConnections => new()
    {
        { """{"id":"x","kind":"openai-compatible","endpoint":"http://192.168.1.5:11434/v1"}""", "allowRemote" },
        { """{"id":"x","kind":"openai-compatible","endpoint":"http://example.com/v1","allowRemote":true}""", "HTTPS" },
        { """{"id":"x","kind":"openai-compatible","endpoint":"http://localhost:11434/v1","auth":"entra"}""", "\"none\" or \"api-key\"" },
        { """{"id":"x","kind":"foundry","endpoint":"https://example.com/","auth":"entra"}""", "not a Foundry host" },
        { """{"id":"x","kind":"foundry","endpoint":"http://example-account.openai.azure.com/","auth":"entra"}""", "HTTPS" },
        { """{"id":"x","kind":"foundry","endpoint":"https://example-account.openai.azure.com/","auth":"none"}""", "\"entra\"" },
        { """{"id":"x","kind":"foundry","endpoint":"https://example-account.openai.azure.com/?key=1","auth":"entra"}""", "query" },
        { """{"id":"x","kind":"foundry","endpoint":"https://example-account.openai.azure.com/","auth":"entra","region":"East US"}""", "region" }
    };

    [Theory]
    [MemberData(nameof(InvalidConnections))]
    public void ValidatorRejectsUnsafeConnections(string json, string expected)
    {
        var connection = System.Text.Json.JsonSerializer.Deserialize<ConnectionSettings>(json, ProviderSettingsJson.Options)!;
        var errors = ProviderSettingsValidator.ValidateConnection(connection);
        Assert.Contains(errors, e => e.Message.Contains(expected, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("openai-compatible", "http://localhost:11434/v1", "none", false)]
    [InlineData("openai-compatible", "http://127.0.0.1:9931/v1", "api-key", false)]
    [InlineData("openai-compatible", "https://models.example.com/v1", "api-key", true)]
    [InlineData("foundry", "https://example-account.services.ai.azure.com/api/projects/demo", "entra", false)]
    [InlineData("foundry", "https://example-account.openai.azure.com/openai/v1", "api-key", false)]
    [InlineData("foundry", "https://example-account.cognitiveservices.azure.com/", "entra", false)]
    public void ValidatorAcceptsSupportedConnections(string kind, string endpoint, string auth, bool allowRemote)
    {
        var connection = System.Text.Json.JsonSerializer.Deserialize<ConnectionSettings>(
            $$"""{"id":"x","kind":"{{kind}}","endpoint":"{{endpoint}}","auth":"{{auth}}","allowRemote":{{(allowRemote ? "true" : "false")}}}""",
            ProviderSettingsJson.Options)!;
        Assert.Empty(ProviderSettingsValidator.ValidateConnection(connection));
    }

    [Fact]
    public void ValidatorReportsCrossReferenceErrorsWithPaths()
    {
        var settings = LocalDefaults with {
            Chat = LocalDefaults.Chat with {
                Tiers = [.. LocalDefaults.Chat.Tiers, new ChatTier { Name = "cloud", Connection = "missing", Model = "chat" }]
            },
            Documents = new DocumentSettings { Candidates = [DocumentCandidates.PdfPig, DocumentCandidates.Mistral] },
            Images = new ImageSettings { Mode = ImageMode.Png }
        };
        var errors = ProviderSettingsValidator.Validate(settings);
        Assert.Contains(errors, e => e.Path == "chat.tiers[2].connection" && e.Message.Contains("Known: local", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Path == "documents.candidates" && e.Message.Contains("configure azd", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Path == "images.mode");
        var exception = Assert.Throws<ProviderSettingsValidationException>(() => ProviderSettingsValidator.ThrowIfInvalid(settings));
        Assert.Contains("Setup wizard", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://example-account.services.ai.azure.com/")]
    [InlineData("https://example-account.services.ai.azure.com/api/projects/demo")]
    [InlineData("https://example-account.services.ai.azure.com/openai/v1/")]
    [InlineData("https://EXAMPLE-account.services.ai.azure.com/anthropic")]
    public void FoundryEndpointsDeriveEveryServiceFromOneEndpoint(string endpoint)
    {
        var endpoints = new FoundryEndpoints(endpoint, "EastUS2", "/subscriptions/0/resourceGroups/rg/providers/Microsoft.CognitiveServices/accounts/example-account");
        const string root = "https://example-account.services.ai.azure.com/";
        Assert.Equal(root, endpoints.Root.AbsoluteUri);
        Assert.Equal(root + "openai/v1/", endpoints.OpenAIv1.AbsoluteUri);
        Assert.Equal(root + "anthropic", endpoints.Anthropic.AbsoluteUri);
        Assert.Equal(root, endpoints.DocumentIntelligence.AbsoluteUri);
        Assert.Equal(root, endpoints.ContentUnderstanding.AbsoluteUri);
        Assert.Equal(root + "providers/mistral/azure/ocr", endpoints.MistralOcr.AbsoluteUri);
        Assert.Equal(root + "tts/cognitiveservices/v1", endpoints.SpeechTts.AbsoluteUri);
        Assert.Equal("https://eastus2.tts.speech.microsoft.com/cognitiveservices/v1", endpoints.SpeechTtsRegional.AbsoluteUri);
        Assert.Equal(root + "speechtotext/transcriptions:transcribe?api-version=" + FoundryEndpoints.SpeechTranscribeApiVersion,
            endpoints.SpeechTranscribe.AbsoluteUri);
        Assert.Equal("Bearer t", endpoints.SpeechAuthorization("t", SpeechTtsMode.CustomDomain));
        Assert.StartsWith("Bearer aad#/subscriptions/0/", endpoints.SpeechAuthorization("t", SpeechTtsMode.RegionalAad), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://example.com/", "not a Foundry host")]
    [InlineData("https://services.ai.azure.com/", "not a Foundry host")]
    [InlineData("https://example-account.openai.azure.com/deployments/x", "Unexpected path")]
    [InlineData("example-account.openai.azure.com", "absolute URL")]
    public void FoundryEndpointsRejectUntrustedInput(string endpoint, string expected)
    {
        Assert.False(FoundryEndpoints.TryNormalizeRoot(endpoint, out _, out var error));
        Assert.Contains(expected, error, StringComparison.Ordinal);
        var regional = new FoundryEndpoints("https://example-account.openai.azure.com/");
        Assert.Throws<InvalidOperationException>(() => regional.SpeechTtsRegional);
    }

    [Fact]
    public void CredentialFactoryNamesTheExactKeyLocationsAndNeverFallsBack()
    {
        Assert.Equal("MEDIASTUDIO__CONNECTIONS__MY_AZURE__APIKEY", CredentialFactory.ApiKeyEnvironmentVariable("my-azure"));
        Assert.Equal("MediaStudio:Connections:my-azure:ApiKey", CredentialFactory.ApiKeyUserSecretName("my-azure"));
        var empty = new ConfigurationBuilder().Build();
        var exception = Assert.Throws<InvalidOperationException>(() => CredentialFactory.GetApiKey("my-azure", _ => null, empty));
        Assert.Contains("MEDIASTUDIO__CONNECTIONS__MY_AZURE__APIKEY", exception.Message, StringComparison.Ordinal);
        Assert.Contains($"--id {CredentialFactory.UserSecretsId}", exception.Message, StringComparison.Ordinal);
        Assert.Contains("No other credential is tried", exception.Message, StringComparison.Ordinal);

        Assert.Equal("from-env", CredentialFactory.GetApiKey("my-azure", name => name.EndsWith("__APIKEY", StringComparison.Ordinal) ? " from-env " : null, empty));
        var secrets = new ConfigurationBuilder().AddInMemoryCollection([new("MediaStudio:Connections:my-azure:ApiKey", "from-secrets")]).Build();
        Assert.Equal("from-secrets", CredentialFactory.GetApiKey("my-azure", _ => null, secrets));
        Assert.Throws<InvalidOperationException>(() => CredentialFactory.CreateTokenCredential(Azure with { Auth = AuthMode.ApiKey }));
        Assert.NotNull(CredentialFactory.CreateTokenCredential(Azure));
    }

    [Fact]
    public void StoreRoundTripsAndRejectsInvalidFilesWithActionableErrors()
    {
        var store = new ProviderSettingsStore(Path.Combine(_root, "settings", ProviderSettingsStore.FileName));
        Assert.Null(store.TryLoad());
        Assert.Contains("configure local", Assert.Throws<FileNotFoundException>(store.Load).Message, StringComparison.Ordinal);

        var settings = ProviderSettingsDefaults.WithAzure(LocalDefaults, Azure, new Dictionary<string, string> { ["chat"] = "gpt-chat" });
        store.Save(settings);
        var text = File.ReadAllText(store.FilePath);
        Assert.Contains("\"kind\": \"foundry\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("apiKey", text, StringComparison.OrdinalIgnoreCase);
        var loaded = store.Load();
        Assert.Equal(ProviderSettingsJson.Serialize(settings), ProviderSettingsJson.Serialize(loaded));
        Assert.True(loaded.Privacy.AllowCloud);
        Assert.Equal(ChatTierNames.Fast, loaded.Chat.Tiers[0].Name);

        Assert.Throws<ProviderSettingsValidationException>(() => store.Save(settings with { Chat = new ChatSettings() }));
        Assert.Equal(text, File.ReadAllText(store.FilePath));
        File.WriteAllText(store.FilePath, "{ \"schemaVersion\": 2, ");
        Assert.Contains("not valid JSON", Assert.Throws<InvalidDataException>(store.Load).Message, StringComparison.Ordinal);
        File.WriteAllText(store.FilePath, "{ \"schemaVersion\": 3 }");
        Assert.Contains("schemaVersion 3", Assert.Throws<InvalidDataException>(store.Load).Message, StringComparison.Ordinal);
        // foundry-openai is only a live (push-to-talk) provider; narration and transcription reject it at load.
        File.WriteAllText(store.FilePath, text.Replace("\"provider\": \"kokoro\"", "\"provider\": \"foundry-openai\"", StringComparison.Ordinal));
        Assert.Contains("speech.tts.provider", Assert.Throws<InvalidDataException>(store.Load).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MigratesV1FileToV2InPlaceWithBackup()
    {
        var fixture = Path.Combine(TestPaths.RepoRoot, "tests", "MediaStudio.Tests", "Configuration", "Fixtures", "v1-model-providers.json");
        var path = Path.Combine(_root, "settings", ProviderSettingsStore.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Copy(fixture, path);
        var store = new ProviderSettingsStore(path, () => LocalDefaults);

        var settings = store.Load();

        Assert.Equal(File.ReadAllText(fixture), File.ReadAllText(store.BackupPath));
        Assert.Equal(2, ProviderSettingsMigrator.ReadSchemaVersion(JsonNode.Parse(File.ReadAllText(path))!));
        var foundry = Assert.Single(settings.Connections, c => c.Id == "foundry");
        Assert.Equal((ConnectionKind.Foundry, "https://example-account.openai.azure.com/", AuthMode.Entra, "eastus2"),
            (foundry.Kind, foundry.Endpoint, foundry.Auth, foundry.Region));
        Assert.Equal(AuthMode.ApiKey, settings.FindConnection("keyed")!.Auth);
        Assert.Equal("https://example-account.services.ai.azure.com/", settings.FindConnection("foundry-claude")!.Endpoint);
        var local = Assert.Single(settings.Connections, c => c.Kind == ConnectionKind.OpenAICompatible);
        Assert.Equal("local", local.Id);

        Assert.Equal([ChatTierNames.Fast, ChatTierNames.Balanced, ChatTierNames.Cloud, ChatTierNames.Strong], settings.Chat.Tiers.Select(t => t.Name));
        Assert.Equal(("foundry", "gpt-general", ChatApi.Responses), (settings.FindTier("cloud")!.Connection, settings.FindTier("cloud")!.Model, settings.FindTier("cloud")!.Api!.Value));
        Assert.Equal(("foundry-claude", ChatApi.Anthropic), (settings.FindTier("strong")!.Connection, settings.FindTier("strong")!.Api!.Value));
        Assert.Equal((LiveSpeechProvider.FoundryOpenAI, "foundry", "gpt-live-transcribe"),
            (settings.Speech.Live.Provider, settings.Speech.Live.Connection, settings.Speech.Live.Deployment));
        Assert.Equal((ImageMode.Png, "keyed", "image"), (settings.Images.Mode, settings.Images.Connection, settings.Images.Deployment));
        Assert.Equal("gpt-decisions", settings.Decisions.CloudDeployment);
        Assert.True(settings.Privacy.AllowCloud);
        Assert.Equal(TtsProvider.Kokoro, settings.Speech.Tts.Provider);
        Assert.Contains(store.LastMigrationChanges, c => c.Contains("MEDIASTUDIO__CONNECTIONS__KEYED__APIKEY", StringComparison.Ordinal));

        var reloaded = new ProviderSettingsStore(path).Load();
        Assert.Equal(ProviderSettingsJson.Serialize(settings), ProviderSettingsJson.Serialize(reloaded));
    }

    private const string AzdJson = """
        {
          "AZURE_ENV_NAME": "demo",
          "AZURE_LOCATION": "EastUS2",
          "AZURE_RESOURCE_GROUP": "rg-demo",
          "AZURE_FOUNDRY_NAME": "example-account",
          "AZURE_FOUNDRY_RESOURCE_ID": "/subscriptions/0/resourceGroups/rg-demo/providers/Microsoft.CognitiveServices/accounts/example-account",
          "AZURE_FOUNDRY_ENDPOINT": "https://example-account.services.ai.azure.com/",
          "AZURE_FOUNDRY_PROJECT_ENDPOINT": "https://example-account.services.ai.azure.com/api/projects/media-studio",
          "MEDIASTUDIO_DEPLOYMENTS": "{\"chat\":\"gpt-chat\",\"authoring\":\"gpt-authoring\",\"image\":\"gpt-image\",\"ocr\":\"mistral-ocr\"}",
          "MEDIASTUDIO_CU_ENABLED": true,
          "MEDIASTUDIO_CU_EMBEDDING_MODEL": "embedding",
          "MEDIASTUDIO_CLAUDE_ENABLED": false,
          "MEDIASTUDIO_OPENAI_AUDIO_ENABLED": false,
          "MEDIASTUDIO_MISTRAL_OCR_ENABLED": true,
          "AZURE_OPENAI_API_KEY": "must-be-ignored"
        }
        """;

    private const string AzdDotenv = """
        AZURE_ENV_NAME="demo"
        AZURE_LOCATION="EastUS2"
        AZURE_FOUNDRY_RESOURCE_ID="/subscriptions/0/resourceGroups/rg-demo/providers/Microsoft.CognitiveServices/accounts/example-account"
        AZURE_FOUNDRY_ENDPOINT="https://example-account.services.ai.azure.com/"
        MEDIASTUDIO_DEPLOYMENTS="{\"chat\":\"gpt-chat\",\"authoring\":\"gpt-authoring\",\"image\":\"gpt-image\",\"ocr\":\"mistral-ocr\"}"
        MEDIASTUDIO_CU_ENABLED="true"
        MEDIASTUDIO_CU_EMBEDDING_MODEL="embedding"
        MEDIASTUDIO_CLAUDE_ENABLED="false"
        MEDIASTUDIO_MISTRAL_OCR_ENABLED="true"
        SOME_SECRET="must-be-ignored"
        """;

    [Theory]
    [InlineData(AzdJson)]
    [InlineData(AzdDotenv)]
    public void ParsesAzdOutputsAndAppliesThemWithoutKeys(string raw)
    {
        var outputs = AzdEnvironmentImporter.Parse(raw);
        Assert.Equal("https://example-account.services.ai.azure.com/", outputs.FoundryEndpoint);
        Assert.Equal("eastus2", outputs.Location);
        Assert.Equal("gpt-chat", outputs.Deployments["chat"]);
        Assert.True(outputs.ContentUnderstanding);
        Assert.True(outputs.MistralOcr);
        Assert.False(outputs.Claude);
        Assert.Equal("embedding", outputs.ContentUnderstandingEmbeddingModel);

        var settings = AzdEnvironmentImporter.Apply(LocalDefaults, outputs);
        Assert.Empty(ProviderSettingsValidator.Validate(settings));
        var azure = settings.FindConnection("azure")!;
        Assert.Equal((ConnectionKind.Foundry, AuthMode.Entra, "eastus2"), (azure.Kind, azure.Auth, azure.Region));
        Assert.Equal(outputs.ResourceId, azure.ResourceId);
        Assert.True(settings.Privacy.AllowCloud);
        Assert.Equal([ChatTierNames.Fast, ChatTierNames.Balanced, ChatTierNames.Cloud, ChatTierNames.Strong], settings.Chat.Tiers.Select(t => t.Name));
        Assert.Equal("gpt-authoring", settings.FindTier("strong")!.Model);
        Assert.Equal("gpt-image", settings.Images.Deployment);
        Assert.Equal(ImageMode.Svg, settings.Images.Mode);
        Assert.Equal("mistral-ocr", settings.Documents.MistralDeployment);
        Assert.Contains(DocumentCandidates.ContentUnderstanding, settings.Documents.Candidates);
        Assert.Contains(DocumentCandidates.Mistral, settings.Documents.Candidates);
        Assert.Equal(TtsProvider.Kokoro, settings.Speech.Tts.Provider);
        Assert.DoesNotContain("must-be-ignored", ProviderSettingsJson.Serialize(settings), StringComparison.Ordinal);
    }

    [Fact]
    public void AzdParseExplainsMissingProvisioning()
    {
        var exception = Assert.Throws<InvalidDataException>(() => AzdEnvironmentImporter.Parse("AZURE_ENV_NAME=\"demo\""));
        Assert.Contains("azd up", exception.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => AzdEnvironmentImporter.Parse("""{"AZURE_FOUNDRY_ENDPOINT":"https://example.com/"}"""));
    }

    [Fact]
    public async Task ConfigureAzdCommandWritesSettingsFromSavedOutput()
    {
        var file = Path.Combine(_root, "azd.json");
        File.WriteAllText(file, AzdJson);
        var settingsPath = Path.Combine(_root, "settings", ProviderSettingsStore.FileName);
        var manifest = Path.Combine(TestPaths.RepoRoot, "assets", ModelManifest.FileName);
        var output = new StringWriter();

        var code = await ConfigureCommands.RunAsync(
            ["configure", "azd", "--from-file", file, "--settings", settingsPath, "--manifest", manifest, "--tier", "S"], output);

        Assert.Equal(0, code);
        Assert.Contains("example-account.services.ai.azure.com", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("no keys were written", output.ToString(), StringComparison.Ordinal);
        var settings = new ProviderSettingsStore(settingsPath).Load();
        Assert.Equal(Manifest.GetTier(HardwareTier.S).Fast, settings.FindTier("fast")!.Model);
        Assert.Equal("gpt-chat", settings.FindTier("cloud")!.Model);

        var show = new StringWriter();
        Assert.Equal(0, await ConfigureCommands.RunAsync(["configure", "show", "--settings", settingsPath], show));
        Assert.Contains("\"allowCloud\": true", show.ToString(), StringComparison.Ordinal);
        Assert.Equal(2, await ConfigureCommands.RunAsync(["configure", "nope"], new StringWriter()));
        Assert.True(ConfigureCommands.Handles(["assets", "list"]));
        Assert.False(ConfigureCommands.Handles(["render"]));
    }

    [Fact]
    public async Task ConfigureCopilotCommandSetsTierRuntimeAndByokWithoutChangingPrivacy()
    {
        var settingsPath = Path.Combine(_root, "copilot", ProviderSettingsStore.FileName);
        var store = new ProviderSettingsStore(settingsPath);
        var azure = new ConnectionSettings { Id = "azure", Kind = ConnectionKind.Foundry, Endpoint = "https://example-account.services.ai.azure.com/", Auth = AuthMode.Entra };
        store.Save(ProviderSettingsDefaults.WithAzure(ProviderSettingsDefaults.Local(HardwareTier.S, Manifest), azure, allowCloud: false));

        var refused = new StringWriter();
        Assert.Equal(1, await ConfigureCommands.RunAsync(["configure", "copilot", "--model", "model-x", "--runtime", "--settings", settingsPath], refused));
        Assert.Contains("allowCloud", refused.ToString(), StringComparison.Ordinal);
        Assert.False(store.Load().Privacy.AllowCloud);

        var output = new StringWriter();
        Assert.Equal(0, await ConfigureCommands.RunAsync(
            ["configure", "copilot", "--model", "model-x", "--tier", "--runtime", "--allow-cloud", "--settings", settingsPath], output));
        Assert.Contains("premium requests", output.ToString(), StringComparison.Ordinal);
        var settings = store.Load();
        Assert.True(settings.Privacy.AllowCloud);
        Assert.Equal(["fast", "balanced", "cloud", "copilot", "strong"], settings.Chat.Tiers.Select(t => t.Name));
        Assert.Equal(ConnectionKind.GitHubCopilot, settings.FindConnection(settings.FindTier("copilot")!.Connection)!.Kind);
        Assert.Equal(AgentRuntime.Copilot, settings.Agents.Authoring.Runtime);
        Assert.Equal(AgentRuntime.Maf, settings.Agents.Illustration.Runtime);
        Assert.True(ProviderSettingsDefaults.UsesCopilot(settings));

        Assert.Equal(0, await ConfigureCommands.RunAsync(["configure", "copilot", "--runtime", "--byok", "local", "--settings", settingsPath], new StringWriter()));
        settings = store.Load();
        Assert.Equal(CopilotBackend.Byok, settings.Agents.Copilot.Backend);
        Assert.Equal("local", settings.Agents.Copilot.ByokConnection);
        Assert.Equal("model-x", settings.Agents.Copilot.Model);
        Assert.Null(settings.FindTier("copilot"));
        Assert.DoesNotContain(settings.Connections, c => c.Kind == ConnectionKind.GitHubCopilot);

        Assert.Equal(1, await ConfigureCommands.RunAsync(["configure", "copilot", "--byok", "nope", "--settings", settingsPath], new StringWriter()));
        Assert.Equal(0, await ConfigureCommands.RunAsync(["configure", "copilot", "--off", "--settings", settingsPath], new StringWriter()));
        settings = store.Load();
        Assert.False(ProviderSettingsDefaults.UsesCopilot(settings));
        Assert.Equal(new CopilotAgentSettings(), settings.Agents.Copilot);
        Assert.Throws<ArgumentException>(() => ProviderSettingsDefaults.WithCopilot(settings, tier: true));
    }

    [Fact]
    public void ManifestPinsEveryAssetToAnImmutableRevision()
    {
        var manifest = Manifest;
        foreach (var asset in manifest.Assets)
            foreach (var file in asset.Files)
            {
                Assert.Matches("^[0-9a-f]{64}$", file.Sha256);
                Assert.True(file.Size > 0, $"{asset.Id}/{file.Path} needs a size");
                Assert.StartsWith("https://", file.Url, StringComparison.Ordinal);
                Assert.DoesNotContain("/resolve/main/", file.Url, StringComparison.Ordinal);
                Assert.Contains(asset.Revision, file.Url, StringComparison.Ordinal);
            }
        foreach (var tier in Enum.GetValues<HardwareTier>())
            Assert.Contains(manifest.GetTier(tier).WhisperAsset, manifest.Assets.Select(a => a.Id));
        Assert.Equal("en-US-Harper:MAI-Voice-2.1-Flash", manifest.DefaultCloudVoice?.Voice);
    }

    [Theory]
    [InlineData(4L, null, HardwareTier.S)]
    [InlineData(16L, null, HardwareTier.M)]
    [InlineData(8L, 8L, HardwareTier.M)]
    [InlineData(32L, 12L, HardwareTier.L)]
    [InlineData(64L, 24L, HardwareTier.L)]
    public void TierSelectorMapsHardware(long ramGb, long? vramGb, HardwareTier expected)
    {
        const long gb = 1024L * 1024 * 1024;
        Assert.Equal(expected, TierSelector.Select(new HardwareProfile(ramGb * gb - gb / 4, vramGb * gb)));
        if (vramGb >= 24) Assert.Equal(HardwareTier.XL, TierSelector.Select(new HardwareProfile(ramGb * gb, vramGb * gb), allowXl: true));
    }

    [Fact]
    public async Task DetectorListsModelsAndDegradesWhenRuntimeIsAbsent()
    {
        static HttpResponseMessage Respond(HttpRequestMessage request) =>
            request.RequestUri!.Port == 11434
                ? Json("""{"object":"list","data":[{"id":"qwen3.5:4b"},{"id":"embeddinggemma:latest"}]}""")
                : throw new HttpRequestException("connection refused");
        var handler = new StubHandler(Respond);
        var detector = new LocalRuntimeDetector(new HttpClient(handler));

        var ollama = await detector.ProbeAsync("Ollama", new Uri("http://localhost:11434/v1"));
        Assert.True(ollama.Available);
        Assert.Equal("http://localhost:11434/v1/models", handler.Requests.Single().RequestUri!.AbsoluteUri);
        Assert.True(ollama.HasModel("embeddinggemma"));
        Assert.True(ollama.HasModel("qwen3.5:4b"));
        Assert.False(ollama.HasModel("qwen3.5:9b"));

        var absent = await detector.ProbeAsync("Llama.app", LocalRuntimeDetector.LlamaAppEndpoint);
        Assert.False(absent.Available);
        Assert.Equal("not running", absent.Detail);

        var report = new LocalRuntimeReport(ollama, null, false, null, new HardwareProfile(0, null), HardwareTier.M);
        Assert.Equal(["qwen3.5:9b"], report.MissingOllamaTags(Manifest.GetTier(HardwareTier.M)).Distinct());
    }

    [Fact]
    public async Task OllamaPullerStreamsProgressAndSurfacesErrors()
    {
        var handler = new StubHandler(_ => Json(
            "{\"status\":\"pulling manifest\"}\n{\"status\":\"pulling abc\",\"completed\":5,\"total\":10}\n{\"status\":\"success\"}\n"));
        var progress = new List<OllamaPullProgress>();
        await new OllamaPuller(new HttpClient(handler)).PullAsync("qwen3.5:4b", new SynchronousProgress<OllamaPullProgress>(progress.Add));
        Assert.Equal(HttpMethod.Post, handler.Requests.Single().Method);
        Assert.Equal("http://localhost:11434/api/pull", handler.Requests.Single().RequestUri!.AbsoluteUri);
        Assert.Equal(["pulling manifest", "pulling abc", "success"], progress.Select(p => p.Status));
        Assert.Equal(0.5, progress[1].Fraction);

        var failing = new OllamaPuller(new HttpClient(new StubHandler(_ => Json("{\"error\":\"model not found\"}"))));
        Assert.Contains("model not found", (await Assert.ThrowsAsync<InvalidOperationException>(() => failing.PullAsync("nope"))).Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ArgumentException>(() => failing.PullAsync("x", api: new Uri("https://example.com/")));
    }

    [Fact]
    public async Task ModelAssetsDownloadVerifyAndRenameAtomically()
    {
        var content = Encoding.UTF8.GetBytes("tiny fake model asset for hashing");
        var (assets, handler) = CreateAssets(content, Convert.ToHexStringLower(SHA256.HashData(content)));
        Assert.Null(assets.GetPath("tiny"));
        Assert.Equal(ModelAssetState.Missing, assets.List().Single().State);

        var fractions = new List<double>();
        var directory = await assets.EnsureAsync("tiny", new SynchronousProgress<double>(fractions.Add));

        Assert.Equal(content, File.ReadAllBytes(Path.Combine(directory, "sub", "model.bin")));
        Assert.False(File.Exists(Path.Combine(directory, "sub", "model.bin.partial")));
        Assert.Equal(1.0, fractions[^1]);
        Assert.Equal(directory, assets.GetPath("tiny"));
        Assert.Equal(ModelAssetState.Verified, (await assets.VerifyAsync("tiny")).State);
        Assert.Single(handler.Requests);
        await assets.EnsureAsync("tiny");
        Assert.Single(handler.Requests);

        File.WriteAllBytes(Path.Combine(directory, "sub", "model.bin"), new byte[content.Length]);
        Assert.Equal(ModelAssetState.Corrupt, (await assets.VerifyAsync("tiny")).State);
        Assert.Null(assets.GetPath("tiny"));
    }

    [Fact]
    public async Task ModelAssetsDeleteHashMismatchAndThrow()
    {
        var content = Encoding.UTF8.GetBytes("tampered bytes");
        var (assets, _) = CreateAssets(content, new string('0', 64));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => assets.EnsureAsync("tiny"));

        Assert.Contains("SHA-256", exception.Message, StringComparison.Ordinal);
        var file = Path.Combine(assets.GetDirectory("tiny"), "sub", "model.bin");
        Assert.False(File.Exists(file));
        Assert.False(File.Exists(file + ".partial"));
        Assert.Null(assets.GetPath("tiny"));
        Assert.Contains("assets ensure --id tiny", Assert.Throws<InvalidOperationException>(() => assets.RequirePath("tiny")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ModelAssetsResumeFromPartialWithRange()
    {
        var content = Encoding.UTF8.GetBytes("0123456789abcdefghijklmnopqrstuvwxyz");
        var (assets, handler) = CreateAssets(content, Convert.ToHexStringLower(SHA256.HashData(content)));
        var file = Path.Combine(assets.GetDirectory("tiny"), "sub", "model.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file + ".partial", content[..10]);

        await assets.EnsureAsync("tiny");

        var request = handler.Requests.Single();
        Assert.Equal(10, request.Headers.Range!.Ranges.Single().From);
        Assert.Equal(content, File.ReadAllBytes(file));
        Assert.False(File.Exists(file + ".partial"));
    }

    [Fact]
    public void ModelOverridesReplaceOnlyTheNamedLocalModels()
    {
        var settings = ConfigureCommands.WithModelOverrides(LocalDefaults, "gemma4:e4b", null, "gemma4:12b");
        Assert.Equal(["gemma4:e4b", LocalDefaults.Chat.Tiers[1].Model], settings.Chat.Tiers.Select(t => t.Model));
        Assert.Equal("gemma4:12b", settings.Vision!.Model);
        Assert.Equal(LocalDefaults.Embeddings, settings.Embeddings);
        Assert.Empty(ProviderSettingsValidator.Validate(settings));
    }

    [Fact]
    public async Task DiagnosticsSummarizeSettingsWithoutSecrets()
    {
        const string secret = "sk-diagnostics-test-value-0123456789";
        var connection = Azure with { Id = "diag-key", Auth = AuthMode.ApiKey, Endpoint = "https://private-account-name.openai.azure.com/" };
        var store = new ProviderSettingsStore(Path.Combine(_root, "settings", ProviderSettingsStore.FileName));
        store.Save(ProviderSettingsDefaults.WithAzure(LocalDefaults, connection, new Dictionary<string, string> { ["chat"] = "gpt-chat" }));
        var variable = CredentialFactory.ApiKeyEnvironmentVariable(connection.Id);
        Environment.SetEnvironmentVariable(variable, secret);
        try
        {
            var detector = new LocalRuntimeDetector(new HttpClient(new StubHandler(_ => throw new HttpRequestException("connection refused"))));
            var output = new StringWriter();
            var exit = await Diagnostics.WriteAsync(output, [new("WebView2", "1.2.3")], store, new ModelAssets(Manifest, Path.Combine(_root, "models")),
                detector, (_, _) => null);
            var text = output.ToString();
            Assert.Equal(0, exit);
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
            Assert.DoesNotContain("private-account-name", text, StringComparison.Ordinal);
            Assert.Contains("api key: set", text, StringComparison.Ordinal);
            Assert.Contains("https://***.azure.com/", text, StringComparison.Ordinal);
            Assert.Contains("WebView2", text, StringComparison.Ordinal);
            Assert.Contains("allowCloud       true", text, StringComparison.Ordinal);
            Assert.Contains("not running", text, StringComparison.Ordinal);
            Assert.Contains("kokoro-v1.0-q8", text, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public void BundledInterFontShipsWithItsOflLicense()
    {
        VideoProduction.BundledFont.RequireFontFile();
        Assert.Contains("SIL OPEN FONT LICENSE Version 1.1", File.ReadAllText(VideoProduction.BundledFont.LicenseFile), StringComparison.Ordinal);
    }

    private (ModelAssets Assets, StubHandler Handler) CreateAssets(byte[] content, string sha256)
    {
        var manifest = new ModelManifest {
            Assets =
            [
                new ModelAsset
                {
                    Id = "tiny", Revision = "abc123", Dir = "tiny/abc123",
                    Files = [new ModelAssetFile { Path = "sub/model.bin", Url = "https://assets.example/abc123/model.bin", Size = content.Length, Sha256 = sha256 }]
                }
            ]
        };
        var handler = new StubHandler(request =>
        {
            if (request.Headers.Range?.Ranges.Single().From is { } from)
                return new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(content[(int)from..]) };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };
        });
        return (new ModelAssets(manifest, Path.Combine(_root, "models"), new HttpClient(handler)), handler);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }
}

public sealed class LocalRuntimeIntegrationTests
{
    [Fact, Trait("Category", "LocalRuntime")]
    public async Task DetectsTheLocallyRunningOllama()
    {
        var probe = await new LocalRuntimeDetector().ProbeAsync("Ollama", LocalRuntimeDetector.OllamaEndpoint);
        Assert.True(probe.Available, "Start Ollama (http://localhost:11434) to run LocalRuntime tests.");
        Assert.NotEmpty(probe.Models);
    }
}
