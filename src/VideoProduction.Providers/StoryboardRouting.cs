using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using VideoProduction;
using VideoProduction.Decisions.Experimental;
using VideoProduction.Decisions;

namespace VideoProduction.Providers;

[JsonConverter(typeof(JsonStringEnumConverter<PlanningRouteId>))]
public enum PlanningRouteId
{
    [JsonStringEnumMemberName("planning-fast")]
    [Description(StoryboardRouteSelector.FastDescription)]
    Fast,
    [JsonStringEnumMemberName("planning-strong")]
    [Description(StoryboardRouteSelector.StrongDescription)]
    Strong
}

[JsonConverter(typeof(JsonStringEnumConverter<StrongFirstRouteId>))]
public enum StrongFirstRouteId
{
    [JsonStringEnumMemberName("planning-strong")]
    [Description(StoryboardRouteSelector.StrongDescription)]
    Strong,
    [JsonStringEnumMemberName("planning-fast")]
    [Description(StoryboardRouteSelector.FastDescription)]
    Fast
}

public sealed record StoryboardRouteChoice(PlanningRouteId Route);
public sealed record StrongFirstRouteChoice(StrongFirstRouteId Route);
public sealed record StoryboardDecisionState(string Brief, string Task, int MaxScenes, int MaxDurationSeconds);
public sealed record StoryboardRoutingPolicy(string Version, double MinimumProbability, double MinimumMargin,
    string? DevelopmentEvidenceSha256 = null);
public sealed record PlanningRouteConfiguration(PlanningRouteId Id, PlannerConfiguration Planner, bool Approved = false,
    bool SupportsJson = false, bool AllowsNoTools = false, int MaxPromptCharacters = 0, int MaxOutputTokens = 0,
    bool Available = true);
public sealed record StoryboardRoutingConfiguration(string ModelDirectory, StoryboardRoutingPolicy Policy,
    PlanningRouteConfiguration[] Routes, bool AllowRemoteGeneration = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] OpenAiDecisionsConfiguration? OpenAi = null);
public sealed record RouteEligibility(PlanningRouteId Route, bool Eligible, string Reason, string ConfigurationSha256,
    string ProviderOrigin, string ConfiguredModel, string GenerationLocality);
public sealed record RouteProbability(PlanningRouteId Route, double Probability);
public sealed record StoryboardRouteBinding(int SchemaVersion, string RequestSha256, string ConfigurationSha256,
    string PolicySha256, string DomainSha256, RouteEligibility[] Eligibility, RouteProbability[] Distribution,
    PlanningRouteId? ObservedRoute, PlanningRouteId SelectedRoute, string Reason, string SelectionMode,
    string StateVersion, string ContractSource, string ProviderSource, string DecisionModel,
    string ModelAssetsSha256, double DecisionMilliseconds, DecisionProvenance? Provenance = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? NativeConfigurationSha256 = null);

public interface IDurableVideoPlanner : IVideoPlanner
{
    Task<PlannedVideo> PlanBound(string jobRoot, string prompt, VideoConstraints constraints, CancellationToken ct);
}

