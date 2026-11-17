using System.ClientModel;
using System.ClientModel.Primitives;
using MediaStudio.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenAI;
using VideoProduction.Decisions.Experimental;
using VideoProduction.Providers.Decisions;

namespace VideoProduction.Providers;

/// <summary>Keys of the AI services registered by <see cref="ProviderFactory.AddMediaStudioAI"/>.</summary>
public static class MediaStudioAIKeys
{
    public const string Chat = "chat";
    public const string Authoring = "authoring";
    public const string Illustration = "illustration";
    public const string Vision = "vision";
    public const string Embedding = "embedding";
    /// <summary>Key of the shared routing <see cref="IDecisionClient"/> (Julia → cloud → heuristic).</summary>
    public const string Decider = "routing";
}

/// <summary>
/// Creates the <see cref="IChatClient"/> for a tier whose connection kind is not built into Providers
/// (for example <see cref="ConnectionKind.GitHubCopilot"/>, supplied by MediaStudio.Agents).
/// </summary>
public interface ITierChatClientFactory
{
    ConnectionKind Kind { get; }
    IChatClient Create(ChatTier tier, ConnectionSettings connection, ProviderSettings settings);
}

public static class ProviderFactory
{
    /// <summary>Tier strength used to order the router pool weakest to strongest; unknown names sit between balanced and cloud.</summary>
    public static int Strength(string tierName) => tierName switch {
        ChatTierNames.Fast => 0,
        ChatTierNames.Balanced => 1,
        ChatTierNames.Cloud => 3,
        ChatTierNames.Strong or ChatTierNames.Copilot => 4,
        _ => 2
    };

    /// <summary>
    /// Registers keyed <see cref="IChatClient"/>s "chat", "authoring" and "illustration" (each a
    /// <see cref="DecisionRoutingChatClient"/> over the settings tiers), "vision" (the configured vision model)
    /// and an "embedding" generator when one is configured and allowed. The decider is the Julia → cloud →
    /// heuristic chain from <see cref="DecisionClientFactory"/>. A role client or embedding generator registered earlier (for
    /// example the Copilot illustration runtime from <c>AddMediaStudioCopilot</c>) is kept.
    /// </summary>
    public static IServiceCollection AddMediaStudioAI(this IServiceCollection services, ProviderSettings settings,
        ModelAssets? assets = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(settings);
        services.AddLogging();
        services.AddKeyedSingleton<IDecisionClient>(MediaStudioAIKeys.Decider, (sp, _) =>
            DecisionClientFactory.Create(settings, assets ?? TryDefaultAssets(), sp.GetService<ILoggerFactory>()));
        if (settings.Chat.Tiers.Length > 0)
        {
            foreach (var purpose in new[] { MediaStudioAIKeys.Chat, MediaStudioAIKeys.Authoring, MediaStudioAIKeys.Illustration }
                         .Where(key => !services.Any(d => d.ServiceType == typeof(IChatClient) && Equals(d.ServiceKey, key))))
                services.AddKeyedChatClient(purpose, sp => CreateRouter(settings, purpose,
                        () => sp.GetRequiredKeyedService<IDecisionClient>(MediaStudioAIKeys.Decider),
                        sp.GetServices<ITierChatClientFactory>(), sp.GetService<ILoggerFactory>()), ServiceLifetime.Singleton)
                    .UseLogging().UseOpenTelemetry();
        }
        if (settings.Vision is { } vision && Allowed(settings, vision.Connection))
            services.AddKeyedChatClient(MediaStudioAIKeys.Vision, sp => CreateRoleClient(settings, vision,
                sp.GetServices<ITierChatClientFactory>()), ServiceLifetime.Singleton).UseLogging().UseOpenTelemetry();
        if (EmbeddingFactory.CanCreate(settings, settings.Embeddings)
            && !services.Any(d => d.ServiceType == typeof(IEmbeddingGenerator<string, Embedding<float>>) && Equals(d.ServiceKey, MediaStudioAIKeys.Embedding)))
            services.AddKeyedEmbeddingGenerator(MediaStudioAIKeys.Embedding,
                _ => EmbeddingFactory.Create(settings, settings.Embeddings)!, ServiceLifetime.Singleton).UseOpenTelemetry();
        Illustrations.IllustrationPipeline.AddMediaStudioIllustrations(services, settings);
        return services;
    }

