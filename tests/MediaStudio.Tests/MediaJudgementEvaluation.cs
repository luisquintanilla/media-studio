using System.Diagnostics;
using System.Text.Json;
using VideoProduction;
using VideoProduction.Decisions.Experimental;
using VideoProduction.Providers;

internal sealed record JudgementExample(string Id, string Family, TemplateKind? Kind = null, string? Text = null,
    string? Mechanism = null, string[]? AcceptedVisuals = null, string? Intended = null, string? Observed = null,
    bool? MeaningChange = null);
internal sealed record JudgementDataset(int SchemaVersion, string Task, string Split, string LabelsVersion,
    string EvidenceBoundary, JudgementExample[] Cases);
internal sealed record JudgementPolicy(int SchemaVersion, string FixtureVersion, double VisualConfidenceMinimum,
    double VisualMarginMinimum, double BinaryUncertainLow, double BinaryUncertainHigh);
internal sealed record JudgementLock(int SchemaVersion, string FixtureSha256, string PolicySha256,
    Dictionary<string, string> DatasetSha256);
internal sealed record JudgementCaseResult(string Id, string Task, string InputSha256, string? DomainSha256,
    string? SelectedId, DecisionProbability[] Distribution, double? MeaningChangeProbability, bool RequiresReview,
    bool Uncertain, bool? LabelError, bool ReviewInvariantError, int DecisionCalls, long ObservedHarnessTicks);
internal sealed record JudgementMetrics(string Task, int Cases, int DecisionCalls, int UncertainCases,
    int LabelledCases, int LabelErrors, double? LabelErrorRate, int ReviewInvariantErrors);
internal sealed record JudgementEvaluationReport(int SchemaVersion, string Split, string Mode, string EvidenceBoundary,
    string LockSha256, string PolicySha256, string FixtureSha256, string? DevelopmentReportSha256,
    long StopwatchFrequency, string TimingBoundary, object? ProviderUsage, bool RealModelQualityMeasured,
    JudgementMetrics[] Metrics, JudgementCaseResult[] Cases);

internal static class MediaJudgementEvaluation
{
    public const string Boundary = "Authored synthetic text/scene examples; mechanical fixture, not live model quality, calibrated probabilities or acoustic evidence.";
    private const string Labels = "synthetic-review-labels-v1";

