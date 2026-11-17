using System.Runtime.CompilerServices;
using System.Text.Json;
using AGUI.Server;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace MediaStudio.Agents;

public sealed class FixtureAgent : AIAgent
{
    public override string Name => "Deterministic storyboard fixture (no model)";
    public List<string> ObservedStates { get; } = [];
    public List<AgentSession?> ObservedSessions { get; } = [];
    public int DisposedRuns { get; private set; }
    public bool Fail { get; set; }
    public TaskCompletionSource? PauseAfterText { get; set; }

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
        => new(new FixtureSession());

    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(AgentSession session,
        JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The deterministic fixture only retains sessions within this process.");

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(JsonElement serializedState,
        JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The deterministic fixture does not resume saved model sessions.");

    protected override Task<AgentResponse> RunCoreAsync(IEnumerable<ChatMessage> messages,
        AgentSession? session = null, AgentRunOptions? options = null, CancellationToken cancellationToken = default)
        => RunCoreStreamingAsync(messages, session, options, cancellationToken).ToAgentResponseAsync(cancellationToken);

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (options is not ChatClientAgentRunOptions { ChatOptions: { } chatOptions }
            || !chatOptions.TryGetRunAgentInput(out var input) || input.State is not { } state)
            throw new InvalidOperationException("Current editable storyboard state is required.");
        ObservedStates.Add(state.GetRawText());
        ObservedSessions.Add(session);
        var messageId = Guid.NewGuid().ToString("N");
        try
        {
            yield return new AgentResponseUpdate(ChatRole.Assistant, "Reviewing scene one. ") { MessageId = messageId };
            if (PauseAfterText is { } pause)
                await pause.Task.WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (Fail)
                throw new InvalidOperationException("Deliberate fixture failure.");

            var board = state.Deserialize<StoryboardState>(StoryboardJson.Options)
                ?? throw new JsonException("Missing storyboard.");
            StoryboardRules.Validate(board);
            var first = board.Scenes[0];
            var words = first.Script.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length < 2)
                throw new InvalidOperationException("The fixture cannot shorten a single-word scene. Edit its narration before retrying.");
            var script = string.Join(' ', words.Take(Math.Min(5, words.Length - 1))).TrimEnd('.', ',') + ".";
            var proposal = new StoryboardProposal(Guid.NewGuid().ToString("N"), board.Revision,
                StoryboardRules.Fingerprint(board), board.Scenes.Select((scene, index) =>
                    index == 0 ? scene with { Script = script } : scene).ToArray());
            var callId = Guid.NewGuid().ToString("N");
            yield return new AgentResponseUpdate(ChatRole.Assistant,
                [new FunctionCallContent(callId, "propose_storyboard",
                    new Dictionary<string, object?> { ["sceneId"] = first.Id, ["script"] = script })]);
            yield return new AgentResponseUpdate(ChatRole.Tool,
                [new FunctionResultContent(callId,
                    JsonSerializer.SerializeToElement(board with { Proposal = proposal }, StoryboardJson.Options))]);
            yield return new AgentResponseUpdate(ChatRole.Assistant,
                "Every other field is unchanged. Review this proposal; nothing has been saved.") { MessageId = messageId };
        }
        finally
        {
            DisposedRuns++;
        }
    }

    private sealed class FixtureSession : AgentSession;
}
