using System.IO;
using Azure.Identity;
using CommunityToolkit.DocumentExtraction.Mistral;
using MediaStudio.Sources;
using Microsoft.Extensions.AI;
using VideoProduction;
using VideoProduction.Providers;

namespace MediaStudio.App;

public sealed record NativeMistralSettings(string Endpoint, string Model, string Version, string Authentication = "azure-cli",
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] bool IncludeImages = false);
public sealed record MediaSourceSettings(int SchemaVersion, PlannerConfiguration? Chat, OcrCapabilities? Capabilities,
    NativeMistralSettings? NativeMistral = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] bool LocalPdf = false,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] string? Extractor = null)
{
    public static MediaSourceSettings Local() => new(1, null, null, LocalPdf: true);
    /// <summary>Routes each PDF or image to an extractor chosen from cheap local features (Settings → Documents); cloud uploads ask first.</summary>
    public static MediaSourceSettings Automatic() => new(1, null, null, Extractor: AutoExtractor);
    /// <summary>An explicit extractor from Settings → Documents (keyed <c>documents:&lt;name&gt;</c>); cloud ones still ask before uploading.</summary>
    public static MediaSourceSettings Explicit(string candidate) => new(1, null, null, Extractor: candidate);
    public const string AutoExtractor = "auto";
    [System.Text.Json.Serialization.JsonIgnore] public bool Auto => Extractor == AutoExtractor;
    /// <summary>Placeholder used until a Foundry connection is configured (configure azd / Setup); it never resolves to a real resource.</summary>
    public const string PlaceholderMistralEndpoint = "https://example-account.services.ai.azure.com/providers/mistral/azure/ocr";
    public static MediaSourceSettings KnownMistral(bool includeImages = false, string? endpoint = null)
    {
        endpoint ??= ConfiguredMistralEndpoint() ?? PlaceholderMistralEndpoint;
        return new(1, null,
            new(endpoint, "mistral-ocr-4-0", true, true, DocumentSourceStore.MaximumBytes, DocumentSourceStore.MaximumPages, ModelVersion: "1"),
            new(endpoint, "mistral-ocr-4-0", "1", IncludeImages: includeImages));
    }

    /// <summary>Mistral OCR operation URI derived from the first Foundry connection in settings v2, if any.</summary>
    public static string? ConfiguredMistralEndpoint(MediaStudio.Configuration.ProviderSettings? settings = null)
    {
        try
        {
            settings ??= new MediaStudio.Configuration.ProviderSettingsStore().TryLoad();
        }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException
                                          or System.Text.Json.JsonException or MediaStudio.Configuration.ProviderSettingsValidationException)
        {
            return null;
        }
        var foundry = settings?.Connections.FirstOrDefault(c => c.Kind == MediaStudio.Configuration.ConnectionKind.Foundry);
        return foundry is null ? null : MediaStudio.Configuration.FoundryEndpoints.From(foundry).MistralOcr.AbsoluteUri;
    }

    public void Validate()
    {
        if (LocalPdf || Extractor is not null)
        {
            Guard.Require(SchemaVersion == 1 && !(LocalPdf && Extractor is not null) && Chat is null && NativeMistral is null && Capabilities is null
                && (Extractor is null or AutoExtractor || MediaStudio.Configuration.DocumentCandidates.All.Contains(Extractor)),
                "source.configuration", "Local PDF reading cannot also select a cloud connection.");
            return;
        }
        Guard.Require(SchemaVersion == 1 && Capabilities is not null && (Chat is null) != (NativeMistral is null),
            "source.configuration", "Choose exactly one explicit native Mistral or chat/VLM OCR connection.");
        if (NativeMistral is { } native)
            Guard.Require(native.Authentication == "azure-cli" && native.Model == "mistral-ocr-4-0"
                && native.Version == "1" && Uri.TryCreate(native.Endpoint, UriKind.Absolute, out var uri)
                && uri.Scheme == "https" && uri.Host.EndsWith(".services.ai.azure.com", StringComparison.OrdinalIgnoreCase)
                && uri.AbsolutePath == "/providers/mistral/azure/ocr" && uri.UserInfo.Length == 0
                && uri.Query.Length == 0 && uri.Fragment.Length == 0
                && native.Endpoint == Capabilities!.Endpoint && native.Model == Capabilities.Model
                && native.Version == Capabilities.ModelVersion,
                "source.configuration", "Native Mistral requires the explicit published operation URI, exact deployment/version and SDK Entra authentication.");
        else
            Guard.Require(Chat!.Endpoint == Capabilities!.Endpoint && Chat.Model == Capabilities.Model,
                "source.configuration", "OCR capability declarations must bind the exact configured endpoint and model.");
    }

#pragma warning disable CTDE0001, MEDE0001
    public (DocumentSourceStore Store, IChatClient? Chat) Open(string root)
        => Open(root, configuration => new ModelProviders.DeferredProviderChat(() => ChatVideoPlanner.CreateClient(configuration)));
    internal (DocumentSourceStore Store, IChatClient? Chat) Open(string root, Func<PlannerConfiguration, IChatClient> createChat)
    {
        Validate();
        if (LocalPdf || Extractor is not null) return (new(root), null);
        if (NativeMistral is { } native)
        {
            var client = new MistralDocumentExtractionClient(new MistralDocumentExtractionClientOptions {
                OcrEndpoint = new(native.Endpoint), DefaultModel = native.Model, IncludeImages = native.IncludeImages
            }, MediaStudio.Configuration.CredentialFactory.CreateEntra());
            try { return (new(root, ocrCapabilities: Capabilities, nativeOcr: client), null); }
            catch { client.Dispose(); throw; }
        }
        var chat = createChat(Chat!);
        try { return (new(root, ocrChat: chat, ocrCapabilities: Capabilities), chat); }
        catch { chat.Dispose(); throw; }
    }
#pragma warning restore CTDE0001, MEDE0001
}

/// <summary>A cloud upload awaiting the user's answer in the "Upload document" dialog.</summary>
public sealed record PendingUpload(string DocumentId, string ProviderName);

/// <summary>Asks the user before Auto sends a document to a cloud extractor; one request at a time.</summary>
public sealed class WorkspaceUploadConsent(Action changed) : IUploadConsent
{
    private readonly object gate = new();
    private TaskCompletionSource<bool>? answer;

    public PendingUpload? Pending { get; private set; }

    public async Task<bool> RequestAsync(string documentId, string providerName, CancellationToken cancellationToken = default)
    {
        TaskCompletionSource<bool> request;
        lock (gate)
        {
            if (answer is not null) return false;
            answer = request = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Pending = new(documentId, providerName);
        }
        changed();
        try
        {
            using var registration = cancellationToken.Register(() => request.TrySetResult(false));
            return await request.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (gate) { answer = null; Pending = null; }
            changed();
        }
    }

    public void Answer(bool allow)
    {
        lock (gate) answer?.TrySetResult(allow);
    }
}