    public static DecisionRoutingChatClient CreateRouter(ProviderSettings settings, string purpose,
        Func<IDecisionClient> decisionFactory, IEnumerable<ITierChatClientFactory>? extensions = null,
        ILoggerFactory? loggerFactory = null, Action<ChatRouteReceipt>? observe = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var tiers = CreateTiers(settings, extensions);
        Guard.Require(tiers.Count > 0, "routing.config", "Configure at least one chat tier in Settings.");
        var routing = settings.Chat.Routing;
        var floor = tiers.FirstOrDefault(t => t.Name == routing.AuthoringFloor);
        return new(tiers, new DecisionRoutingOptions(purpose) {
            AllowCloud = settings.Privacy.AllowCloud,
            ConfidenceFloor = routing.ConfidenceFloor,
            // A floor the user cannot reach (missing or cloud while cloud is off) must not block local authoring.
            AuthoringFloor = floor is not null && (!floor.IsCloud || settings.Privacy.AllowCloud) ? floor.Name : null,
            Failover = routing.Failover
        }, decisionFactory, observe, loggerFactory?.CreateLogger<DecisionRoutingChatClient>());
    }

    /// <summary>The settings tiers ordered weakest → strongest (stable within equal strength), with lazily created clients.</summary>
    public static IReadOnlyList<RoutedChatTier> CreateTiers(ProviderSettings settings,
        IEnumerable<ITierChatClientFactory>? extensions = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var factories = (extensions ?? []).ToArray();
        return settings.Chat.Tiers
            .Select((tier, index) => (tier, index))
            .OrderBy(x => Strength(x.tier.Name)).ThenBy(x => x.index)
            .Select(x => Tier(settings, x.tier, factories))
            .ToArray();
    }

    private static RoutedChatTier Tier(ProviderSettings settings, ChatTier tier, ITierChatClientFactory[] factories)
    {
        var connection = settings.FindConnection(tier.Connection);
        var cloud = connection?.IsCloud ?? true;
        string? unavailable = connection is null ? "connection-missing"
            : connection.Kind == ConnectionKind.GitHubCopilot && factories.All(f => f.Kind != connection.Kind) ? "runtime-missing"
            : null;
        return new RoutedChatTier(tier.Name, Describe(tier.Name), tier.Model, cloud,
            () => CreateTierClient(settings, tier, factories)) {
            UnavailableReason = unavailable,
            SupportsTools = connection?.Kind != ConnectionKind.GitHubCopilot
        };
    }

    private static string Describe(string name) => name switch {
        ChatTierNames.Fast => "Small local model: short answers, quick edits and simple questions.",
        ChatTierNames.Balanced => "Mid-size local model: drafting, summaries and moderate reasoning.",
        ChatTierNames.Cloud => "Cloud chat model: longer context and stronger general reasoning.",
        ChatTierNames.Strong => "Strongest model: hard authoring, long structured output and complex reasoning.",
        ChatTierNames.Copilot => "GitHub Copilot model: hard authoring and complex reasoning.",
        _ => $"Configured tier '{name}'."
    };

    private static bool Allowed(ProviderSettings settings, string connectionId) =>
        settings.FindConnection(connectionId) is { } c && (!c.IsCloud || settings.Privacy.AllowCloud);

    public static IChatClient CreateRoleClient(ProviderSettings settings, RoleModel role,
        IEnumerable<ITierChatClientFactory>? extensions = null) =>
        CreateTierClient(settings, new ChatTier { Name = "role", Connection = role.Connection, Model = role.Model }, extensions);

