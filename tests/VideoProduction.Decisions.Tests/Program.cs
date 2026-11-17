using System.ComponentModel;
using System.Diagnostics;
using System.Numerics.Tensors;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.AI;
using Microsoft.ML.Tokenizers;
using VideoProduction.Decisions.Experimental;
using VideoProduction.Decisions.Experimental.Julia;

if (args.Length != 0)
{
    if (args.Length is not (5 or 7) || args[0] is not ("--native-smoke" or "--mapping-sanity") || args[1] != "--model-directory" || args[3] != "--output"
        || (args.Length == 7 && (args[0] != "--mapping-sanity" || args[5] != "--native-request")))
        throw new ArgumentException("Use no arguments for offline tests, or --native-smoke/--mapping-sanity --model-directory DIRECTORY --output RECEIPT.json [--native-request REQUEST.json (mapping only)].");
    if (args[0] == "--mapping-sanity") await MappingSanity.Run(args[2], args[4], args.Length == 7 ? args[6] : null);
    else await NativeSmoke.Run(args[2], args[4]);
    return;
}

int passed = 0;
#if OPENAI_DECISIONS
await Run("OpenAI 2.15 typed HTTP fixtures, IDs, score, confidence and refusal", OpenAiDecisionTests.TypedHttpFixtures);
#endif
await Run("full distribution, correlation and immutable domain", ContractTests.Distributions);
await Run("typed enum binding and explicit metadata", ContractTests.TypedBinding);
await Run("source-generated wire roundtrip, official MEAI type identity", ContractTests.Serialization);
await Run("native provider seams preserve complete distribution", ProviderTests.Distribution);
await Run("budgets, reserved tokens and no scorer call on rejection", ProviderTests.Budgets);
await Run("runtime failures, cancellation, model and options isolation", ProviderTests.Failures);
await Run("pinned manifest, missing/mismatched asset rejection", ProviderTests.Assets);
Console.WriteLine($"PASS {passed} decision test groups, {Check.Assertions} assertions; fixture-only; no inference or generation.");

async Task Run(string name, Func<Task> test)
{
    int before = Check.Assertions;
    await test();
    passed++;
    Console.WriteLine($"PASS {name} ({Check.Assertions - before} assertions)");
}

internal static class Check
{
    public static int Assertions { get; private set; }

    public static void That(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
        Assertions++;
    }

