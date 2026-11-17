using Microsoft.Extensions.AI;
using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.Documents;

namespace MediaStudio.Sources;

/// <summary>Which extractor produced a document: candidate/type name, defining assembly and package version.</summary>
public sealed record ProviderIdentity(string Name, string Assembly, string? Version)
{
    public static ProviderIdentity Of(Type type) =>
        new(type.FullName ?? type.Name, type.Assembly.GetName().Name ?? type.Assembly.FullName ?? "", SourceJson.PackageVersion(type));
}

/// <summary>Metadata keys stamped on an <see cref="ExtractionDocument"/> by the routing extractor.</summary>
public static class DocumentExtractionTags
{
    /// <summary>Candidate name that produced the document, e.g. <c>pdfpig</c>.</summary>
    public const string Extractor = "mediastudio.extractor";
    /// <summary>Full type name of the package extractor.</summary>
    public const string Provider = "mediastudio.provider";
    public const string Assembly = "mediastudio.provider.assembly";
    public const string Version = "mediastudio.provider.version";
    /// <summary>Set when every candidate fell below the quality bar and the best result was kept anyway.</summary>
    public const string LowQuality = "mediastudio.low-quality";
    /// <summary>Candidates attempted in order, with their outcome.</summary>
    public const string Attempts = "mediastudio.attempts";

    /// <summary>Removes MediaStudio wrappers so the package extractor type is reported.</summary>
    public static DocumentExtractionClient Unwrap(DocumentExtractionClient client)
    {
        for (var depth = 0; depth < 8; depth++)
        {
            switch (client)
            {
                case ConsentGatedDocumentExtractionClient gated: client = gated.Inner; continue;
                case LazyDocumentExtractionClient lazy when lazy.IsCreated: client = lazy.Value; continue;
            }
            break;
        }
        return client;
    }

    public static void Stamp(ExtractionDocument document, string candidate, DocumentExtractionClient client)
    {
        var identity = ProviderIdentity.Of(Unwrap(client).GetType());
        document.Metadata[Extractor] = candidate;
        document.Metadata[Provider] = identity.Name;
        document.Metadata[Assembly] = identity.Assembly;
        if (identity.Version is not null) document.Metadata[Version] = identity.Version;
    }

    /// <summary>Identity for a receipt: routing tags when present, otherwise the (unwrapped) client type.</summary>
    public static ProviderIdentity Identify(ExtractionDocument? document, DocumentExtractionClient client)
    {
        if (document is not null && document.Metadata.TryGetValue(Provider, out var provider) && provider is string type)
        {
            var name = document.Metadata.TryGetValue(Extractor, out var candidate) && candidate is string c ? $"{c} ({type})" : type;
            return new(name, document.Metadata.TryGetValue(Assembly, out var a) && a is string assembly ? assembly : "",
                document.Metadata.TryGetValue(Version, out var v) ? v as string : null);
        }
        return ProviderIdentity.Of(Unwrap(client).GetType());
    }
}

/// <summary>Defers creating an extractor (and its credentials/HTTP client) until a document is routed to it.</summary>
public sealed class LazyDocumentExtractionClient(Func<DocumentExtractionClient> factory) : DocumentExtractionClient
{
    private readonly Lazy<DocumentExtractionClient> value = new(factory, LazyThreadSafetyMode.ExecutionAndPublication);

    public bool IsCreated => value.IsValueCreated;
    public DocumentExtractionClient Value => value.Value;

    public override Task<ExtractionDocument> ExtractAsync(Stream document, string identifier, string mediaType,
        DocumentExtractionOptions? options = null, CancellationToken cancellationToken = default) =>
        value.Value.ExtractAsync(document, identifier, mediaType, options, cancellationToken);

    public override object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : value.IsValueCreated ? value.Value.GetService(serviceType, serviceKey) : null;

    protected override void Dispose(bool disposing)
    {
        if (disposing && value.IsValueCreated) value.Value.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>One extractor the router may choose, with the limits used to filter it before asking the decider.</summary>
/// <param name="Name">Candidate id, one of <c>MediaStudio.Configuration.DocumentCandidates</c>.</param>
/// <param name="Description">What the candidate is good at (shown to the decider).</param>
/// <param name="IsRemote">Uploads the document to a cloud service (consent-gated).</param>
/// <param name="Client">The extractor; remote ones are wrapped in <see cref="ConsentGatedDocumentExtractionClient"/>.</param>
/// <param name="SupportsPdf">Accepts PDFs.</param>
/// <param name="SupportsImages">Accepts image files.</param>
/// <param name="MaxPages">Page limit, or <see langword="null"/>.</param>
/// <param name="MaxBytes">Byte limit, or <see langword="null"/>.</param>
public sealed record ExtractorCandidate(string Name, string Description, bool IsRemote, DocumentExtractionClient Client,
    bool SupportsPdf = true, bool SupportsImages = true, int? MaxPages = null, long? MaxBytes = null)
{
    public bool Fits(DocumentProbe probe) =>
        (probe.IsImageMediaType ? SupportsImages : SupportsPdf)
        && (MaxPages is not { } pages || probe.PageCount <= pages)
        && (MaxBytes is not { } bytes || probe.Bytes <= bytes);
}

/// <summary>Cheap acceptance check on an extraction result, used to decide whether to fail over.</summary>
public static class ExtractionQuality
{
    public const int MinimumCharsPerPage = 200;
    public const int MinimumSparseCharsPerPage = 20;

    public static int TextLength(ExtractionDocument document) => Text(document).Count(c => !char.IsWhiteSpace(c));

    public static bool IsAcceptable(ExtractionDocument document, DocumentProbe probe)
    {
        var text = Text(document);
        var chars = text.Count(c => !char.IsWhiteSpace(c));
        var pages = Math.Max(1, Math.Max(probe.PageCount, document.SourcePages.Count));
        var floor = probe.IsSparse ? MinimumSparseCharsPerPage : MinimumCharsPerPage;
        // Short digital documents (e.g. a one-paragraph PDF) are accepted when the text layer itself is short.
        if (!probe.IsImageMediaType && probe.PageCount > 0 && probe.TextCharsPerPage.Sum() is var layer && layer > 0)
            floor = Math.Min(floor, Math.Max(1, (int)(layer * 0.8 / pages)));
        if (chars / (double)pages < floor) return false;
        var replacement = text.Count(c => c == '\uFFFD');
        return replacement <= chars / 2;
    }

    internal static string Text(ExtractionDocument document)
    {
        var builder = new System.Text.StringBuilder();
        if (document.Content is TextContent { Text.Length: > 0 } root) builder.Append(root.Text).Append('\n');
        foreach (var element in document.EnumerateContent())
            if (element.Content is TextContent text) builder.Append(text.Text).Append('\n');
        return builder.ToString();
    }
}
