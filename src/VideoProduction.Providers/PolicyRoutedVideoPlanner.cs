using Microsoft.Extensions.AI;
using VideoProduction.Decisions.Experimental;

namespace VideoProduction.Providers;

/// <summary>Compatibility adapter: the reusable chat router does not know about storyboards.</summary>
public sealed class PolicyRoutedVideoPlanner(ChatRoutingConfiguration configuration,
    Func<IDecisionClient>? decisionFactory = null,
    Func<PlannerConfiguration, IChatClient>? clientFactory = null) : IDurableVideoPlanner
{
    public void EnsureConfigured() => DecisionRoutingChatClient.Validate(configuration);
    public Task<PlannedVideo> Plan(string prompt, VideoConstraints constraints, CancellationToken ct) =>
        PlanCore(null, prompt, constraints, ct);
    /// <summary>Plans and records the route receipt for telemetry; a retry is routed afresh.</summary>
    public Task<PlannedVideo> PlanBound(string jobRoot, string prompt, VideoConstraints constraints, CancellationToken ct) =>
        PlanCore(Path.Combine(jobRoot, "chat-route.json"), prompt, constraints, ct);

    private async Task<PlannedVideo> PlanCore(string? path, string prompt, VideoConstraints constraints, CancellationToken ct)
    {
        EnsureConfigured();
        ChatRouteReceipt? receipt = null;
        using var router = DecisionRoutingChatClient.FromConfiguration(configuration, "video-outline", selected =>
        {
            if (path is not null) Json.Write(path, selected);
            receipt = selected;
        }, decisionFactory, clientFactory);
        var plan = await new ChatVideoPlanner(router, "policy-selected").Plan(prompt, constraints, ct);
        return plan with {
            PlanningMode = "server-policy-routed-IChatClient",
            ConfiguredModel = receipt!.SelectedModel,
            ChatRouting = receipt
        };
    }
}