    public static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { Assertions++; return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    public static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { Assertions++; return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}

internal static class ContractTests
{
    internal static JsonElement State(string brief = "A synthetic garden overview.") =>
        JsonSerializer.SerializeToElement(new SmokeState(brief), TestJson.Default.SmokeState);

    internal static ChoiceDecisionQuestion Question(string id = "route") =>
        new(id, "Choose an appropriate storyboard planning route.", [new("fast", "Straightforward planning"), new("strong", "Demanding planning")]);

    internal static DecisionResponse Response(DecisionRequest request, double first = .25) =>
        new(request, [new ChoiceDecisionAnswer(request.Questions[0].Id, first > .5 ? "fast" : "strong",
            [new("fast", first), new("strong", 1 - first)])]);

    internal static Task Distributions()
    {
        var candidates = new List<DecisionCandidate> { new("fast", "Straightforward planning"), new("strong", "Demanding planning") };
        var question = new ChoiceDecisionQuestion("route", "Choose.", candidates);
        candidates.Clear();
        var request = new DecisionRequest(State(), [question]);
        var response = Response(request);
        var answer = (ChoiceDecisionAnswer)response.Answers[0];
        Check.That(question.Candidates.Count == 2 && answer.Probabilities.Select(p => p.Value).SequenceEqual([.25, .75]),
            "Domain/distribution lost.");
        foreach (double value in new[] { -.1, 1.1, double.NaN, double.PositiveInfinity })
            Check.Throws<DecisionProtocolException>(() => new ChoiceDecisionAnswer("route", "fast",
                [new("fast", value), new("strong", .5)]));
        Check.Throws<DecisionProtocolException>(() => new ChoiceDecisionAnswer("route", "fast", [new("fast", .2), new("strong", .3)]));
        Check.Throws<DecisionProtocolException>(() => new ChoiceDecisionAnswer("route", "other", [new("fast", .2), new("strong", .8)]));
        Check.Throws<DecisionProtocolException>(() => new ChoiceDecisionAnswer("route", "fast", [new("fast", .2), new("fast", .8)]));
        Check.Throws<DecisionProtocolException>(() => new DecisionResponse(request, []));
        Check.Throws<DecisionProtocolException>(() => new DecisionResponse(request,
            [new ChoiceDecisionAnswer("wrong", "fast", [new("fast", .5), new("strong", .5)])]));
        Check.Throws<DecisionProtocolException>(() => new DecisionResponse(request,
            [new ChoiceDecisionAnswer("route", "fast", [new("strong", .5), new("fast", .5)])]));
        Check.Throws<DecisionProtocolException>(() => new DecisionResponse(request,
            [new ChoiceDecisionAnswer("route", "fast", [new("fast", .5), new("unknown", .5)])]));
        Check.Throws<DecisionProtocolException>(() => new DecisionResponse(request,
            [new ChoiceDecisionAnswer("route", "fast", [new("fast", 1)])]));
        Check.Throws<DecisionProtocolException>(() => response.ValidateAgainst(new(State("Changed brief"), [question])));
        Check.Throws<DecisionProtocolException>(() => response.ValidateAgainst(new(State(),
            [new ChoiceDecisionQuestion("route", "Changed instructions.", question.Candidates)])));
        Check.Throws<DecisionProtocolException>(() => response.ValidateAgainst(new(State(),
            [new ChoiceDecisionQuestion("route", "Choose.", question.Candidates.Reverse().ToArray())])));
        return Task.CompletedTask;
    }

    internal static async Task TypedBinding()
    {
        var definition = DecisionDefinition<SmokeResult>.Create(TestJson.Default.SmokeResult,
            b => b.Choice(x => x.Route, "Choose a planning route."));
        Check.That(((ChoiceDecisionQuestion)definition.Questions[0]).Candidates.Select(c => c.Id)
            .SequenceEqual(["planning-fast", "planning-strong"]), "String enum IDs must match metadata.");
        foreach (bool strong in new[] { false, true })
        {
            using var client = new FixtureClient(strong);
            var result = await client.GetResponseAsync(State(), definition);
            Check.That(result.Result.Route == (strong ? SmokeRoute.Strong : SmokeRoute.Fast)
                && ((ChoiceDecisionAnswer)result.Evidence.Answers[0]).Probabilities.Count == 2,
                "Typed result or complete evidence lost.");
        }
        using var altered = new FixtureClient(true) { ChangeState = true };
        await Check.ThrowsAsync<DecisionProtocolException>(() => altered.GetResponseAsync(State(), definition));
        Check.Throws<NotSupportedException>(() => DecisionDefinition<SmokeResult>.Create(b => b.Choice(x => x.Route)));
    }

    internal static Task Serialization()
    {
        Check.That(!JsonSerializer.IsReflectionEnabledByDefault, "This test must actually disable reflection serialization.");
        var request = new DecisionRequest(State(),
            [Question(), new BinaryDecisionQuestion("binary", "Is it synthetic?"),
                new ScoreDecisionQuestion("score", "Rate complexity.", [new("low", "Simple"), new("high", "Complex")])]);
        var usage = new UsageDetails { InputTokenCount = 17, OutputTokenCount = 0, TotalTokenCount = 17 };
        var properties = new AdditionalPropertiesDictionary { ["count"] = 3L, ["label"] = "fixture" };
        var response = new DecisionResponse(request,
            [new ChoiceDecisionAnswer("route", "strong", [new("fast", .25), new("strong", .75)]),
                new BinaryDecisionAnswer("binary", .9),
                new ScoreDecisionAnswer("score", null, [new("low", .2), new("high", .8)])],
            new("fixture", modelId: "not-an-inference-model"), usage, new object(), properties);
        string json = JsonSerializer.Serialize(response, DecisionJsonContext.Default.DecisionResponse);
        var restored = JsonSerializer.Deserialize(json, DecisionJsonContext.Default.DecisionResponse)!;
        restored.ValidateAgainst(request);
        Check.That(restored.Answers.Count == 3 && restored.RawRepresentation is null
            && restored.Usage?.InputTokenCount == 17 && restored.Provenance?.ModelId == "not-an-inference-model"
            && ((ChoiceDecisionAnswer)restored.Answers[0]).Probabilities[1].Value == .75
            && ((ScoreDecisionAnswer)restored.Answers[2]).Score is null
            && restored.AdditionalProperties?["label"] is JsonElement { ValueKind: JsonValueKind.String },
            "Source-generated roundtrip lost public contract data.");
        Check.That(typeof(UsageDetails).Assembly != typeof(DecisionResponse).Assembly
            && typeof(DecisionOptions).GetProperty(nameof(DecisionOptions.AdditionalProperties))!.PropertyType
                == typeof(AdditionalPropertiesDictionary), "Official MEAI type identity was replaced.");
        properties["count"] = 99L;
        Check.That((long)response.AdditionalProperties!["count"]! == 3L, "Extension snapshot is not owned.");
        return Task.CompletedTask;
    }
}

internal static class ProviderTests
{
    private static PrepareDecisionInputs Prepare() => new(new SyntheticTokenizer(), ["<mask>"], [4]);

    internal static async Task Distribution()
    {
        using var tokenizer = new SyntheticTokenizer();
        using var score = new FixtureScorer();
        using var client = new JuliaDecisionClient(tokenizer, ["<mask>"], [4], score, new());
        var request = new DecisionRequest(ContractTests.State(), [ContractTests.Question()]);
        var response = await client.GetResponseAsync(request);
        response.ValidateAgainst(request);
        var answer = (ChoiceDecisionAnswer)response.Answers.Single();
        Check.That(score.Calls == 1 && answer.SelectedCandidateId == "strong"
            && answer.Probabilities.Select(p => p.Id).SequenceEqual(["fast", "strong"])
            && Math.Abs(answer.Probabilities[0].Value - 1 / (1 + Math.Exp(2))) < 1e-12
            && Math.Abs(answer.Probabilities.Sum(p => p.Value) - 1) < 1e-12
            && answer.RawRepresentation is JuliaRawAnswer { Logits.Count: 2 },
            "Prepare/score/decode or public bridge changed the full softmax.");
    }

    internal static async Task Budgets()
    {
        using var prepare = Prepare();
        using var score = new FixtureScorer();
        using var client = new JuliaDecisionClient(prepare, score, new());
        Task Infer(string text, ChoiceDecisionQuestion? question = null) =>
            client.GetResponseAsync(new(ContractTests.State(text), [question ?? ContractTests.Question()]));
        await Check.ThrowsAsync<NotSupportedException>(() => Infer("Reserved <mask> text"));
        await Check.ThrowsAsync<ArgumentException>(() => Infer(new string('x', 1100)));
        await Check.ThrowsAsync<ArgumentException>(() => Infer("brief", new("route", new string('x', 270),
            [new("fast", "Simple"), new("strong", "Complex")])));
        await Check.ThrowsAsync<ArgumentException>(() => Infer("brief", new("route", "Choose.",
            [new("fast", new string('x', 49)), new("strong", "Complex")])));
        await Check.ThrowsAsync<ArgumentException>(() => Infer("brief", new("route", "Choose.", [new("only", "Only")])));
        await Check.ThrowsAsync<ArgumentException>(() => Infer("brief", new("route", "Choose.",
            Enumerable.Range(0, 21).Select(i => new DecisionCandidate(i.ToString(), "Option")).ToArray())));
        Check.That(score.Calls == 0, "Invalid input must never reach the native scoring seam.");
        await Infer("brief");
        Check.That(score.Calls == 1, "Rejected inputs corrupted the reusable client.");
    }

    internal static async Task Failures()
    {
        using var prepare = Prepare();
        using var score = new FixtureScorer();
        var client = new JuliaDecisionClient(prepare, score, new());
        var request = new DecisionRequest(ContractTests.State(), [ContractTests.Question()]);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Check.ThrowsAsync<OperationCanceledException>(() => client.GetResponseAsync(request, cancellationToken: cancelled.Token));
        await Check.ThrowsAsync<NotSupportedException>(() => client.GetResponseAsync(request, new() { ModelId = "other" }));
        await Check.ThrowsAsync<NotSupportedException>(() => client.GetResponseAsync(request,
            new() { AdditionalProperties = new() { ["unrecognized"] = true } }));
        await Check.ThrowsAsync<NotSupportedException>(() => client.GetResponseAsync(request,
            new() { RawRepresentationFactory = _ => new object() }));
        Check.That(score.Calls == 0, "Invalid options/cancellation reached scorer.");
        score.Failure = new InvalidOperationException("Synthetic ORT failure");
        await Check.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync(request));
        score.Failure = null;
        score.Nonfinite = true;
        await Check.ThrowsAsync<DecisionProtocolException>(() => client.GetResponseAsync(request));
        score.Nonfinite = false;
        using var inFlight = new CancellationTokenSource();
        score.OnScore = inFlight.Cancel;
        await Check.ThrowsAsync<OperationCanceledException>(() => client.GetResponseAsync(request, cancellationToken: inFlight.Token));
        score.OnScore = null;
        var options = new DecisionOptions { ModelId = JuliaDecisionClient.ModelId };
        await client.GetResponseAsync(request, options);
        Check.That(options.ModelId == JuliaDecisionClient.ModelId && options.AdditionalProperties is null,
            "Provider mutated caller options.");
        client.Dispose();
        await Check.ThrowsAsync<ObjectDisposedException>(() => client.GetResponseAsync(request));
        Check.That(!score.Disposed, "Borrowed stage was disposed by the client.");
        using var tokenizer = new SyntheticTokenizer();
        var borrowed = new PrepareDecisionInputs(tokenizer, ["<mask>"], [4]);
        borrowed.Dispose();
        Check.That(!tokenizer.Disposed, "Borrowed Microsoft tokenizer was disposed.");
        var owned = new PrepareDecisionInputs(tokenizer, ["<mask>"], [4], ownsTokenizer: true);
        owned.Dispose();
        owned.Dispose();
        Check.That(tokenizer.Disposed, "Owned Microsoft tokenizer was not disposed.");
    }

    internal static Task Assets()
    {
        Check.That(JuliaModelAssets.Fingerprint == Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(JuliaModelAssets.Manifest))), "Manifest identity changed.");
        string directory = Path.Combine(AppContext.BaseDirectory, "asset-rejection-test");
        Directory.CreateDirectory(directory);
        try
        {
            Check.Throws<FileNotFoundException>(() => JuliaDecisionClient.LoadFromDirectory(directory));
            File.WriteAllText(Path.Combine(directory, "model.onnx"), "Not a model");
            Check.Throws<InvalidDataException>(() => JuliaDecisionClient.LoadFromDirectory(directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
        return Task.CompletedTask;
    }
}

internal sealed class SyntheticTokenizer : Tokenizer, IDisposable
{
    internal bool Disposed { get; private set; }
    protected override EncodeResults<int> EncodeToIds(string? text, ReadOnlySpan<char> textSpan, EncodeSettings settings)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        string input = text ?? textSpan.ToString();
        return new() { Tokens = input.Select(c => (int)c + 10).ToArray(), CharsConsumed = input.Length };
    }
    protected override EncodeResults<EncodedToken> EncodeToTokens(string? text, ReadOnlySpan<char> textSpan, EncodeSettings settings) =>
        throw new NotSupportedException("The synthetic decision fixture only encodes IDs.");
    protected override int CountTokens(string? text, ReadOnlySpan<char> textSpan, EncodeSettings settings) =>
        throw new NotSupportedException();
    protected override int GetIndexByTokenCount(string? text, ReadOnlySpan<char> textSpan, EncodeSettings settings,
        bool fromEnd, out string? normalizedText, out int tokenCount) => throw new NotSupportedException();
    public override string Decode(IEnumerable<int> ids) => throw new NotSupportedException();
    public override System.Buffers.OperationStatus Decode(IEnumerable<int> ids, Span<char> destination,
        out int idsConsumed, out int charsWritten) => throw new NotSupportedException();
    public void Dispose() => Disposed = true;
}

internal sealed class FixtureScorer : IJuliaDecisionScorer
{
    internal int Calls { get; private set; }
    internal Exception? Failure { get; set; }
    internal bool Nonfinite { get; set; }
    internal bool Disposed { get; private set; }
    internal Action? OnScore { get; set; }
    public Tensor<float> Score(JuliaInputBatch batch, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        OnScore?.Invoke();
        if (Failure is not null) throw Failure;
        return Tensor.Create(new[] { Nonfinite ? float.NaN : 0f, 2f }, [1, 2]);
    }
    public void Dispose() => Disposed = true;
}

internal sealed class FixtureClient(bool strong) : IDecisionClient
{
    internal bool ChangeState { get; init; }
    public Task<DecisionResponse> GetResponseAsync(DecisionRequest request, DecisionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ChangeState) request = new(ContractTests.State("Changed"), request.Questions);
        var question = (ChoiceDecisionQuestion)request.Questions.Single();
        return Task.FromResult(new DecisionResponse(request,
            [new ChoiceDecisionAnswer(question.Id, question.Candidates[strong ? 1 : 0].Id,
                question.Candidates.Select((c, i) => new DecisionProbability(c.Id, i == (strong ? 1 : 0) ? .75 : .25)).ToArray())]));
    }
    public object? GetService(Type type, object? key = null) => null;
    public void Dispose() { }
}