public sealed class StoryboardRouteSelector
{
    public const string FastDescription =
        "A straightforward, unambiguous single-topic explanation with a simple linear sequence and no interacting constraints.";
    public const string StrongDescription =
        "A multi-part, ambiguous or evidence-sensitive storyboard needing constraint reconciliation, careful technical distinctions or narrative synthesis.";
    public const string Instructions =
        "Classify the storyboard brief's planning requirements. Is it straightforward and linear, or does it require resolving ambiguity, interacting constraints or source-evidence limitations?";
    public const string StateVersion = "storyboard-brief-constraints-v1";
    public const string UncertaintyRule = "exact tie OR selected probability below minimum OR selected-minus-other below margin; approved eligible strong only";
    public const string ContractSource = "dotnet/extensions@e87f1126cf22108fb27f6391c7826fb91c25138e";
    public const string ProviderSource = "luisquintanilla/typesafe-meai@ce28e92b338b0edfbc22cca3c62101fa75c008f7";
    public const string Model = "SupersonicLabs/Julia-1-ONNX@82a2fadf8fccfccdc5fd4e1009ba8f1a265eb7a8";
    private static readonly DecisionDefinition<StoryboardRouteChoice> Definition =
        DecisionDefinition<StoryboardRouteChoice>.Create(StoryboardRoutingJson.Default.StoryboardRouteChoice,
            builder => builder.Choice(result => result.Route, Instructions));
    private static readonly DecisionDefinition<StrongFirstRouteChoice> ReverseDefinition =
        DecisionDefinition<StrongFirstRouteChoice>.Create(StoryboardRoutingJson.Default.StrongFirstRouteChoice,
            builder => builder.Choice(result => result.Route, Instructions));
    private readonly Func<IDecisionClient> decision;
    private readonly StoryboardRoutingConfiguration config;
    private readonly string modelAssetsSha256;
    private string DecisionSource => config.OpenAi is null ? ProviderSource : "openai/openai-dotnet@OpenAI_2.15.0 / experimental Layer 1";
    private string DecisionModel => config.OpenAi?.Model ?? Model;
    private string DecisionMode => config.OpenAi is null ? "local-decision" : "typed-openai-decision";

