using System.Runtime.CompilerServices;
using System.Text.Json;
using Azure.Core;
using GitHub.Copilot;
using MediaStudio.Configuration;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.GitHub.Copilot;
using Microsoft.Extensions.AI;
using VideoProduction;

namespace MediaStudio.Agents.Copilot;

#pragma warning disable GHCP001

/// <summary>
/// Creates locked-down GitHub Copilot <see cref="AIAgent"/>s for a role. Subscription mode uses the user's
/// Copilot models; BYOK points the same harness at a configured connection (Ollama or Foundry) instead.
/// </summary>
public sealed class CopilotAgentFactory
{
    private readonly CopilotClientFactory clients;
    private readonly Func<ConnectionSettings, TokenCredential> credentials;
    private readonly Func<string, string> apiKeys;

    public CopilotAgentFactory(CopilotClientFactory clients, Func<ConnectionSettings, TokenCredential>? credentials = null,
        Func<string, string>? apiKeys = null)
    {
        ArgumentNullException.ThrowIfNull(clients);
        this.clients = clients;
        this.credentials = credentials ?? CredentialFactory.CreateTokenCredential;
        this.apiKeys = apiKeys ?? (id => CredentialFactory.GetApiKey(id));
    }

    public CopilotClientFactory Clients => clients;
    public CopilotAgentSettings Settings => clients.Settings.Agents.Copilot;

    public AgentRuntimeInfo RuntimeInfo(string? model = null) => new(AuthoringRuntime.Copilot,
        clients.IsByok ? "copilot-byok:" + Settings.ByokConnection : "github-copilot", model ?? Settings.Model, clients.IsCloud);

    /// <summary>BYOK provider for the configured connection; null for the subscription backend.</summary>
    public ProviderConfig? CreateProvider()
    {
        if (!clients.IsByok) return null;
        var id = Settings.ByokConnection;
        var connection = clients.Settings.FindConnection(id);
        Guard.Require(connection is not null, "copilot.byok-connection", $"BYOK connection '{id}' is not configured.");
        return CreateByokProvider(connection!, credentials, apiKeys);
    }

    /// <summary>
    /// Ollama/openai-compatible → <c>openai</c> + chat completions (no key unless the connection uses api-key);
    /// Foundry → <c>openai</c> + responses on <see cref="FoundryEndpoints.OpenAIv1"/> with an Entra bearer provider or API key.
    /// </summary>
    public static ProviderConfig CreateByokProvider(ConnectionSettings connection,
        Func<ConnectionSettings, TokenCredential>? credentials = null, Func<string, string>? apiKeys = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        credentials ??= CredentialFactory.CreateTokenCredential;
        apiKeys ??= id => CredentialFactory.GetApiKey(id);
        switch (connection.Kind)
        {
            case ConnectionKind.OpenAICompatible:
                Guard.Require(connection.Endpoint is not null, "copilot.byok-connection", $"Connection '{connection.Id}' has no endpoint.");
                return new ProviderConfig {
                    Type = "openai",
                    BaseUrl = connection.Endpoint!.TrimEnd('/'),
                    WireApi = "completions",
                    ApiKey = connection.Auth == AuthMode.ApiKey ? apiKeys(connection.Id) : null
                };
            case ConnectionKind.Foundry:
                var endpoints = FoundryEndpoints.From(connection);
                var provider = new ProviderConfig {
                    Type = "openai",
                    BaseUrl = endpoints.OpenAIv1.AbsoluteUri.TrimEnd('/'),
                    WireApi = "responses"
                };
                if (connection.Auth == AuthMode.ApiKey) provider.ApiKey = apiKeys(connection.Id);
                else
                {
                    var credential = credentials(connection);
                    provider.BearerTokenProvider = async _ => (await credential.GetTokenAsync(
                        new TokenRequestContext([FoundryEndpoints.CognitiveServicesScope]), CancellationToken.None).ConfigureAwait(false)).Token;
                }
                return provider;
            default:
                throw new VideoException("copilot.byok-connection", "BYOK needs a local (openai-compatible) or Foundry connection, not GitHub Copilot itself.");
        }
    }

    public SessionConfig CreateSessionConfig(AgentRole role, IReadOnlyList<AIFunction> tools, string? instructions, string? model = null)
        => CopilotSessionPolicy.Create(role, model ?? Settings.Model, tools, instructions, clients.WorkingDirectory, CreateProvider());

    /// <summary>
    /// A stateless, tool-less <see cref="IChatClient"/> over a Copilot agent for the role: the router's <c>copilot</c>
    /// tier (<see cref="AgentRole.Tier"/>) or SVG illustrations (<see cref="AgentRole.Illustration"/>).
    /// </summary>
    public CopilotTierChatClient CreateChatClient(AgentRole role, string? instructions = null, string? model = null)
    {
        Guard.Require(role != AgentRole.Authoring, "copilot.role", "Authoring uses the agent runtime, not a chat client.");
        instructions ??= role == AgentRole.Illustration ? CopilotSessionPolicy.IllustrationInstructions : null;
        return new(Create(role, [], instructions, model), model ?? Settings.Model);
    }

    /// <summary>
    /// A Copilot agent for the role. The shared client starts lazily on the first session, so creating the agent
    /// (and the app) never starts the runtime or signs in.
    /// </summary>
    public AIAgent Create(AgentRole role, IReadOnlyList<AIFunction> tools, string? instructions, string? model = null)
    {
        var configuration = CreateSessionConfig(role, tools, instructions, model);
        return new DeferredCopilotAgent(RuntimeInfo(configuration.Model), async ct =>
            (await clients.GetClientAsync(ct).ConfigureAwait(false)).AsAIAgent(configuration, ownsClient: false));
    }
}

/// <summary>Starts the real Copilot agent on first use and reports its <see cref="AgentRuntimeInfo"/>.</summary>
internal sealed class DeferredCopilotAgent(AgentRuntimeInfo info, Func<CancellationToken, Task<AIAgent>> factory) : AIAgent
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private AIAgent? inner;

    public override string Name => "GitHub Copilot " + (info.Model ?? "default model");

    private async Task<AIAgent> InnerAsync(CancellationToken cancellationToken)
    {
        if (inner is not null) return inner;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return inner ??= await factory(cancellationToken).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    public override object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType == typeof(AgentRuntimeInfo) ? info
        : base.GetService(serviceType, serviceKey) ?? inner?.GetService(serviceType, serviceKey);

    protected override async ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
        => await (await InnerAsync(cancellationToken).ConfigureAwait(false)).CreateSessionAsync(cancellationToken).ConfigureAwait(false);

    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(AgentSession session,
        JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Copilot sessions are not persisted.");

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(JsonElement serializedState,
        JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Copilot sessions are not persisted.");

    protected override async Task<AgentResponse> RunCoreAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null,
        AgentRunOptions? options = null, CancellationToken cancellationToken = default)
        => await (await InnerAsync(cancellationToken).ConfigureAwait(false)).RunAsync(messages, session, options, cancellationToken).ConfigureAwait(false);

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(IEnumerable<ChatMessage> messages,
        AgentSession? session = null, AgentRunOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var agent = await InnerAsync(cancellationToken).ConfigureAwait(false);
        await foreach (var update in agent.RunStreamingAsync(messages, session, options, cancellationToken).ConfigureAwait(false))
            yield return update;
    }
}
