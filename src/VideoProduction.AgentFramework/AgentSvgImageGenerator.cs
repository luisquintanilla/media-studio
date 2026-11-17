using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using VideoProduction.Providers;

namespace VideoProduction.AgentFramework;

public sealed record AgentIllustrationAcknowledgement(bool ToolsDenied, bool NoPersistedHistory,
    bool ProviderOptionsMayBeIgnored);

public sealed class AgentSvgImageGenerator : IImageGenerator
{
    private readonly IChatClient client;
    private readonly ChatSvgImageGenerator inner;
    public AgentSvgImageGenerator(AIAgent agent, string configuredModel, SvgRasterConfiguration raster,
        AgentIllustrationAcknowledgement acknowledgement)
    {
        ArgumentNullException.ThrowIfNull(agent);
        Guard.Require(acknowledgement is { ToolsDenied: true, NoPersistedHistory: true, ProviderOptionsMayBeIgnored: true },
            "image.agent-policy", "The injector must configure tools/permissions denied and no persistent history, and acknowledge non-chat agents may ignore model/token options. The adapter cannot audit or revoke an agent's hidden capabilities.");
#pragma warning disable MAAI001
        client = agent.AsIChatClient(allowNonChatClientAgents: true);
#pragma warning restore MAAI001
        inner = new ChatSvgImageGenerator(client, configuredModel, raster);
    }
    public Task<ImageGenerationResponse> GenerateAsync(ImageGenerationRequest request, ImageGenerationOptions? options = null,
        CancellationToken cancellationToken = default) => inner.GenerateAsync(request, options, cancellationToken);
    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    public void Dispose() { inner.Dispose(); client.Dispose(); }
}