    public StoryboardRouteSelector(StoryboardRoutingConfiguration config, Func<IDecisionClient> decision,
        string modelAssetsSha256)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(decision);
        Guard.Require(config.Policy is not null && !string.IsNullOrWhiteSpace(config.Policy.Version)
            && double.IsFinite(config.Policy.MinimumProbability) && config.Policy.MinimumProbability is >= 0 and <= 1
            && double.IsFinite(config.Policy.MinimumMargin) && config.Policy.MinimumMargin is >= 0 and <= 1, "routing.policy",
            "An explicit versioned probability/margin policy is required; no calibrated default is assumed.");
        Guard.Require(config.Routes is { Length: >= 1 and <= 2 }
            && config.Routes.All(route => route is not null && Enum.IsDefined(route.Id) && route.Planner is not null)
            && config.Routes.Select(route => route.Id).Distinct().Count() == config.Routes.Length,
            "routing.config", "Configure a unique bounded planning-fast/planning-strong domain.");
        Guard.Require(Files.ValidHash(modelAssetsSha256), "routing.assets", "Decision assets need a verified manifest fingerprint.");
        this.config = config with { Routes = config.Routes.ToArray() };
        this.decision = decision;
        this.modelAssetsSha256 = modelAssetsSha256;
    }

    public string ConfigurationSha256 => Files.Fingerprint(new
    {
        config.AllowRemoteGeneration,
        modelDirectorySha256 = Files.HashText(config.ModelDirectory),
        routes = config.Routes.OrderBy(route => route.Id).ToArray(),
        config.Policy,
        modelAssetsSha256,
        StateVersion, FastDescription, StrongDescription, Instructions, UncertaintyRule, ContractSource, ProviderSource, Model
    });

    public RouteEligibility[] Eligibility(string prompt) => config.Routes.Select(route =>
    {
        var validOrigin = Uri.TryCreate(route.Planner.Endpoint, UriKind.Absolute, out var endpoint)
            && endpoint.Scheme is "http" or "https" && string.IsNullOrEmpty(endpoint.UserInfo)
            && string.IsNullOrEmpty(endpoint.Query) && string.IsNullOrEmpty(endpoint.Fragment);
        var validModel = !string.IsNullOrWhiteSpace(route.Planner.Model) && route.Planner.Model.Length <= 256
            && !route.Planner.Model.Any(char.IsControl);
        var reason = !route.Available ? "unavailable"
            : !validOrigin ? "invalid-origin"
            : !endpoint!.IsLoopback && !(config.AllowRemoteGeneration && route.Planner.AllowRemote && endpoint.Scheme == "https")
                ? "remote-not-approved"
            : !validModel ? "invalid-model"
            : !route.SupportsJson ? "json-not-supported"
            : !route.AllowsNoTools ? "no-tools-not-supported"
            : route.MaxPromptCharacters < prompt.Length ? "prompt-capacity"
            : route.MaxOutputTokens is < 4096 or > 32768 ? "output-budget"
            : "eligible";
        return new RouteEligibility(route.Id, reason == "eligible", reason, Files.Fingerprint(route),
            validOrigin ? endpoint!.GetLeftPart(UriPartial.Authority) : "invalid",
            validModel ? route.Planner.Model : "invalid",
            validOrigin ? endpoint!.IsLoopback ? "local-endpoint" : "remote-endpoint" : "invalid");
    }).ToArray();

    public async Task<StoryboardRouteBinding> Select(string prompt, VideoConstraints constraints, CancellationToken ct,
        bool reverseCandidates = false)
    {
        try { return await SelectCore(prompt, constraints, ct, reverseCandidates); }
        catch (DecisionProtocolException)
        {
            throw new VideoException("routing.protocol", "Local decision response failed exact state/domain/distribution validation; no planner was called.");
        }
        catch (DecisionRefusedException)
        {
            throw new VideoException("routing.refused", "The explicitly configured decision provider refused. No low-confidence fallback or planner was called.");
        }
        catch (DecisionClientException)
        {
            throw new VideoException("routing.provider", "The explicitly configured decision provider failed. No fallback or planner was called.");
        }
        catch (ArgumentException)
        {
            throw new VideoException("routing.input", "Local decision input or lossless tokenizer budget is invalid; no truncation or planner call occurred.");
        }
        catch (NotSupportedException)
        {
            throw new VideoException("routing.unsupported", "Local decision tokenizer/input/options are unsupported; no alternate backend or planner was called.");
        }
        catch (FileNotFoundException)
        {
            throw new VideoException("routing.model-missing", "An explicitly pinned local decision asset is missing; nothing was downloaded and no planner was called.");
        }
        catch (DirectoryNotFoundException)
        {
            throw new VideoException("routing.model-missing", "The explicitly configured local decision directory is missing; nothing was downloaded and no planner was called.");
        }
        catch (InvalidDataException)
        {
            throw new VideoException("routing.assets", "Local decision bytes do not match the pinned asset contract; no planner was called.");
        }
        catch (DllNotFoundException)
        {
            throw new VideoException("routing.native-missing", "A required local native decision runtime is unavailable; no planner was called.");
        }
        catch (BadImageFormatException)
        {
            throw new VideoException("routing.native-incompatible", "Local native decision runtime is incompatible with this process; no planner was called.");
        }
        catch (Microsoft.ML.OnnxRuntime.OnnxRuntimeException)
        {
            throw new VideoException("routing.runtime", "Local ONNX decision execution failed; no alternate backend or planner was called.");
        }
    }

    private async Task<StoryboardRouteBinding> SelectCore(string prompt, VideoConstraints constraints, CancellationToken ct,
        bool reverseCandidates = false)
    {
        ct.ThrowIfCancellationRequested();
        Guard.Require(!string.IsNullOrWhiteSpace(prompt) && prompt.Length <= 6000 && !prompt.Contains('\0')
            && constraints.MaxScenes is >= 1 and <= 12 && constraints.MaxDurationSeconds is >= 5 and <= 300,
            "routing.input", "Routing requires the original bounded free-form storyboard request and constraints.");
        var eligibility = Eligibility(prompt);
        var eligible = eligibility.Where(route => route.Eligible).ToArray();
        Guard.Require(eligible.Length > 0, "routing.no-eligible", "No approved compatible planning route is eligible.");
        var requestSha = RequestSha256(prompt, constraints);
        var policySha = Files.Fingerprint(config.Policy);
        var domainSha = Files.Fingerprint(new
        {
            FastDescription, StrongDescription, Instructions, reverseCandidates,
            routes = eligible.Length == 1 ? [eligible[0].Route] : reverseCandidates
                ? new[] { PlanningRouteId.Strong, PlanningRouteId.Fast } : [PlanningRouteId.Fast, PlanningRouteId.Strong]
        });
        if (eligible.Length == 1)
            return Receipt([], null, eligible[0].Route, "sole-eligible", "deterministic-no-inference", 0, null);
        var state = new StoryboardDecisionState(prompt, "storyboard-planning", constraints.MaxScenes, constraints.MaxDurationSeconds);
        var timer = Stopwatch.StartNew();
        PlanningRouteId observed;
        RouteProbability[] distribution;
        DecisionProvenance? provenance;
        if (!reverseCandidates)
        {
            var response = await decision().GetResponseAsync(state, StoryboardRoutingJson.Default.StoryboardDecisionState,
                Definition, cancellationToken: ct);
            observed = response.Result.Route;
            distribution = response.GetDistribution(result => result.Route)
                .Select(pair => new RouteProbability(pair.Key, pair.Value)).ToArray();
            provenance = response.Evidence.Provenance;
        }
        else
        {
            var response = await decision().GetResponseAsync(state, StoryboardRoutingJson.Default.StoryboardDecisionState,
                ReverseDefinition, cancellationToken: ct);
            observed = response.Result.Route == StrongFirstRouteId.Fast ? PlanningRouteId.Fast : PlanningRouteId.Strong;
            distribution = response.GetDistribution(result => result.Route).Select(pair =>
                new RouteProbability(pair.Key == StrongFirstRouteId.Fast ? PlanningRouteId.Fast : PlanningRouteId.Strong, pair.Value)).ToArray();
            provenance = response.Evidence.Provenance;
        }
        ct.ThrowIfCancellationRequested();
        timer.Stop();
        var observedProbability = distribution.Single(probability => probability.Route == observed).Probability;
        var otherProbability = distribution.Single(probability => probability.Route != observed).Probability;
        Guard.Require(observedProbability >= otherProbability, "routing.protocol",
            "Selected route disagrees with the complete native distribution; no planner was called.");
        var uncertain = observedProbability == otherProbability || observedProbability < config.Policy.MinimumProbability
            || observedProbability - otherProbability < config.Policy.MinimumMargin;
        var selected = uncertain ? PlanningRouteId.Strong : observed;
        Guard.Require(eligible.Any(route => route.Route == selected), "routing.uncertain-ineligible",
            "Uncertainty may use only the already approved eligible stronger route.");
        return Receipt(distribution, observed, selected,
            uncertain ? "valid-uncertainty-approved-strong" : "native-choice", DecisionMode, timer.Elapsed.TotalMilliseconds, provenance);

        StoryboardRouteBinding Receipt(RouteProbability[] probabilities, PlanningRouteId? native, PlanningRouteId selected,
            string reason, string mode, double milliseconds, DecisionProvenance? provenance) =>
            new(1, requestSha, ConfigurationSha256, policySha, domainSha, eligibility, probabilities, native, selected,
                reason, mode, StateVersion, ContractSource, DecisionSource, DecisionModel, modelAssetsSha256, milliseconds, provenance,
                config.OpenAi is null ? null : Files.Fingerprint(config.OpenAi));
    }

    public void ValidateBinding(StoryboardRouteBinding binding, string prompt, VideoConstraints constraints)
    {
        var distribution = binding.Distribution
            ?? throw new VideoException("routing.binding-conflict", "Persisted binding lacks a distribution.");
        var boundEligibility = binding.Eligibility
            ?? throw new VideoException("routing.binding-conflict", "Persisted binding lacks eligibility evidence.");
        var eligibility = Eligibility(prompt);
        var eligible = eligibility.Where(route => route.Eligible).ToArray();
        var domainSha = Files.Fingerprint(new
        {
            FastDescription, StrongDescription, Instructions, reverseCandidates = false,
            routes = eligible.Length == 1 ? [eligible[0].Route] : new[] { PlanningRouteId.Fast, PlanningRouteId.Strong }
        });
        Guard.Require(binding.SchemaVersion == 1 && binding.RequestSha256 == RequestSha256(prompt, constraints)
            && binding.ConfigurationSha256 == ConfigurationSha256 && binding.PolicySha256 == Files.Fingerprint(config.Policy)
            && binding.ModelAssetsSha256 == modelAssetsSha256 && Enum.IsDefined(binding.SelectedRoute)
            && binding.DomainSha256 == domainSha && binding.StateVersion == StateVersion
            && binding.ContractSource == ContractSource && binding.ProviderSource == DecisionSource && binding.DecisionModel == DecisionModel
            && binding.NativeConfigurationSha256 == (config.OpenAi is null ? null : Files.Fingerprint(config.OpenAi))
            && boundEligibility.SequenceEqual(eligibility) && eligible.Any(route => route.Route == binding.SelectedRoute)
            && distribution.All(item => item is not null && Enum.IsDefined(item.Route)),
            "routing.binding-conflict", "Persisted routing binding differs from the original request/configuration/policy/assets; it will not be rerouted.");
        if (eligible.Length == 1)
        {
            Guard.Require(binding.ObservedRoute is null && distribution.Length == 0
                && binding.Reason == "sole-eligible" && binding.SelectionMode == "deterministic-no-inference",
                "routing.binding-conflict", "Persisted sole-eligible binding is inconsistent.");
            return;
        }
        Guard.Require(binding.ObservedRoute is { } observed && Enum.IsDefined(observed)
            && binding.SelectionMode == DecisionMode && distribution.Length == 2
            && (config.OpenAi is null || binding.Provenance?.Precision is not null),
            "routing.binding-conflict", "Persisted local-decision binding is incomplete.");
        var request = DecisionRequest.Create(
            new StoryboardDecisionState(prompt, "storyboard-planning", constraints.MaxScenes, constraints.MaxDurationSeconds),
            StoryboardRoutingJson.Default.StoryboardDecisionState, Definition.Questions);
        try
        {
            _ = new DecisionResponse(request, [new ChoiceDecisionAnswer("route", Id(binding.ObservedRoute!.Value),
                distribution.Select(item => new DecisionProbability(Id(item.Route), item.Probability)).ToArray(),
                binding.Provenance?.Precision ?? DecisionPrecision.HighPrecision)]);
        }
        catch (Exception error) when (error is DecisionProtocolException or ArgumentException)
        {
            throw new VideoException("routing.binding-conflict", "Persisted decision distribution is invalid; it will not be rerouted.");
        }
        var native = distribution.Single(item => item.Route == binding.ObservedRoute).Probability;
        var other = distribution.Single(item => item.Route != binding.ObservedRoute).Probability;
        var uncertain = native == other || native < config.Policy.MinimumProbability || native - other < config.Policy.MinimumMargin;
        Guard.Require(native >= other && binding.SelectedRoute == (uncertain ? PlanningRouteId.Strong : binding.ObservedRoute)
            && binding.Reason == (uncertain ? "valid-uncertainty-approved-strong" : "native-choice"),
            "routing.binding-conflict", "Persisted policy decision is inconsistent; it will not be rerouted.");

        static string Id(PlanningRouteId route) =>
            JsonSerializer.SerializeToElement(route, StoryboardRoutingJson.Default.PlanningRouteId).GetString()!;
    }

    public static string RequestSha256(string prompt, VideoConstraints constraints) => Files.Fingerprint(new { prompt, constraints });
}

