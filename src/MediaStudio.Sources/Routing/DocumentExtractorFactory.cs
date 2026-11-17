using Azure;
using Azure.AI.ContentUnderstanding;
using Azure.AI.DocumentIntelligence;
using Azure.Core;
using Azure.Core.Pipeline;
using CommunityToolkit.DocumentExtraction.AzureContentUnderstanding;
using CommunityToolkit.DocumentExtraction.AzureDocumentIntelligence;
using CommunityToolkit.DocumentExtraction.Mistral;
using CommunityToolkit.DocumentExtraction.PdfPig;
using CommunityToolkit.DocumentExtraction.VisionLanguageModel;
using MediaStudio.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.Logging;
using VideoProduction.Decisions.Experimental;

namespace MediaStudio.Sources;

/// <summary>Overrides for <see cref="DocumentExtractorFactory"/>; tests supply fake transports and credentials.</summary>
public sealed class DocumentExtractorFactoryOptions
{
    /// <summary>HTTP handler for every cloud extractor (tests use a fake handler; nothing leaves the machine).</summary>
    public HttpMessageHandler? Handler { get; init; }
    /// <summary>Credential used instead of <see cref="CredentialFactory.CreateTokenCredential"/> for Entra connections.</summary>
    public TokenCredential? Credential { get; init; }
    /// <summary>API key lookup used instead of <see cref="CredentialFactory.GetApiKey"/> for api-key connections.</summary>
    public Func<string, string>? ApiKey { get; init; }
    /// <summary>Content Understanding analyzer; <c>prebuilt-documentSearch</c> by default (plan §3.1).</summary>
    public string ContentUnderstandingAnalyzer { get; init; } = DocumentExtractorFactory.DefaultAnalyzer;
}

/// <summary>Builds the document extractors named in <see cref="DocumentSettings.Candidates"/> from provider settings.</summary>
public static class DocumentExtractorFactory
{
    public const string DefaultAnalyzer = "prebuilt-documentSearch";
    public const string DocumentIntelligenceModel = "prebuilt-layout";
    /// <summary>Mistral OCR accepts at most 30 pages / 30 MB per request; larger documents are not chunked in v1.</summary>
    public const int MistralMaximumPages = 30;
    public const long MistralMaximumBytes = 30L * 1024 * 1024;

    public static string DisplayName(string candidate) => candidate switch {
        DocumentCandidates.PdfPig => "Local text (PdfPig)",
        DocumentCandidates.Vlm => "Local vision model",
        DocumentCandidates.DocumentIntelligence => "Azure Document Intelligence",
        DocumentCandidates.ContentUnderstanding => "Azure Content Understanding",
        DocumentCandidates.Mistral => "Mistral OCR (Azure)",
        _ => candidate
    };

    /// <summary>
    /// Candidates that can be built from <paramref name="settings"/>: <c>pdfpig</c> always, <c>vlm</c> when a vision model is set,
    /// cloud candidates when listed in Settings → Documents and a Foundry connection exists.
    /// </summary>
    public static string[] Available(ProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return DocumentCandidates.All.Where(n => n == DocumentCandidates.PdfPig || settings.Documents.Candidates.Contains(n, StringComparer.Ordinal)
            && (n == DocumentCandidates.Vlm ? settings.Vision is not null : FoundryConnection(settings) is not null)).ToArray();
    }

    /// <summary>The Foundry connection used by cloud extractors: the first <see cref="ConnectionKind.Foundry"/> connection.</summary>
    public static ConnectionSettings? FoundryConnection(ProviderSettings settings) =>
        settings.Connections.FirstOrDefault(c => c.Kind == ConnectionKind.Foundry && !string.IsNullOrWhiteSpace(c.Endpoint));

