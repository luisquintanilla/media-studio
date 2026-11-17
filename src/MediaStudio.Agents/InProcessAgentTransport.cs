using System.Runtime.CompilerServices;
using AGUI.Abstractions;
using AGUI.Client;
using AGUI.Server;
using Microsoft.Agents.AI;

namespace MediaStudio.Agents;

public sealed class InProcessAgentTransport(AIAgent agent, string threadId, bool mapToolSnapshot = true,
    Action<BaseEvent>? observeEvent = null) : IAGUITransport
{
    private readonly SemaphoreSlim runGate = new(1, 1);
    private AgentSession? session;

    public async IAsyncEnumerable<BaseEvent> SendAsync(RunAgentInput input,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (input.ThreadId != threadId)
            throw new InvalidOperationException("This private transport is bound to one storyboard thread.");
        if (!await runGate.WaitAsync(0, cancellationToken))
            throw new InvalidOperationException("An authoring run is already active.");
        try
        {
            session ??= await agent.CreateSessionAsync(cancellationToken);
            var streamOptions = new AGUIStreamOptions();
            if (mapToolSnapshot) streamOptions.MapResultAsStateSnapshot("propose_storyboard");
            var context = input.ToChatRequestContext(
                StoryboardJson.Options, streamOptions);
            await foreach (var item in agent.RunStreamingAsync(
                context.Messages, session,
                new ChatClientAgentRunOptions { ChatOptions = context.ChatOptions },
                cancellationToken)
                .AsChatResponseUpdatesAsync()
                .AsAGUIEventStreamAsync(context, cancellationToken))
            {
                observeEvent?.Invoke(item);
                yield return item;
            }
        }
        finally
        {
            runGate.Release();
        }
    }
}