public sealed class RouteOptionsChatClient(IChatClient inner, string model, Action<ChatResponse>? observe = null) : IChatClient
{
    private static ChatOptions Options(ChatOptions? options, string model)
    {
        var copy = options?.Clone() ?? new ChatOptions();
        Guard.Require(copy.Tools is null or { Count: 0 } && copy.ResponseFormat is ChatResponseFormatJson
            && copy.MaxOutputTokens is > 0 and <= 4096 && copy.RawRepresentationFactory is null
            && copy.AdditionalProperties is null or { Count: 0 },
            "routing.options", "Storyboard routes require bounded JSON output with no tools or opaque provider options.");
        copy.ModelId = model;
        copy.Tools = null;
        copy.ToolMode = null;
        return copy;
    }
    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var response = await inner.GetResponseAsync(messages, Options(options, model), cancellationToken);
        observe?.Invoke(response);
        return response;
    }
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        inner.GetStreamingResponseAsync(messages, Options(options, model), cancellationToken);
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    }
    public void Dispose() { }
}

public sealed class RoutedVideoPlanner : IDurableVideoPlanner
{
    private readonly StoryboardRouteSelector selector;
    private readonly IReadOnlyDictionary<PlanningRouteId, (string Model, Func<IChatClient> Client)> routes;

