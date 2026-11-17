using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.Tokenizers;
using AI = VideoProduction.Decisions.Experimental;
using VideoProduction.Decisions.Experimental.Generator;

namespace VideoProduction.Decisions.Experimental.Julia;

#pragma warning disable MEAI001 // The pinned upstream decision API is experimental.
public sealed record JuliaDecisionClientOptions
{
    public required string ModelPath { get; init; }
    public required string TokenizerJsonPath { get; init; }
    public Action<SessionOptions>? ConfigureSession { get; init; }

    internal JuliaDecisionGeneratorOptions ToGeneratorOptions() =>
        new() { ModelPath = ModelPath, TokenizerJsonPath = TokenizerJsonPath, ConfigureSession = ConfigureSession };
}

/// <summary>Native MEAI Julia client using the existing lossless prepare, score and decode stages.</summary>
public sealed class JuliaDecisionClient : AI.IDecisionClient
{
    public const string ModelId = JuliaDecisionGenerator.ModelId;
    private readonly DecisionClientCore _core;

    private JuliaDecisionClient(JuliaDecisionGenerator generator) => _core = new(generator);

    public JuliaDecisionClient(string modelPath, string tokenizerJsonPath, SessionOptions? sessionOptions = null)
        : this(new JuliaDecisionGenerator(modelPath, tokenizerJsonPath, sessionOptions))
    {
    }

    public JuliaDecisionClient(Tokenizer tokenizer, IEnumerable<string> reservedTokens, IEnumerable<long> reservedIds,
        ScoreOnnxDecisionModel score, DecodeDecisions decode, bool ownsStages = false)
        : this(tokenizer, reservedTokens, reservedIds, (IJuliaDecisionScorer)score, decode, ownsStages)
    {
    }

    internal JuliaDecisionClient(Tokenizer tokenizer, IEnumerable<string> reservedTokens, IEnumerable<long> reservedIds,
        IJuliaDecisionScorer score, DecodeDecisions decode, bool ownsStages = false)
        : this(new PrepareDecisionInputs(tokenizer, reservedTokens, reservedIds, ownsTokenizer: ownsStages),
            score, decode, ownsStages)
    {
    }

    public JuliaDecisionClient(PrepareDecisionInputs prepare, ScoreOnnxDecisionModel score,
        DecodeDecisions decode, bool ownsStages = false)
        : this(new JuliaDecisionGenerator(prepare, score, decode, ownsStages))
    {
    }

    internal JuliaDecisionClient(PrepareDecisionInputs prepare, IJuliaDecisionScorer score,
        DecodeDecisions decode, bool ownsStages = false)
        : this(new JuliaDecisionGenerator(prepare, score, decode, ownsStages))
    {
    }

    /// <summary>Loads explicit local model.onnx and tokenizer.json; never downloads files.</summary>
    public static JuliaDecisionClient LoadFromDirectory(string modelDirectory,
        Action<SessionOptions>? configureSession = null)
    {
        string directory = JuliaModelAssets.VerifyDirectory(modelDirectory);
        using var sessionOptions = new SessionOptions();
        configureSession?.Invoke(sessionOptions);
        var tokenizer = new JuliaTokenizer(Path.Combine(directory, "tokenizer.json"));
        ScoreOnnxDecisionModel? score = null;
        try
        {
            score = ScoreOnnxDecisionModel.Load(Path.Combine(directory, "model.onnx"), sessionOptions);
            return new(tokenizer, tokenizer.ReservedTokens, tokenizer.ReservedIds, score, new(), ownsStages: true);
        }
        catch { score?.Dispose(); tokenizer.Dispose(); throw; }
    }

    public static JuliaDecisionClient Load(JuliaDecisionClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new(JuliaDecisionGenerator.Load(options.ToGeneratorOptions()));
    }

    public Task<AI.DecisionResponse> GetResponseAsync(AI.DecisionRequest request,
        AI.DecisionOptions? options = null, CancellationToken cancellationToken = default) =>
        _core.GetResponseAsync(this, request, options, cancellationToken);

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        _core.GetService(this, serviceType, serviceKey);

    public void Dispose() => _core.Dispose();
}