    /// <summary>Creates the provider client for one tier. Cloud connections are refused unless cloud is allowed.</summary>
    public static IChatClient CreateTierClient(ProviderSettings settings, ChatTier tier,
        IEnumerable<ITierChatClientFactory>? extensions = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(tier);
        var connection = settings.FindConnection(tier.Connection)
            ?? throw new VideoException("routing.config", $"Tier '{tier.Name}' references unknown connection '{tier.Connection}'.");
        Guard.Require(!connection.IsCloud || settings.Privacy.AllowCloud, "routing.cloud-disabled",
            $"Connection '{connection.Id}' is a cloud connection and cloud is disabled in Settings.");
#pragma warning disable OPENAI001
        switch (connection.Kind)
        {
            case ConnectionKind.OpenAICompatible:
                var local = Chat(OpenAICompatible(connection), tier);
                return tier.Thinking == true ? local : local.AsBuilder()
                    .ConfigureOptions(o => o.Reasoning ??= new() { Effort = ReasoningEffort.None }).Build();
            case ConnectionKind.Foundry when tier.Api == ChatApi.Anthropic:
                var endpoints = FoundryEndpoints.From(connection);
                var entra = connection.Auth == AuthMode.Entra;
                return FoundryAnthropic.CreateClient(
                    new PlannerConfiguration(endpoints.Anthropic.AbsoluteUri, tier.Model, null, AllowRemote: true,
                        Authentication: entra ? "azure-cli" : "api-key", TenantId: connection.TenantId, Provider: ChatProviderKind.Anthropic),
                    entra ? null : CredentialFactory.GetApiKey(connection.Id), null,
                    entra ? CredentialFactory.CreateTokenCredential(connection) : null, null);
            case ConnectionKind.Foundry:
                return Chat(Foundry(connection), tier);
            default:
                var factory = (extensions ?? []).FirstOrDefault(f => f.Kind == connection.Kind)
                    ?? throw new VideoException("routing.config",
                        $"Connection '{connection.Id}' ({connection.Kind}) needs its runtime registered (for GitHub Copilot call AddMediaStudioCopilot()).");
                return factory.Create(tier, connection, settings);
        }
#pragma warning restore OPENAI001
    }

#pragma warning disable OPENAI001
    private static IChatClient Chat(OpenAIClient client, ChatTier tier) => tier.Api == ChatApi.Responses
        ? client.GetResponsesClient().AsIChatClient(tier.Model)
        : client.GetChatClient(tier.Model).AsIChatClient();
#pragma warning restore OPENAI001

    internal static OpenAIClient OpenAICompatible(ConnectionSettings connection)
    {
        Guard.Require(Uri.TryCreate(connection.Endpoint, UriKind.Absolute, out var endpoint)
            && endpoint.Scheme is "http" or "https" && endpoint.UserInfo.Length == 0
            && (endpoint.IsLoopback || connection.AllowRemote && endpoint.Scheme == "https"), "routing.config",
            $"Connection '{connection.Id}' needs a loopback endpoint, or allowRemote with HTTPS.");
        var key = connection.Auth == AuthMode.ApiKey ? CredentialFactory.GetApiKey(connection.Id) : "ollama";
        return new OpenAIClient(new ApiKeyCredential(key), Options(endpoint!));
    }

    internal static OpenAIClient Foundry(ConnectionSettings connection)
    {
        var options = Options(FoundryEndpoints.From(connection).OpenAIv1);
#pragma warning disable OPENAI001
        return connection.Auth == AuthMode.ApiKey
            ? new OpenAIClient(new ApiKeyCredential(CredentialFactory.GetApiKey(connection.Id)), options)
            : new OpenAIClient(new BearerTokenPolicy(CredentialFactory.CreateTokenCredential(connection),
                FoundryEndpoints.CognitiveServicesScope), options);
#pragma warning restore OPENAI001
    }

    private static OpenAIClientOptions Options(Uri endpoint) => new() {
        Endpoint = endpoint, RetryPolicy = new ClientRetryPolicy(0), NetworkTimeout = TimeSpan.FromSeconds(120)
    };

    private static ModelAssets? TryDefaultAssets()
    {
        try { return File.Exists(ModelManifest.DefaultPath) ? new ModelAssets(ModelManifest.LoadDefault()) : null; }
        catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException) { return null; }
    }
}

/// <summary>Creates the embedding generator for the configured role; null when none is configured or it is cloud while cloud is off.</summary>
public static class EmbeddingFactory
{
    public static bool CanCreate(ProviderSettings settings, RoleModel? role) =>
        role is not null && settings.FindConnection(role.Connection) is { Kind: not ConnectionKind.GitHubCopilot } c
        && (!c.IsCloud || settings.Privacy.AllowCloud);

    public static IEmbeddingGenerator<string, Embedding<float>>? Create(ProviderSettings settings, RoleModel? role)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!CanCreate(settings, role)) return null;
        var connection = settings.FindConnection(role!.Connection)!;
        var client = connection.Kind == ConnectionKind.Foundry
            ? ProviderFactory.Foundry(connection) : ProviderFactory.OpenAICompatible(connection);
        return client.GetEmbeddingClient(role.Model).AsIEmbeddingGenerator();
    }
}
