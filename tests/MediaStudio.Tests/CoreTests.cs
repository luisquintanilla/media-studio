namespace MediaStudio.Tests;

public sealed class CoreTests
{
    private static async Task Passes(string prefix, Func<string, Task<int>> run) =>
        Assert.True(await TestPaths.InPrivateRoot(prefix, run) > 0);

    [Fact] public Task Routing() => Passes("routing-", RoutingTests.Run);
    [Fact] public Task ChatRouting() => Passes("chat-routing-", ChatRoutingTests.Run);
    [Fact] public Task Providers() => Passes("providers-", ProviderTests.Run);
    [Fact] public Task ChatProviders() => Passes("chat-providers-", ChatProviderTests.Run);
    [Fact] public Task DocumentSources() => Passes("sources-", DocumentSourceTests.Run);
    [Fact] public Task MediaJudgement() => Passes("judgement-", MediaJudgementTests.Run);
    [Fact] public Task MediaJudgementEvaluation() => Passes("judgement-eval-", MediaJudgementEvaluationTests.Run);
    [Fact] public Task MediaDraft() => Passes("draft-", root => Task.FromResult(MediaDraftTests.Run(root)));
    [Fact] public Task MediaProjects() => Passes("projects-", root => MediaProjectTests.Run(root));
    [Fact] public Task AudioAdmission() => Passes("audio-admission-", AudioAdmissionTests.Run);
    [Fact] public Task CliAdapter() => Passes("cli-adapter-", CliAdapterTests.Run);

    [Fact, Trait("Category", "Julia")]
    public void JuliaTokenizerCompatibility()
    {
        var tokenizer = Environment.GetEnvironmentVariable("MEDIASTUDIO_JULIA_TOKENIZER");
        Assert.False(string.IsNullOrWhiteSpace(tokenizer), "Set MEDIASTUDIO_JULIA_TOKENIZER to the pinned Julia tokenizer.json.");
        Assert.True(JuliaTokenizerCompatibilityTests.Run(tokenizer!,
            Path.Combine(TestPaths.RepoRoot, "tests", "VideoProduction.Decisions.Tests", "Evidence")) > 0);
    }
}
