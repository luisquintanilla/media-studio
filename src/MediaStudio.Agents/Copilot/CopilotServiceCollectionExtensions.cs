using MediaStudio.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using VideoProduction.Providers;

namespace MediaStudio.Agents.Copilot;

public static class CopilotServiceCollectionExtensions
{
    /// <summary>
    /// Registers the GitHub Copilot runtime: one lazily started <see cref="CopilotClientFactory"/>, the
    /// <see cref="CopilotAgentFactory"/>, <see cref="CopilotProbe"/>, and the router's <c>copilot</c> tier factory.
    /// Call before <see cref="ProviderFactory.AddMediaStudioAI"/> so routed clients can use a Copilot tier.
    /// Nothing starts or signs in until a Copilot agent or tier is first used.
    /// </summary>
    public static IServiceCollection AddMediaStudioCopilot(this IServiceCollection services, ProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(settings);
        services.TryAddSingleton(_ => new CopilotClientFactory(settings));
        services.TryAddSingleton(sp => new CopilotAgentFactory(sp.GetRequiredService<CopilotClientFactory>()));
        services.TryAddSingleton(sp => new CopilotProbe(sp.GetRequiredService<CopilotClientFactory>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ITierChatClientFactory, CopilotTierChatClientFactory>(
            sp => new CopilotTierChatClientFactory(sp.GetRequiredService<CopilotAgentFactory>())));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigurationCheck, CopilotConfigurationCheck>(_ => new CopilotConfigurationCheck()));
        // agents.illustration.runtime = copilot: SVG illustrations come from a tool-less Copilot session instead of the router.
        if (settings.Agents.Illustration.Runtime == AgentRuntime.Copilot)
            services.AddKeyedChatClient(MediaStudioAIKeys.Illustration,
                    sp => sp.GetRequiredService<CopilotAgentFactory>().CreateChatClient(AgentRole.Illustration), ServiceLifetime.Singleton)
                .UseLogging().UseOpenTelemetry();
        return services;
    }
}
