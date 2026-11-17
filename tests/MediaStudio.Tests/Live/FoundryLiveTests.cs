using MediaStudio.Configuration;
using MediaStudio.Sources;
using MediaStudio.Speech;
using MediaStudio.Tests.Documents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Documents;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Writer;
using VideoProduction.Providers;
using VideoProduction.Providers.Decisions;

namespace MediaStudio.Tests.Live;

/// <summary>
/// Settings for the Live suite: the per-user settings file written by <c>configure azd</c> (or <c>MEDIASTUDIO_HOME</c>),
/// with <c>privacy.allowCloud</c> on and a Foundry connection. Authentication is whatever that connection uses
/// (Entra via <c>DefaultAzureCredential</c>, or an API key from <c>MEDIASTUDIO__CONNECTIONS__AZURE__APIKEY</c>).
/// </summary>
internal static class LiveSettings
{
    private static readonly Lazy<(ProviderSettings? Settings, string? Reason)> Loaded = new(Load);

    public static ProviderSettings Settings => Loaded.Value.Settings ?? throw new InvalidOperationException(Loaded.Value.Reason);
    public static string? SkipReason => Loaded.Value.Reason;
    public static ConnectionSettings Foundry => DocumentExtractorFactory.FoundryConnection(Settings)!;

    /// <summary>Deployment name for a role; <c>infra/models.json</c> names deployments after their role.</summary>
    public static string Deployment(string role) =>
        Environment.GetEnvironmentVariable($"MEDIASTUDIO_LIVE_{role.ToUpperInvariant()}_DEPLOYMENT") is { Length: > 0 } name ? name : role;

    private static (ProviderSettings?, string?) Load()
    {
        // Read-only on purpose: attributes run during test discovery, so never let the store migrate the user's file.
        var path = MediaStudioPaths.SettingsFile;
        if (!File.Exists(path)) return (null, "No Media Studio settings. Run `azd up`, then `video-production configure azd`.");
        ProviderSettings settings;
        try
        {
            settings = ProviderSettingsJson.Deserialize(File.ReadAllText(path));
            ProviderSettingsValidator.ThrowIfInvalid(settings);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            return (null, $"Settings could not be read ({ex.GetType().Name}). Run `video-production configure test`.");
        }
        if (settings.SchemaVersion != ProviderSettings.CurrentSchemaVersion) return (null, "Settings are not schema v2. Run `video-production configure test` once to migrate them.");
        if (!settings.Privacy.AllowCloud) return (null, "privacy.allowCloud is off. Run `video-production configure azd`.");
        if (DocumentExtractorFactory.FoundryConnection(settings) is null) return (null, "No Foundry connection. Run `video-production configure azd`.");
        return (settings, null);
    }
}

/// <summary>Skips unless <see cref="LiveSettings"/> found a cloud-enabled Foundry configuration.</summary>
public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (LiveSettings.SkipReason is { } reason) Skip = reason;
    }
}

/// <summary>
/// Calls the azd-provisioned Foundry resource. Run by <c>.github/workflows/live-smoke.yml</c> after <c>configure azd</c>,
/// or locally with <c>dotnet test tests/MediaStudio.Tests --filter Category=Live</c>. Excluded from the default run.
/// </summary>
[Trait("Category", "Live")]
public sealed class FoundryLiveTests
{
    private static CancellationToken Timeout(int minutes = 3) => new CancellationTokenSource(TimeSpan.FromMinutes(minutes)).Token;

    [LiveFact]
    public async Task EveryFoundryChatTierStreamsAnAnswer()
    {
        var settings = LiveSettings.Settings;
        var tiers = settings.Chat.Tiers.Where(t => t.Connection == LiveSettings.Foundry.Id).ToArray();
        Assert.NotEmpty(tiers);

        foreach (var tier in tiers)
        {
            using var client = ProviderFactory.CreateTierClient(settings, tier);
            // Streaming is the path the OpenAI 2.15 break affects (see docs/troubleshooting.md#openai-sdk-version).
            var response = await client.GetStreamingResponseAsync("Reply with the single word: ready.",
                new ChatOptions { MaxOutputTokens = 64 }, Timeout()).ToChatResponseAsync(Timeout());

            Assert.False(string.IsNullOrWhiteSpace(response.Text), $"Tier '{tier.Name}' ({tier.Model}) returned no text.");
        }
    }

