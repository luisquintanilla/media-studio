using System.Runtime.CompilerServices;
using System.Text.Json;
using AGUI.Server;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using VideoProduction;
using VideoProduction.Providers;

namespace MediaStudio.Agents;

// Explicit protocol fixture for native acceptance; never selected as a production provider fallback.
public sealed class MediaCanvasFixtureAgent : AIAgent
{
    public TaskCompletionSource? HoldAfterFirst { get; set; }
    public bool FailAfterFirst { get; set; }
    public bool SequentialEdits { get; set; } = true;
    public bool SdkStringToolResults { get; set; } = true;
    public string? RecordedSdkResult { get; init; }
    public string? OverrideSdkToolResult { get; init; }
    public bool RejectAudioIntoVideo { get; set; }
    public bool ClarifyWithoutEditing { get; set; }
    public MotionVisual? MotionEdit { get; set; }
    public bool ClearMotionAfterSet { get; set; }
    public string? MotionCallNameOverride { get; set; }
    public bool OmitMotionAcknowledgment { get; set; }
    public TaskCompletionSource? HoldBeforeFirst { get; set; }
    public List<MediaDraftState> Inputs { get; } = [];
    public List<string> SourceInputs { get; } = [];
    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default) => new(new Session());
    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(AgentSession session, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(JsonElement serializedState, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    protected override Task<AgentResponse> RunCoreAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null,
        AgentRunOptions? options = null, CancellationToken cancellationToken = default) =>
        RunCoreStreamingAsync(messages, session, options, cancellationToken).ToAgentResponseAsync(cancellationToken);
    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(IEnumerable<ChatMessage> messages,
        AgentSession? session = null, AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (options is not ChatClientAgentRunOptions { ChatOptions: { } chat } || !chat.TryGetRunAgentInput(out var input)
            || input.State is not { } graph) throw new InvalidOperationException("Explicit media draft required.");
        var state = graph.Deserialize<MediaDraftState>(Json.Options)!;
        Inputs.Add(state);
        SourceInputs.Add(input.Context?.SingleOrDefault(c => c.Description == "media-source-context-v1")?.Value ?? "");
        if (HoldBeforeFirst is { } before) await before.Task.WaitAsync(cancellationToken);
        if (ClarifyWithoutEditing)
        {
            yield return new(ChatRole.Assistant, "Choose Audio in Output if you want an audio script.\n\nThe selected output is **Video**.\n\n- Choose **Audio** for a script.\n- Choose **Both** for audio and video.\n\nNo draft changes have been made.") { MessageId = "fixture-message" };
            yield break;
        }
        if (MotionEdit is { } motion)
        {
            var motionTool = new MediaDraftTool();
            motionTool.Begin(state, "Animate the existing Diagram through the real native function.", cancellationToken, nativeFunctions: true);
            try
            {
                foreach (var name in ClearMotionAfterSet
                    ? new[] { MediaDraftTool.SetMotionName, MediaDraftTool.ClearMotionName }
                    : new[] { MediaDraftTool.SetMotionName })
                {
                    var function = (AIFunction)motionTool.FunctionsForOutput(state.Frame.Content.Outputs).Single(t => t.Name == name);
                    var arguments = new AIFunctionArguments { ["sceneId"] = state.Frame.SceneIds[0] };
                    if (name == MediaDraftTool.SetMotionName) arguments["motion"] = JsonSerializer.SerializeToElement(motion, Json.Options);
                    var callId = Guid.NewGuid().ToString("N");
                    yield return new(ChatRole.Assistant, [new FunctionCallContent(callId, MotionCallNameOverride ?? name, arguments)]);
                    if (OmitMotionAcknowledgment) continue;
                    var result = await function.InvokeAsync(arguments, cancellationToken);
                    yield return new(ChatRole.Tool, [new FunctionResultContent(callId,
                        SdkStringToolResults ? Json.Text(result) : result)]);
                }
            }
            finally { motionTool.End(); }
            yield break;
        }
        if (RecordedSdkResult is { } recorded)
        {
            var acknowledgment = JsonSerializer.Deserialize<MediaDraftAcknowledgement>(recorded, Json.Options)!;
            var recordedCall = "recorded-owned-call";
            yield return new(ChatRole.Assistant, [new FunctionCallContent(recordedCall, MediaDraftTool.ToolName,
                new Dictionary<string, object?> { ["transaction"] = acknowledgment.Transaction })]);
            yield return new(ChatRole.Tool, [new FunctionResultContent(recordedCall, recorded)]);
            yield break;
        }
        yield return new(ChatRole.Assistant, "Protocol fixture: editing the actual media draft.") { MessageId = "fixture-message" };
        var id = "scene-" + Guid.NewGuid().ToString("N");
        MediaDraftOperation[] edits = state.Frame.Content.Outputs == MediaOutput.Audio
            ? [new(MediaDraftOperationKind.AddPart, Part: new("part-" + Guid.NewGuid().ToString("N"), "A document becomes source text. Draft edits do not authorize production.", 0))]
            : [new(MediaDraftOperationKind.AddScene, id, Scene: new(TemplateKind.Diagram, "Source to storyboard", "Document\nSource context\nReviewed storyboard", "Supplied text informs the draft. Explicit approval remains separate."))];
        if (RejectAudioIntoVideo)
            edits = [new(MediaDraftOperationKind.AddPart, Part: new("part-fixture", "Owned fixture narration.", 0))];
        var transaction = new MediaDraftTransaction("op-" + Guid.NewGuid().ToString("N"), state.DraftId,
            state.ThreadId!, state.ActiveRunId!, state.Version, state.ContentSha256, edits, "Added an explanation scene (protocol fixture)");
        var tool = new MediaDraftTool();
        tool.Begin(state, "Explicit native protocol acceptance", cancellationToken);
        var first = JsonSerializer.SerializeToElement(await tool.Function.InvokeAsync(new AIFunctionArguments {
            ["transaction"] = JsonSerializer.SerializeToElement(transaction, Json.Options)
        }), Json.Options);
        var call = Guid.NewGuid().ToString("N");
        yield return new(ChatRole.Assistant, [new FunctionCallContent(call, MediaDraftTool.ToolName,
            new Dictionary<string, object?> { ["transaction"] = transaction })]);
        yield return new(ChatRole.Tool, [new FunctionResultContent(call,
            OverrideSdkToolResult ?? (SdkStringToolResults ? first.GetRawText() : (object)first))]);
        if (HoldAfterFirst is { } hold) await hold.Task.WaitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (FailAfterFirst) throw new InvalidOperationException("Explicit fixture failure after a complete committed edit.");
        if (SequentialEdits)
        {
            // The second base is agent-observable tool output, never a host-store read.
            var response = first.Deserialize<MediaDraftAcknowledgement>(Json.Options)!;
            var next = new MediaDraftTransaction("op-" + Guid.NewGuid().ToString("N"), response.CurrentDraft.DraftId,
                response.CurrentDraft.ThreadId!, response.CurrentDraft.ActiveRunId!, response.NextVersion, response.NextSha256,
                [new(MediaDraftOperationKind.ReviseDirection, Direction: response.CurrentDraft.Frame.Direction with { Audience = "New developers" })],
                "Refined audience using first tool acknowledgment (protocol fixture)");
            call = Guid.NewGuid().ToString("N");
            yield return new(ChatRole.Assistant, [new FunctionCallContent(call, MediaDraftTool.ToolName,
                new Dictionary<string, object?> { ["transaction"] = next })]);
            var nextResult = await tool.Function.InvokeAsync(new AIFunctionArguments {
                ["transaction"] = JsonSerializer.SerializeToElement(next, Json.Options)
            });
            var nextJson = JsonSerializer.SerializeToElement(nextResult, Json.Options);
            yield return new(ChatRole.Tool, [new FunctionResultContent(call, SdkStringToolResults ? nextJson.GetRawText() : (object)nextJson)]);
        }
        tool.End();
        yield return new(ChatRole.Assistant, "Fixture edit completed. Nothing was generated.") { MessageId = "fixture-message" };
    }
    private sealed class Session : AgentSession;
}
