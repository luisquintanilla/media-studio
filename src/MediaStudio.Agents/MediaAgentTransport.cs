using System.Runtime.CompilerServices;
using System.Text.Json;
using AGUI.Abstractions;
using AGUI.Client;
using AGUI.Server;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using VideoProduction;

namespace MediaStudio.Agents;

public sealed class MediaAgentTransport(AIAgent agent, string threadId, Action<BaseEvent>? observe = null) : IAGUITransport
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public Exception? Failure { get; private set; }
    public Exception? ToolFailure { get; private set; }
    public async IAsyncEnumerable<BaseEvent> SendAsync(RunAgentInput input,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Guard.Require(input.ThreadId == threadId && input.State is not null && !string.IsNullOrWhiteSpace(input.RunId),
            "draft.thread", "Media authoring needs its bound thread/run and explicit current state.");
        if (!await gate.WaitAsync(0, cancellationToken)) throw new VideoException("draft.busy", "Authoring is already running.");
        Failure = null; ToolFailure = null;
        try
        {
            var session = await agent.CreateSessionAsync(cancellationToken);
            var streamOptions = new AGUIStreamOptions();
            foreach (var name in new[] { MediaDraftTool.ToolName }.Concat(MediaDraftTool.NativeToolNames))
                streamOptions.MapResult(name, result =>
                result.Exception is null && result.Result is JsonElement acknowledgement
                    && acknowledgement.ValueKind == JsonValueKind.Object
                    && acknowledgement.TryGetProperty("transaction", out _)
                    && acknowledgement.TryGetProperty("currentDraft", out _)
                    ? [new StateSnapshotEvent { Snapshot = acknowledgement }]
                    : []);
            var context = input.ToChatRequestContext(Json.Options, streamOptions);
            await foreach (var item in ReadUpdates()
                .AsChatResponseUpdatesAsync().AsAGUIEventStreamAsync(context, cancellationToken))
            {
                observe?.Invoke(item);
                yield return item;
            }

            async IAsyncEnumerable<AgentResponseUpdate> ReadUpdates()
            {
                var calls = new HashSet<string>(StringComparer.Ordinal);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                await using var updates = agent.RunStreamingAsync(context.Messages, session,
                    new ChatClientAgentRunOptions { ChatOptions = context.ChatOptions }, cancellationToken)
                    .GetAsyncEnumerator(cancellationToken);
                while (true)
                {
                    bool hasNext;
                    try { hasNext = await updates.MoveNextAsync(); }
                    catch (Exception error) { Failure = error; throw; }
                    if (!hasNext)
                    {
                        if (ToolFailure is { } unresolved)
                        {
                            Failure = unresolved;
                            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(unresolved).Throw();
                        }
                        Guard.Require(calls.Count == 0, "draft.tool-result", "Authoring ended before every draft tool returned its acknowledgment.");
                        break;
                    }
                    var update = updates.Current;
                    try
                    {
                        for (var index = 0; index < update.Contents.Count; index++)
                        {
                            var content = update.Contents[index];
                            if (content is FunctionCallContent call)
                            {
                                Guard.Require(call.Name == MediaDraftTool.ToolName || MediaDraftTool.NativeToolNames.Contains(call.Name),
                                    "draft.tool-name", "The author called an unregistered draft tool; no changes were silently accepted.");
                                Guard.Require(seen.Count < 12 && seen.Add(call.CallId) && calls.Add(call.CallId),
                                    "draft.tool-result", "Only bounded unique media draft tool calls can produce state.");
                            }
                            else if (content is FunctionResultContent result
                                && calls.Remove(result.CallId))
                            {
                                if (result.Exception is { } toolError)
                                {
                                    ToolFailure = toolError;
                                    continue;
                                }
                                if (result.Result is string failure && failure.StartsWith("Error:", StringComparison.Ordinal))
                                {
                                    ToolFailure = new VideoException("draft.tool-failed", "The draft-edit tool failed.");
                                    continue;
                                }
                                // The pinned Copilot adapter exposes successful SDK Result.Content as JSON text.
                                var json = result.Result switch {
                                    string text => text,
                                    JsonElement element => element.GetRawText(),
                                    MediaDraftAcknowledgement typed => JsonSerializer.Serialize(typed, Json.Options),
                                    MediaDraftRejection rejected => JsonSerializer.Serialize(rejected, Json.Options),
                                    _ => throw new VideoException("draft.tool-result", "A complete typed media draft acknowledgment is required.")
                                };
                                Guard.Require(System.Text.Encoding.UTF8.GetByteCount(json) <= 512_000,
                                    "draft.tool-result", "The media acknowledgment exceeded its wire bound.");
                                using var document = JsonDocument.Parse(json);
                                if (document.RootElement.TryGetProperty("code", out var code)
                                    && document.RootElement.TryGetProperty("message", out var message)
                                    && code.GetString() is { } rejectionCode && message.GetString() is { } rejectionMessage
                                    && rejectionCode.StartsWith("motion.", StringComparison.Ordinal))
                                {
                                    ToolFailure = new VideoException(rejectionCode, rejectionMessage);
                                    continue;
                                }
                                var acknowledgment = JsonSerializer.Deserialize<MediaDraftAcknowledgement>(json, Json.Options)
                                    ?? throw new JsonException("A complete media draft acknowledgment is required.");
                                update.Contents[index] = new FunctionResultContent(result.CallId,
                                    JsonSerializer.SerializeToElement(acknowledgment, Json.Options)) {
                                    RawRepresentation = result.RawRepresentation
                                };
                                ToolFailure = null;
                            }
                        }
                    }
                    catch (Exception error) { Failure = error; throw; }
                    yield return update;
                }
            }
        }
        finally { gate.Release(); }
    }
}