    /// <summary>
    /// Candidates for the router, in settings order. <c>pdfpig</c> is always present; <c>vlm</c> needs a vision client;
    /// cloud candidates need a Foundry connection and are wrapped in <see cref="ConsentGatedDocumentExtractionClient"/>.
    /// Clients are created lazily, so no credential is touched until a document is routed to them.
    /// </summary>
    public static IReadOnlyList<ExtractorCandidate> CreateCandidates(ProviderSettings settings, IChatClient? vision,
        IUploadConsent consent, DocumentExtractorFactoryOptions? options = null, bool visionIsRemote = false)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(consent);
        var names = settings.Documents.Candidates.Contains(DocumentCandidates.PdfPig, StringComparer.Ordinal)
            ? settings.Documents.Candidates : [DocumentCandidates.PdfPig, .. settings.Documents.Candidates];
        var result = new List<ExtractorCandidate>();
        foreach (var name in names.Distinct(StringComparer.Ordinal))
            if (TryCreate(name, settings, vision, consent, options, visionIsRemote) is { } candidate) result.Add(candidate);
        return result;
    }

    /// <summary>The "Auto" extractor: candidates from settings, routed by <paramref name="decider"/>.</summary>
    public static DecisionRoutingDocumentExtractionClient CreateRouter(ProviderSettings settings, IChatClient? vision,
        IDecisionClient? decider, IUploadConsent consent, ILoggerFactory? loggerFactory = null,
        DocumentExtractorFactoryOptions? options = null, bool visionIsRemote = false) =>
        new(CreateCandidates(settings, vision, consent, options, visionIsRemote), decider, settings.Privacy.AllowCloud,
            loggerFactory?.CreateLogger<DecisionRoutingDocumentExtractionClient>());

    /// <summary>An explicit choice: bypasses the router but keeps the consent gate for cloud providers.</summary>
    public static DocumentExtractionClient CreateExplicit(string candidate, ProviderSettings settings, IChatClient? vision,
        IUploadConsent consent, DocumentExtractorFactoryOptions? options = null, bool visionIsRemote = false) =>
        (TryCreate(candidate, settings, vision, consent, options, visionIsRemote)
            ?? throw new InvalidOperationException($"The document extractor '{candidate}' is not configured.")).Client;

    private static ExtractorCandidate? TryCreate(string name, ProviderSettings settings, IChatClient? vision,
        IUploadConsent consent, DocumentExtractorFactoryOptions? options, bool visionIsRemote)
    {
        options ??= new();
        var allowCloud = settings.Privacy.AllowCloud;
        DocumentExtractionClient Gate(DocumentExtractionClient client) =>
            new ConsentGatedDocumentExtractionClient(client, DisplayName(name), consent, allowCloud);
        switch (name)
        {
            case DocumentCandidates.PdfPig:
                return new(name, "Reads the PDF's embedded text layer locally. Fast and exact for digital PDFs; returns nothing for scanned pages.",
                    false, new PdfPigDocumentExtractionClient(), SupportsImages: false);
            case DocumentCandidates.Vlm when vision is not null:
                DocumentExtractionClient reader = new PdfPageImagesDocumentExtractionClient(
                    new VisionLanguageModelDocumentExtractionClient(vision, new()));
                return new(name, "Local vision-language model that transcribes scanned pages and images. Slower; no upload.",
                    visionIsRemote, visionIsRemote ? Gate(reader) : reader);
        }
        if (!DocumentCandidates.IsCloud(name) || FoundryConnection(settings) is not { } connection) return null;
        var endpoints = FoundryEndpoints.From(connection);
        return name switch {
            DocumentCandidates.DocumentIntelligence => new(name,
                "Azure Document Intelligence prebuilt-layout: OCR with tables, forms and reading order. Cloud upload.", true,
                Gate(new LazyDocumentExtractionClient(() => DocumentIntelligence(endpoints.DocumentIntelligence, connection, options)))),
            DocumentCandidates.ContentUnderstanding => new(name,
                "Azure Content Understanding: layout-aware OCR for long or mixed documents. Cloud upload.", true,
                Gate(new LazyDocumentExtractionClient(() => ContentUnderstanding(endpoints.ContentUnderstanding, connection, options)))),
            DocumentCandidates.Mistral => new(name,
                "Mistral OCR on Azure: fast OCR to Markdown for scanned pages and images up to 30 pages / 30 MB. Cloud upload.", true,
                Gate(new LazyDocumentExtractionClient(() => Mistral(endpoints.MistralOcr, connection, settings.Documents.MistralDeployment, options))),
                MaxPages: MistralMaximumPages, MaxBytes: MistralMaximumBytes),
            _ => null
        };
    }

    private static T Transport<T>(T clientOptions, DocumentExtractorFactoryOptions options) where T : ClientOptions
    {
        if (options.Handler is { } handler) clientOptions.Transport = new HttpClientTransport(new HttpClient(handler, disposeHandler: false));
        return clientOptions;
    }

    private static TokenCredential Token(ConnectionSettings connection, DocumentExtractorFactoryOptions options) =>
        options.Credential ?? CredentialFactory.CreateTokenCredential(connection);

    private static string Key(ConnectionSettings connection, DocumentExtractorFactoryOptions options) =>
        options.ApiKey?.Invoke(connection.Id) ?? CredentialFactory.GetApiKey(connection.Id);

    private static DocumentExtractionClient DocumentIntelligence(Uri endpoint, ConnectionSettings connection, DocumentExtractorFactoryOptions options)
    {
        var clientOptions = Transport(new DocumentIntelligenceClientOptions(), options);
        var client = connection.Auth == AuthMode.ApiKey
            ? new DocumentIntelligenceClient(endpoint, new AzureKeyCredential(Key(connection, options)), clientOptions)
            : new DocumentIntelligenceClient(endpoint, Token(connection, options), clientOptions);
        return new DocumentIntelligenceExtractionClient(client, new() { DefaultModel = DocumentIntelligenceModel });
    }

    private static DocumentExtractionClient ContentUnderstanding(Uri endpoint, ConnectionSettings connection, DocumentExtractorFactoryOptions options)
    {
        var clientOptions = Transport(new ContentUnderstandingClientOptions(), options);
        var client = connection.Auth == AuthMode.ApiKey
            ? new ContentUnderstandingClient(endpoint, new AzureKeyCredential(Key(connection, options)), clientOptions)
            : new ContentUnderstandingClient(endpoint, Token(connection, options), clientOptions);
        return new ContentUnderstandingExtractionClient(client, new() { AnalyzerId = options.ContentUnderstandingAnalyzer });
    }

    private static DocumentExtractionClient Mistral(Uri endpoint, ConnectionSettings connection, string deployment,
        DocumentExtractorFactoryOptions options)
    {
        var http = options.Handler is { } handler ? new HttpClient(handler, disposeHandler: false) : null;
        var mistral = new MistralDocumentExtractionClientOptions { OcrEndpoint = endpoint, DefaultModel = deployment };
        return connection.Auth == AuthMode.ApiKey
            ? new MistralDocumentExtractionClient(mistral, Key(connection, options), http)
            : new MistralDocumentExtractionClient(mistral, Token(connection, options), http);
    }
}

