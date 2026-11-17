using System.Diagnostics;
using System.Numerics.Tensors;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using VideoProduction.Decisions.Experimental;
using VideoProduction.Decisions.Experimental.Julia;
using Tokenizers.HuggingFace.Tokenizer;

internal static class MappingSanity
{
    internal static async Task Run(string directory, string output, string? nativeRequestPath = null)
    {
        directory = JuliaModelAssets.VerifyDirectory(directory);
        using var prepare = new PrepareDecisionInputs(Path.Combine(directory, "tokenizer.json"));
        using var options = new Microsoft.ML.OnnxRuntime.SessionOptions { IntraOpNumThreads = 2, InterOpNumThreads = 1 };
        using var score = ScoreOnnxDecisionModel.Load(Path.Combine(directory, "model.onnx"), options);
        using var trace = new NativeTracingScorer(score);
        using var client = new JuliaDecisionClient(prepare, trace, new DecodeDecisions());
        using var tokenizer = Tokenizer.FromFile(Path.Combine(directory, "tokenizer.json"));
        var observations = new List<MappingObservation>();
        MappingCandidate[] sentiments =
        [
            new("sentiment-positive", "The text expresses positive sentiment."),
            new("sentiment-negative", "The text expresses negative sentiment.")
        ];
        MappingCandidate[] planning =
        [
            new("planning-fast", "Straightforward storyboard planning with few constraints."),
            new("planning-strong", "Demanding storyboard planning with complex constraints.")
        ];
        await Observe("positive", ContractTests.State("I love this wonderful movie. It was delightful and made me happy."),
            "What is the sentiment of the text?", sentiments, "sentiment-positive");
        await Observe("negative", ContractTests.State("I hate this terrible movie. It was awful and made me miserable."),
            "What is the sentiment of the text?", sentiments, "sentiment-negative");
        string? nativeRequestSha256 = null;
        if (nativeRequestPath is not null)
        {
            byte[] bytes = File.ReadAllBytes(nativeRequestPath);
            nativeRequestSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            var request = root.TryGetProperty("actualRequest", out var actual) ? actual
                : root.TryGetProperty("request", out var wrapped) ? wrapped : root;
            var question = request.GetProperty("questions").EnumerateArray().Single();
            var candidates = question.GetProperty("candidates").EnumerateArray()
                .Select(c => new MappingCandidate(c.GetProperty("id").GetString()!, c.GetProperty("description").GetString()!)).ToArray();
            await Observe("application-storyboard", request.GetProperty("state"),
                question.GetProperty("instructions").GetString()!, candidates, null, question.GetProperty("id").GetString()!);
        }
        else
        {
            await Observe("garden", ContractTests.State("Plan a short, straightforward garden tour with three scenes and concise narration."),
                "Choose the appropriate storyboard planning route for this brief.", planning, null);
        }
        Check.That(trace.Calls == 6, "The mapping sanity must issue exactly six actual native runs.");
        var pairs = observations.GroupBy(o => o.Scenario).Select(group =>
        {
            var normal = group.Single(o => !o.Reversed);
            var reversed = group.Single(o => o.Reversed);
            return new MappingPair(group.Key, normal.SelectedId, reversed.SelectedId,
                normal.SelectedId == reversed.SelectedId,
                normal.Probabilities.Max(p => Math.Abs(p.Value - reversed.Probabilities.Single(r => r.Id == p.Id).Value)),
                normal.Candidates.Select((candidate, i) => Math.Abs(normal.RawLogits[i]
                    - reversed.RawLogits[Array.FindIndex(reversed.Candidates, c => c.Id == candidate.Id)])).Max());
        }).ToArray();
        bool different = observations.Single(o => o.Scenario == "positive" && !o.Reversed).SelectedId
            != observations.Single(o => o.Scenario == "negative" && !o.Reversed).SelectedId;
        var receipt = new MappingReceipt(
            "Bounded native mapping sanity, not a video quality benchmark or route-balance objective. No generation/cloud calls.",
            DateTimeOffset.UtcNow, JuliaModelAssets.Repository, JuliaModelAssets.Revision,
            JuliaModelAssets.ContractRevision, JuliaModelAssets.ProviderRevision, JuliaModelAssets.Fingerprint,
            "Microsoft.Extensions.AI.Abstractions/10.11.0; Microsoft.ML.OnnxRuntime/1.23.2; Tokenizers.HuggingFace/3.23.1; System.Numerics.Tensors/10.0.12 (integrated MAF compatibility patch; provider original 10.0.9)",
            RuntimeInformation.OSDescription, RuntimeInformation.ProcessArchitecture.ToString(),
            "Windows CPU only. No Ubuntu/Linux parity, AOT or task-quality claim.",
            trace.Calls, Check.Assertions, nativeRequestPath is null ? null : Path.GetFileName(nativeRequestPath), nativeRequestSha256, different,
            observations.Where(o => o.KnownAnswer is not null).All(o => o.SelectedId == o.KnownAnswer),
            observations.ToArray(), pairs);
        string path = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(receipt, MappingJson.Default.MappingReceipt) + Environment.NewLine);
        Console.WriteLine($"PASS actual encoded-state/domain/index/distribution mapping; six native runs, {Check.Assertions} structural assertions: {path}");
        Console.WriteLine($"Observed positive/negative choices differ: {different}; all four known-answer checks: {receipt.AllKnownAnswersMatched}");
        foreach (var pair in pairs)
            Console.WriteLine($"{pair.Scenario}: normal={pair.NormalSelectedId}, reversed={pair.ReversedSelectedId}, max probability shift by ID={pair.MaxProbabilityShiftById:G17}");

