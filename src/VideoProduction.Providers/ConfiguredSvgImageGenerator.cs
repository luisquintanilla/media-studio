using Microsoft.Extensions.AI;

namespace VideoProduction.Providers;

public sealed class ConfiguredSvgImageGenerator : IImageGenerator
{
    private readonly Lazy<(IChatClient Client, ChatSvgImageGenerator Generator)> configured;
    public ConfiguredSvgImageGenerator(PlannerConfiguration planner, SvgRasterConfiguration raster,
        ChatRoutingConfiguration? routing = null)
        : this(planner, () => raster, routing) { }
    public ConfiguredSvgImageGenerator(PlannerConfiguration planner, Func<SvgRasterConfiguration> raster,
        ChatRoutingConfiguration? routing = null)
    {
        configured = new(() =>
        {
            var resolved = raster();
            IChatClient client = routing is null ? ChatVideoPlanner.CreateClient(planner)
                : DecisionRoutingChatClient.FromConfiguration(routing, "svg-illustration",
                    receipt => Json.Write(Path.Combine(resolved.ScratchRoot,
                        "chat-route-" + Guid.NewGuid().ToString("N") + ".json"), receipt));
            return (client, new ChatSvgImageGenerator(client, planner.Model, resolved));
        });
    }
    public Task<ImageGenerationResponse> GenerateAsync(ImageGenerationRequest request, ImageGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return configured.Value.Generator.GenerateAsync(request, options, cancellationToken);
    }
    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    public void Dispose()
    {
        if (configured.IsValueCreated) { configured.Value.Generator.Dispose(); configured.Value.Client.Dispose(); }
    }
}