    public static async Task<JudgementEvaluationReport> Run(string datasetRoot, string split, string output,
        string? developmentReport = null)
    {
        Guard.Require(split is "development" or "locked-heldout", "evaluation.split", "Use development or locked-heldout.");
        var locked = Json.Read<JudgementLock>(Path.Combine(datasetRoot, "lock-v1.json"));
        var policyPath = Path.Combine(datasetRoot, "policy-v1.json");
        var fixturePath = Path.Combine(datasetRoot, "..", "MediaJudgementEvaluation.cs");
        Guard.Require(locked.SchemaVersion == 1 && locked.FixtureSha256 == Files.Hash(fixturePath)
            && locked.PolicySha256 == Files.Hash(policyPath), "evaluation.lock", "The frozen fixture/policy changed; do not tune against held-out examples.");
        var expectedNames = new[] { "visual-development-v1.json", "visual-heldout-v1.json",
            "triage-development-v1.json", "triage-heldout-v1.json" };
        Guard.Require(locked.DatasetSha256.Keys.Order().SequenceEqual(expectedNames.Order()), "evaluation.lock", "The lock must cover exactly both tasks and both splits.");
        foreach (var name in expectedNames)
            Guard.Require(locked.DatasetSha256[name] == Files.Hash(Path.Combine(datasetRoot, name)),
                "evaluation.lock", "A versioned dataset changed; frozen held-out data cannot be edited in place.");
        var policy = Json.Read<JudgementPolicy>(policyPath);
        Guard.Require(policy.SchemaVersion == 1 && policy.FixtureVersion == "mechanical-v1"
            && policy.VisualConfidenceMinimum is > 0 and <= 1 && policy.VisualMarginMinimum is >= 0 and <= 1
            && policy.BinaryUncertainLow is > 0 and < .5 && policy.BinaryUncertainHigh is > .5 and < 1,
            "evaluation.policy", "Invalid versioned uncertainty policy.");
        var sets = expectedNames.Select(n => Json.Read<JudgementDataset>(Path.Combine(datasetRoot, n))).ToArray();
        ValidateDatasets(sets);
        var lockSha = Files.Hash(Path.Combine(datasetRoot, "lock-v1.json"));
        string? developmentSha = null;
        if (split == "locked-heldout")
        {
            Guard.Require(developmentReport is not null, "evaluation.development", "Run the frozen policy on development before opening held-out results.");
            var earlier = Json.Read<JudgementEvaluationReport>(developmentReport!);
            Guard.Require(earlier.SchemaVersion == 1 && earlier.Split == "development"
                && earlier.Mode == "synthetic-mechanical-baseline-v1" && !earlier.RealModelQualityMeasured
                && earlier.LockSha256 == lockSha && earlier.PolicySha256 == locked.PolicySha256
                && earlier.FixtureSha256 == locked.FixtureSha256
                && earlier.Cases.Select(c => c.Id).Order().SequenceEqual(sets.Where(s => s.Split == "development")
                    .SelectMany(s => s.Cases).Select(c => c.Id).Order()),
                "evaluation.development", "Development evidence does not bind this exact frozen baseline and case set.");
            developmentSha = Files.Hash(developmentReport!);
        }
        var results = new List<JudgementCaseResult>();
        foreach (var set in sets.Where(s => s.Split == split))
        {
            foreach (var item in set.Cases)
            {
                using var fixture = new MechanicalDecision();
                using var judgement = new MediaJudgements(() => fixture);
                var watch = Stopwatch.StartNew();
                if (set.Task == "visual-choice")
                {
                    var suggestion = await judgement.SuggestVisual(Scene(item), default);
                    var sorted = suggestion.Distribution.OrderByDescending(p => p.Value).ToArray();
                    var uncertain = sorted.Length > 0 && (sorted[0].Value < policy.VisualConfidenceMinimum
                        || sorted[0].Value - sorted[1].Value < policy.VisualMarginMinimum);
                    results.Add(new(item.Id, set.Task, suggestion.RequestSha256, suggestion.DomainSha256,
                        suggestion.SelectedId, suggestion.Distribution, null, false, uncertain,
                        !item.AcceptedVisuals!.Contains(suggestion.SelectedId, StringComparer.Ordinal), false,
                        fixture.Calls, watch.ElapsedTicks));
                }
                else
                {
                    var triage = await judgement.Triage(Observation(item), default);
                    var probability = triage.MeaningChangeProbability;
                    results.Add(new(item.Id, set.Task, triage.ObservationSha256, null, null, [], probability,
                        triage.RequiresReview, probability >= policy.BinaryUncertainLow && probability <= policy.BinaryUncertainHigh,
                        probability is null ? null : (probability >= .5) != item.MeaningChange,
                        triage.RequiresReview != Transcription.Compare(item.Intended!, item.Observed!).RequiresReview,
                        fixture.Calls, watch.ElapsedTicks));
                }
            }
        }
        Guard.Require(results.All(r => !r.ReviewInvariantError), "evaluation.review", "Advisory triage must never clear a deterministic discrepancy.");
        var metrics = results.GroupBy(r => r.Task).Select(group =>
        {
            var labelled = group.Count(r => r.LabelError is not null);
            var errors = group.Count(r => r.LabelError == true);
            return new JudgementMetrics(group.Key, group.Count(), group.Sum(r => r.DecisionCalls),
                group.Count(r => r.Uncertain), labelled, errors, labelled == 0 ? null : (double)errors / labelled,
                group.Count(r => r.ReviewInvariantError));
        }).ToArray();
        var report = new JudgementEvaluationReport(1, split, "synthetic-mechanical-baseline-v1", Boundary,
            lockSha, locked.PolicySha256, locked.FixtureSha256, developmentSha, Stopwatch.Frequency,
            "Observed local harness/fixture elapsed ticks, including validation/serialization; not provider latency or token usage.",
            null, false, metrics, results.ToArray());
        Json.Write(output, report);
        return report;
    }

