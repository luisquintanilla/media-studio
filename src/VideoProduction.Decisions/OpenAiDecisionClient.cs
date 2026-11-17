using Microsoft.Extensions.AI;
#if OPENAI_DECISIONS
using OpenAI.Decisions;
#endif
using System.ClientModel;
using System.Text.Json;
using VideoProduction.Decisions.Experimental;
using NeutralScoreLevel = VideoProduction.Decisions.Experimental.DecisionScoreLevel;

#pragma warning disable OPENAI001, VPD001
namespace VideoProduction.Decisions;

public sealed class DecisionRefusedException(string questionId)
    : Exception($"Decision provider refused question '{questionId}'; no partial batch was accepted.")
{
    public string QuestionId { get; } = questionId;
}

#if OPENAI_DECISIONS
public sealed class OpenAiDecisionClient(DecisionsClient client, string model,
    DecisionPrecision precision = DecisionPrecision.TwoDecimalPlaces, bool stateAsText = false) : IDecisionClient
{
    public async Task<DecisionResponse> GetResponseAsync(DecisionRequest request, DecisionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        if (options?.RawRepresentationFactory is not null || options?.AdditionalProperties is { Count: > 0 })
            throw new NotSupportedException("Opaque decision options are not supported by this typed adapter.");
        var creation = new DecisionCreationOptions
        {
            Model = options?.ModelId ?? model,
            InputItems = { new DecisionsUserMessageItem
            {
                Content = { stateAsText
                    ? new DecisionsInputTextContentPart(request.State.ValueKind == JsonValueKind.String
                        ? request.State.GetString()! : request.State.GetRawText())
                    : new DecisionsInputJsonContentPart([BinaryData.FromString(request.State.GetRawText())]) }
            } }
        };
        foreach (var question in request.Questions)
            creation.Questions.Add(MapQuestion(question));
        cancellationToken.ThrowIfCancellationRequested();
        ClientResult<DecisionResource> response;
        try { response = await client.CreateDecisionAsync(creation, cancellationToken); }
        catch (ClientResultException error)
        {
            throw new DecisionClientException("OpenAI decision request failed; no alternate provider was called.",
                isTransient: error.Status is 408 or 429 or >= 500, error);
        }
        cancellationToken.ThrowIfCancellationRequested();
        ValidateWire(response.GetRawResponse().Content, request);
        var resource = response.Value;
        if (resource.Answers.Count != request.Questions.Count || string.IsNullOrWhiteSpace(resource.Model))
            throw new DecisionProtocolException("Decision response must name its model and answer the complete batch.");
        var answers = new List<DecisionAnswer>();
        for (var index = 0; index < request.Questions.Count; index++)
        {
            var question = request.Questions[index];
            var native = resource.Answers[index];
            if (native is DecisionsRefusalAnswer refusal)
            {
                if (refusal.Name != question.Id)
                    throw new DecisionProtocolException("Refusal is missing the exact requested question identity.");
                throw new DecisionRefusedException(refusal.Name);
            }
            answers.Add((question, native) switch
            {
                (BinaryDecisionQuestion binary, DecisionsPredicateAnswer predicate) when predicate.Name == binary.Id =>
                    new BinaryDecisionAnswer(binary.Id, predicate.Probability, precision, predicate),
                (ChoiceDecisionQuestion choice, DecisionsChoiceAnswer answer) when answer.Name == choice.Id =>
                    new ChoiceDecisionAnswer(choice.Id, StringChoice(answer.Choice),
                        answer.Probabilities.Select(p => new DecisionProbability(StringChoice(p.Value), p.Probability)).ToArray(),
                        precision, answer, Confidence(answer.Confidence)),
                (ScoreDecisionQuestion score, DecisionsScoreAnswer answer) when answer.Name == score.Id =>
                    Score(score, answer),
                _ => throw new DecisionProtocolException("Unknown, mismatched or uncorrelated typed decision answer.")
            });
        }

        static void ValidateWire(BinaryData content, DecisionRequest request)
        {
            using var document = JsonDocument.Parse(content.ToMemory());
            var root = document.RootElement;
            if (!root.TryGetProperty("answers", out var answers) || answers.ValueKind != JsonValueKind.Array
                || answers.GetArrayLength() != request.Questions.Count)
                throw new DecisionProtocolException("The raw response must contain the complete answer batch.");
            static string Text(JsonElement value, string name) =>
                value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(property.GetString()) ? property.GetString()!
                    : throw new DecisionProtocolException($"Required decision field '{name}' is absent or not a string.");
            static void Number(JsonElement value, string name)
            {
                if (!value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Number
                    || !property.TryGetDouble(out var number) || !double.IsFinite(number))
                    throw new DecisionProtocolException($"Required decision number '{name}' is absent or invalid; SDK defaults are not observations.");
            }
            for (var index = 0; index < answers.GetArrayLength(); index++)
            {
                var answer = answers[index];
                if (answer.ValueKind != JsonValueKind.Object || Text(answer, "name") != request.Questions[index].Id)
                    throw new DecisionProtocolException("Raw answer/refusal must correlate to the exact requested question.");
                var type = Text(answer, "type");
                if (type == "refusal") continue;
                if (type == "predicate") { Number(answer, "probability"); continue; }
                if (type is not ("choice" or "score"))
                    throw new DecisionProtocolException("Unknown raw decision answer type.");
                Number(answer, "confidence");
                if (type == "score") Number(answer, "score");
                else _ = Text(answer, "choice");
                if (!answer.TryGetProperty("probabilities", out var probabilities) || probabilities.ValueKind != JsonValueKind.Array)
                    throw new DecisionProtocolException("Required decision distribution is absent.");
                foreach (var probability in probabilities.EnumerateArray())
                {
                    Number(probability, "probability");
                    if (type == "score") { Number(probability, "value"); _ = Text(probability, "label"); }
                    else _ = Text(probability, "value");
                }
            }
        }
        return new DecisionResponse(request, answers,
            new DecisionProvenance("OpenAI .NET 2.15.0 / experimental Layer 1 Decisions", client.Endpoint,
                resource.Model, precision: precision),
            resource.Usage is { } usage ? new UsageDetails
            {
                InputTokenCount = usage.InputTokenCount, OutputTokenCount = usage.OutputTokenCount,
                TotalTokenCount = usage.TotalTokenCount
            } : null,
            resource);
    }

    private ScoreDecisionAnswer Score(ScoreDecisionQuestion question, DecisionsScoreAnswer answer)
    {
        if (answer.Probabilities.Count != question.Levels.Count)
            throw new DecisionProtocolException("Score distribution omitted a rubric level.");
        var probabilities = new DecisionProbability[question.Levels.Count];
        foreach (var item in answer.Probabilities)
        {
            if (item.Value < 0 || item.Value >= probabilities.Length || probabilities[item.Value] is not null
                || item.Label != question.Levels[checked((int)item.Value)].Id)
                throw new DecisionProtocolException("Score labels or ordinal values differ from the requested rubric.");
            probabilities[item.Value] = new(item.Label, item.Probability);
        }
        return new(question.Id, answer.Score, probabilities, precision, answer, Confidence(answer.Confidence));
    }

    private static IReadOnlyDictionary<string, object?> Confidence(double confidence)
    {
        if (!double.IsFinite(confidence) || confidence is < 0 or > 1)
            throw new DecisionProtocolException("Native confidence must be a finite value in [0,1].");
        return new Dictionary<string, object?> { ["nativeConfidence"] = confidence };
    }

    private static string StringChoice(DecisionsChoiceValue value) =>
        value.StringValue is { } text && value.BooleanValue is null ? text
            : throw new DecisionProtocolException("The choice value must preserve the exact caller string ID.");

    private static DecisionsQuestion MapQuestion(DecisionQuestion question)
    {
        switch (question)
        {
            case BinaryDecisionQuestion binary:
                return new DecisionsPredicateQuestion
                {
                    Name = binary.Id,
                    Instructions = { new DecisionsInputTextInstructionsPart
                    {
                        Text = binary.Instructions
                            + (binary.TrueDescription is null ? "" : "\nTrue: " + binary.TrueDescription)
                            + (binary.FalseDescription is null ? "" : "\nFalse: " + binary.FalseDescription)
                    } }
                };
            case ChoiceDecisionQuestion choice:
                var nativeChoice = new DecisionsChoiceQuestion
                {
                    Name = choice.Id,
                    Instructions = { new DecisionsInputTextInstructionsPart { Text = choice.Instructions } }
                };
                foreach (var candidate in choice.Candidates)
                    nativeChoice.Choices.Add(new DecisionsChoice
                        { Value = new DecisionsChoiceValue(candidate.Id), Description = candidate.Description });
                return nativeChoice;
            case ScoreDecisionQuestion score:
                var nativeScore = new DecisionsScoreQuestion
                {
                    Name = score.Id,
                    Instructions = { new DecisionsInputTextInstructionsPart { Text = score.Instructions } }
                };
                foreach (NeutralScoreLevel level in score.Levels)
                    nativeScore.Levels.Add(new DecisionsScoreLevel { Label = level.Id, Description = level.Description });
                return nativeScore;
            default:
                throw new NotSupportedException("Only explicit binary, choice and score questions are supported.");
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    }
    public void Dispose() { }
}
#endif
