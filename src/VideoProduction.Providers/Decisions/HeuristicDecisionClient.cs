using System.Text.Json;
using VideoProduction.Decisions.Experimental;

namespace VideoProduction.Providers.Decisions;

/// <summary>
/// Deterministic, offline decision client. It needs no model, never throws for a valid request and is the last link
/// of every decision chain so the app always works with zero models installed. Choice candidates are treated as ordered
/// weakest → strongest; long, structured, tool-using or authoring work picks the strongest, short Q&amp;A the first.
/// </summary>
public sealed class HeuristicDecisionClient : IDecisionClient
{
    public const string ProviderName = "heuristic";
    public const string ModelId = "heuristic-v1";
    public const int LongInputCharacters = 2000;
    public const int LongOutputTokens = 2048;
    public const double PickProbability = 0.9;
    private static readonly DecisionClientMetadata Metadata = new(ProviderName, defaultModelId: ModelId,
        supportedKinds: [DecisionKind.Choice, DecisionKind.Binary, DecisionKind.Score]);
    private static readonly string[] HardPurposes =
        ["authoring", "video-outline", "storyboard", "document", "document-authoring"];
    private readonly Func<JsonElement, ChoiceDecisionQuestion, string?>? choiceRule;

    /// <param name="choiceRule">
    /// Optional domain rule. Return a candidate id to override the generic complexity rule, or <c>null</c> to defer to it.
    /// Unknown ids are ignored.
    /// </param>
    public HeuristicDecisionClient(Func<JsonElement, ChoiceDecisionQuestion, string?>? choiceRule = null) =>
        this.choiceRule = choiceRule;

    public Task<DecisionResponse> GetResponseAsync(DecisionRequest request, DecisionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var hard = IsComplex(request.State);
        var answers = request.Questions.Select(question => Answer(request.State, question, hard)).ToArray();
        return Task.FromResult(new DecisionResponse(request, answers,
            new DecisionProvenance(ProviderName, null, ModelId, null, DecisionPrecision.HighPrecision)));
    }

    /// <summary>True when the request state describes long, structured, tool-using or authoring work.</summary>
    public static bool IsComplex(JsonElement state)
    {
        if (state.ValueKind != JsonValueKind.Object) return Length(state) > LongInputCharacters;
        if (Bool(state, "json") || Bool(state, "tools") || Bool(state, "requireJson")) return true;
        if (Text(state, "purpose") is { } purpose && HardPurposes.Contains(purpose, StringComparer.OrdinalIgnoreCase)) return true;
        if (Number(state, "maxOutputTokens") is > LongOutputTokens) return true;
        var input = Number(state, "inputCharacters") ?? Length(state);
        return input > LongInputCharacters;
    }

    private DecisionAnswer Answer(JsonElement state, DecisionQuestion question, bool hard) => question switch {
        ChoiceDecisionQuestion choice => Choose(state, choice, hard),
        BinaryDecisionQuestion binary => new BinaryDecisionAnswer(binary.Id, 0.5),
        ScoreDecisionQuestion score => Uniform(score),
        _ => throw new DecisionProtocolException($"Unsupported decision kind '{question.Kind}'.")
    };

    private ChoiceDecisionAnswer Choose(JsonElement state, ChoiceDecisionQuestion question, bool hard)
    {
        var candidates = question.Candidates;
        var ruled = choiceRule?.Invoke(state, question);
        var pick = ruled is not null && candidates.Any(c => c.Id == ruled) ? ruled
            : hard ? candidates[^1].Id : candidates[0].Id;
        if (candidates.Count == 1) return new(question.Id, pick, [new(pick, 1)]);
        var rest = (1 - PickProbability) / (candidates.Count - 1);
        return new(question.Id, pick, candidates.Select(c => new DecisionProbability(c.Id, c.Id == pick ? PickProbability : rest)).ToArray());
    }

    private static ScoreDecisionAnswer Uniform(ScoreDecisionQuestion question)
    {
        var p = 1d / question.Levels.Count;
        return new(question.Id, (question.Levels.Count - 1) / 2d,
            question.Levels.Select(level => new DecisionProbability(level.Id, p)).ToArray());
    }

    private static bool Bool(JsonElement state, string name) =>
        state.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string? Text(JsonElement state, string name) =>
        state.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double? Number(JsonElement state, string name) =>
        state.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    private static int Length(JsonElement element) => element.ValueKind switch {
        JsonValueKind.String => element.GetString()!.Length,
        JsonValueKind.Object => element.EnumerateObject().Sum(p => Length(p.Value)),
        JsonValueKind.Array => element.EnumerateArray().Sum(Length),
        _ => 0
    };

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is not null ? null
        : serviceType == typeof(DecisionClientMetadata) ? Metadata
        : serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }
}
