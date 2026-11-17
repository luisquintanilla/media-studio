using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediaStudio.Configuration;
using VideoProduction.Decisions.Experimental;
using VideoProduction.Providers;
using VideoProduction.Providers.Decisions;

namespace MediaStudio.Tests.Routing;

public sealed class DecisionChainTests
{
    private static readonly ChoiceDecisionQuestion Tiers = new("route", "Pick the tier.",
        [new("fast", "Short answers."), new("balanced", "Medium work."), new("strong", "Hard work.")]);

    private static DecisionRequest Request(object state, params DecisionQuestion[] questions) =>
        new(JsonSerializer.SerializeToElement(state), questions.Length == 0 ? [Tiers] : questions);

    private static string Pick(DecisionResponse response) => ((ChoiceDecisionAnswer)response.Answers[0]).SelectedCandidateId;

    [Theory]
    [InlineData("chat", 120, false, false, 256, "fast")]
    [InlineData("chat", 9000, false, false, 256, "strong")]
    [InlineData("chat", 120, true, false, 256, "strong")]
    [InlineData("chat", 120, false, true, 256, "strong")]
    [InlineData("chat", 120, false, false, 8192, "strong")]
    [InlineData("authoring", 50, false, false, 256, "strong")]
    [InlineData("illustration", 300, false, false, 1024, "fast")]
    public async Task HeuristicGoldenCases(string purpose, int inputCharacters, bool json, bool tools, int maxOutputTokens, string expected)
    {
        using var client = new HeuristicDecisionClient();
        var response = await client.GetResponseAsync(Request(new { purpose, inputCharacters, json, tools, maxOutputTokens }));
        Assert.Equal(expected, Pick(response));
        var answer = (ChoiceDecisionAnswer)response.Answers[0];
        Assert.Equal(HeuristicDecisionClient.PickProbability, answer.Probabilities.Single(p => p.Id == expected).Value, 6);
        Assert.Equal(1, answer.Probabilities.Sum(p => p.Value), 6);
        Assert.Equal(HeuristicDecisionClient.ProviderName, response.Provenance?.ProviderName);
    }

    [Fact]
    public async Task HeuristicAnswersEveryKindAndHonoursDomainRule()
    {
        using var client = new HeuristicDecisionClient((_, q) => q.Id == "route" ? "balanced" : null);
        var response = await client.GetResponseAsync(Request(new { purpose = "chat" }, Tiers,
            new BinaryDecisionQuestion("risky", "Is it risky?"),
            new ScoreDecisionQuestion("quality", "Rate it.", [new("low", "Low"), new("mid", "Mid"), new("high", "High")])));
        Assert.Equal("balanced", Pick(response));
        Assert.Equal(0.5, ((BinaryDecisionAnswer)response.Answers[1]).TrueProbability);
        Assert.Equal(1, ((ScoreDecisionAnswer)response.Answers[2]).Score);
    }

    [Fact]
    public async Task FallbackSkipsFailingAndSlowLinksAndOwnsTheChain()
    {
        var failing = new ScriptedDecision(_ => throw new DecisionClientException("down", isTransient: true));
        var slow = new ScriptedDecision(async ct => { await Task.Delay(TimeSpan.FromSeconds(30), ct); return null!; });
        var fallback = new FallbackDecisionClient([failing, slow, new HeuristicDecisionClient()], timeout: TimeSpan.FromMilliseconds(100));
        var response = await fallback.GetResponseAsync(Request(new { purpose = "chat", inputCharacters = 10 }));
        Assert.Equal("fast", Pick(response));
        Assert.Equal(1, failing.Calls);
        Assert.Equal(1, slow.Calls);
        fallback.Dispose();
        Assert.True(failing.Disposed && slow.Disposed);
    }

