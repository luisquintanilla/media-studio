using System.Globalization;
using MediaStudio.Configuration;
using Microsoft.Extensions.AI;
using VideoProduction;
using VideoProduction.Decisions.Experimental;
using VideoProduction.Providers;

namespace MediaStudio.Tests.Routing;

/// <summary>Neutral-API routing behaviour: fake tiers plus a scripted decider (no models, no network).</summary>
public sealed class DecisionRoutingTests
{
    internal sealed class ScriptedDecider(params (string Id, double P)[] script) : IDecisionClient
    {
        public List<string[]> Asked { get; } = [];
        public Task<DecisionResponse> GetResponseAsync(DecisionRequest request, DecisionOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var question = (ChoiceDecisionQuestion)request.Questions.Single();
            var ids = question.Candidates.Select(c => c.Id).ToArray();
            Asked.Add(ids);
            var raw = ids.Select(id => script.FirstOrDefault(s => s.Id == id).P).ToArray();
            var total = raw.Sum();
            var probabilities = ids.Select((id, i) => new DecisionProbability(id, total == 0 ? 1d / ids.Length : raw[i] / total)).ToArray();
            var pick = probabilities.MaxBy(p => p.Value)!.Id;
            return Task.FromResult(new DecisionResponse(request, [new ChoiceDecisionAnswer(question.Id, pick, probabilities)],
                new DecisionProvenance("julia", null, "julia-1", null, DecisionPrecision.HighPrecision)));
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    internal sealed class FakeTier(string name, bool transientFailure = false) : IChatClient
    {
        public int Calls { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            if (transientFailure) throw new HttpRequestException($"{name} is down");
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "from " + name)));
        }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++;
            await Task.Yield();
            if (transientFailure) throw new HttpRequestException($"{name} is down");
            yield return new ChatResponseUpdate(ChatRole.Assistant, "from " + name);
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class Pool
    {
        public Dictionary<string, FakeTier> Clients { get; } = [];
        public HashSet<string> Created { get; } = [];
        public RoutedChatTier Add(string name, bool cloud = false, bool fails = false)
        {
            Clients[name] = new FakeTier(name, fails);
            return new RoutedChatTier(name, $"{name} tier", name + "-model", cloud, () => { Created.Add(name); return Clients[name]; });
        }
    }

    private static async Task<(ChatResponse Response, ChatRouteReceipt Receipt)> Ask(DecisionRoutingChatClient router, string prompt = "Summarize this.")
    {
        var response = await router.GetResponseAsync(prompt);
        return (response, (ChatRouteReceipt)response.AdditionalProperties![DecisionRoutingChatClient.ReceiptKey]!);
    }

    [Fact]
    public async Task PicksTheDecidersTopTierAndShowsTheBadge()
    {
        var pool = new Pool();
        var decider = new ScriptedDecider(("fast", .05), ("balanced", .9), ("strong", .05));
        using var router = new DecisionRoutingChatClient([pool.Add("fast"), pool.Add("balanced"), pool.Add("strong")],
            new("chat"), () => decider);
        var (response, receipt) = await Ask(router);
        Assert.Equal("from balanced", response.Text);
        Assert.Equal(("balanced", "decision-choice"), (receipt.SelectedRoute, receipt.Reason));
        Assert.Equal("⚡ local · balanced-model · Julia 0.90", receipt.Badge());
    }

    [Theory]
    [InlineData(.5, .4, "balanced")]  // below the floor
    [InlineData(.62, .38, "balanced")] // above the floor but inside the margin
    public async Task LowConfidenceBumpsOneTierStronger(double fast, double balanced, string expected)
    {
        var pool = new Pool();
        using var router = new DecisionRoutingChatClient([pool.Add("fast"), pool.Add("balanced"), pool.Add("strong")],
            new("chat") { ConfidenceFloor = .6, MinimumMargin = .3 }, () => new ScriptedDecider(("fast", fast), ("balanced", balanced)));
        var (_, receipt) = await Ask(router);
        Assert.Equal(("fast", expected, "low-confidence-escalated"), (receipt.ObservedRoute, receipt.SelectedRoute, receipt.Reason));
    }

    [Fact]
    public async Task CloudOffNeverSelectsOrAsksAboutTheCloudTier()
    {
        var pool = new Pool();
        var decider = new ScriptedDecider(("cloud", .99), ("fast", .005), ("balanced", .005));
        using var router = new DecisionRoutingChatClient([pool.Add("fast"), pool.Add("balanced"), pool.Add("cloud", cloud: true)],
            new("chat") { AllowCloud = false, ConfidenceFloor = 0, MinimumMargin = 0 }, () => decider);
        var (_, receipt) = await Ask(router);
        Assert.DoesNotContain("cloud", decider.Asked.Single());
        Assert.DoesNotContain("cloud", pool.Created);
        Assert.Equal("cloud-disabled", receipt.Eligibility.Single(e => e.Name == "cloud").Reason);
        Assert.False(receipt.IsCloud);
    }

