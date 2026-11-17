using System.Text.Json;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace MediaStudio.Sources;

/// <summary>
/// Cheap, local features of a document used to choose an extractor. Computed with PdfPig only: no model, no upload.
/// </summary>
/// <param name="PageCount">Pages in the PDF; 1 for an image or text file; 0 when the PDF could not be opened.</param>
/// <param name="Bytes">Size of the original bytes.</param>
/// <param name="TextCharsPerPage">Non-whitespace characters in each page's text layer.</param>
/// <param name="ScannedRatio">Share of pages with fewer than <see cref="ScannedPageChars"/> characters of text (1 for images).</param>
/// <param name="HasImagesOnly">Every page has images and none has a usable text layer.</param>
/// <param name="IsImageMediaType">The input is an image file rather than a PDF.</param>
/// <param name="TableLikeLineRatio">Share of text lines laid out in three or more widely spaced columns.</param>
/// <param name="FormFieldCount">AcroForm fields in the PDF.</param>
/// <param name="Language">Dominant script of the text layer (<c>latin</c>, <c>cyrillic</c>, <c>greek</c>, <c>arabic</c>, <c>hebrew</c>, <c>cjk</c>), or <see langword="null"/>.</param>
/// <param name="LandscapeRatio">Share of pages wider than tall (slides).</param>
public sealed record DocumentProbe(int PageCount, long Bytes, int[] TextCharsPerPage, double ScannedRatio, bool HasImagesOnly,
    bool IsImageMediaType, double TableLikeLineRatio, int FormFieldCount, string? Language, double LandscapeRatio = 0)
{
    /// <summary>A page with fewer characters than this is treated as scanned.</summary>
    public const int ScannedPageChars = 20;

    /// <summary>The probe could not open the PDF (encrypted or malformed).</summary>
    public bool Unreadable => PageCount == 0 && !IsImageMediaType;

    public bool IsDigital => !IsImageMediaType && PageCount > 0 && ScannedRatio < 0.05;

    /// <summary>Images and slide decks legitimately carry little text per page.</summary>
    public bool IsSparse => IsImageMediaType || LandscapeRatio >= 0.5;

    public static async Task<DocumentProbe> AnalyzeAsync(Stream document, string mediaType, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        using var copy = new MemoryStream();
        await document.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);
        return Analyze(copy.ToArray(), mediaType);
    }

    public static DocumentProbe Analyze(byte[] bytes, string mediaType)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return new(1, bytes.Length, [0], 1, true, true, 0, 0, null);
        if (mediaType != "application/pdf")
        {
            var text = System.Text.Encoding.UTF8.GetString(bytes);
            return new(1, bytes.Length, [CountChars(text)], 0, false, false, 0, 0, Script(text));
        }
        try
        {
            using var pdf = PdfDocument.Open(bytes);
            var chars = new List<int>();
            int imageOnly = 0, landscape = 0, lines = 0, tableLines = 0;
            var sample = new System.Text.StringBuilder();
            foreach (var page in pdf.GetPages())
            {
                var count = page.Letters.Count(l => !string.IsNullOrWhiteSpace(l.Value));
                chars.Add(count);
                if (count < ScannedPageChars && page.NumberOfImages > 0) imageOnly++;
                if (page.Width > page.Height) landscape++;
                var (pageLines, pageTables) = Lines(page);
                lines += pageLines; tableLines += pageTables;
                if (sample.Length < 4000) sample.Append(page.Text.AsSpan(0, Math.Min(page.Text.Length, 4000 - sample.Length)));
            }
            var pages = chars.Count;
            var fields = pdf.TryGetForm(out var form) ? form.Fields.Count : 0;
            return new(pages, bytes.Length, chars.ToArray(),
                pages == 0 ? 1 : chars.Count(c => c < ScannedPageChars) / (double)pages,
                pages > 0 && imageOnly == pages, false,
                lines == 0 ? 0 : tableLines / (double)lines, fields, Script(sample.ToString()),
                pages == 0 ? 0 : landscape / (double)pages);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return new(0, bytes.Length, [], 1, false, false, 0, 0, null);
        }
    }

    /// <summary>The probe as decision state (no document text is included).</summary>
    public JsonElement ToState(string purpose = "document-extraction") => JsonSerializer.SerializeToElement(new
    {
        purpose,
        mediaType = IsImageMediaType ? "image" : "pdf",
        pageCount = PageCount,
        bytes = Bytes,
        meanTextCharsPerPage = PageCount == 0 ? 0 : Math.Round(TextCharsPerPage.DefaultIfEmpty().Average(), 1),
        scannedRatio = Math.Round(ScannedRatio, 3),
        hasImagesOnly = HasImagesOnly,
        tableLikeLineRatio = Math.Round(TableLikeLineRatio, 3),
        formFieldCount = FormFieldCount,
        language = Language,
        landscapeRatio = Math.Round(LandscapeRatio, 3),
        unreadable = Unreadable
    });

    private static int CountChars(string text) => text.Count(c => !char.IsWhiteSpace(c));

    private static (int Lines, int TableLines) Lines(Page page)
    {
        var words = page.GetWords().Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToArray();
        if (words.Length == 0) return (0, 0);
        int lines = 0, tables = 0;
        foreach (var line in words.GroupBy(w => Math.Round(w.BoundingBox.Bottom / 3)))
        {
            var ordered = line.OrderBy(w => w.BoundingBox.Left).ToArray();
            lines++;
            if (ordered.Length < 3) continue;
            var charWidth = ordered.Average(w => w.BoundingBox.Width / Math.Max(1, w.Text.Length));
            var gaps = 0;
            for (var i = 1; i < ordered.Length; i++)
                if (ordered[i].BoundingBox.Left - ordered[i - 1].BoundingBox.Right > 3 * charWidth) gaps++;
            if (gaps >= 2) tables++;
        }
        return (lines, tables);
    }

    private static string? Script(string text)
    {
        int latin = 0, cyrillic = 0, greek = 0, arabic = 0, hebrew = 0, cjk = 0;
        foreach (var c in text)
        {
            if (!char.IsLetter(c)) continue;
            if (c < 0x0250) latin++;
            else if (c is >= '\u0370' and < '\u0400') greek++;
            else if (c is >= '\u0400' and < '\u0530') cyrillic++;
            else if (c is >= '\u0590' and < '\u0600') hebrew++;
            else if (c is >= '\u0600' and < '\u0780') arabic++;
            else if (c is >= '\u3040' and < '\uA000' or >= '\uAC00' and < '\uD7B0') cjk++;
        }
        var scripts = new[] { ("latin", latin), ("cyrillic", cyrillic), ("greek", greek), ("arabic", arabic), ("hebrew", hebrew), ("cjk", cjk) };
        var best = scripts.MaxBy(s => s.Item2);
        return best.Item2 == 0 ? null : best.Item1;
    }
}