    [Fact]
    public async Task FallbackNeverSwallowsCallerCancellation()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        using var fallback = new FallbackDecisionClient([new HeuristicDecisionClient()]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fallback.GetResponseAsync(Request(new { }), null, cancel.Token));
    }

    [Fact]
    public async Task SettingsChainWithZeroModelsIsHeuristicOnly()
    {
        using var client = DecisionClientFactory.Create(new ProviderSettings(), assets: null);
        var chain = Assert.IsType<FallbackDecisionClient>(client).Chain;
        Assert.IsType<HeuristicDecisionClient>(Assert.Single(chain));
        Assert.Equal("strong", Pick(await client.GetResponseAsync(Request(new { purpose = "authoring" }))));
    }

    [Fact]
    public async Task SettingsChainPutsJuliaFirstAndFallsBackWhenItFailsToLoad()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "decision-chain", Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var assets = await SyntheticJuliaAssets(root);
            var loads = 0;
            using var client = DecisionClientFactory.Create(new ProviderSettings(), assets,
                juliaLoader: _ => { loads++; throw new FileNotFoundException("synthetic"); });
            Assert.Equal(["julia", "heuristic"], ((FallbackDecisionClient)client).Chain.Select(FallbackDecisionClient.Name));
            Assert.Equal("fast", Pick(await client.GetResponseAsync(Request(new { purpose = "chat", inputCharacters = 5 }))));
            Assert.Equal(1, loads);

            using var off = DecisionClientFactory.Create(new ProviderSettings { Decisions = new() { Julia = JuliaMode.Off } }, assets);
            Assert.Single(((FallbackDecisionClient)off).Chain);
            using var heuristicOnly = DecisionClientFactory.Create(new ProviderSettings {
                Chat = new() { Routing = new() { Decider = "heuristic" } }
            }, assets);
            Assert.Single(((FallbackDecisionClient)heuristicOnly).Chain);
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    [Fact]
    public async Task LegacyConfigurationAlwaysAnswers()
    {
        using var none = DecisionClientFactory.Create(new DecisionProviderConfiguration(null, null));
        Assert.Equal("fast", Pick(await none.GetResponseAsync(Request(new { purpose = "chat" }))));
        using var missingJulia = DecisionClientFactory.Create(new DecisionProviderConfiguration(
            Path.Combine(AppContext.BaseDirectory, "no-such-julia"), null));
        Assert.Equal("fast", Pick(await missingJulia.GetResponseAsync(Request(new { purpose = "chat" }))));
        Assert.Throws<VideoProduction.VideoException>(() => DecisionClientFactory.CreateStrict(new(null, null)));
    }

    /// <summary>A one-file synthetic julia-1 asset, verified through the real ModelAssets path (no model bytes).</summary>
    internal static async Task<ModelAssets> SyntheticJuliaAssets(string root)
    {
        var bytes = Encoding.UTF8.GetBytes("synthetic");
        var manifest = new ModelManifest {
            Assets =
            [
                new ModelAsset
                {
                    Id = DecisionClientFactory.JuliaAssetId, Revision = "synthetic", Dir = "julia",
                    Files = [new ModelAssetFile { Path = "model.onnx", Url = "https://invalid.example/model.onnx", Size = bytes.Length,
                        Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) }]
                }
            ]
        };
        var assets = new ModelAssets(manifest, root);
        Directory.CreateDirectory(assets.GetDirectory(DecisionClientFactory.JuliaAssetId));
        await File.WriteAllBytesAsync(Path.Combine(assets.GetDirectory(DecisionClientFactory.JuliaAssetId), "model.onnx"), bytes);
        Assert.Equal(ModelAssetState.Verified, (await assets.VerifyAsync(DecisionClientFactory.JuliaAssetId)).State);
        return assets;
    }

    internal sealed class ScriptedDecision(Func<CancellationToken, Task<DecisionResponse>> respond) : IDecisionClient
    {
        public int Calls { get; private set; }
        public bool Disposed { get; private set; }
        public Task<DecisionResponse> GetResponseAsync(DecisionRequest request, DecisionOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return respond(cancellationToken);
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() => Disposed = true;
    }
}
