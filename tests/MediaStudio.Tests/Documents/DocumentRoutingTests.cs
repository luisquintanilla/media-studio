using System.Net;
using System.Text;
using MediaStudio.Configuration;
using MediaStudio.Sources;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.Documents;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using VideoProduction.Decisions.Experimental;

namespace MediaStudio.Tests.Documents;

public sealed class DocumentRoutingTests
{
    internal static readonly string LongText = string.Join(' ', Enumerable.Repeat("Recovered scanned text from the page.", 20));

    [Fact]
    public async Task DigitalPdfTakesTheFastPathWithoutAskingTheDecider()
    {
        var decider = new ScriptedDecider(DocumentCandidates.Vlm);
        var vlm = new FakeExtractor(LongText);
        using var router = new DecisionRoutingDocumentExtractionClient(
            [Local(DocumentCandidates.PdfPig, new CommunityToolkit.DocumentExtraction.PdfPig.PdfPigDocumentExtractionClient(), images: false),
             Local(DocumentCandidates.Vlm, vlm)], decider, allowCloud: false);

        var document = await router.ExtractAsync(new MemoryStream(DocumentSourceTests.Pdf(2)), "doc", "application/pdf");

        Assert.Equal(DocumentCandidates.PdfPig, document.Metadata[DocumentExtractionTags.Extractor]);
        Assert.Contains("PdfPigDocumentExtractionClient", (string)document.Metadata[DocumentExtractionTags.Provider]!);
        Assert.Equal(0, decider.Calls);
        Assert.Equal(0, vlm.Calls);
        Assert.Equal("fast-path", router.LastPlan!.Source);
    }

    [Fact]
    public async Task ScannedPdfWithCloudOffUsesTheLocalVisionModelAndHidesCloudCandidates()
    {
        var decider = new ScriptedDecider(DocumentCandidates.Vlm);
        var mistral = new FakeExtractor(LongText);
        var vlm = new FakeExtractor(LongText);
        using var router = new DecisionRoutingDocumentExtractionClient(
            [Local(DocumentCandidates.PdfPig, new CommunityToolkit.DocumentExtraction.PdfPig.PdfPigDocumentExtractionClient(), images: false),
             Local(DocumentCandidates.Vlm, vlm),
             Remote(DocumentCandidates.Mistral, mistral, FixedUploadConsent.Allow, allowCloud: false)], decider, allowCloud: false);

        var document = await router.ExtractAsync(new MemoryStream(DocumentSourceTests.Pdf(2, imageOnly: true)), "doc", "application/pdf");

        Assert.Equal(DocumentCandidates.Vlm, document.Metadata[DocumentExtractionTags.Extractor]);
        Assert.Equal(0, mistral.Calls);
        Assert.Equal([DocumentCandidates.PdfPig, DocumentCandidates.Vlm], decider.LastCandidates);
    }

    [Fact]
    public async Task DeciderOrderIsFollowedAndFailsOverOnErrors()
    {
        var decider = new ScriptedDecider(DocumentCandidates.DocumentIntelligence,
            (DocumentCandidates.Vlm, 0.2), (DocumentCandidates.DocumentIntelligence, 0.5), (DocumentCandidates.Mistral, 0.3));
        var di = new FakeExtractor(error: new HttpRequestException("503"));
        var mistral = new FakeExtractor(LongText);
        var vlm = new FakeExtractor(LongText);
        using var router = new DecisionRoutingDocumentExtractionClient(
            [Local(DocumentCandidates.Vlm, vlm),
             Remote(DocumentCandidates.DocumentIntelligence, di, FixedUploadConsent.Allow),
             Remote(DocumentCandidates.Mistral, mistral, FixedUploadConsent.Allow)], decider, allowCloud: true);

        var document = await router.ExtractAsync(new MemoryStream(DocumentSourceTests.Pdf(1, imageOnly: true)), "doc", "application/pdf");

        Assert.Equal(DocumentCandidates.Mistral, document.Metadata[DocumentExtractionTags.Extractor]);
        Assert.Equal(1, di.Calls);
        Assert.Equal(0, vlm.Calls);
        Assert.Equal("decision", router.LastPlan!.Source);
    }

