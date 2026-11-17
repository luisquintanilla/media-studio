using MediaStudio.Configuration;
using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.Documents;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VideoProduction.Decisions.Experimental;

namespace MediaStudio.Sources;

/// <summary>
/// Picks an extractor per document from <see cref="DocumentProbe"/> features. Digital PDFs go straight to the local
/// text-layer reader; otherwise the decision model ranks the eligible candidates and the router tries them in order,
/// failing over on errors, declined uploads and results below <see cref="ExtractionQuality"/>.
/// </summary>
public sealed class DecisionRoutingDocumentExtractionClient : DocumentExtractionClient
{
    public const string QuestionId = "document-extractor";
    public const long MaximumBufferedBytes = 100L * 1024 * 1024;
    private readonly IReadOnlyList<ExtractorCandidate> candidates;
    private readonly IDecisionClient? decider;
    private readonly bool allowCloud;
    private readonly ILogger logger;

    public DecisionRoutingDocumentExtractionClient(IEnumerable<ExtractorCandidate> candidates, IDecisionClient? decider,
        bool allowCloud, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        this.candidates = candidates.ToArray();
        if (this.candidates.Count == 0) throw new ArgumentException("At least one extractor candidate is required.", nameof(candidates));
        if (this.candidates.Select(c => c.Name).Distinct(StringComparer.Ordinal).Count() != this.candidates.Count)
            throw new ArgumentException("Extractor candidate names must be unique.", nameof(candidates));
        this.decider = decider;
        this.allowCloud = allowCloud;
        this.logger = logger ?? NullLogger.Instance;
    }

    public IReadOnlyList<ExtractorCandidate> Candidates => candidates;

    /// <summary>The most recent routing plan, for diagnostics and the probe command.</summary>
    public DocumentRoutingPlan? LastPlan { get; private set; }

    /// <summary>Probes the document and ranks the candidates without extracting.</summary>
    public async Task<DocumentRoutingPlan> PlanAsync(DocumentProbe probe, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(probe);
        var eligible = candidates.Where(c => c.Fits(probe) && (!c.IsRemote || CloudGuard.Allows(allowCloud, true))).ToArray();
        if (eligible.Length == 0)
            return new(probe, [], "none", "No configured extractor accepts this document (type, size or privacy settings).");
        var pdfpig = eligible.FirstOrDefault(c => c.Name == DocumentCandidates.PdfPig);
        if (probe.IsDigital && pdfpig is not null)
            return new(probe, [pdfpig, .. eligible.Where(c => c != pdfpig)], "fast-path", "Digital PDF: the text layer is read locally.");

        var rules = DocumentRoutingRules.Rank(probe, eligible);
        if (eligible.Length == 1 || decider is null)
            return new(probe, rules, "rules", decider is null ? "No decision model configured; using routing rules." : "Only one extractor is eligible.");
        try
        {
            var question = new ChoiceDecisionQuestion(QuestionId,
                "Choose the document extractor most likely to return complete, accurate text for this document. Prefer local extractors when they are likely to succeed.",
                eligible.Select(c => new DecisionCandidate(c.Name, c.Description)).ToArray());
            var response = await decider.GetResponseAsync(new DecisionRequest(probe.ToState(), [question]), null, cancellationToken)
                .ConfigureAwait(false);
            if (response.Provenance?.ProviderName == "heuristic")
                return new(probe, rules, "rules", "The decision model is unavailable; using routing rules.");
            if (response.Answers.FirstOrDefault() is not ChoiceDecisionAnswer answer)
                return new(probe, rules, "rules", "The decision model returned no choice; using routing rules.");
            var probability = answer.Probabilities.ToDictionary(p => p.Id, p => p.Value, StringComparer.Ordinal);
            var ranked = eligible
                .OrderByDescending(c => c.Name == answer.SelectedCandidateId)
                .ThenByDescending(c => probability.GetValueOrDefault(c.Name))
                .ThenBy(c => Array.IndexOf(rules, c))
                .ToArray();
            return new(probe, ranked, "decision", $"Chosen by {response.Provenance?.ProviderName ?? "the decision model"}.", answer.Probabilities
                .ToDictionary(p => p.Id, p => p.Value, StringComparer.Ordinal));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogWarning(error, "Document routing decision failed; using routing rules.");
            return new(probe, rules, "rules", "The decision model failed; using routing rules.");
        }
    }