    [Fact]
    public async Task CloudDeciderIsRefusedWhenCloudIsOff()
    {
        var pool = new Pool();
        using var router = new DecisionRoutingChatClient([pool.Add("fast"), pool.Add("balanced")],
            new("chat") { DeciderIsCloud = true }, () => new ScriptedDecider());
        var error = await Assert.ThrowsAsync<VideoException>(() => router.GetResponseAsync("hi"));
        Assert.Equal("routing.scorer-privacy", error.Code);
        Assert.Empty(pool.Created);
    }

    [Fact]
    public async Task AuthoringRespectsTheFloor()
    {
        var pool = new Pool();
        var decider = new ScriptedDecider(("fast", .9), ("balanced", .05), ("strong", .05));
        using var router = new DecisionRoutingChatClient([pool.Add("fast"), pool.Add("balanced"), pool.Add("strong")],
            new("authoring") { AuthoringFloor = "balanced" }, () => decider);
        var (_, receipt) = await Ask(router);
        Assert.Equal("below-authoring-floor", receipt.Eligibility.Single(e => e.Name == "fast").Reason);
        Assert.DoesNotContain("fast", decider.Asked.Single());
        Assert.NotEqual("fast", receipt.SelectedRoute);

        using var chat = new DecisionRoutingChatClient([pool.Add("fast"), pool.Add("balanced"), pool.Add("strong")],
            new("chat") { AuthoringFloor = "balanced" }, () => decider);
        Assert.Equal("fast", (await Ask(chat)).Receipt.SelectedRoute);
    }

    [Fact]
    public async Task LocalFailureFailsOverToTheNextStrongerTier()
    {
        var pool = new Pool();
        using var router = new DecisionRoutingChatClient([pool.Add("fast", fails: true), pool.Add("balanced"), pool.Add("strong")],
            new("chat"), () => new ScriptedDecider(("fast", .9), ("balanced", .05), ("strong", .05)));
        var (response, receipt) = await Ask(router);
        Assert.Equal("from balanced", response.Text);
        Assert.Equal(("balanced", "fast"), (receipt.SelectedRoute, receipt.FailedOverFrom));
        Assert.EndsWith("· failover", receipt.Badge());
        Assert.Equal(1, pool.Clients["fast"].Calls);

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in router.GetStreamingResponseAsync("stream please")) updates.Add(update);
        Assert.Equal("from balanced", string.Concat(updates.Select(u => u.Text)));
    }

    [Fact]
    public async Task FailoverNeverCrossesThePrivacyBoundary()
    {
        var pool = new Pool();
        using var router = new DecisionRoutingChatClient(
            [pool.Add("fast", fails: true), pool.Add("balanced", fails: true), pool.Add("cloud", cloud: true)],
            new("chat"), () => new ScriptedDecider(("fast", .9), ("balanced", .1)));
        var error = await Assert.ThrowsAsync<VideoException>(() => router.GetResponseAsync("hi"));
        Assert.Equal("routing.local-unavailable", error.Code);
        Assert.Equal("Local model unavailable; cloud is disabled in Settings.", error.Message);
        Assert.DoesNotContain("cloud", pool.Created);
    }

    [Fact]
    public async Task CloudAllowedFailsOverToCloud()
    {
        var pool = new Pool();
        using var router = new DecisionRoutingChatClient(
            [pool.Add("fast", fails: true), pool.Add("cloud", cloud: true)],
            new("chat") { AllowCloud = true }, () => new ScriptedDecider(("fast", .95), ("cloud", .05)));
        var (response, receipt) = await Ask(router);
        Assert.Equal("from cloud", response.Text);
        Assert.True(receipt.IsCloud);
        Assert.StartsWith("☁ cloud", receipt.Badge());
    }

    [Fact]
    public async Task FailoverDisabledSurfacesTheProviderError()
    {
        var pool = new Pool();
        using var router = new DecisionRoutingChatClient([pool.Add("fast", fails: true), pool.Add("balanced")],
            new("chat") { Failover = false }, () => new ScriptedDecider(("fast", .9), ("balanced", .1)));
        await Assert.ThrowsAsync<HttpRequestException>(() => router.GetResponseAsync("hi"));
        Assert.Equal(0, pool.Clients["balanced"].Calls);
    }

    [Fact]
    public async Task ZeroModelsStillRoutesWithTheHeuristic()
    {
        var pool = new Pool();
        using var router = new DecisionRoutingChatClient([pool.Add("fast"), pool.Add("strong")], new("chat"),
            () => new VideoProduction.Providers.Decisions.HeuristicDecisionClient());
        Assert.Equal("fast", (await Ask(router, "What time is it?")).Receipt.SelectedRoute);
        var longPrompt = string.Join(' ', Enumerable.Repeat("context", 2000));
        var receipt = (await Ask(router, longPrompt)).Receipt;
        Assert.Equal("strong", receipt.SelectedRoute);
        Assert.Contains("heuristic", receipt.Badge(), StringComparison.Ordinal);
        _ = CultureInfo.InvariantCulture;
    }
}