    [Fact]
    public async Task DeclinedConsentFallsBackToALocalCandidateWithoutUploading()
    {
        var mistral = new FakeExtractor(LongText);
        var vlm = new FakeExtractor(LongText);
        using var router = new DecisionRoutingDocumentExtractionClient(
            [Remote(DocumentCandidates.Mistral, mistral, FixedUploadConsent.Deny), Local(DocumentCandidates.Vlm, vlm)],
            new ScriptedDecider(DocumentCandidates.Mistral, (DocumentCandidates.Mistral, 0.9), (DocumentCandidates.Vlm, 0.1)), allowCloud: true);

        var document = await router.ExtractAsync(new MemoryStream(DocumentSourceTests.Pdf(1, imageOnly: true)), "doc", "application/pdf");

        Assert.Equal(DocumentCandidates.Vlm, document.Metadata[DocumentExtractionTags.Extractor]);
        Assert.Equal(0, mistral.Calls);
    }

    [Fact]
    public async Task EveryCandidateFailingThrowsWithAllFailures()
    {
        using var router = new DecisionRoutingDocumentExtractionClient(
            [Local(DocumentCandidates.Vlm, new FakeExtractor(error: new InvalidOperationException("down")))], null, allowCloud: false);

        var error = await Assert.ThrowsAsync<DocumentRoutingException>(() =>
            router.ExtractAsync(new MemoryStream(DocumentSourceTests.Pdf(1, imageOnly: true)), "doc", "application/pdf"));
        Assert.Single(error.InnerExceptions);
    }

    [Fact]
    public async Task LowQualityResultsAreReturnedOnlyAfterEveryCandidateWasTried()
    {
        var first = new FakeExtractor("short");
        var second = new FakeExtractor("a bit longer text");
        using var router = new DecisionRoutingDocumentExtractionClient(
            [Local(DocumentCandidates.Vlm, first), Local("vlm-2", second)], null, allowCloud: false);

        var document = await router.ExtractAsync(new MemoryStream(DocumentSourceTests.Pdf(1, imageOnly: true)), "doc", "application/pdf");

        Assert.Equal(1, first.Calls);
        Assert.Equal(1, second.Calls);
        Assert.True(document.Metadata.ContainsKey(DocumentExtractionTags.LowQuality));
        Assert.Equal("vlm-2", document.Metadata[DocumentExtractionTags.Extractor]);
    }

    [Fact]
    public async Task MistralIsExcludedAboveThirtyPagesAndHeuristicDeciderUsesRules()
    {
        var mistral = new ExtractorCandidate(DocumentCandidates.Mistral, "m", true, new FakeExtractor(LongText),
            MaxPages: DocumentExtractorFactory.MistralMaximumPages, MaxBytes: DocumentExtractorFactory.MistralMaximumBytes);
        var di = new ExtractorCandidate(DocumentCandidates.DocumentIntelligence, "d", true, new FakeExtractor(LongText));
        var cu = new ExtractorCandidate(DocumentCandidates.ContentUnderstanding, "c", true, new FakeExtractor(LongText));
        var probe = new DocumentProbe(31, 1000, new int[31], 1, true, false, 0, 0, null);
        using var router = new DecisionRoutingDocumentExtractionClient([mistral, di, cu],
            new ScriptedDecider(DocumentCandidates.DocumentIntelligence, provider: "heuristic"), allowCloud: true);

        var plan = await router.PlanAsync(probe);

        Assert.False(mistral.Fits(probe));
        Assert.Equal([DocumentCandidates.ContentUnderstanding, DocumentCandidates.DocumentIntelligence], plan.Ranked.Select(c => c.Name));
        Assert.Equal("rules", plan.Source);
    }

