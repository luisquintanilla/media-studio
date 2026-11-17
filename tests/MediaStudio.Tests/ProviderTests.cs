using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using VideoProduction;
using VideoProduction.Providers;
using VideoProduction.Providers.Experimental;
using VideoProduction.AgentFramework;
using Microsoft.Agents.AI;

internal static class ProviderTests
{
    public const string Svg = """
        <svg xmlns="http://www.w3.org/2000/svg" width="1920" height="1080" viewBox="0 0 1920 1080">
          <rect x="0" y="0" width="1920" height="1080" fill="#080C1A"/>
          <rect x="120" y="260" width="480" height="240" rx="20" fill="#132D46" stroke="#64D8F2" stroke-width="4"/>
          <rect x="1040" y="260" width="720" height="240" rx="20" fill="#132D46" stroke="#64D8F2" stroke-width="4"/>
          <line x1="640" y1="380" x2="1000" y2="380" stroke="#FFDF70" stroke-width="8"/>
          <text x="140" y="160" fill="#64D8F2" font-size="64">Token-aware chunking</text>
          <text x="160" y="350" fill="#FFFFFF" font-size="40">Document</text>
          <text x="1080" y="350" fill="#FFFFFF" font-size="40">Passages + source links</text>
          <text x="140" y="700" fill="#FFFFFF" font-size="36">Illustration: chat SVG adapter / deterministic test client</text>
        </svg>
        """;
    public static async Task<int> Run(string root)
    {
        var count = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); count++; }
        async Task Reject(Func<Task> work, string code)
        {
            try { await work(); throw new Exception("Expected " + code); }
            catch (VideoException e) { Check(e.Code == code, "Expected " + code + ", got " + e.Code); }
        }
        Check(SafeSvg.Validate(Svg).Contains("Token-aware chunking", StringComparison.Ordinal), "Bounded safe SVG retains actual inert artwork");
        Check(SafeSvg.Validate(Svg.Replace("<rect ", "<!-- harmless group label --><rect ", StringComparison.Ordinal))
            == SafeSvg.Validate(Svg), "New explicit SVG policy structurally discards bounded inert comments");
        Check(SafeSvg.Validate(Svg.Replace("<rect ", "<!-- <script>bad()</script><image href='https://example.com'/> --><rect ",
            StringComparison.Ordinal)) == SafeSvg.Validate(Svg),
            "Markup inside XML comments remains inert/discarded, never parsed as active elements");
        foreach (var bad in new[]
        {
            Svg.Replace("<rect ", "<script ", StringComparison.Ordinal),
            Svg.Replace("fill=\"#080C1A\"", "onload=\"alert(1)\"", StringComparison.Ordinal),
            Svg.Replace("fill=\"#080C1A\"", "fill=\"url(http://example.com)\"", StringComparison.Ordinal),
            Svg.Replace("<line ", "<image href=\"file:///secret\" ", StringComparison.Ordinal),
            Svg.Replace("font-size=\"64\"", "style=\"font-size:64px\"", StringComparison.Ordinal),
            Svg.Replace("stroke-width=\"8\"", "stroke-width=\"8\" stroke-linecap=\"round\"", StringComparison.Ordinal),
            Svg.Replace("font-size=\"64\"", "font-size=\"NaN\"", StringComparison.Ordinal),
            Svg.Replace("font-size=\"64\"", "font-size=\"1000000\"", StringComparison.Ordinal),
            Svg.Replace("width=\"1920\"", "width=\"3840\"", StringComparison.Ordinal),
            "<!DOCTYPE svg [<!ENTITY x SYSTEM 'file:///secret'>]>" + Svg,
            "<?processing bad?>" + Svg, "```svg\n" + Svg + "\n```",
            Svg.Replace("Document", "<![CDATA[Document]]>", StringComparison.Ordinal),
            Svg.Replace("Document", "<foreignObject>Document</foreignObject>", StringComparison.Ordinal),
            new string('x', 32001)
        })
            await Reject(() => Task.Run(() => SafeSvg.Validate(bad)), "image.svg");
        using var chat = new SvgFixtureClient(Svg.Replace("fill=\"#080C1A\"", "onload=\"bad()\"", StringComparison.Ordinal));
        using var image = new ChatSvgImageGenerator(chat, "fixture-not-live", new(root, "dotnet", root, "unused", "unused"));
        await Reject(() => image.GenerateAsync(new("A technical illustration")), "image.svg");
        Check(chat.Calls == 1 && chat.LastOptions!.ModelId == "fixture-not-live" && chat.LastOptions.Tools is null,
            "Official IImageGenerator invokes injected IChatClient with explicit model/no tools; malformed SVG stops before raster");
        await Reject(() => image.GenerateAsync(new("p", [])), "image.request");
        foreach (var invalid in new[]
        {
            new ImageGenerationOptions { Count = 2 }, new() { MediaType = "image/svg+xml" },
            new() { ImageSize = new(100, 100) }, new() { ResponseFormat = ImageGenerationResponseFormat.Uri },
            new() { ModelId = "different" }, new() { StreamingCount = 1 }, new() { AdditionalProperties = [] }
        })
            await Reject(() => image.GenerateAsync(new("p"), invalid), "image.options");
        Check(chat.Calls == 1, "Unsupported image options reject before another chat/resource operation");
        foreach (var unsupported in new AIContent[]
            { new FunctionCallContent("fixture-call", "unapproved-tool"), new DataContent(new byte[] { 1 }, "image/png") })
        {
            using var unsafeChat = new SvgFixtureClient(Svg, [unsupported]);
            using var unsafeImage = new ChatSvgImageGenerator(unsafeChat, "fixture-not-live", new(root, "unused-node", root, "unused", "unused"));
            await Reject(() => unsafeImage.GenerateAsync(new("Bounded illustration")), "image.output");
        }
        using (var reasoningOnly = new SvgFixtureClient(Svg, [new TextReasoningContent(Svg)], omitSvg: true))
        using (var reasoningImage = new ChatSvgImageGenerator(reasoningOnly, "fixture-not-live", new(root, "unused-node", root, "unused", "unused")))
            await Reject(() => reasoningImage.GenerateAsync(new("Reasoning is not an SVG answer")), "image.svg");
        using (var agentChat = new SvgFixtureClient("<svg>invalid</svg>"))
        {
            var agent = new ChatClientAgent(agentChat);
            using var bridge = new AgentSvgImageGenerator(agent, "fixture-agent-not-live",
                new(root, "dotnet", root, "unused", "unused"), new(true, true, true));
            await Reject(() => bridge.GenerateAsync(new("first isolated request")), "image.svg");
            await Reject(() => bridge.GenerateAsync(new("second isolated request")), "image.svg");
            Check(agentChat.Calls == 2 && agentChat.InputCounts.SequenceEqual(new[] { 2, 2 }),
                "Official AIAgent.AsIChatClient executes shared image validator with fresh per-request messages, not shared adapter history");
            await Reject(() => Task.Run(() => new AgentSvgImageGenerator(agent, "fixture-agent",
                new(root, "dotnet", root, "unused", "unused"), new(false, true, true))), "image.agent-policy");
        }
        await Reject(() => Task.Run(() =>
        {
            using var azure = ChatVideoPlanner.CreateClient(new("https://example.com/openai/v1/", "gpt-4.1", null,
                true, "azure-cli"));
        }), "planner.config");
        using (var azure = ChatVideoPlanner.CreateClient(new("https://fixture.openai.azure.com/openai/v1/", "gpt-4.1", null,
            true, "azure-cli")))
            Check(azure is not null, "Explicit trusted AzureCLI client construction needs no key/token/inference until used");
        var copilot = MediaStudio.Agents.Copilot.CopilotSessionPolicy.Create(MediaStudio.Agents.Copilot.AgentRole.Illustration,
            "explicit-test-model-not-invoked", [], null, Path.Combine(root, "copilot-work"));
        Check(copilot.AvailableTools is { Count: 0 } && copilot.Tools is { Count: 0 } && copilot.McpServers is { Count: 0 },
            "Compiled official Copilot factory registers no tools/MCP backends, without starting SDK or model");