    internal static void ValidateDatasets(JudgementDataset[] sets)
    {
        Guard.Require(sets.Length == 4 && sets.Select(s => (s.Task, s.Split)).Distinct().Count() == 4,
            "evaluation.dataset", "Both tasks need exactly one development and one locked-heldout dataset.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var families = new HashSet<string>(StringComparer.Ordinal);
        var inputs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var set in sets)
        {
            Guard.Require(set.SchemaVersion == 1 && set.Task is "visual-choice" or "transcript-triage"
                && set.Split is "development" or "locked-heldout" && set.LabelsVersion == Labels
                && set.EvidenceBoundary == Boundary && set.Cases is { Length: > 0 and <= 100 },
                "evaluation.dataset", "Invalid dataset/task/label version or missing synthetic boundary.");
            foreach (var item in set.Cases)
            {
                Guard.Require(!string.IsNullOrWhiteSpace(item.Id) && !string.IsNullOrWhiteSpace(item.Family)
                    && ids.Add(item.Id) && families.Add(item.Family), "evaluation.overlap", "Case IDs and source families must be disjoint.");
                if (set.Task == "visual-choice")
                {
                    Guard.Require(item.Kind is not null && !string.IsNullOrWhiteSpace(item.Text)
                        && !string.IsNullOrWhiteSpace(item.Mechanism) && item.AcceptedVisuals is { Length: > 0 }
                        && item.Intended is null && item.Observed is null && item.MeaningChange is null,
                        "evaluation.label", "Visual examples require scene-only input and nonempty eligible target labels.");
                    var eligible = MediaJudgements.EligibleVisuals(Scene(item)).Select(c => c.Id).ToArray();
                    Guard.Require(item.AcceptedVisuals!.Distinct().Count() == item.AcceptedVisuals!.Length
                        && item.AcceptedVisuals!.All(label => eligible.Contains(label, StringComparer.Ordinal)),
                        "evaluation.label", "A visual target label is not renderer-eligible.");
                    Guard.Require(inputs.Add(Files.Fingerprint(new { item.Kind, Text = Normalize(item.Text!), Mechanism = Normalize(item.Mechanism!) })),
                        "evaluation.overlap", "Duplicate normalized visual input across splits.");
                }
                else
                {
                    Guard.Require(item.Kind is null && item.Text is null && item.Mechanism is null && item.AcceptedVisuals is null
                        && !string.IsNullOrWhiteSpace(item.Intended) && !string.IsNullOrWhiteSpace(item.Observed)
                        && item.MeaningChange is not null && item.Intended.Length <= 32000 && item.Observed.Length <= 32000
                        && (Transcription.Compare(item.Intended, item.Observed).RequiresReview || item.MeaningChange == false),
                        "evaluation.label", "Triage requires bounded supplied text and a meaning label; exact text cannot be labelled changed.");
                    Guard.Require(inputs.Add(Files.Fingerprint(new { Intended = Normalize(item.Intended!), Observed = Normalize(item.Observed!) })),
                        "evaluation.overlap", "Duplicate normalized transcript input across splits.");
                }
            }
        }
    }

    private static string Normalize(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
    private static ProposedScene Scene(JudgementExample item) => new(item.Kind!.Value, "Synthetic review example",
        item.Text!, "This inert example explains the authored relationship.",
        Teaching: new("Explain the supplied mechanism", "The exact supplied text", "Make the relationship legible",
            item.Mechanism!, "Synthetic teaching example; not measured production evidence"));

    private static TranscriptLearning Observation(JudgementExample item)
    {
        var hash = Files.Fingerprint(new { item.Id, item.Intended, item.Observed, Boundary });
        return new(1, "synthetic.wav", hash, 16000, 16000, "derived.wav", hash, 16000, 16000,
            "Synthetic sample-clock fixture, not recorded audio", "synthetic-typed-fixture-not-STT", "fixture", "fixture",
            hash, [], hash, hash, item.Intended!, "", item.Observed!,
            [new(0, 1000, 0, 16000, item.Observed!, Evidence.AsrSegment)],
            Transcription.Compare(item.Intended!, item.Observed!));
    }

    private sealed class MechanicalDecision : IDecisionClient
    {
        public int Calls { get; private set; }
        public Task<DecisionResponse> GetResponseAsync(DecisionRequest request, DecisionOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            var question = request.Questions.Single();
            DecisionAnswer answer;
            if (question is ChoiceDecisionQuestion choice)
            {
                var mechanism = request.State.GetProperty("scene").GetProperty("teaching").GetProperty("mechanism").GetString()!.ToLowerInvariant();
                var preferred = mechanism.Contains("before/after", StringComparison.Ordinal) ? "beforeAfterComparison"
                    : mechanism.Contains("flow", StringComparison.Ordinal) ? "architectureFlowEmphasis"
                    : mechanism.Contains("change", StringComparison.Ordinal) ? "progressiveCodeFocus" : "plain";
                var selected = choice.Candidates.Any(c => c.Id == preferred) ? preferred : "plain";
                var confidence = mechanism.Contains("uncertain", StringComparison.Ordinal) ? .55 : .85;
                answer = new ChoiceDecisionAnswer(question.Id, selected, choice.Candidates.Select(c =>
                    new DecisionProbability(c.Id, c.Id == selected ? confidence : (1 - confidence) / (choice.Candidates.Count - 1))).ToArray());
            }
            else
            {
                var intended = request.State.GetProperty("intendedText").GetString()!.ToLowerInvariant();
                var observed = request.State.GetProperty("observedText").GetString()!.ToLowerInvariant();
                var probability = observed.Contains("unclear", StringComparison.Ordinal) ? .5
                    : intended.Contains("not ", StringComparison.Ordinal) != observed.Contains("not ", StringComparison.Ordinal)
                        || intended.Contains("only", StringComparison.Ordinal) && observed.Contains("every", StringComparison.Ordinal)
                        || intended.Contains("may", StringComparison.Ordinal) && observed.Contains("always", StringComparison.Ordinal) ? .9 : .15;
                answer = new BinaryDecisionAnswer(question.Id, probability);
            }
            return Task.FromResult(new DecisionResponse(request, [answer]));
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
