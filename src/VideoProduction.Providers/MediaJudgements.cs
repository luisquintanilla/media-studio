using System.Text.Json;
using System.Text.Json.Serialization;
using VideoProduction.Decisions.Experimental;

namespace VideoProduction.Providers;

public sealed record VisualDecisionCandidate(string Id, string Description, VisualTreatment? Treatment);
public sealed record VisualDecisionState(string Task, ProposedScene Scene, GuidanceIdentity Guidance);
public sealed record VisualSuggestion(string RequestSha256, string DomainSha256, string SelectedId,
    VisualTreatment? Treatment, DecisionProbability[] Distribution, DecisionProvenance? Provenance);
public sealed record TranscriptTriageState(string Task, string IntendedText, string ObservedText,
    string ObservationSha256, string EvidenceBoundary);
public sealed record TranscriptTriage(string ObservationSha256, bool RequiresReview, double? MeaningChangeProbability,
    string Mode, string EvidenceBoundary, DecisionProvenance? Provenance);

public sealed class MediaJudgements : IDisposable
{
    private readonly Lazy<IDecisionClient> client;
    public MediaJudgements(Func<IDecisionClient> factory) => client = new(factory);
    public MediaJudgements(DecisionProviderConfiguration configuration) : this(() => Decisions.DecisionClientFactory.Create(configuration)) { }

    public static VisualDecisionCandidate[] EligibleVisuals(ProposedScene scene)
    {
        ChatVideoPlanner.Validate(new("Visual review", [scene]), new(300, 1));
        Guard.Require(scene.Motion is null, "decision.motion",
            "Remove the authored animation explicitly before asking Decisions to replace it with a plain/text preset.");
        var candidates = new List<VisualDecisionCandidate>
        {
            new("plain", "Keep the plain trusted template; do not invent a relationship or comparative evidence.", null)
        };
        if (scene.Kind == TemplateKind.Title || scene.IllustrationPrompt is not null) return candidates.ToArray();
        var lines = scene.Text.Split('\n');
        var kind = scene.Kind == TemplateKind.Code ? SceneKind.Code : SceneKind.Diagram;
        foreach (var preset in Enum.GetValues<VisualPreset>())
        {
            VisualAnchor[] anchors;
            if (preset == VisualPreset.ArchitectureFlowEmphasis)
                anchors = lines.Select((_, i) => new VisualAnchor($"node-{i + 1}", i + 1, i + 1)).ToArray();
            else if (preset == VisualPreset.BeforeAfterComparison)
            {
                var middle = (lines.Length + 1) / 2;
                if (lines.Length < 2) continue;
                anchors = [new("before", 1, middle), new("after", middle + 1, lines.Length)];
            }
            else
            {
                var middle = (lines.Length + 1) / 2;
                anchors = lines.Length == 1 ? [new("focus", 1, 1)]
                    : [new("context", 1, middle), new("change", middle + 1, lines.Length)];
            }
            var treatment = new VisualTreatment(preset, anchors,
                anchors.Select((anchor, i) => new VisualReveal(anchor.Id, i * 30L, (i + 1) * 30L)).ToArray());
            try { Visuals.Validate(treatment, kind, scene.Text); }
            catch (VideoException error) when (error.Code.StartsWith("visual.", StringComparison.Ordinal)) { continue; }
            var id = preset switch {
                VisualPreset.ArchitectureFlowEmphasis => "architectureFlowEmphasis",
                VisualPreset.ProgressiveCodeFocus => "progressiveCodeFocus",
                _ => "beforeAfterComparison"
            };
            var description = preset switch {
                VisualPreset.ArchitectureFlowEmphasis => "The explanation's mechanism is an ordered relationship/flow among the exact authored nodes.",
                VisualPreset.ProgressiveCodeFocus => "The mechanism is code context and a specific change; focus exact inert code ranges, never execute them.",
                _ => "The authored text contains a genuine qualified before/after comparison, not invented benchmark data or decoration."
            };
            candidates.Add(new(id, description, treatment));
        }
        return candidates.ToArray();
    }