internal static class NativeSmoke
{
    internal static async Task Run(string directory, string output)
    {
        var load = Stopwatch.StartNew();
        using var client = JuliaDecisionClient.LoadFromDirectory(directory, o =>
        {
            o.IntraOpNumThreads = 2;
            o.InterOpNumThreads = 1;
        });
        load.Stop();
        var definition = DecisionDefinition<SmokeResult>.Create(TestJson.Default.SmokeResult,
            b => b.Choice(x => x.Route, "Choose the appropriate storyboard planning route for this brief."));
        var state = ContractTests.State("Plan a short, straightforward garden tour with three scenes and concise narration.");
        var observations = new List<SmokeObservation>();
        for (int i = 0; i < 2; i++)
        {
            var timer = Stopwatch.StartNew();
            var response = await client.GetResponseAsync(state, definition);
            timer.Stop();
            var answer = (ChoiceDecisionAnswer)response.Evidence.Answers.Single();
            Check.That(answer.Probabilities.Count == 2 && Math.Abs(answer.Probabilities.Sum(p => p.Value) - 1) < 1e-6,
                "Native inference failed to retain a normalized full distribution.");
            observations.Add(new(i == 0 ? "cold-inference" : "warm-inference", timer.Elapsed.TotalMilliseconds,
                response.Result.Route.ToString(), response.Evidence,
                ((JuliaRawAnswer)answer.RawRepresentation!).Logits.ToArray()));
        }
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Check.ThrowsAsync<OperationCanceledException>(() => client.GetResponseAsync(state, definition, cancellationToken: cancel.Token));
        await Check.ThrowsAsync<NotSupportedException>(() => client.GetResponseAsync(ContractTests.State("Reserved <mask> token"), definition));
        await Check.ThrowsAsync<ArgumentException>(() => client.GetResponseAsync(ContractTests.State(string.Join(' ', Enumerable.Repeat("garden", 1500))), definition));
        var nativeBinaries = new List<NativeBinary>();
        using (var process = Process.GetCurrentProcess())
        {
            foreach (ProcessModule module in process.Modules)
            {
                if (module.ModuleName.Equals("onnxruntime.dll", StringComparison.OrdinalIgnoreCase)
                    || module.ModuleName.Equals("onnxruntime_providers_shared.dll", StringComparison.OrdinalIgnoreCase)
                    || module.ModuleName.Equals("tokenizers_proto.dll", StringComparison.OrdinalIgnoreCase))
                {
                    using FileStream stream = File.OpenRead(module.FileName);
                    nativeBinaries.Add(new(module.ModuleName, Convert.ToHexStringLower(SHA256.HashData(stream))));
                }
            }
        }
        Check.That(nativeBinaries.Any(b => b.Name.Contains("onnxruntime", StringComparison.OrdinalIgnoreCase))
            && nativeBinaries.Any(b => b.Name.Contains("tokenizers_proto", StringComparison.OrdinalIgnoreCase)),
            "Expected ONNX runtime and HF tokenizer implementation behind the Microsoft abstraction: " + string.Join(", ", nativeBinaries.Select(b => b.Name)));
        var receipt = new SmokeReceipt("actual native CPU ONNX decision inference; no generative/cloud calls",
            DateTimeOffset.UtcNow, JuliaModelAssets.Repository, JuliaModelAssets.Revision,
            JuliaModelAssets.ContractRevision, JuliaModelAssets.ProviderRevision, JuliaModelAssets.Fingerprint,
            JuliaModelAssets.Manifest, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(state.GetRawText()))),
            "Microsoft.Extensions.AI.Abstractions/11.0.0", "Microsoft.ML.OnnxRuntime/1.23.2", "Microsoft.ML.Tokenizers/2.0.0 abstraction + Tokenizers.HuggingFace/3.23.1 implementation",
            "System.Numerics.Tensors/11.0.0", load.Elapsed.TotalMilliseconds,
            observations.ToArray(), RuntimeInformation.OSDescription, RuntimeInformation.ProcessArchitecture.ToString(),
            "This run proves Windows native CPU inference only; no Ubuntu/Linux parity, cross-platform or AOT claim.",
            nativeBinaries.OrderBy(b => b.Name, StringComparer.Ordinal).ToArray(),
            "Provider originally used Tensors 10.0.9; integrated MAF 1.26 / AI 10.10.0 requires >=10.0.12. Exact 10.0.12 preserves that existing closure; no full AI 10.11.0/cache uplift.",
            Check.Assertions, PreCancelledRejected: true, ReservedRejected: true, OverBudgetRejected: true);
        string path = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(receipt, TestJson.Default.SmokeReceipt) + Environment.NewLine);
        Console.WriteLine($"PASS actual native Julia CPU smoke: {path}");
        Console.WriteLine($"Manifest SHA-256: {JuliaModelAssets.Fingerprint}; load {load.Elapsed.TotalMilliseconds:F1} ms");
        foreach (var observation in observations)
            Console.WriteLine($"{observation.Stage}: {observation.Milliseconds:F1} ms; {observation.Selected}");
    }
}