/// <summary>Service keys registered by <see cref="DocumentServiceCollectionExtensions.AddMediaStudioDocuments"/>.</summary>
public static class MediaStudioDocumentKeys
{
    /// <summary>The "Auto" routing extractor.</summary>
    public const string Documents = "documents";
    /// <summary>Prefix of the per-candidate keys, e.g. <c>documents:pdfpig</c>.</summary>
    public const string CandidatePrefix = "documents:";
    /// <summary>Same string as <c>MediaStudioAIKeys.Vision</c>; Sources does not reference Providers.</summary>
    public const string Vision = "vision";
    /// <summary>Same string as <c>MediaStudioAIKeys.Decider</c>.</summary>
    public const string Decider = "routing";
}

public static class DocumentServiceCollectionExtensions
{
    /// <summary>
    /// Registers keyed <see cref="DocumentExtractionClient"/>s: <c>documents</c> (the routing extractor) and
    /// <c>documents:&lt;candidate&gt;</c> for each configured candidate (explicit choices; cloud ones consent-gated).
    /// Uses the keyed "vision" chat client and "routing" decider when registered (call after <c>AddMediaStudioAI</c>) and an
    /// <see cref="IUploadConsent"/> service when registered; otherwise uploads are denied.
    /// </summary>
    public static IServiceCollection AddMediaStudioDocuments(this IServiceCollection services, ProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(settings);
        var visionIsRemote = settings.Vision is { } vision && settings.IsCloud(vision.Connection);
        services.AddKeyedDocumentExtractionClient(MediaStudioDocumentKeys.Documents, sp => DocumentExtractorFactory.CreateRouter(settings,
            sp.GetKeyedService<IChatClient>(MediaStudioDocumentKeys.Vision), sp.GetKeyedService<IDecisionClient>(MediaStudioDocumentKeys.Decider),
            sp.GetService<IUploadConsent>() ?? FixedUploadConsent.Deny, sp.GetService<ILoggerFactory>(), visionIsRemote: visionIsRemote),
            ServiceLifetime.Singleton);
        foreach (var name in DocumentExtractorFactory.Available(settings))
            services.AddKeyedDocumentExtractionClient(MediaStudioDocumentKeys.CandidatePrefix + name, sp => DocumentExtractorFactory.CreateExplicit(
                name, settings, sp.GetKeyedService<IChatClient>(MediaStudioDocumentKeys.Vision),
                sp.GetService<IUploadConsent>() ?? FixedUploadConsent.Deny, visionIsRemote: visionIsRemote), ServiceLifetime.Singleton);
        return services;
    }
}
