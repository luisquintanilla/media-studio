using GitHub.Copilot;
using Microsoft.Extensions.AI;

namespace MediaStudio.Agents.Copilot;

#pragma warning disable GHCP001

/// <summary>What a Copilot session is used for. Each role gets the same locks; only the tool allow-list differs.</summary>
public enum AgentRole
{
    /// <summary>Storyboard/media authoring: only the role's bounded draft tool is allowed.</summary>
    Authoring,
    /// <summary>SVG illustration: no tools at all.</summary>
    Illustration,
    /// <summary>A router chat tier: no tools at all, stateless.</summary>
    Tier
}

/// <summary>
/// The locked-down Copilot <see cref="SessionConfig"/> (moved from the original authoring agent): only the role's
/// tools are available, no MCP servers, custom agents, skills, config/instruction discovery, file hooks, git or
/// session store, no infinite sessions, the system message is replaced (never Copilot's coding-agent prompt),
/// every permission request is rejected, and a pre-tool hook denies anything outside the allow-list.
/// </summary>
public static class CopilotSessionPolicy
{
    public const string DefaultTierInstructions =
        "You are a careful writing and reasoning assistant inside a media studio. Answer only from the supplied conversation. You have no tools, files, shell or network.";
    public const string IllustrationInstructions =
        "You draw a single self-contained SVG illustration for a teaching video. Return only the SVG markup. You have no tools, files, shell or network.";

    public static SessionConfig Create(AgentRole role, string? model, IReadOnlyList<AIFunction> tools, string? instructions,
        string workingDirectory, ProviderConfig? provider = null, bool streaming = true)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        if (role != AgentRole.Authoring && tools.Count > 0)
            throw new ArgumentException("Only the authoring role may register tools.", nameof(tools));
        var allowed = tools.Select(t => t.Name).ToArray();
        return new SessionConfig {
            Model = string.IsNullOrWhiteSpace(model) ? null : model,
            Provider = provider,
            Streaming = streaming,
            AvailableTools = [.. allowed],
            Tools = [.. tools],
            EnableConfigDiscovery = false,
            EnableOnDemandInstructionDiscovery = false,
            EnableFileHooks = false,
            EnableSkills = false,
            EnableHostGitOperations = false,
            EnableSessionStore = false,
            SkipCustomInstructions = true,
            SkipEmbeddingRetrieval = true,
            WorkingDirectory = workingDirectory,
            AdditionalDirectories = [],
            McpServers = new Dictionary<string, McpServerConfig>(),
            CustomAgents = [],
            InfiniteSessions = new InfiniteSessionConfig { Enabled = false },
            SystemMessage = new SystemMessageConfig {
                Mode = SystemMessageMode.Replace,
                Content = instructions ?? (role == AgentRole.Illustration ? IllustrationInstructions : DefaultTierInstructions)
            },
            OnPermissionRequest = (_, _) => Task.FromResult<GitHub.Copilot.Rpc.PermissionDecision>(
                new GitHub.Copilot.Rpc.PermissionDecisionReject()),
            Hooks = new SessionHooks {
                OnPreToolUse = (input, _) => Task.FromResult<PreToolUseHookOutput?>(new() {
                    PermissionDecision = input is not null && allowed.Contains(input.ToolName, StringComparer.Ordinal) ? "allow" : "deny",
                    PermissionDecisionReason = allowed.Length == 0
                        ? "This Copilot session has no tools; every tool is denied."
                        : "Only the inert revision-bound proposal capability is permitted; it cannot save or execute."
                })
            }
        };
    }
}
