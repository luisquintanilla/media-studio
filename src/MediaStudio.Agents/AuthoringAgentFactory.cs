using Microsoft.Extensions.AI;

namespace MediaStudio.Agents;

public enum AuthoringRuntime { Maf, Copilot }

public sealed record AgentRuntimeInfo(AuthoringRuntime Runtime, string Provider, string? Model, bool IsCloud = false);

// First version: "maf" wraps a provided IChatClient in a ChatClientAgent; "copilot" keeps the existing
// Copilot SDK path. Both expose the same bounded media-draft tool over AG-UI.
public static class AuthoringAgentFactory
{
    public static CopilotAuthoringAgent CreateMaf(IChatClient chatClient, string model, string providerName,
        MediaDraftTool? draftTool = null)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        return new(new CopilotAuthoringSettings(model, Concierge: true), draftTool: draftTool ?? new(),
            chatClient: chatClient, providerName: providerName);
    }

    public static CopilotAuthoringAgent CreateCopilot(CopilotAuthoringSettings settings, MediaDraftTool? draftTool = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new(settings, draftTool: draftTool ?? new());
    }

    public static AgentRuntimeInfo Describe(CopilotAuthoringAgent agent, AuthoringRuntime runtime, string provider)
        => new(runtime, provider, agent.ConfiguredModel, agent.IsCloud);
}
