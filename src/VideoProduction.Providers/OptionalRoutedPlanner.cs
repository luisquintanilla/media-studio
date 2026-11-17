using Microsoft.Extensions.AI;
using VideoProduction;
using VideoProduction.Decisions.Experimental;
using VideoProduction.Decisions.Experimental.Julia;

namespace VideoProduction.Providers;

public sealed class OptionalRoutedPlanner : IDurableVideoPlanner, IDisposable
{
    private readonly Lazy<IDecisionClient> decision;
    private readonly Lazy<RoutedVideoPlanner> planner;
    private readonly Dictionary<PlanningRouteId, Lazy<IChatClient>> clients = [];
    private readonly bool conflictingPlanner;

    public OptionalRoutedPlanner(StoryboardRoutingConfiguration config, bool conflictingPlanner = false)
        : this(config, () => Decisions.DecisionClientFactory.CreateStrict(new(config.OpenAi is null ? config.ModelDirectory : null, config.OpenAi)),
            config.OpenAi is null ? JuliaModelAssets.Fingerprint : Files.Fingerprint(new DecisionProviderConfiguration(null, config.OpenAi)),
            conflictingPlanner)
    { }

    public OptionalRoutedPlanner(StoryboardRoutingConfiguration config, Func<IDecisionClient> decisionFactory,
        string modelAssetsSha256, bool conflictingPlanner = false, Func<PlannerConfiguration, IChatClient>? clientFactory = null)
    {
        this.conflictingPlanner = conflictingPlanner;
        decision = new(decisionFactory, LazyThreadSafetyMode.ExecutionAndPublication);
        planner = new(() =>
        {
            var selector = new StoryboardRouteSelector(config, () => decision.Value, modelAssetsSha256);
            foreach (var route in config.Routes)
                clients.Add(route.Id, new(() => (clientFactory ?? (configuration => ChatVideoPlanner.CreateClient(configuration)))(route.Planner),
                    LazyThreadSafetyMode.ExecutionAndPublication));
            return new RoutedVideoPlanner(selector, config.Routes.ToDictionary(route => route.Id,
                route => (route.Planner.Model, (Func<IChatClient>)(() => clients[route.Id].Value))));
        }, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public void EnsureConfigured()
    {
        Guard.Require(!conflictingPlanner, "routing.config", "Configure either a legacy planner or routed planning, not both.");
        planner.Value.EnsureConfigured();
    }
    public Task<PlannedVideo> Plan(string prompt, VideoConstraints constraints, CancellationToken ct)
    {
        EnsureConfigured();
        return planner.Value.Plan(prompt, constraints, ct);
    }
    public Task<PlannedVideo> PlanBound(string jobRoot, string prompt, VideoConstraints constraints, CancellationToken ct)
    {
        EnsureConfigured();
        return planner.Value.PlanBound(jobRoot, prompt, constraints, ct);
    }
    public void Dispose()
    {
        foreach (var client in clients.Values)
            if (client.IsValueCreated) client.Value.Dispose();
        if (decision.IsValueCreated) decision.Value.Dispose();
    }
}
