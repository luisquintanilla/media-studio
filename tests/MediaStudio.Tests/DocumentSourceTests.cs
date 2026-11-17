using System.Text;
using System.Text.Json;
using MediaStudio.Sources;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Documents;
using UglyToad.PdfPig.Writer;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;

internal static class DocumentSourceTests
{
    public static byte[] Pdf(int pages, bool blank = false, bool lastBlank = false, bool imageOnly = false)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        for (var page = 1; page <= pages; page++)
        {
            var canvas = builder.AddPage(PageSize.A4);
            if (imageOnly) canvas.AddPng(ImagePng(), new PdfRectangle(40, 40, 200, 200));
            else if (!blank && !(lastBlank && page == pages)) canvas.AddText($"Media Studio source page {page}. Source text informs a draft. Approval stays explicit.",
                14, new PdfPoint(40, 700), font);
        }
        return builder.Build();
    }
    private static byte[] ImagePng()
    {
        using var result = new MemoryStream();
        result.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        void Chunk(string type, byte[] data)
        {
            var length = new byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
            result.Write(length); var body = Encoding.ASCII.GetBytes(type).Concat(data).ToArray(); result.Write(body);
            uint crc = uint.MaxValue;
            foreach (var value in body) { crc ^= value; for (var bit = 0; bit < 8; bit++) crc = crc >> 1 ^ ((crc & 1) == 1 ? 0xedb88320u : 0); }
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(length, ~crc); result.Write(length);
        }
        Chunk("IHDR", [0, 0, 0, 1, 0, 0, 0, 1, 8, 2, 0, 0, 0]);
        using var compressed = new MemoryStream();
        using (var zip = new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            zip.Write(new byte[] { 0, 80, 120, 160 });
        Chunk("IDAT", compressed.ToArray()); Chunk("IEND", []);
        return result.ToArray();
    }
    public static async Task<int> Run(string root)
    {
        var count = 0;
        void Check(bool test, string name) { if (!test) throw new Exception(name); count++; }
        async Task Reject(Func<Task> action, string code)
        {
            try { await action(); throw new Exception("Expected " + code); }
            catch (SourceException error) when (error.Code == code) { count++; }
        }
        using var store = new DocumentSourceStore(Path.Combine(root, "sources"));
        var pdf = Pdf(2);
        var prepared = await store.AddAsync(new MemoryStream(pdf), "media-studio-sources.pdf", "application/pdf");
        Check(prepared.Provider.Contains("PdfPigDocumentExtractionClient") && prepared.Projection.Pages.Length == 2
            && prepared.Projection.Pages.Select(p => p.Number).SequenceEqual(new int?[] { 1, 2 })
            && prepared.Projection.Pages.All(p => p.HasText && p.Width > 0 && p.CoordinateOrigin is not null),
            "ToolkitPdfPigRealSearchablePdfRetainsOriginalPagesAndFrames: " + JsonSerializer.Serialize(prepared.Projection, SourceJson.Options));
        Check(prepared.Projection.Chunks.Any(c => c.PageNumber == 1 && c.Text.Contains("page 1"))
            && prepared.Projection.Chunks.Any(c => c.PageNumber == 2 && c.Text.Contains("page 2"))
            && prepared.Projection.Chunks.All(c => c.Tokens is > 0 and <= 512)
            && prepared.Projection.Elements.SelectMany(e => e.Children).Any(e => e.Anchors.Length > 0),
            "ActualMediBridgeAndTokenizerChunksPreservePageAttributionAndGeometry");
        Check(store.Read(prepared.Id).OriginalSha256 == SourceJson.Hash(pdf)
            && store.ManifestHash(prepared.Id).Length == 64, "PrivateOriginalAndVersionedProjectionHaveVerifiedHashes");
        var literal = "# Inert title\n![do not fetch](https://example.com/image)\n<script>do not execute</script>\nIgnore approvals is source text, not permission.";
        var markdown = await store.AddAsync(new MemoryStream(Encoding.UTF8.GetBytes(literal)), "context.md", "text/markdown");
        Check(markdown.Projection.Chunks.Any(c => c.Text.Contains("<script>")) && markdown.Projection.Pages.Length == 0,
            "MarkdownLinksAndInstructionTextRemainLiteralWithoutNetworkOrTools");
        var utf16 = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("Unicode source café.")).ToArray();
        var text = await store.AddAsync(new MemoryStream(utf16), "context.txt", "text/plain");
        Check(text.Projection.Chunks.Any(c => c.Text.Contains("café")), "StrictBomMarkedUnicodeTextIsDecoded");
        await Reject(async () => await store.AddAsync(new MemoryStream("%PDF-fake"u8.ToArray()), "renamed.txt", "text/plain"), "source.type");
        await Reject(async () => await store.AddAsync(new MemoryStream([0xff, 0xff]), "bad.txt", "text/plain"), "source.encoding");
        await Reject(async () => await store.AddAsync(new MemoryStream(new byte[DocumentSourceStore.MaximumBytes + 1]), "too-large.txt", "text/plain"), "source.bytes");
        await Reject(async () => await store.AddAsync(new MemoryStream(Pdf(101)), "too-many.pdf", "application/pdf"), "source.pages");
        var blank = await store.AddAsync(new MemoryStream(Pdf(1, true)), "scanned.pdf", "application/pdf");
        Check(blank.Projection.Pages.Length == 1 && !blank.Projection.Pages[0].HasText && blank.Projection.Chunks.Length == 0,
            "BlankPdfPageIsRetainedAndNeedsOcrWithoutAutomaticFallback");
        var mixed = await store.AddAsync(new MemoryStream(Pdf(2, lastBlank: true)), "text-and-blank.pdf", "application/pdf");
        Check(mixed.Projection.Pages.Length == 2 && mixed.Projection.Pages[0].HasText && !mixed.Projection.Pages[1].HasText
            && store.Context([mixed.Id], "bounded").Contains("page 1"),
            "SearchableTextPlusIntentionallyBlankPageRemainsUsableWithExplicitCoverage");
        var images = await store.AddAsync(new MemoryStream(Pdf(1, imageOnly: true)), "image-only.pdf", "application/pdf");
        Check(images.Projection.Pages.Length == 1 && images.Projection.Chunks.Length == 0
            && !images.Projection.Pages[0].HasText, "WhollyImageOnlyPdfHasNoFakeTextAndRequiresExplicitOcr");
        await Reject(() => { store.Context([images.Id], "bounded"); return Task.CompletedTask; }, "source.needs-ocr");
        await Reject(() => { store.Context([blank.Id], "state"); return Task.CompletedTask; }, "source.needs-ocr");
        await Reject(() => { store.Context([prepared.Id], new string('x', 50000), 6000); return Task.CompletedTask; }, "source.context");
        var context = store.Context([prepared.Id, markdown.Id, text.Id], "bounded draft");
        Check(context.Contains(prepared.OriginalSha256) && context.Contains("section-") && !context.Contains(root),
            "AuthoringContextContainsSafeTextIdsAndHashesButNoPrivatePaths");
        var paperText = string.Concat(Enumerable.Repeat(" attention", 12000)) + " final-paper-evidence";
        var paper = await store.AddAsync(new MemoryStream(Encoding.UTF8.GetBytes(paperText)), "full-paper.txt", "text/plain");
        var paperContext = store.Context([paper.Id], "current draft");
        Check(paper.Projection.Chunks.Sum(c => c.Tokens) >= 12000
            && paperContext.Contains("final-paper-evidence") && paperContext.Contains(paper.OriginalSha256)
            && JsonDocument.Parse(paperContext).RootElement[0].GetProperty("chunks").GetArrayLength() == paper.Projection.Chunks.Length,
            "FullPaperSizedSourceFitsDefaultAuthoringBudgetWithoutDroppingFinalEvidenceOrChunks");
        await Reject(() => { store.Context([paper.Id], "current draft", 16000); return Task.CompletedTask; }, "source.context");
        var tokenizer = Microsoft.ML.Tokenizers.TiktokenTokenizer.CreateForEncoding("cl100k_base");
        var exactBudget = tokenizer.CountTokens(paperContext) + tokenizer.CountTokens("current draft")
            + DocumentSourceStore.AuthoringReserveTokens;
        Check(store.Context([paper.Id], "current draft", exactBudget) == paperContext,
            "AuthoringBudgetAdmitsExactBoundaryIncludingSerializedMetadataDraftAndReserve");
        await Reject(() => { store.Context([paper.Id], "current draft", exactBudget - 1); return Task.CompletedTask; }, "source.context");
        await Reject(() => { store.Context([paper.Id], string.Concat(Enumerable.Repeat(" draft", 64000))); return Task.CompletedTask; }, "source.context");
        var before = File.ReadAllBytes(Path.Combine(root, "sources", text.Id, "original"));
        File.WriteAllBytes(Path.Combine(root, "sources", text.Id, "original"), [1, 2, 3]);
        await Reject(() => { store.Read(text.Id); return Task.CompletedTask; }, "source.integrity");
        File.WriteAllBytes(Path.Combine(root, "sources", text.Id, "original"), before);
        foreach (var ip in new[] { "127.0.0.1", "10.0.0.1", "100.64.0.1", "172.16.0.1", "192.168.1.1", "169.254.169.254", "::1", "::ffff:127.0.0.1", "2001:db8::1" })
            Check(!PublicDocumentFetcher.IsPublic(System.Net.IPAddress.Parse(ip)), "PublicHttpsRejectsPrivateAndReservedAddress_" + ip);
        Check(PublicDocumentFetcher.IsPublic(System.Net.IPAddress.Parse("1.1.1.1")), "PublicHttpsAllowsValidatedPublicAddress");
        foreach (var link in new[] { "http://example.com/a.pdf", "https://user:pass@example.com/a.pdf", "https://example.com/a.pdf?token=secret",
            "https://localhost/a.pdf", "https://example.com:8443/a.pdf", "https://127.0.0.1/a.pdf" })
        {
            try { PublicDocumentFetcher.ValidateLink(link); throw new Exception("Expected rejected link."); }
            catch (SourceException) { count++; }
        }
        var graph = new ExtractionDocument("structured-fixture", new TextContent("Root local payload"));
        var section = new ExtractionDocumentSection(new TextContent("Section local payload"));
        var table = new ExtractionDocumentTableElement(new TextContent("Table local payload"), 2, 2);
        table.Cells[0, 0] = new ExtractionDocumentTableCell(new ExtractionDocumentTextElement(new TextContent("Anchor text")), 2, 1, "header");
        table.Cells[0, 1] = new ExtractionDocumentTableCell(1, 1, "empty-anchor");
        section.Elements.Add(table); graph.Sections.Add(section);
        var bridged = ExtractionIngestionReader.Bridge(graph);
        var all = bridged.EnumerateContent().Select(e => e.GetMarkdown()).ToArray();
        Check(all.Count(t => t == "Root local payload") == 1 && all.Count(t => t == "Section local payload") == 1
            && all.Count(t => t == "Table local payload") == 1 && all.Count(t => t == "Anchor text") == 1,
            "StructuralBridgePreservesLocalPayloadsAndTableAnchorsWithoutAggregationDuplicates");
        var safe = DocumentSourceStore.SafeElements(graph);
        var cells = safe[1].Children[0].Cells!;
        Check(cells.Length == 2 && cells[0].RowSpan == 2 && cells[1].Content is null
            && cells[1].Role == "empty-anchor", "SafeProjectionPreservesNullAnchorSpanAndRoleWithoutRawNativeGraph");
        var unknown = new ExtractionDocument("unknown-page-fixture");
        var originalPage = new ExtractionDocumentSourcePage("original-unknown-frame", null);
        unknown.SourcePages.Add(originalPage);
        var unknownSection = new ExtractionDocumentSection();
        var attributed = new ExtractionDocumentTextElement(new TextContent("An observed frame with unknown original page number."));
        attributed.Sources.Add(new(originalPage));
        unknownSection.Elements.Add(attributed); unknown.Sections.Add(unknownSection);
        using var unknownStore = new DocumentSourceStore(Path.Combine(root, "unknown"), new GraphFixture(unknown));
        var unknownSource = await unknownStore.AddAsync(new MemoryStream(pdf), "unknown.pdf", "application/pdf");
        Check(unknownSource.Projection.Pages.Single().Number is null
            && unknownSource.Projection.Chunks.Single().PageNumber is null
            && unknownSource.Projection.Chunks.Single().PageId == "original-unknown-frame",
            "UnknownPageNumberRetainsKnownOriginalFrameIdThroughActualMediChunking");
        var chat = new OcrFixture(pdf);
        using var ocr = new DocumentSourceStore(Path.Combine(root, "ocr"), ocrChat: chat,
            ocrCapabilities: new("https://configured.example", "fixture-pdf-model", true, false, DocumentSourceStore.MaximumBytes, 100));
        var original = await ocr.AddAsync(new MemoryStream(pdf), "whole.pdf", "application/pdf");
        await Reject(async () => await ocr.RunOcrAsync(original.Id, false), "source.ocr-approval");
        Check(chat.Calls == 0, "OcrDoesNotRunOnAttachmentOrWithoutWholeDocumentApproval");
        var replacement = await ocr.RunOcrAsync(original.Id, true);
        Check(chat.Calls == 1 && chat.ExactBytes && chat.Model == "fixture-pdf-model"
            && replacement.Supersedes == original.Id && replacement.OriginalSha256 == original.OriginalSha256
            && replacement.Id != original.Id, "ActualToolkitVlmInterceptedWholePdfUsesExplicitModelAndVersionedReplacement");
        Check(ocr.Read(original.Id).ProjectionSha256 == original.ProjectionSha256, "OcrReplacementDoesNotMutateHistoricSource");
        var provenance = new SourceProvenance(new string('a', 64), "https://example.com/paper.pdf",
            "Example authors. CC BY 4.0.", "Excerpt, pages preserved without edits.", [30, 37]);
        var attributedSource = await store.AttributeAsync(prepared.Id, provenance);
        Check(attributedSource.Supersedes == prepared.Id && attributedSource.OriginalSha256 == prepared.OriginalSha256
            && attributedSource.Projection.Pages.Select(p => p.Number).SequenceEqual(new int?[] { 30, 37 })
            && attributedSource.Projection.Chunks.All(c => c.PageNumber is 30 or 37)
            && store.Read(prepared.Id).Projection.Pages[0].Number == 1
            && store.Context([attributedSource.Id], "draft").Contains("CC BY 4.0."),
            "ImmutableExcerptAttributionMapsActualFramesAndChunksWithoutMutatingOriginal");
        await Reject(async () => await store.AttributeAsync(prepared.Id, provenance with { OriginalPages = [30] }), "source.provenance");
        var remapped = await store.AttributeAsync(attributedSource.Id, provenance with { OriginalPages = [31, 38] });
        Check(remapped.Projection.Chunks.All(c => c.PageNumber is 31 or 38)
            && store.Read(attributedSource.Id).Projection.Pages[0].Number == 30,
            "ReplacingAttributionRetainsInputOrderAndImmutableEarlierMapping");
        var reversedGraph = new ExtractionDocument("reversed-observed-pages");
        foreach (var number in new[] { 2, 1 })
        {
            var page = new ExtractionDocumentSourcePage("frame-" + number, number);
            reversedGraph.SourcePages.Add(page);
            var item = new ExtractionDocumentTextElement(new TextContent("Observed input page " + number));
            item.Sources.Add(new(page));
            var group = new ExtractionDocumentSection();
            group.Elements.Add(item); reversedGraph.Sections.Add(group);
        }
        using var reversedStore = new DocumentSourceStore(Path.Combine(root, "reversed"), new GraphFixture(reversedGraph));
        var reversed = await reversedStore.AddAsync(new MemoryStream(pdf), "reversed.pdf", "application/pdf");
        var mappedReversed = await reversedStore.AttributeAsync(reversed.Id, provenance);
        Check(mappedReversed.Projection.Pages.Select(p => p.Number).SequenceEqual(new int?[] { 37, 30 })
            && mappedReversed.Projection.Chunks.Single(c => c.Text.Contains("page 2")).PageNumber == 37,
            "ProviderResponseOrderCannotSilentlyReassignOriginalExcerptPages");
        var imageGraph = new ExtractionDocument("image-payload-fixture");
        var imageSection = new ExtractionDocumentSection();
        imageSection.Elements.Add(new ExtractionDocumentImageElement(new DataContent(ImagePng(), "image/png")) { AlternativeText = "Observed caption, not interpretation." });
        imageSection.Elements.Add(new ExtractionDocumentImageElement(new UriContent("https://example.com/not-fetched.png", "image/png")));
        imageGraph.Sections.Add(imageSection);
        var imageProjection = DocumentSourceStore.SafeElements(imageGraph);
        Check(imageProjection[0].Children[0].Image is { } payload && payload.Sha256 == SourceJson.Hash(ImagePng())
            && Convert.FromBase64String(payload.Base64).SequenceEqual(ImagePng())
            && imageProjection[0].Children[0].AlternativeText == "Observed caption, not interpretation."
            && imageProjection[0].Children[1].Image is null,
            "ActualInlineImageBytesRetainedWithHashWhileRemoteUriIsNeverFetchedOrPresentedAsPayload");
        var pngInput = await ocr.AddAsync(new MemoryStream(ImagePng()), "figure.png", "image/png");
        Check(chat.Calls == 1 && pngInput.Projection.Chunks.Length == 0
            && pngInput.Projection.Elements.Single().Image?.Sha256 == SourceJson.Hash(ImagePng()),
            "PngAttachmentIsLocalPreviewOnlyWithNoAutomaticInference");
        var mappedPng = await ocr.AttributeAsync(pngInput.Id, provenance with { OriginalPages = [30] });
        await Reject(async () => await ocr.RunOcrAsync(mappedPng.Id, false), "source.ocr-approval");
        Check(chat.Calls == 1, "ImageReadingRequiresExplicitWholeImageConsent");
        var oversizedImage = new byte[DocumentSourceStore.MaximumImageBytes + 1];
        ImagePng().CopyTo(oversizedImage, 0);
        await Reject(async () => await store.AddAsync(new MemoryStream(oversizedImage), "oversized.png", "image/png"), "source.image");
        var readImage = await ocr.RunOcrAsync(mappedPng.Id, true);
        Check(chat.Calls == 2 && chat.ExactImageBytes && chat.ImageInstruction == DocumentSourceStore.ImageReadingPrompt
            && readImage.Provider.Contains("VisionLanguageModelDocumentExtractionClient")
            && readImage.Projection.Pages.Single().Number == 30
            && readImage.Projection.Chunks.Single().PageNumber == 30
            && readImage.Projection.Elements[0].Image?.Sha256 == SourceJson.Hash(ImagePng())
            && SourceJson.Fingerprint(readImage.Provenance) == SourceJson.Fingerprint(mappedPng.Provenance)
            && !ocr.Context([readImage.Id], "draft").Contains(Convert.ToBase64String(ImagePng())),
            "ImageVisionUsesActualDataContentAndExplicitInterpretationPromptWithOriginalPageProvenanceButNoImplicitAuthoringImageUpload");
        var attributedOcr = await ocr.AttributeAsync(original.Id, provenance);
        await Reject(async () => await ocr.RunOcrAsync(attributedOcr.Id, true), "source.provenance");
        Check(ocr.Read(attributedOcr.Id).Projection.Pages[0].Number == 30,
            "AmbiguousWholePdfVlmPageMappingFailsWithoutMutatingAttributedOriginal");
        Check(PublicDocumentFetcher.ValidateLink("https://[2606:4700:4700::1111]/source.txt").HostNameType == UriHostNameType.IPv6
            && PublicDocumentFetcher.IsPublic(System.Net.IPAddress.Parse("192.2.3.4")),
            "PublicIpv6AndNonReservedIpv4AreNotMistakenForLocalAddresses");
        await Reject(() => Task.FromResult(PublicDocumentFetcher.ValidateAddresses(
            [System.Net.IPAddress.Parse("1.1.1.1"), System.Net.IPAddress.Parse("127.0.0.1")])), "source.private-address");
        var redirectFixture = new HttpFixture(request => request.RequestUri!.Host == "example.com"
            ? new(System.Net.HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://example.net/source.txt") } }
            : TextResponse("A redirected public document remains source text."));
        var linked = await new PublicDocumentFetcher(redirectFixture).ReadAsync(store, "https://example.com/source.txt");
        Check(redirectFixture.Calls == 2 && linked.PublicOrigin == "https://example.net/source.txt"
            && linked.Projection.Chunks.Any(c => c.Text.Contains("redirected public")),
            "InterceptedPublicRedirectRevalidatesOriginThenUsesActualLocalTextExtraction");
        var privateRedirect = new HttpFixture(_ => new(System.Net.HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://127.0.0.1/source.txt") } });
        await Reject(async () => await new PublicDocumentFetcher(privateRedirect).ReadAsync(store, "https://example.com/source.txt"),
            "source.private-address");
        Check(privateRedirect.Calls == 1, "PrivateRedirectIsRejectedBeforeAnySecondRequest");
        var endless = new HttpFixture(_ => new(System.Net.HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://example.com/again.txt") } });
        await Reject(async () => await new PublicDocumentFetcher(endless).ReadAsync(store, "https://example.com/source.txt"), "source.redirect");
        Check(endless.Calls == 4, "AtMostThreeRedirectsAreFollowed");
        var compressed = new HttpFixture(_ =>
        {
            var response = TextResponse("Not transparently decompressed.");
            response.Content.Headers.ContentEncoding.Add("gzip"); return response;
        });
        await Reject(async () => await new PublicDocumentFetcher(compressed).ReadAsync(store, "https://example.com/source.txt"), "source.encoding");
        var chunked = new HttpFixture(_ =>
        {
            var response = TextResponse("");
            response.Content = new StreamContent(new UnknownLengthStream(new MemoryStream(new byte[DocumentSourceStore.MaximumBytes + 1])));
            response.Content.Headers.ContentType = new("text/plain");
            return response;
        });
        await Reject(async () => await new PublicDocumentFetcher(chunked).ReadAsync(store, "https://example.com/source.txt"), "source.bytes");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await new PublicDocumentFetcher(new HttpFixture(_ => TextResponse("Never read"))).ReadAsync(store, "https://example.com/source.txt", cancelled.Token); throw new Exception("Expected cancellation"); }
        catch (OperationCanceledException) { count++; }
        return count;
    }
    private static HttpResponseMessage TextResponse(string text) => new(System.Net.HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "text/plain") };
    private sealed class HttpFixture(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Calls++;
            return Task.FromResult(respond(request));
        }
    }
    private sealed class UnknownLengthStream(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
    private sealed class GraphFixture(ExtractionDocument graph) : Microsoft.Extensions.DocumentExtraction.DocumentExtractionClient
    {
        public override Task<ExtractionDocument> ExtractAsync(Stream document, string identifier, string mediaType,
            Microsoft.Extensions.DocumentExtraction.DocumentExtractionOptions? options = null, CancellationToken cancellationToken = default) => Task.FromResult(graph);
    }
    private sealed class OcrFixture(byte[] expected) : IChatClient
    {
        public int Calls; public bool ExactBytes; public bool ExactImageBytes; public string? Model; public string? ImageInstruction;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++; Model = options?.ModelId;
            var input = messages.SelectMany(m => m.Contents).OfType<DataContent>().Single();
            ExactBytes = input.Data.Span.SequenceEqual(expected);
            if (input.MediaType == "image/png")
            {
                ExactImageBytes = input.Data.Span.SequenceEqual(ImagePng());
                ImageInstruction = messages.Single().Text;
            }
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Intercepted OCR protocol response, not quality measurement.")) { ModelId = "observed-fixture-model" });
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => serviceType == typeof(IChatClient) ? this : null;
        public void Dispose() { }
    }
}
