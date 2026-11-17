using System.Text.Json;
using AGUI.Abstractions;
using AGUI.Client;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.GitHub.Copilot;
using Microsoft.Extensions.AI;
using MediaStudio.Agents;
using VideoProduction;
using VideoProduction.Providers;

namespace MediaStudio.Tests.Agents;

public sealed class AgentTransportTests
{
    [Fact]
    public Task MediaDraftToolContract() => MediaDraftToolProof.Run();

    [Fact]
    public async Task SdkTextAcknowledgmentsProduceSequentialFrameworkSnapshots()
    {
        var (state, input) = MediaInput();
        var events = await Collect(new MediaAgentTransport(new MediaCanvasFixtureAgent { SdkStringToolResults = true },
            state.ThreadId!).SendAsync(input));
        Assert.IsType<RunFinishedEvent>(events.Last());
        Assert.Empty(events.OfType<RunErrorEvent>());
        Assert.Equal(2, events.OfType<StateSnapshotEvent>().Count());
        var ack = events.OfType<StateSnapshotEvent>().Last().Snapshot.Deserialize<MediaDraftAcknowledgement>(Json.Options)!;
        Assert.Equal(state.Version + 2, ack.NextVersion);
        Assert.Equal("New developers", ack.CurrentDraft.Frame.Direction.Audience);
    }

    [Fact]
    public async Task MalformedSdkTextResultCannotBecomeStateOrSuccess()
    {
        var (state, input) = MediaInput();
        var events = await Collect(new MediaAgentTransport(new MediaCanvasFixtureAgent { OverrideSdkToolResult = "{}" },
            state.ThreadId!).SendAsync(input));
        Assert.IsType<RunErrorEvent>(events.Last());
        Assert.Empty(events.OfType<RunFinishedEvent>());
        Assert.Empty(events.OfType<StateSnapshotEvent>());
    }

    [Fact]
    public async Task StoryboardTransportStreamsOrderedTextToolAndStateEvents()
    {
        var fixture = new FixtureAgent();
        var transport = new InProcessAgentTransport(fixture, "private-fixture");
        var state = StoryboardRules.Sample();
        var events = await Collect(transport.SendAsync(Input(state)));
        Assert.IsType<RunStartedEvent>(events.First());
        Assert.IsType<RunFinishedEvent>(events.Last());
        Assert.Contains(events.OfType<TextMessageContentEvent>(), e => e.Delta.Contains("Reviewing"));
        Assert.Single(events.OfType<ToolCallStartEvent>());
        Assert.Single(events.OfType<ToolCallArgsEvent>());
        Assert.Single(events.OfType<ToolCallEndEvent>());
        Assert.Single(events.OfType<ToolCallResultEvent>());
        var proposed = events.OfType<StateSnapshotEvent>().Single().Snapshot.Deserialize<StoryboardState>(StoryboardJson.Options)!;
        StoryboardRules.ValidateProposal(state, proposed.Proposal!);
        Assert.True(proposed.Proposal!.Scenes[0].Script.Length < state.Scenes[0].Script.Length);
        Assert.Equal(state.Scenes[1], proposed.Proposal.Scenes[1]);

        using var client = new AGUIChatClient(new AGUIChatClientOptions { Transport = transport });
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Shorten scene one.")],
            new ChatOptions { RawRepresentationFactory = _ => Input(state) });
        Assert.Contains("Every other field is unchanged", response.Text);
        Assert.Same(fixture.ObservedSessions[0], fixture.ObservedSessions[1]);

        fixture.PauseAfterText = new();
        await using (var stream = transport.SendAsync(Input(state)).GetAsyncEnumerator())
        {
            while (await stream.MoveNextAsync())
                if (stream.Current is TextMessageContentEvent) break;
            Assert.False(fixture.PauseAfterText.Task.IsCompleted, "Text was buffered until completion.");
        }
        Assert.Equal(3, fixture.DisposedRuns);
        fixture.PauseAfterText = null;

        using (var cancelled = new CancellationTokenSource())
        {
            fixture.PauseAfterText = new();
            await using var stream = transport.SendAsync(Input(state), cancelled.Token).GetAsyncEnumerator();
            while (await stream.MoveNextAsync())
                if (stream.Current is TextMessageContentEvent) break;
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await stream.MoveNextAsync());
            fixture.PauseAfterText = null;
        }

        fixture.Fail = true;
        var failure = await Collect(transport.SendAsync(Input(state)));
        Assert.IsType<RunErrorEvent>(failure.Last());
        Assert.Empty(failure.OfType<RunFinishedEvent>());
        fixture.Fail = false;

        var changed = state with { Scenes = [state.Scenes[0], state.Scenes[1] with { Text = "A latest unsaved user edit." }] };
        await Collect(transport.SendAsync(Input(changed)));
        Assert.Contains("A latest unsaved user edit.", fixture.ObservedStates.Last());
    }

    [Fact]
    public void CopilotAdapterExposesPublicAgentSurface()
    {
        Assert.True(typeof(GitHubCopilotAgent).IsAssignableTo(typeof(AIAgent)));
        Assert.NotNull(typeof(GitHubCopilotAgent).GetMethod("CreateSessionAsync", [typeof(string)]));
    }

    [Fact]
    public async Task MafRuntimeWrapsProvidedChatClient()
    {
        using var client = new FixtureChatClient();
        await using var agent = AuthoringAgentFactory.CreateMaf(client, "fixture-model", "Fixture");
        var info = AuthoringAgentFactory.Describe(agent, AuthoringRuntime.Maf, "Fixture");
        Assert.Equal(new AgentRuntimeInfo(AuthoringRuntime.Maf, "Fixture", "fixture-model"), info);
        Assert.Equal(info, agent.GetService<AgentRuntimeInfo>());
        Assert.Equal(info, AgentPipeline.Wrap(agent, allowCloud: false).GetService<AgentRuntimeInfo>());
    }

    private static RunAgentInput Input(StoryboardState board) => new() {
        ThreadId = "private-fixture", RunId = Guid.NewGuid().ToString("N"),
        State = JsonSerializer.SerializeToElement(board, StoryboardJson.Options),
        Messages = [], Tools = [], Context = []
    };

    private static (MediaDraftState State, RunAgentInput Input) MediaInput()
    {
        var frame = MediaDraftRules.FromContent(new("SDK result fixture", "", "", "", MediaOutput.Video, 300, null, null));
        var state = new MediaDraftState(1, "draft-" + new string('d', 32), null, 0, 3,
            Files.Fingerprint(frame), frame, [], [], "run-fixture", "thread-fixture");
        return (state, new RunAgentInput {
            ThreadId = state.ThreadId!, RunId = state.ActiveRunId!,
            State = JsonSerializer.SerializeToElement(state, Json.Options),
            Messages = [], Tools = [], Context = []
        });
    }

    private static async Task<List<BaseEvent>> Collect(IAsyncEnumerable<BaseEvent> events)
    {
        var list = new List<BaseEvent>();
        await foreach (var item in events) list.Add(item);
        return list;
    }
}