#pragma warning disable GHCP001
        var denied = await copilot.OnPermissionRequest!(null!, null!);
        Check(denied is GitHub.Copilot.Rpc.PermissionDecisionReject, "Copilot factory rejects every permission, never ApproveAll");
#pragma warning restore GHCP001
        var deniedTool = await copilot.Hooks!.OnPreToolUse!(null!, null!);
        Check(deniedTool!.PermissionDecision == "deny", "Copilot pre-tool hook denies even unregistered/inherited tools");

        var jobConfig = new VideoJobConfiguration(Path.Combine(root, "provider-state"), 3);
        var planner = new ChatVideoPlanner(new FixtureChatClient(), "fixture");
        using var jobs = new VideoJobs(jobConfig, new FixturePipeline(planner), NullLogger<VideoJobs>.Instance);
        await jobs.StartAsync(CancellationToken.None);
        using var provider = new CompositeVideoGenerator(jobs, jobConfig);
        IVideoGenerator seam = provider;
        var local = new LocalVideoGenerationOptions { IdempotencyKey = "direct-fixture", Plan = FixtureChatClient.Outline() };
        var operation = await seam.GenerateAsync(new("direct-fixture"), local);
        var replay = await seam.GenerateAsync(new("direct-fixture"), local);
        Check(operation.OperationId == replay.OperationId && seam.GetService(typeof(IVideoGenerator)) == seam,
            "Direct proposal-shaped API reuses actual durable idempotency and service identity");
        await operation.WaitForCompletionAsync();
        Check(operation.IsCompleted && operation.Status == "completed" && operation.PercentComplete == 100,
            "Operation observes underlying job completion (fixture backend, not media proof)");
        var wait = (LocalVideoOperation)await seam.GenerateAsync(new("wait"),
            new LocalVideoGenerationOptions { IdempotencyKey = "direct-wait", Plan = FixtureChatClient.Outline() });
        using (var cancelledWait = new CancellationTokenSource(30))
        {
            try { await wait.WaitForCompletionAsync(cancellationToken: cancelledWait.Token); throw new Exception("Expected wait cancellation"); }
            catch (OperationCanceledException) { Check(wait.Status is "queued" or "running", "Cancelling the wait does NOT cancel the durable generation job"); }
        }
        wait.Cancel();
        for (var i = 0; i < 100 && !wait.IsCompleted; i++) await Task.Delay(10);
        Check(wait.Status == "cancelled", "Explicit operation Cancel cooperates with the same shared job service");
        await Reject(() => seam.GenerateAsync(new("p") { EndFrame = new DataContent(new byte[] { 1 }, "image/png") }, local), "video.unsupported");
        await Reject(() => seam.GenerateAsync(new("p") { OperationKind = VideoOperationKind.Edit }, local), "video.unsupported");
        await Reject(() => seam.GenerateAsync(new("p") { SourceVideoId = "fake" }, local), "video.unsupported");
        await Reject(() => seam.GenerateAsync(new("p"), new LocalVideoGenerationOptions { IdempotencyKey = "x", Seed = 1 }), "video.unsupported");
        await Reject(() => seam.GenerateAsync(new("p"), new LocalVideoGenerationOptions { IdempotencyKey = "x", GenerateAudio = false }), "video.unsupported");
        await Reject(() => seam.GenerateAsync(new("p"), new LocalVideoGenerationOptions { IdempotencyKey = "x", Duration = TimeSpan.FromSeconds(5.5) }), "video.options");
        try { operation.CreateEditRequest("bad"); throw new Exception("Expected unsupported edit"); }
        catch (NotSupportedException) { Check(true, "No misleading model-edit follow-up request"); }
        await jobs.StopAsync(CancellationToken.None);

        // Pre-provider JSON deliberately omits illustrationPrompt. Do not regenerate it with the new DTO.
        const string oldRequest = """
            {"prompt":"old persisted request","constraints":{"maxDurationSeconds":20,"maxScenes":2},"plan":{"title":"Old","scenes":[{"kind":"title","title":"Old","text":"Old display","script":"Old narration."}]}}
            """;
        var old = JsonSerializer.Deserialize<VideoRequest>(oldRequest, Json.Options)!;
        using var oldJson = JsonDocument.Parse(oldRequest);
        var priorCanonical = JsonSerializer.Serialize(oldJson.RootElement, Json.Options);
        Check(Files.HashText(priorCanonical) == Files.Fingerprint(old), "Old-schema canonical request bytes unchanged by absent optional illustration field");
        var oldRoot = Path.Combine(root, "old-provider-state");
        var oldConfig = new VideoJobConfiguration(oldRoot);
        using (var initialize = new VideoJobs(oldConfig, new FixturePipeline(planner), NullLogger<VideoJobs>.Instance)) { }
        var id = "job-" + new string('a', 32);
        var folder = Path.Combine(oldRoot, "jobs", id);
        Directory.CreateDirectory(folder);
        // This is an old-schema persisted record, not a newly serialized VideoJob.
        var record = $$"""
            {"id":"{{id}}","idempotencyHash":"{{Files.HashText("old-key")}}","requestHash":"{{Files.HashText(priorCanonical)}}","request":{{oldRequest}},"status":"completed","stage":"finished","createdAt":"2026-10-01T00:00:00Z","updatedAt":"2026-10-01T00:00:00Z","cancellationRequested":false,"artifacts":[],"errorCode":null}
            """;
        File.WriteAllText(Path.Combine(folder, "job.json"), record);
        using (var upgraded = new VideoJobs(oldConfig, new FixturePipeline(planner), NullLogger<VideoJobs>.Instance))
        {
            Check(upgraded.Submit(old.Prompt, "old-key", old.Constraints, old.Plan).JobId == id,
                "Old-schema persisted terminal job replays unchanged after provider upgrade");
            await Reject(() => Task.Run(() => upgraded.Submit(old.Prompt, "old-key", old.Constraints,
                old.Plan! with { Scenes = [old.Plan.Scenes[0] with { IllustrationPrompt = "New image" }] })), "jobs.idempotency-conflict");
        }
        return count;
    }
}

internal sealed class SvgFixtureClient(string svg, AIContent[]? extra = null, bool omitSvg = false) : IChatClient
{
    public int Calls { get; private set; }
    public ChatOptions? LastOptions { get; private set; }
    public List<int> InputCounts { get; } = [];
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        InputCounts.Add(messages.Count());
        LastOptions = options;
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [.. omitSvg ? Array.Empty<AIContent>() : new AIContent[] { new TextContent(svg) }, .. extra ?? []])) { ModelId = "deterministic-svg-fixture-NOT-live-inference" });
    }
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
