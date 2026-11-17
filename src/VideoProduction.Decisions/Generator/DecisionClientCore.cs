using AI = VideoProduction.Decisions.Experimental;
using MEAI = Microsoft.Extensions.AI;

namespace VideoProduction.Decisions.Experimental.Generator;

#pragma warning disable MEAI001 // The pinned upstream decision API is experimental.
internal sealed class DecisionClientCore : IDisposable
{
    private readonly IDecisionGenerator _generator;
    private readonly bool _ownsGenerator;
    private readonly AI.DecisionClientMetadata? _metadata;
    private int _disposed;

    internal DecisionClientCore(IDecisionGenerator generator, bool ownsGenerator = true)
    {
        _generator = generator ?? throw new ArgumentNullException(nameof(generator));
        _ownsGenerator = ownsGenerator;
        if (generator.GetService(typeof(DecisionGeneratorMetadata)) is DecisionGeneratorMetadata metadata)
            _metadata = new(metadata.ProviderName, metadata.ProviderUri, metadata.DefaultModelId,
                metadata.SupportedKinds?.Select(MapKind).ToArray());
    }

    internal async Task<AI.DecisionResponse> GetResponseAsync(AI.IDecisionClient client, AI.DecisionRequest request,
        AI.DecisionOptions? options = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed(client);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        if (options is not null && options.GetType() != typeof(AI.DecisionOptions))
            throw new NotSupportedException("Use DecisionOptions.RawRepresentationFactory for provider-specific options.");
        var snapshot = options?.Clone();
        object? rawOptions = snapshot?.RawRepresentationFactory?.Invoke(client);
        if (rawOptions is not null && rawOptions is not DecisionGenerationOptions)
            throw new NotSupportedException("RawRepresentationFactory must return provider DecisionGenerationOptions or null.");
        var providerOptions = (rawOptions as DecisionGenerationOptions)?.Clone() ?? new();
        if (snapshot?.ModelId is { } model) providerOptions.ModelId = model;
        if (snapshot?.AdditionalProperties is { Count: > 0 } || providerOptions.AdditionalProperties is { Count: > 0 })
            throw new NotSupportedException("These providers do not support AdditionalProperties input keys.");

        var input = new DecisionInput(request.State, request.Questions.Select(MapQuestion));
        DecisionResult result;
        try
        {
            result = await _generator.GenerateAsync(input, providerOptions, cancellationToken).ConfigureAwait(false);
            _ = new DecisionResult(input, result.ModelId, result.Answers, result.Usage);
        }
        catch (DecisionProtocolException error)
        {
            throw new AI.DecisionProtocolException(error.Message, error);
        }
        cancellationToken.ThrowIfCancellationRequested();

        var answers = request.Questions.Select((question, index) => MapAnswer(question, result.Answers[index])).ToArray();
        AI.DecisionPrecision? precision = answers.Select(answer => answer.Precision).Distinct().Count() == 1
            ? answers[0].Precision : null;
        string? responseId = result.AdditionalProperties?.TryGetValue("systemone.requestId", out var id) == true
            ? id as string : null;
        return new(request, answers,
            new AI.DecisionProvenance(_metadata?.ProviderName, _metadata?.ProviderUri, result.ModelId, responseId, precision),
            result.Usage, result.RawRepresentation, result.AdditionalProperties);
    }

    private static DecisionQuestion MapQuestion(AI.DecisionQuestion question) => question switch {
        AI.BinaryDecisionQuestion binary => new BinaryDecisionQuestion(binary.Id, binary.Instructions,
            binary.TrueDescription, binary.FalseDescription),
        AI.ChoiceDecisionQuestion choice => new ChoiceDecisionQuestion(choice.Id, choice.Instructions,
            choice.Candidates.Select(candidate => new DecisionCandidate(candidate.Id, candidate.Description))),
        AI.ScoreDecisionQuestion score => new ScoreDecisionQuestion(score.Id, score.Instructions,
            score.Levels.Select(level => level.Description)),
        _ => throw new NotSupportedException("Unsupported upstream decision question.")
    };

    private static AI.DecisionAnswer MapAnswer(AI.DecisionQuestion question, DecisionAnswer answer)
    {
        var precision = answer.Precision switch {
            DecisionPrecision.HighPrecision => AI.DecisionPrecision.HighPrecision,
            DecisionPrecision.FourDecimalPlaces => AI.DecisionPrecision.FourDecimalPlaces,
            DecisionPrecision.TwoDecimalPlaces => AI.DecisionPrecision.TwoDecimalPlaces,
            _ => throw new AI.DecisionProtocolException("Unknown provider precision.")
        };
        if (!string.Equals(question.Id, answer.QuestionId, StringComparison.Ordinal))
            throw new AI.DecisionProtocolException("Provider answer does not match the outgoing question.");
        return (question, answer) switch {
            (AI.BinaryDecisionQuestion, BinaryDecisionAnswer binary) => new AI.BinaryDecisionAnswer(
                binary.QuestionId, binary.TrueProbability, precision, binary.RawRepresentation, binary.AdditionalProperties),
            (AI.ChoiceDecisionQuestion choice, ChoiceDecisionAnswer selected) => new AI.ChoiceDecisionAnswer(
                selected.QuestionId, selected.SelectedCandidateId,
                choice.Candidates.Select(candidate => new AI.DecisionProbability(candidate.Id, selected.Probabilities[candidate.Id])).ToArray(),
                precision, selected.RawRepresentation, selected.AdditionalProperties),
            (AI.ScoreDecisionQuestion score, ScoreDecisionAnswer ranked) => new AI.ScoreDecisionAnswer(
                ranked.QuestionId, ranked.NativeScore,
                score.Levels.Select((level, index) => new AI.DecisionProbability(level.Id, ranked.Probabilities[index])).ToArray(),
                precision, ranked.RawRepresentation, ScoreProperties(ranked)),
            _ => throw new AI.DecisionProtocolException("Provider answer kind does not match the outgoing question.")
        };
    }

    private static IReadOnlyDictionary<string, object?> ScoreProperties(ScoreDecisionAnswer answer)
    {
        var properties = answer.AdditionalProperties?.Clone() ?? new MEAI.AdditionalPropertiesDictionary();
        properties.Add("decisionInference.expectedIndexLowerBound", answer.ExpectedIndexLowerBound);
        properties.Add("decisionInference.expectedIndexUpperBound", answer.ExpectedIndexUpperBound);
        return properties;
    }

    private static AI.DecisionKind MapKind(DecisionKind kind) => kind switch {
        DecisionKind.Binary => AI.DecisionKind.Binary,
        DecisionKind.Choice => AI.DecisionKind.Choice,
        DecisionKind.Score => AI.DecisionKind.Score,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    internal object? GetService(AI.IDecisionClient client, Type serviceType, object? serviceKey = null)
    {
        ThrowIfDisposed(client);
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is null)
        {
            if (serviceType == typeof(AI.DecisionClientMetadata)) return _metadata;
            if (serviceType == typeof(AI.IDecisionClient) || serviceType == client.GetType()) return client;
        }
        return _generator.GetService(serviceType, serviceKey);
    }

    private void ThrowIfDisposed(AI.IDecisionClient client) =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, client);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && _ownsGenerator) _generator.Dispose();
    }
}
