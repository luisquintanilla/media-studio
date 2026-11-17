using System.Text.Json;
using MediaStudio.Configuration;
using Microsoft.Extensions.AI;
using VideoProduction.Decisions.Experimental;
using VideoProduction.Providers;
using VideoProduction.Providers.Decisions;
using Xunit.Abstractions;

namespace MediaStudio.Tests.Routing;

internal sealed record RoutingFixture(string Id, string Purpose, string Prompt, string Expected,
    bool Json = false, bool Tools = false, int? MaxOutputTokens = null, int Padding = 0);
internal sealed record RoutingFixtureSet(int SchemaVersion, string Notice, RoutingFixture[] Cases);
internal sealed record RoutingAccuracyReport(string Decider, int Cases, int RawCorrect, int SelectedCorrect,
    int StrongMisses, int FastOverrouting, double MedianDecisionMilliseconds)
{
    public double RawAccuracy => (double)RawCorrect / Cases;
    public double SelectedAccuracy => (double)SelectedCorrect / Cases;
    public override string ToString() =>
        $"{Decider,-10} raw {RawCorrect}/{Cases} ({RawAccuracy:P0})  with-floor {SelectedCorrect}/{Cases} ({SelectedAccuracy:P0})  " +
        $"strong-misses {StrongMisses}  fast-overrouting {FastOverrouting}  median {MedianDecisionMilliseconds:0.0} ms";
}

/// <summary>
/// Reports how well a decider routes the authored fixture set through the real <see cref="DecisionRoutingChatClient"/>
/// selection path (fast vs strong tier; fake tier clients, no generation).
/// </summary>
internal static class RoutingAccuracy
{
    public static RoutingFixtureSet Load() => JsonSerializer.Deserialize<RoutingFixtureSet>(
        File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "tests", "MediaStudio.Tests", "Routing", "routing-fixtures-v1.json")),
        new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    public static async Task<RoutingAccuracyReport> Evaluate(string name, Func<IDecisionClient> decider)
    {
        var set = Load();
        var tiers = new[] { "fast", "strong" }.Select(t => new RoutedChatTier(t, t == "fast"
                ? "Small local model: short answers, quick edits and simple questions."
                : "Strongest model: hard authoring, long structured output and complex reasoning.", t + "-model", false,
            () => new DecisionRoutingTests.FakeTier(t))).ToArray();
        using var shared = decider();
        int raw = 0, selected = 0, misses = 0, over = 0;
        var timings = new List<double>();
        foreach (var item in set.Cases)
        {
            using var router = new DecisionRoutingChatClient(tiers, new(item.Purpose), () => new Unowned(shared));
            var prompt = item.Padding > 0 ? item.Prompt + "\n" + new string('x', item.Padding) : item.Prompt;
            var options = new ChatOptions {
                ResponseFormat = item.Json ? ChatResponseFormat.Json : null,
                Tools = item.Tools ? [AIFunctionFactory.Create(() => "ok", "add_scene")] : null,
                MaxOutputTokens = item.MaxOutputTokens
            };
            var receipt = await router.SelectAsync([new ChatMessage(ChatRole.User, prompt)], options, default);
            timings.Add(receipt.DecisionMilliseconds);
            if (receipt.Distribution.Length > 0 && Math.Abs(receipt.Distribution.Sum(p => p.Probability) - 1) > 1e-3)
                throw new InvalidOperationException($"{name} returned a distribution that does not sum to 1 for '{item.Id}'.");
            if ((receipt.ObservedRoute ?? receipt.SelectedRoute) == item.Expected) raw++;
            if (receipt.SelectedRoute == item.Expected) selected++;
            if (item.Expected == "strong" && receipt.SelectedRoute == "fast") misses++;
            if (item.Expected == "fast" && receipt.SelectedRoute == "strong") over++;
        }
        timings.Sort();
        return new(name, set.Cases.Length, raw, selected, misses, over, timings[timings.Count / 2]);
    }

    private sealed class Unowned(IDecisionClient inner) : IDecisionClient
    {
        public Task<DecisionResponse> GetResponseAsync(DecisionRequest request, DecisionOptions? options = null,
            CancellationToken cancellationToken = default) => inner.GetResponseAsync(request, options, cancellationToken);
        public object? GetService(Type serviceType, object? serviceKey = null) => inner.GetService(serviceType, serviceKey);
        public void Dispose() { }
    }

    public static string? JuliaDirectory()
    {
        try { return File.Exists(ModelManifest.DefaultPath) ? new ModelAssets(ModelManifest.LoadDefault()).GetPath(DecisionClientFactory.JuliaAssetId) : null; }
        catch (Exception e) when (e is IOException or InvalidDataException or JsonException or KeyNotFoundException) { return null; }
    }
}

public sealed class RoutingAccuracyTests(ITestOutputHelper output)
{
    [Fact]
    public async Task HeuristicAccuracyOnTheFixtureSet()
    {
        var report = await RoutingAccuracy.Evaluate("heuristic", () => new HeuristicDecisionClient());
        output.WriteLine(report.ToString());
        output.WriteLine("openai     unavailable: native OpenAI Decisions are compiled out (-p:OpenAIDecisions=true).");
        // The heuristic only sees request shape, so short-but-hard prompts are its known blind spot.
        Assert.Equal(14, report.Cases);
        Assert.Equal(0, report.FastOverrouting);
        Assert.True(report.SelectedCorrect >= 12, report.ToString());
    }

    [Fact, Trait("Category", "Julia")]
    public async Task JuliaAccuracyAndLatencyOnTheFixtureSet()
    {
        var directory = RoutingAccuracy.JuliaDirectory();
        Assert.True(directory is not null, "Run `video-production assets ensure --id julia-1` to download the verified Julia asset.");
        var report = await RoutingAccuracy.Evaluate("julia", () => DecisionClientFactory.LoadJulia(directory!));
        var heuristic = await RoutingAccuracy.Evaluate("heuristic", () => new HeuristicDecisionClient());
        output.WriteLine(report.ToString());
        output.WriteLine(heuristic.ToString());
        Assert.True(report.MedianDecisionMilliseconds < 500, report.ToString());
    }
}
