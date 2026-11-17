using MediaStudio.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using VideoProduction.Providers;

namespace MediaStudio.Tests.Routing;

/// <summary>Talks to a real local Ollama (http://localhost:11434) with qwen3.5:4b pulled. Excluded from the default run.</summary>
public sealed class LocalRuntimeRoutingTests
{
    [Fact, Trait("Category", "LocalRuntime")]
    public async Task SettingsDrivenChatRouterAnswersFromLocalOllama()
    {
        var settings = new ProviderSettings {
            Privacy = new() { AllowCloud = false },
            Connections = [new() { Id = "local", Kind = ConnectionKind.OpenAICompatible, Endpoint = ProviderSettingsDefaults.OllamaEndpoint.ToString() }],
            Chat = new() { Tiers = [new() { Name = "fast", Connection = "local", Model = "qwen3.5:4b" }] },
            Decisions = new() { Julia = JuliaMode.Off }
        };
        using var services = new ServiceCollection().AddMediaStudioAI(settings).BuildServiceProvider();
        var chat = services.GetRequiredKeyedService<IChatClient>(MediaStudioAIKeys.Chat);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));

        var response = await chat.GetResponseAsync("Say hi in one short sentence.",
            new ChatOptions { MaxOutputTokens = 256 }, timeout.Token);

        Assert.False(string.IsNullOrWhiteSpace(response.Text));
        var receipt = Assert.IsType<ChatRouteReceipt>(response.AdditionalProperties![DecisionRoutingChatClient.ReceiptKey]);
        Assert.StartsWith("⚡ local · qwen3.5:4b", receipt.Badge());
        Assert.False(receipt.IsCloud);
    }
}