    [Fact]
    public void RoutingRulesFollowThePlan()
    {
        ExtractorCandidate C(string name) => new(name, name, DocumentCandidates.IsCloud(name), new FakeExtractor(LongText));
        var all = DocumentCandidates.All.Select(C).ToArray();
        string First(DocumentProbe probe, bool cloud) =>
            DocumentRoutingRules.Rank(probe, cloud ? all : all.Where(c => !c.IsRemote).ToArray())[0].Name;

        Assert.Equal(DocumentCandidates.PdfPig, First(new(3, 1, [500, 500, 500], 0, false, false, 0, 0, "latin"), true));
        Assert.Equal(DocumentCandidates.Vlm, First(new(3, 1, [0, 0, 0], 1, true, false, 0, 0, null), false));
        Assert.Equal(DocumentCandidates.DocumentIntelligence, First(new(3, 1, [0, 0, 0], 1, true, false, 0, 4, null), true));
        Assert.Equal(DocumentCandidates.ContentUnderstanding, First(new(40, 1, new int[40], 1, true, false, 0, 0, null), true));
        Assert.Equal(DocumentCandidates.Mistral, First(new(1, 1, [0], 1, true, true, 0, 0, null), true));
    }

    [Fact]
    public async Task ConsentGateBlocksUploadsUnlessCloudIsAllowedAndTheUserApproves()
    {
        var inner = new FakeExtractor(LongText);
        var consent = new RecordingConsent(false);

        await Assert.ThrowsAsync<UploadConsentDeniedException>(() =>
            new ConsentGatedDocumentExtractionClient(inner, "Mistral OCR", consent, allowCloud: false)
                .ExtractAsync(new MemoryStream([1]), "doc", "image/png"));
        Assert.Empty(consent.Requests);

        var denied = await Assert.ThrowsAsync<UploadConsentDeniedException>(() =>
            new ConsentGatedDocumentExtractionClient(inner, "Mistral OCR", consent, allowCloud: true)
                .ExtractAsync(new MemoryStream([1]), "doc", "image/png"));
        Assert.Equal("Mistral OCR", denied.ProviderName);
        Assert.Equal([("doc", "Mistral OCR")], consent.Requests);
        Assert.Equal(0, inner.Calls);

        await new ConsentGatedDocumentExtractionClient(inner, "Mistral OCR", new RecordingConsent(true), allowCloud: true)
            .ExtractAsync(new MemoryStream([1]), "doc", "image/png");
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public void ProbeDistinguishesDigitalScannedAndTabularDocuments()
    {
        var digital = DocumentProbe.Analyze(DocumentSourceTests.Pdf(2), "application/pdf");
        Assert.True(digital.IsDigital);
        Assert.Equal(2, digital.PageCount);
        Assert.Equal("latin", digital.Language);

        var scanned = DocumentProbe.Analyze(DocumentSourceTests.Pdf(2, imageOnly: true), "application/pdf");
        Assert.Equal(1, scanned.ScannedRatio);
        Assert.True(scanned.HasImagesOnly);
        Assert.False(scanned.IsDigital);

        var table = DocumentProbe.Analyze(TablePdf(), "application/pdf");
        Assert.True(table.TableLikeLineRatio >= 0.5, table.TableLikeLineRatio.ToString());

        var image = DocumentProbe.Analyze([137, 80, 78, 71], "image/png");
        Assert.True(image.IsImageMediaType && image.IsSparse);
        Assert.Equal("image", image.ToState().GetProperty("mediaType").GetString());

        Assert.True(DocumentProbe.Analyze(Encoding.ASCII.GetBytes("%PDF-broken"), "application/pdf").Unreadable);
    }

    [Fact]
    public async Task PageImagesWrapperSendsEachScannedPageAsAnImage()
    {
        var inner = new FakeExtractor(LongText);
        var wrapper = new PdfPageImagesDocumentExtractionClient(inner);

        var document = await wrapper.ExtractAsync(new MemoryStream(DocumentSourceTests.Pdf(2, imageOnly: true)), "doc", "application/pdf");

        Assert.Equal(2, inner.Calls);
        Assert.All(inner.MediaTypes, m => Assert.StartsWith("image/", m));
        Assert.Equal(2, document.SourcePages.Count);
        Assert.Equal(2 * LongText.Count(c => !char.IsWhiteSpace(c)), ExtractionQuality.TextLength(document));
    }

    [Fact]
    public async Task StoreReadsWithTheRouterAndRecordsCandidateAndPackageVersion()
    {
        var root = Path.Combine(Path.GetTempPath(), "ms-doc-route-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new DocumentSourceStore(root);
            var added = await store.AddAsync(new MemoryStream(DocumentSourceTests.Pdf(2)), "digital.pdf", "application/pdf");
            Assert.Equal(DocumentSource.CurrentSchemaVersion, added.SchemaVersion);
            Assert.NotNull(added.ProviderVersion);

            using var router = DocumentExtractorFactory.CreateRouter(new ProviderSettings(), null, null, FixedUploadConsent.Deny);
            var reread = await store.ReadWithAsync(added.Id, router);

            Assert.StartsWith("pdfpig (", reread.Provider);
            Assert.Equal("CommunityToolkit.DocumentExtraction.PdfPig", reread.ProviderAssembly);
            Assert.Equal(added.Id, reread.Supersedes);
            Assert.Equal(reread.ProjectionSha256, store.Read(reread.Id).ProjectionSha256);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void SchemaOneReceiptsStillRead()
    {
        var current = new DocumentSource(2, "source-" + new string('a', 32), "a.pdf", "application/pdf", 1, "x", "y", "p", "asm", "1.0",
            new([], [], [], []), DateTimeOffset.UnixEpoch);
        var node = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(current, SourceJson.Options))!.AsObject();
        node["schemaVersion"] = 1;
        node.Remove("providerVersion");
        node["providerAssemblySha256"] = new string('0', 64);

        var read = SourceJson.ReadReceipt(node.ToJsonString())!;

        Assert.Equal(1, read.SchemaVersion);
        Assert.Null(read.ProviderVersion);
        Assert.Equal("asm", read.ProviderAssembly);
    }

    [Fact]
    public void FactoryBuildsCandidatesFromSettingsAndGatesCloudOnes()
    {
        var candidates = DocumentExtractorFactory.CreateCandidates(CloudSettings(), new FakeChat(), FixedUploadConsent.Deny);

        Assert.Equal(DocumentCandidates.All, candidates.Select(c => c.Name));
        Assert.All(candidates.Where(c => c.IsRemote), c => Assert.IsType<ConsentGatedDocumentExtractionClient>(c.Client));
        Assert.All(candidates.Where(c => !c.IsRemote), c => Assert.IsNotType<ConsentGatedDocumentExtractionClient>(c.Client));
        Assert.Equal(30, candidates.Single(c => c.Name == DocumentCandidates.Mistral).MaxPages);

        var local = DocumentExtractorFactory.CreateCandidates(new ProviderSettings(), null, FixedUploadConsent.Deny);
        Assert.Equal([DocumentCandidates.PdfPig], local.Select(c => c.Name));
    }

    [Theory]
    [InlineData(DocumentCandidates.DocumentIntelligence, "/documentintelligence/documentModels/prebuilt-layout:analyze")]
    [InlineData(DocumentCandidates.ContentUnderstanding, "/contentunderstanding/analyzers/prebuilt-documentSearch:analyze")]
    [InlineData(DocumentCandidates.Mistral, "/providers/mistral/azure/ocr")]
    public async Task CloudExtractorsCallTheSingleFoundryResource(string candidate, string path)
    {
        var handler = new RecordingHandler();
        using var client = DocumentExtractorFactory.CreateExplicit(candidate, CloudSettings(), null, FixedUploadConsent.Allow,
            new DocumentExtractorFactoryOptions { Handler = handler, ApiKey = _ => "test-key" });

        await Assert.ThrowsAnyAsync<Exception>(() => client.ExtractAsync(new MemoryStream(DocumentSourceTests.Pdf(1)), "doc", "application/pdf"));

        var request = handler.Requests[0];
        Assert.Equal("contoso.services.ai.azure.com", request.Uri.Host);
        Assert.StartsWith(path, request.Uri.AbsolutePath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("test-key", string.Join(' ', request.Headers.Values));
    }

    [Fact]
    public async Task CloudExtractorsAreRefusedWhenPrivacyForbidsCloud()
    {
        var handler = new RecordingHandler();
        using var client = DocumentExtractorFactory.CreateExplicit(DocumentCandidates.Mistral,
            CloudSettings() with { Privacy = new() { AllowCloud = false } }, null, FixedUploadConsent.Allow,
            new DocumentExtractorFactoryOptions { Handler = handler, ApiKey = _ => "test-key" });

        await Assert.ThrowsAsync<UploadConsentDeniedException>(() =>
            client.ExtractAsync(new MemoryStream(DocumentSourceTests.Pdf(1)), "doc", "application/pdf"));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void AddMediaStudioDocumentsRegistersRouterAndCandidates()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IUploadConsent>(FixedUploadConsent.Deny);
        services.AddMediaStudioDocuments(new ProviderSettings());
        using var provider = services.BuildServiceProvider();

        Assert.IsType<DecisionRoutingDocumentExtractionClient>(
            provider.GetRequiredKeyedService<DocumentExtractionClient>(MediaStudioDocumentKeys.Documents));
        Assert.IsType<CommunityToolkit.DocumentExtraction.PdfPig.PdfPigDocumentExtractionClient>(
            provider.GetRequiredKeyedService<DocumentExtractionClient>(MediaStudioDocumentKeys.CandidatePrefix + DocumentCandidates.PdfPig));
    }

    internal static ProviderSettings CloudSettings() => new() {
        Privacy = new() { AllowCloud = true },
        Connections = [new() { Id = "foundry", Kind = ConnectionKind.Foundry, Endpoint = "https://contoso.services.ai.azure.com/", Auth = AuthMode.ApiKey }],
        Documents = new() { Candidates = [.. DocumentCandidates.All] }
    };

    private static ExtractorCandidate Local(string name, DocumentExtractionClient client, bool images = true) =>
        new(name, name + " extractor", false, client, SupportsImages: images);

    private static ExtractorCandidate Remote(string name, DocumentExtractionClient client, IUploadConsent consent, bool allowCloud = true) =>
        new(name, name + " extractor", true, new ConsentGatedDocumentExtractionClient(client, name, consent, allowCloud));

    [Fact]
    public async Task ProbeAndBenchCommandsRouteADigitalPdfLocally()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "document-commands", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var file = Path.Combine(directory, "digital.pdf");
            await File.WriteAllBytesAsync(file, DocumentSourceTests.Pdf(2));
            var settings = Path.Combine(directory, "missing-settings.json");
            DocumentCommandServices Services(ProviderSettings _) => new(null, new ScriptedDecider(DocumentCandidates.Vlm));

            var probe = new StringWriter();
            Assert.Equal(0, await DocumentCommands.RunAsync(["documents", "probe", file, "--extract", "--settings", settings], probe, Services));
            using var json = System.Text.Json.JsonDocument.Parse(probe.ToString());
            Assert.Equal("fast-path", json.RootElement.GetProperty("plan").GetProperty("source").GetString());
            Assert.Equal(DocumentCandidates.PdfPig, json.RootElement.GetProperty("extraction").GetProperty("extractor").GetString());

            var bench = new StringWriter();
            Assert.Equal(0, await DocumentCommands.RunAsync(["bench", "documents", file, "--local-only", "--settings", settings], bench, Services));
            Assert.Contains("digital.pdf\tauto\t", bench.ToString(), StringComparison.Ordinal);
            Assert.Contains("via pdfpig", bench.ToString(), StringComparison.Ordinal);
            Assert.Equal(2, await DocumentCommands.RunAsync(["documents", "probe"], new StringWriter(), Services));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
    [Fact]
    public async Task ConfigureTestExtractsLocallyAndFlagsAMissingVisionModel()
    {
        var settings = new ProviderSettings { Vision = null, Documents = new() { Candidates = [DocumentCandidates.PdfPig, DocumentCandidates.Vlm] } };

        var checks = await new DocumentConfigurationCheck().CheckAsync(settings, cloudOnly: false, CancellationToken.None);

        Assert.Equal(RoleCheckStatus.Ok, Assert.Single(checks, c => c.Role == "documents:extract (pdfpig)").Status);
        var vlm = Assert.Single(checks, c => c.Role == "documents:vlm");
        Assert.Equal(RoleCheckStatus.Failed, vlm.Status);
        Assert.NotNull(vlm.Remediation);
        Assert.Empty(await new DocumentConfigurationCheck().CheckAsync(settings, cloudOnly: true, CancellationToken.None));
    }
    private static byte[] TablePdf()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PageSize.A4);
        for (var row = 0; row < 6; row++)
            for (var column = 0; column < 4; column++)
                page.AddText($"R{row}C{column}", 10, new PdfPoint(40 + column * 120, 700 - row * 20), font);
        return builder.Build();
    }

    internal sealed class FakeExtractor(string text = "", Exception? error = null) : DocumentExtractionClient
    {
        public int Calls { get; private set; }
        public List<string> MediaTypes { get; } = [];

        public override Task<ExtractionDocument> ExtractAsync(Stream document, string identifier, string mediaType,
            DocumentExtractionOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            MediaTypes.Add(mediaType);
            if (error is not null) throw error;
            var result = new ExtractionDocument(identifier, null!);
            var section = new ExtractionDocumentSection(null!);
            section.Elements.Add(new ExtractionDocumentTextElement(new TextContent(text), "paragraph"));
            result.Sections.Add(section);
            return Task.FromResult(result);
        }
    }

    private sealed class ScriptedDecider(string selected, params (string Id, double Value)[] probabilities) : IDecisionClient
    {
        private readonly string? provider;
        public ScriptedDecider(string selected, string provider) : this(selected) => this.provider = provider;
        public int Calls { get; private set; }
        public string[] LastCandidates { get; private set; } = [];

        public Task<DecisionResponse> GetResponseAsync(DecisionRequest request, DecisionOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            var question = (ChoiceDecisionQuestion)request.Questions[0];
            LastCandidates = question.Candidates.Select(c => c.Id).ToArray();
            var choice = LastCandidates.Contains(selected) ? selected : LastCandidates[0];
            var distribution = probabilities.Length == LastCandidates.Length && probabilities.All(p => LastCandidates.Contains(p.Id))
                ? probabilities
                : LastCandidates.Select(id => (id, id == choice ? 1.0 : 0.0)).ToArray();
            return Task.FromResult(new DecisionResponse(request,
                [new ChoiceDecisionAnswer(question.Id, choice, distribution.Select(p => new DecisionProbability(p.Item1, p.Item2)).ToArray())],
                new DecisionProvenance(provider ?? "scripted")));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class RecordingConsent(bool answer) : IUploadConsent
    {
        public List<(string, string)> Requests { get; } = [];
        public Task<bool> RequestAsync(string documentId, string providerName, CancellationToken cancellationToken = default)
        {
            Requests.Add((documentId, providerName));
            return Task.FromResult(answer);
        }
    }

    private sealed class FakeChat : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, LongText)));
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed record RecordedRequest(Uri Uri, Dictionary<string, string> Headers);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new(request.RequestUri!, request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase)));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) {
                Content = new StringContent("""{"error":{"code":"InvalidRequest","message":"fake"}}""", Encoding.UTF8, "application/json")
            });
        }
    }
}
