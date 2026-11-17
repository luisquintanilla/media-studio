using Microsoft.Extensions.AI;
using VideoProduction;
using VideoProduction.Providers;

internal static class TestPaths
{
    public static string RepoRoot { get; } = FindRoot();
    public static string Evaluation => Path.Combine(RepoRoot, "tests", "MediaStudio.Tests", "Evaluation");

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "MediaStudio.slnx"))) return dir.FullName;
        throw new InvalidOperationException("Repository root (MediaStudio.slnx) not found above the test output.");
    }

    public static async Task<int> InPrivateRoot(string prefix, Func<string, Task<int>> run)
    {
        var root = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        PrivateFiles.Protect(root, directory: true);
        try { return await run(root); }
        finally { Directory.Delete(root, recursive: true); }
    }
}

internal sealed class FixtureChatClient(string? json = null) : IChatClient
{
    public int Calls { get; private set; }
    public ChatOptions? LastOptions { get; private set; }
    public string? LastInput { get; private set; }
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        LastOptions = options;
        LastInput = messages.Last().Text;
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, json ?? Json.Text(Outline()))) { ModelId = "fixture-no-live-model" });
    }
    public static VideoOutline Outline() => new("Fixture, not a model-generated video",
        [new ProposedScene(TemplateKind.Title, "Fixture", "Inert text", "This is explicitly a test fixture.")]);
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

internal sealed class FixturePipeline(IVideoPlanner planner) : IVideoJobPipeline
{
    public void EnsureConfigured(VideoRequest request)
    {
        if (request.Plan is null) planner.EnsureConfigured();
        else ChatVideoPlanner.Validate(request.Plan, request.Constraints);
    }
    public async Task<VideoArtifact[]> Execute(string jobRoot, VideoRequest request, Action<string> progress, CancellationToken ct)
    {
        progress("fixture-planning");
        var plan = request.Plan is null ? await planner.Plan(request.Prompt, request.Constraints, ct)
            : new PlannedVideo(request.Plan, "caller-supplied-plan");
        Json.Write(Path.Combine(jobRoot, "planning.json"), plan);
        progress("fixture-not-rendering");
        if (request.Prompt == "wait") await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        if (request.Prompt == "fail") throw new VideoException("fixture.failure", "Sensitive sentinel must not reach jobs.");
        if (request.Prompt == "discrepancy") throw new VideoException("transcription.discrepancy", "Private sentinel raw transcript/config must not be exposed.");
        await Task.Delay(80, ct);
        var output = Path.Combine(jobRoot, "fixture.txt");
        Atomic.Write(output, "NOT A VIDEO: lifecycle fixture, no real model/render inference.");
        return [new VideoArtifact("fixture.txt", Files.Hash(output), "test-fixture-not-video")];
    }
}
