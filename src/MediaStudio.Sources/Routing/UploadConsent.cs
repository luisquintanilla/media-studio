using MediaStudio.Configuration;
using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.Documents;

namespace MediaStudio.Sources;

/// <summary>Asks the user whether a whole document may be uploaded to a cloud extractor.</summary>
public interface IUploadConsent
{
    /// <summary>Returns <see langword="true"/> only when the user approved uploading <paramref name="documentId"/> to <paramref name="providerName"/>.</summary>
    Task<bool> RequestAsync(string documentId, string providerName, CancellationToken cancellationToken = default);
}

/// <summary>A fixed answer: <see cref="Deny"/> for unattended/local-only use, <see cref="Allow"/> after an explicit confirmation elsewhere.</summary>
public sealed class FixedUploadConsent(bool granted) : IUploadConsent
{
    public static FixedUploadConsent Deny { get; } = new(false);
    public static FixedUploadConsent Allow { get; } = new(true);

    public Task<bool> RequestAsync(string documentId, string providerName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(granted);
    }
}

/// <summary>The user declined (or privacy settings forbid) uploading a document to a cloud extractor.</summary>
public sealed class UploadConsentDeniedException(string documentId, string providerName, string message)
    : Exception(message)
{
    public UploadConsentDeniedException(string documentId, string providerName)
        : this(documentId, providerName, $"Uploading the document to {providerName} was not approved; nothing was sent.") { }

    public string DocumentId { get; } = documentId;
    public string ProviderName { get; } = providerName;
}

/// <summary>
/// Wraps a remote extractor so nothing is uploaded unless <c>privacy.allowCloud</c> is on (<see cref="CloudGuard"/>) and
/// <see cref="IUploadConsent"/> approves this document for this provider. Every remote route (auto or explicit) passes here.
/// </summary>
public sealed class ConsentGatedDocumentExtractionClient : DelegatingDocumentExtractionClient
{
    private readonly IUploadConsent consent;
    private readonly bool allowCloud;

    public ConsentGatedDocumentExtractionClient(DocumentExtractionClient inner, string providerName, IUploadConsent consent,
        bool allowCloud) : base(inner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        ArgumentNullException.ThrowIfNull(consent);
        Inner = inner;
        ProviderName = providerName;
        this.consent = consent;
        this.allowCloud = allowCloud;
    }

    /// <summary>The display name shown in the consent prompt, e.g. "Azure Document Intelligence".</summary>
    public string ProviderName { get; }

    /// <summary>The wrapped remote extractor.</summary>
    public DocumentExtractionClient Inner { get; }

    public override async Task<ExtractionDocument> ExtractAsync(Stream document, string identifier, string mediaType,
        DocumentExtractionOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!CloudGuard.Allows(allowCloud, isCloud: true))
            throw new UploadConsentDeniedException(identifier, ProviderName,
                $"{ProviderName} is a cloud service and privacy.allowCloud is false; nothing was sent.");
        if (!await consent.RequestAsync(identifier, ProviderName, cancellationToken).ConfigureAwait(false))
            throw new UploadConsentDeniedException(identifier, ProviderName);
        return await base.ExtractAsync(document, identifier, mediaType, options, cancellationToken).ConfigureAwait(false);
    }
}