        async Task Observe(string scenario, JsonElement state, string instructions, MappingCandidate[] candidates, string? knownAnswer,
            string questionId = "decision")
        {
            foreach (bool reversed in new[] { false, true })
            {
                var ordered = reversed ? candidates.Reverse().ToArray() : candidates;
                var request = new DecisionRequest(state,
                    [new ChoiceDecisionQuestion(questionId, instructions, ordered.Select(c => new DecisionCandidate(c.Id, c.Description)).ToArray())]);
                var timer = Stopwatch.StartNew();
                var response = await client.GetResponseAsync(request);
                timer.Stop();
                response.ValidateAgainst(request);
                var answer = (ChoiceDecisionAnswer)response.Answers.Single();
                var batch = trace.LastBatch!;
                string stateText = PythonJson.RenderState(request.State);
                string headText = "choice question: " + instructions;
                string[] optionTexts = ordered.Select(c => " " + c.Description).ToArray();
                long[] stateTokens = Encode(stateText);
                long[] headTokens = Encode(headText);
                long[][] optionTokens = optionTexts.Select(Encode).ToArray();
                List<long> expected = [2, .. headTokens, 1];
                List<long> expectedPositions = [];
                foreach (long[] option in optionTokens)
                {
                    expectedPositions.Add(expected.Count);
                    expected.Add(4);
                    expected.AddRange(option);
                }
                expected.Add(1);
                expected.AddRange(stateTokens);
                expected.Add(1);
                Check.That(batch.BatchSize == 1 && batch.Ids.Take(expected.Count).SequenceEqual(expected)
                    && batch.Ids.Skip(expected.Count).All(v => v == 0),
                    "Actual scorer input did not contain exact full state, question and both option descriptions.");
                Check.That(batch.Attention.Take(expected.Count).All(v => v == 1)
                    && batch.Attention.Skip(expected.Count).All(v => v == 0)
                    && batch.Positions.SequenceEqual(expectedPositions) && batch.Masks.All(v => v)
                    && batch.Types.SequenceEqual([0L]), "Actual scorer framing, padding, attention or marker indices differ.");
                var raw = trace.LastLogits!;
                int winner = Array.IndexOf(raw, raw.Max());
                Check.That(answer.SelectedCandidateId == ordered[winner].Id
                    && answer.Probabilities.Select(p => p.Id).SequenceEqual(ordered.Select(c => c.Id)),
                    "Native output indices were not mapped to the exact ordered application IDs.");
                double max = raw.Max();
                double mass = raw.Sum(v => Math.Exp(v - max));
                Check.That(answer.Probabilities.Select((p, i) => Math.Abs(p.Value - Math.Exp(raw[i] - max) / mass)).All(e => e < 1e-12)
                    && ((JuliaRawAnswer)answer.RawRepresentation!).Logits.SequenceEqual(raw.Select(v => (double)v)),
                    "The complete pre-policy distribution/raw logits were changed by the public bridge.");
                InputTensorEvidence[] tensors =
                [
                    LongTensor("input_ids", [1, batch.SequenceLength], batch.Ids),
                    LongTensor("attention_mask", [1, batch.SequenceLength], batch.Attention),
                    LongTensor("marker_pos", [1, batch.MarkerWidth], batch.Positions),
                    new("marker_mask", "bool", [1, batch.MarkerWidth], null, batch.Masks.ToArray(), Hash(batch.Masks)),
                    LongTensor("qtype", [1], batch.Types)
                ];
                observations.Add(new(scenario, reversed, request.State, questionId, instructions, ordered, knownAnswer,
                    answer.SelectedCandidateId, knownAnswer is null ? null : answer.SelectedCandidateId == knownAnswer,
                    stateText, headText, optionTexts, stateTokens, headTokens, optionTokens,
                    Decode(batch.Ids.Take(expected.Count)), Decode(stateTokens), Decode(headTokens), optionTokens.Select(Decode).ToArray(),
                    tensors, "logits",
                    [1, batch.MarkerWidth], raw, Hash(raw), winner,
                    answer.Probabilities.Select(p => new MappingProbability(p.Id, p.Value)).ToArray(), timer.Elapsed.TotalMilliseconds));
            }
        }