    public async Task<VisualSuggestion> SuggestVisual(ProposedScene scene, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        scene = JsonSerializer.Deserialize<ProposedScene>(Json.Text(scene), Json.Options)
            ?? throw new VideoException("decision.input", "An explicit bounded scene is required.");
        var candidates = EligibleVisuals(scene);
        var state = new VisualDecisionState("suggest-renderer-eligible-treatment-for-review", scene, TeachingGuidance.Identity);
        var question = new ChoiceDecisionQuestion("visual-treatment",
            "Choose only an eligible treatment that explains the authored teaching mechanism. Plain is preferable to a decorative/unsupported visual. This is a review proposal, never production or evidence authority.",
            candidates.Select(c => new DecisionCandidate(c.Id, c.Description)).ToArray());
        var requestSha = Files.Fingerprint(state);
        var domainSha = Files.Fingerprint(candidates);
        if (candidates.Length == 1)
            return new(requestSha, domainSha, "plain", null, [], null);
        var response = await client.Value.GetResponseAsync(state, MediaJudgementJson.Default.VisualDecisionState,
            [question], cancellationToken: ct);
        ct.ThrowIfCancellationRequested();
        var answer = (ChoiceDecisionAnswer)response.GetAnswer(question.Id);
        var selected = candidates.Single(c => c.Id == answer.SelectedCandidateId);
        Guard.Require(answer.Probabilities.Single(p => p.Id == selected.Id).Value
            == answer.Probabilities.Max(p => p.Value), "decision.protocol",
            "Selected visual disagrees with the complete distribution; nothing was applied.");
        return new(requestSha, domainSha, selected.Id, selected.Treatment, answer.Probabilities.ToArray(), response.Provenance);
    }

    public async Task<TranscriptTriage> Triage(TranscriptLearning observation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        observation = JsonSerializer.Deserialize<TranscriptLearning>(Json.Text(observation), Json.Options)
            ?? throw new VideoException("decision.input", "An explicit observed transcript report is required.");
        Guard.Require(observation.SchemaVersion == 1 && Files.ValidHash(observation.MasterSha256)
            && Files.ValidHash(observation.RawResultSha256) && observation.MasterSampleRate > 0
            && observation.MasterSampleCount > 0 && !string.IsNullOrWhiteSpace(observation.IntendedScript)
            && !string.IsNullOrWhiteSpace(observation.ObservedText)
            && observation.IntendedScript.Length <= 32000 && observation.ObservedText.Length <= 32000
            && observation.Segments is { Length: > 0 and <= 1000 },
            "decision.input", "Missing/empty observed evidence is not a low-confidence observation.");
        var sha = Files.Fingerprint(observation);
        const string boundary = "Advisory text comparison of a reviewer-supplied observed report; not acoustic verification, caption adoption, pass/fail, release or production approval.";
        var mismatch = Transcription.Compare(observation.IntendedScript, observation.ObservedText).RequiresReview;
        if (!mismatch) return new(sha, false, null, "exact-normalized-text-no-inference", boundary, null);
        var state = new TranscriptTriageState("advisory-transcript-discrepancy-triage",
            observation.IntendedScript, observation.ObservedText, sha, boundary);
        var question = new BinaryDecisionQuestion("possible-meaning-change",
            "Does the observed text plausibly change the intended explanation's technical meaning, omit a material qualifier, or contradict it? Judge only supplied text. Always retain the deterministic discrepancy and require human review; never certify audio.");
        var response = await client.Value.GetResponseAsync(state, MediaJudgementJson.Default.TranscriptTriageState,
            [question], cancellationToken: ct);
        ct.ThrowIfCancellationRequested();
        return new(sha, true, ((BinaryDecisionAnswer)response.GetAnswer(question.Id)).TrueProbability,
            "native-advisory-only", boundary, response.Provenance);
    }
    public void Dispose() { if (client.IsValueCreated) client.Value.Dispose(); }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(VisualDecisionState))]
[JsonSerializable(typeof(TranscriptTriageState))]
internal partial class MediaJudgementJson : JsonSerializerContext;
