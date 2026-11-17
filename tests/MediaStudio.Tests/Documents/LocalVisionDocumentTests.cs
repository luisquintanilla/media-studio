using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using MediaStudio.Configuration;
using MediaStudio.Sources;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Writer;
using VideoProduction.Providers;

namespace MediaStudio.Tests.Documents;

public sealed class LocalVisionDocumentTests
{
    [Fact, Trait("Category", "LocalRuntime")]
    public async Task ScannedPdfIsReadByTheLocalVisionModel()
    {
        var model = Environment.GetEnvironmentVariable("MEDIASTUDIO_VISION_MODEL") ?? "qwen3.5:4b";
        var settings = new ProviderSettings {
            Connections = [new() { Id = "ollama", Kind = ConnectionKind.OpenAICompatible, Endpoint = "http://localhost:11434/v1" }],
            Vision = new() { Connection = "ollama", Model = model }
        };
        using var vision = ProviderFactory.CreateRoleClient(settings, settings.Vision!);
        using var router = DocumentExtractorFactory.CreateRouter(settings, vision, null, FixedUploadConsent.Deny);

        var builder = new PdfDocumentBuilder();
        builder.AddPage(PageSize.A4).AddPng(TextPng("HELLO WORLD"), new PdfRectangle(40, 500, 540, 600));
        var document = await router.ExtractAsync(new MemoryStream(builder.Build()), "scan", "application/pdf");

        Assert.Equal(DocumentCandidates.Vlm, document.Metadata[DocumentExtractionTags.Extractor]);
        Assert.True(ExtractionQuality.TextLength(document) > 0, "The vision model returned no text.");
        Assert.Contains("HELLO", ExtractionText(document), StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractionText(Microsoft.Extensions.Documents.ExtractionDocument document) =>
        string.Join('\n', document.Sections.SelectMany(s => s.Elements)
            .OfType<Microsoft.Extensions.Documents.ExtractionDocumentTextElement>().Select(e => (e.Content as Microsoft.Extensions.AI.TextContent)?.Text));

    private static readonly Dictionary<char, string[]> Glyphs = new() {
        ['H'] = ["10001", "10001", "10001", "11111", "10001", "10001", "10001"],
        ['E'] = ["11111", "10000", "10000", "11110", "10000", "10000", "11111"],
        ['L'] = ["10000", "10000", "10000", "10000", "10000", "10000", "11111"],
        ['O'] = ["01110", "10001", "10001", "10001", "10001", "10001", "01110"],
        ['W'] = ["10001", "10001", "10001", "10101", "10101", "10101", "01010"],
        ['R'] = ["11110", "10001", "10001", "11110", "10100", "10010", "10001"],
        ['D'] = ["11110", "10001", "10001", "10001", "10001", "10001", "11110"],
        [' '] = ["00000", "00000", "00000", "00000", "00000", "00000", "00000"],
    };

    /// <summary>Renders block capitals into a grayscale PNG (no imaging library needed).</summary>
    internal static byte[] TextPng(string text, int scale = 10)
    {
        int width = (text.Length * 6 + 1) * scale, height = 9 * scale;
        var raw = new byte[height * (width + 1)];
        for (var y = 0; y < height; y++)
        {
            raw[y * (width + 1)] = 0;
            for (var x = 0; x < width; x++)
            {
                int gx = x / scale - 1, gy = y / scale - 1, index = gx / 6, column = gx % 6;
                var ink = gx >= 0 && gy is >= 0 and < 7 && index < text.Length && column < 5 && Glyphs[text[index]][gy][column] == '1';
                raw[y * (width + 1) + 1 + x] = ink ? (byte)0 : (byte)255;
            }
        }
        using var png = new MemoryStream();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        void Chunk(string type, byte[] data)
        {
            var buffer = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(buffer, data.Length); png.Write(buffer);
            var body = Encoding.ASCII.GetBytes(type).Concat(data).ToArray(); png.Write(body);
            var crc = uint.MaxValue;
            foreach (var value in body) { crc ^= value; for (var bit = 0; bit < 8; bit++) crc = crc >> 1 ^ ((crc & 1) == 1 ? 0xedb88320u : 0); }
            BinaryPrimitives.WriteUInt32BigEndian(buffer, ~crc); png.Write(buffer);
        }
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; header[9] = 0;
        Chunk("IHDR", header);
        using var compressed = new MemoryStream();
        using (var zip = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) zip.Write(raw);
        Chunk("IDAT", compressed.ToArray());
        Chunk("IEND", []);
        return png.ToArray();
    }
}