    [LiveFact]
    public async Task CloudOnlyRouterReturnsACloudReceipt()
    {
        var settings = LiveSettings.Settings;
        var cloud = settings with {
            Chat = settings.Chat with { Tiers = [.. settings.Chat.Tiers.Where(t => t.Connection == LiveSettings.Foundry.Id)] },
            Decisions = settings.Decisions with { Julia = JuliaMode.Off }
        };
        var router = ProviderFactory.CreateRouter(cloud, MediaStudioAIKeys.Chat, () => DecisionClientFactory.Create(cloud, null));

        var response = await router.GetResponseAsync("Say hi in one short sentence.",
            new ChatOptions { MaxOutputTokens = 128 }, Timeout());

        Assert.False(string.IsNullOrWhiteSpace(response.Text));
        var receipt = Assert.IsType<ChatRouteReceipt>(response.AdditionalProperties![DecisionRoutingChatClient.ReceiptKey]);
        Assert.True(receipt.IsCloud, receipt.Badge());
    }

    [LiveFact]
    public async Task FoundryEmbeddingsHaveAStableDimension()
    {
        var settings = LiveSettings.Settings;
        var role = settings.Embeddings is { } configured && configured.Connection == LiveSettings.Foundry.Id
            ? configured
            : new RoleModel { Connection = LiveSettings.Foundry.Id, Model = LiveSettings.Deployment("embedding") };
        using var generator = EmbeddingFactory.Create(settings, role) ?? throw new InvalidOperationException("No embedding generator.");

        var embeddings = await generator.GenerateAsync(["a cat on a mat", "a kitten on a rug"], cancellationToken: Timeout());

        Assert.Equal(2, embeddings.Count);
        Assert.True(embeddings[0].Vector.Length > 0);
        Assert.Equal(embeddings[0].Vector.Length, embeddings[1].Vector.Length);
    }

    [LiveFact]
    public async Task AzureSpeechNarrationRoundTripsThroughCloudTranscription()
    {
        var settings = ProviderSettingsDefaults.WithCloudSpeech(LiveSettings.Settings, true);
        var factory = new SpeechClientFactory(settings, new ModelAssets(ModelManifest.LoadDefault(),
            Path.Combine(Path.GetTempPath(), "no-models-" + Guid.NewGuid().ToString("N"))));

        using var tts = factory.CreateTextToSpeech();
        var audio = Assert.IsType<DataContent>(Assert.Single((await tts.GetAudioAsync("Hello world. This is Media Studio.",
            cancellationToken: Timeout())).Contents));
        Assert.True(audio.Data.Length > 1000, $"Narration returned {audio.Data.Length} bytes.");

        using var stt = factory.CreateSpeechToText();
        var text = await stt.GetTextAsync(new MemoryStream(audio.Data.ToArray()),
            new SpeechToTextOptions { SpeechLanguage = "en" }, Timeout(5));

        Assert.Contains("hello", text.Text, StringComparison.OrdinalIgnoreCase);
    }

    [LiveFact]
    public async Task DocumentIntelligenceReadsAScannedPdfOnlyWithConsent()
    {
        var settings = LiveSettings.Settings;
        Assert.Contains(DocumentCandidates.DocumentIntelligence, settings.Documents.Candidates);
        var builder = new PdfDocumentBuilder();
        builder.AddPage(PageSize.A4).AddPng(LocalVisionDocumentTests.TextPng("HELLO WORLD"), new PdfRectangle(40, 500, 540, 600));
        var pdf = builder.Build();

        var denied = DocumentExtractorFactory.CreateExplicit(DocumentCandidates.DocumentIntelligence, settings, null, FixedUploadConsent.Deny);
        await Assert.ThrowsAsync<UploadConsentDeniedException>(() =>
            denied.ExtractAsync(new MemoryStream(pdf), "scan", "application/pdf", cancellationToken: Timeout()));

        var allowed = DocumentExtractorFactory.CreateExplicit(DocumentCandidates.DocumentIntelligence, settings, null, FixedUploadConsent.Allow);
        var document = await allowed.ExtractAsync(new MemoryStream(pdf), "scan", "application/pdf", cancellationToken: Timeout(5));

        var text = string.Join('\n', document.Sections.SelectMany(s => s.Elements)
            .OfType<ExtractionDocumentTextElement>().Select(e => (e.Content as TextContent)?.Text));
        Assert.Contains("HELLO", text, StringComparison.OrdinalIgnoreCase);
    }
}
