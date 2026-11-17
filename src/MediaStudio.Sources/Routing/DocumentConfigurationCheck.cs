using CommunityToolkit.DocumentExtraction.PdfPig;
using MediaStudio.Configuration;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace MediaStudio.Sources;

/// <summary>
/// <c>configure test</c> rows for local document extraction: a real PdfPig extraction of a generated one-page PDF, and
/// whether the <c>vlm</c> candidate has a vision model. Cloud candidates are checked by <see cref="ConfigurationTester"/>.
/// </summary>
public sealed class DocumentConfigurationCheck : IConfigurationCheck
{
    public const string Phrase = "Media Studio document check";

    public async Task<IReadOnlyList<RoleCheck>> CheckAsync(ProviderSettings settings, bool cloudOnly, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (cloudOnly) return [];
        var checks = new List<RoleCheck>();
        try
        {
            using var input = new MemoryStream(SamplePdf(), writable: false);
            using var client = new PdfPigDocumentExtractionClient();
            var document = await client.ExtractAsync(input, "configure-test.pdf", "application/pdf", null, cancellationToken).ConfigureAwait(false);
            var text = ExtractionQuality.Text(document);
            checks.Add(text.Contains("document check", StringComparison.Ordinal)
                ? new("documents:extract (pdfpig)", RoleCheckStatus.Ok, "extracted the text layer of a generated PDF locally")
                : new("documents:extract (pdfpig)", RoleCheckStatus.Failed, "PdfPig returned no text for a generated PDF"));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            checks.Add(new("documents:extract (pdfpig)", RoleCheckStatus.Failed, error.Message));
        }
        if (settings.Documents.Candidates.Contains(DocumentCandidates.Vlm, StringComparer.Ordinal))
            checks.Add(settings.Vision is { } vision
                ? new("documents:vlm", RoleCheckStatus.Ok, $"uses the vision model {vision.Model} (see the vision row)")
                : new("documents:vlm", RoleCheckStatus.Failed, "scanned pages need a vision model, but none is set",
                    "Set a vision model in Settings → Models (for example: ollama pull qwen3.5:4b), or remove vlm from documents.candidates."));
        return checks;
    }

    internal static byte[] SamplePdf()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4).AddText(Phrase, 12, new UglyToad.PdfPig.Core.PdfPoint(72, 720), font);
        return builder.Build();
    }
}
