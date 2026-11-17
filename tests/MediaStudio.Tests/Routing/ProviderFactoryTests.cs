using MediaStudio.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using VideoProduction;
using VideoProduction.Decisions.Experimental;
using VideoProduction.Providers;
using VideoProduction.Providers.Decisions;

namespace MediaStudio.Tests.Routing;

public sealed class ProviderFactoryTests
{
    private static readonly ConnectionSettings Local = new() { Id = "local", Kind = ConnectionKind.OpenAICompatible, Endpoint = "http://localhost:11434/v1" };
    private static readonly ConnectionSettings Azure = new() { Id = "azure", Kind = ConnectionKind.Foundry, Endpoint = "https://example.services.ai.azure.com/", Auth = AuthMode.Entra };
    private static readonly ConnectionSettings Copilot = new() { Id = "copilot", Kind = ConnectionKind.GitHubCopilot, Auth = AuthMode.LoggedInUser };

    private static ProviderSettings Settings(bool allowCloud, params ChatTier[] tiers) => new() {
        Privacy = new() { AllowCloud = allowCloud },
        Connections = [Local, Azure, Copilot],
        Chat = new() { Tiers = tiers },
        Decisions = new() { Julia = JuliaMode.Off },
        Embeddings = new() { Connection = "local", Model = "embeddinggemma" },
        Vision = new() { Connection = "azure", Model = "vision" }
    };

    private static ChatTier Tier(string name, string connection, string model) => new() { Name = name, Connection = connection, Model = model };

    [Fact]
    public void LocalSettingsResolveKeyedRoutersOrderedWeakestToStrongest()
    {
        var settings = Settings(false, Tier("strong", "azure", "big"), Tier("balanced", "local", "qwen3.5:9b"), Tier("fast", "local", "qwen3.5:4b"));
        using var services = new ServiceCollection().AddMediaStudioAI(settings).BuildServiceProvider();
        foreach (var key in new[] { MediaStudioAIKeys.Chat, MediaStudioAIKeys.Authoring, MediaStudioAIKeys.Illustration })
        {
            var router = services.GetRequiredKeyedService<IChatClient>(key).GetService<DecisionRoutingChatClient>();
            Assert.NotNull(router);
            Assert.Equal(["fast", "balanced", "strong"], router!.Tiers.Select(t => t.Name));
            Assert.Equal([false, false, true], router.Tiers.Select(t => t.IsCloud));
            Assert.False(router.Options.AllowCloud);
            Assert.Equal(key, router.Options.Purpose);
        }
        Assert.Equal(HeuristicDecisionClient.ProviderName, FallbackDecisionClient.Name(
            ((FallbackDecisionClient)services.GetRequiredKeyedService<IDecisionClient>(MediaStudioAIKeys.Decider)).Chain.Single()));
        // The vision role is on a cloud connection and cloud is off: not registered. Local embeddings are.
        Assert.Null(services.GetKeyedService<IChatClient>(MediaStudioAIKeys.Vision));
        Assert.NotNull(services.GetKeyedService<IEmbeddingGenerator<string, Embedding<float>>>(MediaStudioAIKeys.Embedding));
    }

    [Fact]
    public void CloudTierClientIsRefusedWhenCloudIsOff()
    {
        var settings = Settings(false, Tier("cloud", "azure", "chat"));
        var error = Assert.Throws<VideoException>(() => ProviderFactory.CreateTierClient(settings, settings.Chat.Tiers[0]));
        Assert.Equal("routing.cloud-disabled", error.Code);
        Assert.False(EmbeddingFactory.CanCreate(settings, new() { Connection = "azure", Model = "embed" }));
    }

    [Fact]
    public void AuthoringFloorIsDroppedWhenItIsAnUnreachableCloudTier()
    {
        var settings = Settings(false, Tier("fast", "local", "a"), Tier("balanced", "azure", "b")) with { };
        using var router = ProviderFactory.CreateRouter(settings, "authoring", () => new HeuristicDecisionClient());
        Assert.Null(router.Options.AuthoringFloor);
        using var cloudRouter = ProviderFactory.CreateRouter(settings with { Privacy = new() { AllowCloud = true } },
            "authoring", () => new HeuristicDecisionClient());
        Assert.Equal("balanced", cloudRouter.Options.AuthoringFloor);
    }

    [Fact]
    public async Task CopilotTierNeedsItsRuntimeAndRoutesThroughTheRegisteredFactory()
    {
        var without = ProviderFactory.CreateTiers(Settings(true, Tier("copilot", "copilot", "model-x")));
        Assert.Equal("runtime-missing", without.Single().UnavailableReason);

        var factory = new FakeTierFactory();
        var settings = Settings(true, Tier("copilot", "copilot", "model-x"));
        using var services = new ServiceCollection().AddSingleton<ITierChatClientFactory>(factory)
            .AddMediaStudioAI(settings).BuildServiceProvider();
        var chat = services.GetRequiredKeyedService<IChatClient>(MediaStudioAIKeys.Chat);
        var response = await chat.GetResponseAsync("hello");
        Assert.Equal("copilot:model-x", response.Text);
        var receipt = Assert.IsType<ChatRouteReceipt>(response.AdditionalProperties![DecisionRoutingChatClient.ReceiptKey]);
        Assert.Equal("copilot", receipt.SelectedRoute);
        Assert.True(receipt.IsCloud);
        Assert.Equal(1, factory.Created);
    }

    [Fact]
    public async Task CopilotTierIsNeverCreatedWhenCloudIsOff()
    {
        var factory = new FakeTierFactory();
        var settings = Settings(false, Tier("copilot", "copilot", "model-x"));
        using var services = new ServiceCollection().AddSingleton<ITierChatClientFactory>(factory)
            .AddMediaStudioAI(settings).BuildServiceProvider();
        var chat = services.GetRequiredKeyedService<IChatClient>(MediaStudioAIKeys.Chat);
        var error = await Assert.ThrowsAsync<VideoException>(() => chat.GetResponseAsync("hello"));
        Assert.Equal("routing.no-eligible", error.Code);
        Assert.Equal(0, factory.Created);
    }

    internal sealed class FakeTierFactory : ITierChatClientFactory
    {
        public int Created;
        public ConnectionKind Kind => ConnectionKind.GitHubCopilot;
        public IChatClient Create(ChatTier tier, ConnectionSettings connection, ProviderSettings settings)
        {
            Created++;
            return new EchoClient($"{tier.Name}:{tier.Model}");
        }
    }

    internal sealed class EchoClient(string text) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, text);
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