    public RoutedVideoPlanner(StoryboardRouteSelector selector,
        IReadOnlyDictionary<PlanningRouteId, (string Model, Func<IChatClient> Client)> routes)
    {
        this.selector = selector;
        this.routes = routes.ToDictionary(pair => pair.Key, pair => pair.Value);
    }
    public void EnsureConfigured() => Guard.Require(routes.Count > 0, "routing.config", "Configure opt-in planning routes.");
    public Task<PlannedVideo> Plan(string prompt, VideoConstraints constraints, CancellationToken ct) =>
        PlanCore(null, prompt, constraints, ct);
    public Task<PlannedVideo> PlanBound(string jobRoot, string prompt, VideoConstraints constraints, CancellationToken ct) =>
        PlanCore(Path.Combine(jobRoot, "route-binding.json"), prompt, constraints, ct);

    private async Task<PlannedVideo> PlanCore(string? bindingPath, string prompt, VideoConstraints constraints, CancellationToken ct)
    {
        EnsureConfigured();
        ct.ThrowIfCancellationRequested();
        StoryboardRouteBinding? binding = null;
        string? selectedModel = null;
        UsageDetails? usage = null;
        using var router = RoutingChatClient.Create(async (context, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (bindingPath is not null && File.Exists(bindingPath))
            {
                binding = JsonSerializer.Deserialize(File.ReadAllText(bindingPath), StoryboardRoutingJson.Default.StoryboardRouteBinding)
                    ?? throw new VideoException("routing.binding", "Persisted route binding is invalid.");
                selector.ValidateBinding(binding, prompt, constraints);
            }
            else
            {
                binding = await selector.Select(prompt, constraints, cancellationToken);
                if (bindingPath is not null)
                {
                    var text = JsonSerializer.Serialize(binding, StoryboardRoutingJson.Default.StoryboardRouteBinding);
                    var temporary = bindingPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        File.WriteAllText(temporary, text);
                        File.Move(temporary, bindingPath, overwrite: false);
                    }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                }
            }
            Guard.Require(routes.TryGetValue(binding.SelectedRoute, out var route), "routing.binding",
                "Bound planning route is absent; no alternate planner was called.");
            selectedModel = route.Model;
            return new RouteOptionsChatClient(route.Client(), route.Model, response => usage = response.Usage);
        });
        var timer = Stopwatch.StartNew();
        var planned = await new ChatVideoPlanner(router, "decision-route-selected-in-router").Plan(prompt, constraints, ct);
        timer.Stop();
        var committed = binding ?? throw new VideoException("routing.protocol", "The chat router did not select a bound planning route.");
        return planned with {
            PlanningMode = committed.NativeConfigurationSha256 is not null
            ? "server-typed-openai-decision-routed-IChatClient" : "server-local-decision-routed-IChatClient",
            Routing = binding,
            ConfiguredModel = selectedModel,
            GenerationUsage = new(usage?.InputTokenCount, usage?.OutputTokenCount, usage?.TotalTokenCount, timer.Elapsed.TotalMilliseconds)
        };
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true,
    RespectNullableAnnotations = true)]
[JsonSerializable(typeof(StoryboardDecisionState))]
[JsonSerializable(typeof(StoryboardRouteChoice))]
[JsonSerializable(typeof(StrongFirstRouteChoice))]
[JsonSerializable(typeof(PlanningRouteId))]
[JsonSerializable(typeof(StrongFirstRouteId))]
[JsonSerializable(typeof(StoryboardRouteBinding))]
public partial class StoryboardRoutingJson : JsonSerializerContext;