    public override async Task<ExtractionDocument> ExtractAsync(Stream document, string identifier, string mediaType,
        DocumentExtractionOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        var bytes = await Buffer(document, cancellationToken).ConfigureAwait(false);
        var probe = DocumentProbe.Analyze(bytes, mediaType);
        var plan = LastPlan = await PlanAsync(probe, cancellationToken).ConfigureAwait(false);
        if (plan.Ranked.Count == 0) throw new DocumentRoutingException(plan.Reason, []);

        var attempts = new List<string>();
        var failures = new List<Exception>();
        (ExtractionDocument Document, ExtractorCandidate Candidate, int Chars)? best = null;
        foreach (var candidate in plan.Ranked)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var input = new MemoryStream(bytes, writable: false);
                var result = await candidate.Client.ExtractAsync(input, identifier, mediaType, options, cancellationToken).ConfigureAwait(false);
                DocumentExtractionTags.Stamp(result, candidate.Name, candidate.Client);
                if (ExtractionQuality.IsAcceptable(result, probe))
                {
                    attempts.Add($"{candidate.Name}:ok");
                    result.Metadata[DocumentExtractionTags.Attempts] = string.Join(", ", attempts);
                    return result;
                }
                var chars = ExtractionQuality.TextLength(result);
                attempts.Add($"{candidate.Name}:low-quality");
                logger.LogInformation("Extractor {Extractor} returned {Chars} characters for {Pages} pages; trying the next one.",
                    candidate.Name, chars, probe.PageCount);
                if (best is null || chars > best.Value.Chars) best = (result, candidate, chars);
            }
            catch (UploadConsentDeniedException denied)
            {
                attempts.Add($"{candidate.Name}:declined");
                failures.Add(denied);
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                attempts.Add($"{candidate.Name}:failed");
                failures.Add(error);
                logger.LogWarning(error, "Extractor {Extractor} failed; trying the next one.", candidate.Name);
            }
        }
        if (best is { Chars: > 0 } kept)
        {
            kept.Document.Metadata[DocumentExtractionTags.LowQuality] = true;
            kept.Document.Metadata[DocumentExtractionTags.Attempts] = string.Join(", ", attempts);
            return kept.Document;
        }
        throw new DocumentRoutingException($"No extractor produced text ({string.Join(", ", attempts)}).", failures);
    }

    public override object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            foreach (var candidate in candidates) candidate.Client.Dispose();
        base.Dispose(disposing);
    }

    private static async Task<byte[]> Buffer(Stream document, CancellationToken cancellationToken)
    {
        using var copy = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await document.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (copy.Length + read > MaximumBufferedBytes)
                throw new DocumentRoutingException("The document is larger than the 100 MB routing limit.", []);
            copy.Write(buffer, 0, read);
        }
        return copy.ToArray();
    }
}

/// <summary>Candidates in the order the router will try them, with how the order was chosen.</summary>
/// <param name="Source"><c>fast-path</c>, <c>decision</c>, <c>rules</c> or <c>none</c>.</param>
public sealed record DocumentRoutingPlan(DocumentProbe Probe, IReadOnlyList<ExtractorCandidate> Ranked, string Source, string Reason,
    IReadOnlyDictionary<string, double>? Probabilities = null);

/// <summary>Every eligible extractor failed, was declined, or none was eligible.</summary>
public sealed class DocumentRoutingException(string message, IReadOnlyList<Exception> failures)
    : AggregateException(message, failures)
{
    public IReadOnlyList<Exception> Failures { get; } = failures;
}

/// <summary>Deterministic ordering used when no decision model answers (and as the tie-breaker when one does).</summary>
public static class DocumentRoutingRules
{
    public static ExtractorCandidate[] Rank(DocumentProbe probe, IReadOnlyList<ExtractorCandidate> eligible)
    {
        string[] order;
        var structured = probe.TableLikeLineRatio >= 0.2 || probe.FormFieldCount > 0;
        if (probe.IsDigital)
            order = [DocumentCandidates.PdfPig, DocumentCandidates.DocumentIntelligence, DocumentCandidates.ContentUnderstanding,
                DocumentCandidates.Mistral, DocumentCandidates.Vlm];
        else if (structured)
            order = [DocumentCandidates.DocumentIntelligence, DocumentCandidates.ContentUnderstanding, DocumentCandidates.Mistral,
                DocumentCandidates.Vlm, DocumentCandidates.PdfPig];
        else if (probe.PageCount > 30)
            order = [DocumentCandidates.ContentUnderstanding, DocumentCandidates.DocumentIntelligence, DocumentCandidates.Vlm,
                DocumentCandidates.Mistral, DocumentCandidates.PdfPig];
        else if (eligible.Any(c => c.IsRemote))
            order = [DocumentCandidates.Mistral, DocumentCandidates.DocumentIntelligence, DocumentCandidates.ContentUnderstanding,
                DocumentCandidates.Vlm, DocumentCandidates.PdfPig];
        else
            order = [DocumentCandidates.Vlm, DocumentCandidates.PdfPig];
        // Partly scanned PDFs still try the local text layer before anything is uploaded.
        if (!probe.IsImageMediaType && probe.ScannedRatio < 0.5)
            order = [DocumentCandidates.PdfPig, .. order.Where(o => o != DocumentCandidates.PdfPig)];
        return eligible.OrderBy(c => Array.IndexOf(order, c.Name) is var i && i < 0 ? int.MaxValue : i).ToArray();
    }
}