public record SmokeState(string Brief);
public record SmokeResult(SmokeRoute Route);
public enum SmokeRoute
{
    [JsonStringEnumMemberName("planning-fast"), Description("Straightforward storyboard planning with few constraints.")] Fast,
    [JsonStringEnumMemberName("planning-strong"), Description("Demanding storyboard planning with complex constraints.")] Strong
}
public record SmokeObservation(string Stage, double Milliseconds, string Selected, DecisionResponse Evidence, double[] Logits);
public record NativeBinary(string Name, string Sha256);
public record SmokeReceipt(string Scope, DateTimeOffset Timestamp, string Model, string Revision, string ContractRevision,
    string ProviderRevision, string ManifestFingerprint, string AssetManifest, string InputSha256, string Meai, string OnnxRuntime,
    string Tokenizer, string Tensors, double LoadMilliseconds, SmokeObservation[] Observations,
    string OperatingSystem, string ProcessArchitecture, string PlatformScope, NativeBinary[] LoadedNativeBinaries,
    string DependencyAdaptation, int Assertions,
    bool PreCancelledRejected, bool ReservedRejected, bool OverBudgetRejected);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, WriteIndented = true)]
[JsonSerializable(typeof(SmokeState))]
[JsonSerializable(typeof(SmokeResult))]
[JsonSerializable(typeof(SmokeRoute))]
[JsonSerializable(typeof(SmokeReceipt))]
internal partial class TestJson : JsonSerializerContext;
