using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.Documents;
using UglyToad.PdfPig;

namespace MediaStudio.Sources;

/// <summary>
/// Lets an image-only extractor (a local vision model that cannot take PDF files) read scanned PDFs: each page's largest
/// embedded image is sent on its own, and pages that already have a text layer keep that text. Images pass straight through.
/// </summary>
public sealed class PdfPageImagesDocumentExtractionClient(DocumentExtractionClient imageExtractor, int maximumPages = 100)
    : DelegatingDocumentExtractionClient(imageExtractor)
{
    public override async Task<ExtractionDocument> ExtractAsync(Stream document, string identifier, string mediaType,
        DocumentExtractionOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (mediaType != "application/pdf")
            return await base.ExtractAsync(document, identifier, mediaType, options, cancellationToken).ConfigureAwait(false);

        using var copy = new MemoryStream();
        await document.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);
        var result = new ExtractionDocument(identifier, null!);
        using var pdf = PdfDocument.Open(copy.ToArray());
        if (pdf.NumberOfPages > maximumPages)
            throw new InvalidOperationException($"The PDF has {pdf.NumberOfPages} pages; this reader accepts at most {maximumPages}.");
        foreach (var page in pdf.GetPages())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = new ExtractionDocumentSourcePage($"{identifier}:page:{page.Number}", page.Number,
                new ExtractionDocumentPageDimensions(page.Width, page.Height), null, null);
            result.SourcePages.Add(source);
            string text;
            if (page.Letters.Count(l => !string.IsNullOrWhiteSpace(l.Value)) >= DocumentProbe.ScannedPageChars)
                text = page.Text;
            else if (LargestImage(page) is { } image)
            {
                using var input = new MemoryStream(image.Bytes, writable: false);
                var read = await base.ExtractAsync(input, $"{identifier}:page:{page.Number}", image.MediaType, options, cancellationToken)
                    .ConfigureAwait(false);
                text = TextOf(read);
            }
            else continue;
            if (string.IsNullOrWhiteSpace(text)) continue;
            var section = new ExtractionDocumentSection(null!);
            var element = new ExtractionDocumentTextElement(new TextContent(text), "paragraph");
            element.Sources.Add(new ExtractionDocumentSourceReference(source, [], null!));
            section.Elements.Add(element);
            result.Sections.Add(section);
        }
        return result;
    }

    private static (byte[] Bytes, string MediaType)? LargestImage(UglyToad.PdfPig.Content.Page page)
    {
        foreach (var image in page.GetImages().OrderByDescending(i => (long)i.WidthInSamples * i.HeightInSamples))
        {
            if (image.TryGetPng(out var png) && png is { Length: > 0 }) return (png, "image/png");
            var raw = image.RawMemory.ToArray();
            if (raw.Length > 3 && raw[0] == 0xFF && raw[1] == 0xD8 && raw[2] == 0xFF) return (raw, "image/jpeg");
        }
        return null;
    }

    private static string TextOf(ExtractionDocument document)
    {
        var builder = new StringBuilder();
        if (document.Content is TextContent { Text.Length: > 0 } root) builder.AppendLine(root.Text);
        foreach (var element in document.EnumerateContent())
            if (element.Content is TextContent { Text.Length: > 0 } text) builder.AppendLine(text.Text);
        return builder.ToString().Trim();
    }
}