        long[] Encode(string text) => tokenizer.Encode(text, addSpecialTokens: false).Single().Ids.Select(id => (long)id).ToArray();
        string Decode(IEnumerable<long> tokens) => tokenizer.Decode(tokens.Select(id => checked((uint)id)), skipSpecialTokens: false);
    }

    private static InputTensorEvidence LongTensor(string name, long[] shape, long[] values) =>
        new(name, "int64", shape, values.ToArray(), null, Hash(values));

    private static string Hash<T>(T[] data) where T : unmanaged =>
        Convert.ToHexStringLower(SHA256.HashData(MemoryMarshal.AsBytes(data.AsSpan())));
}

internal sealed class NativeTracingScorer(ScoreOnnxDecisionModel native) : IJuliaDecisionScorer
{
    internal int Calls { get; private set; }
    internal JuliaInputBatch? LastBatch { get; private set; }
    internal float[]? LastLogits { get; private set; }

    public Tensor<float> Score(JuliaInputBatch batch, CancellationToken cancellationToken = default)
    {
        LastBatch = batch;
        Calls++;
        var result = native.Score(batch, cancellationToken);
        LastLogits = Enumerable.Range(0, batch.MarkerWidth).Select(i => result[0, i]).ToArray();
        return result;
    }

    public void Dispose() { }
}

public record MappingCandidate(string Id, string Description);
public record MappingProbability(string Id, double Value);
public record InputTensorEvidence(string Name, string ElementType, long[] Shape, long[]? Int64Values, bool[]? BoolValues, string Sha256);
public record MappingObservation(string Scenario, bool Reversed, JsonElement State, string QuestionId, string Instructions,
    MappingCandidate[] Candidates, string? KnownAnswer, string SelectedId, bool? KnownAnswerMatched,
    string EncodedStateText, string EncodedHeadText, string[] EncodedOptionTexts, long[] StateTokenIds, long[] HeadTokenIds,
    long[][] OptionTokenIds, string DecodedUnpaddedInput, string DecodedState, string DecodedHead, string[] DecodedOptions,
    InputTensorEvidence[] Inputs, string OutputName, long[] OutputShape, float[] RawLogits,
    string OutputSha256, int WinnerIndex, MappingProbability[] Probabilities, double Milliseconds);
public record MappingPair(string Scenario, string NormalSelectedId, string ReversedSelectedId, bool ChoiceStable,
    double MaxProbabilityShiftById, double MaxLogitShiftById);
public record MappingReceipt(string Scope, DateTimeOffset Timestamp, string Model, string Revision, string ContractRevision,
    string ProviderRevision, string ManifestFingerprint, string Dependencies, string OperatingSystem, string ProcessArchitecture,
    string PlatformScope, int NativeCalls, int StructuralAssertions, string? NativeRequestFile, string? NativeRequestSha256,
    bool PositiveAndNegativeNormalChoicesDiffer,
    bool AllKnownAnswersMatched, MappingObservation[] Observations, MappingPair[] Pairs);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(MappingReceipt))]
internal partial class MappingJson : JsonSerializerContext;
