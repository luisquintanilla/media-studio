using System.Diagnostics;
using System.Text.Json;
using VideoProduction;
using VideoProduction.Providers;
using VideoProduction.Decisions.Experimental;

internal sealed record RoutingEvaluationCase(string Id, string Family, string Brief, string ExpectedPolicy);
internal sealed record RoutingEvaluationSet(int SchemaVersion, string Split, string Task, RoutingEvaluationCase[] Cases,
    string[] ReverseCandidateOrderCaseIds, JsonElement? DoNotReadUntilPolicyFrozen = null);
internal sealed record RoutingObservation(string CaseId, string ExpectedPolicy, bool ReverseOrder, string BriefSha256,
    StoryboardRouteBinding? Binding, string? ErrorCode, string? ErrorType, string RuleRoute, string DirectStrongBaseline);

internal static class RoutingEvaluation
{
    public static async Task Run(string modelDirectory, string datasetFile, string policyFile, string outputDirectory,
        bool develop, Func<string, IDecisionClient> loadDecision, string modelManifestSha256,
        string? recordedResponsesDirectory = null)
    {
        Guard.Require(!Directory.Exists(outputDirectory), "evaluation.output", "Choose a new evidence directory; prior receipts are never overwritten.");
        Directory.CreateDirectory(outputDirectory);
        PrivateFiles.Protect(outputDirectory, directory: true);
        var dataset = Json.Read<RoutingEvaluationSet>(datasetFile);
        Guard.Require(dataset.SchemaVersion == 1 && dataset.Task == "storyboard-planning"
            && (develop || recordedResponsesDirectory is not null ? dataset.Split == "development"
                : dataset.Split is "heldout" or "locked-heldout")
            && dataset.Cases is { Length: > 0 and <= 200 }
            && dataset.Cases.All(item => Files.ValidId(item.Id))
            && dataset.Cases.Select(item => item.Id).Distinct().Count() == dataset.Cases.Length
            && dataset.Cases.All(item => item.ExpectedPolicy is "prefer-fast" or "prefer-strong" or "conservative-strong")
            && dataset.ReverseCandidateOrderCaseIds.All(id => dataset.Cases.Any(item => item.Id == id)),
            "evaluation.dataset", "Use the declared development or sealed heldout storyboard dataset without altering its cases.");
        var datasetSha = Files.Hash(datasetFile);
        var policy = develop ? new StoryboardRoutingPolicy("development-observation-v1", 0, 0, datasetSha)
            : Json.Read<StoryboardRoutingPolicy>(policyFile);
        var loadTimer = Stopwatch.StartNew();
        using var native = recordedResponsesDirectory is null ? loadDecision(modelDirectory) : new RecordedDecisionClient();
        loadTimer.Stop();
        using var tracing = new TracingDecisionClient(native);
        var config = RoutingTests.Config(policy) with { ModelDirectory = modelDirectory };
        var selector = new StoryboardRouteSelector(config, () => tracing, modelManifestSha256);
        var constraints = new VideoConstraints(180, 6);
        var observations = new List<RoutingObservation>();
        foreach (var item in dataset.Cases)
        {
            await Observe(item, reverse: false);
            if (dataset.ReverseCandidateOrderCaseIds.Contains(item.Id, StringComparer.Ordinal))
                await Observe(item, reverse: true);
        }
        StoryboardRoutingPolicy frozenPolicy = policy;
        object[] grid = [];
        if (develop)
        {
            Guard.Require(observations.All(item => item.Binding is not null), "evaluation.development-errors",
                "Development inference failures are retained; do not freeze a policy using an incomplete pilot.");
            var candidates = new List<(double Probability, double Margin, int WeightedErrors, int Overrides)>();
            foreach (var probability in new[] { .5, .6, .7, .8, .9 })
                foreach (var margin in new[] { 0, .1, .2, .3 })
                {
                    var evaluated = observations.Where(item => !item.ReverseOrder && item.Binding is not null).ToArray();
                    var weightedErrors = evaluated.Sum(item =>
                    {
                        var selected = Final(item.Binding!, probability, margin);
                        return item.ExpectedPolicy != "prefer-fast" && selected == PlanningRouteId.Fast ? 3
                            : item.ExpectedPolicy == "prefer-fast" && selected == PlanningRouteId.Strong ? 1 : 0;
                    });
                    var overrides = evaluated.Count(item => item.Binding!.ObservedRoute != Final(item.Binding, probability, margin));
                    candidates.Add((probability, margin, weightedErrors, overrides));
                }
            var best = candidates.OrderBy(item => item.WeightedErrors).ThenBy(item => item.Overrides)
                .ThenBy(item => item.Probability).ThenBy(item => item.Margin).First();
            frozenPolicy = new("storyboard-development-pilot-v1", best.Probability, best.Margin, Files.Fingerprint(new
            {
                datasetSha, observationsSha256 = Files.Hash(Path.Combine(outputDirectory, "observations.json")),
                selection = "grid p=[.5,.6,.7,.8,.9],margin=[0,.1,.2,.3]; errors=3*strong-misses+fast-overrouting; ties fewer overrides,lower p,lower margin"
            }));
            grid = candidates.Select(item => (object)new
            { item.Probability, item.Margin, item.WeightedErrors, item.Overrides }).ToArray();
            Json.Write(Path.Combine(outputDirectory, "frozen-policy.json"), frozenPolicy);
            Json.Write(Path.Combine(outputDirectory, "frozen-routing-definition.json"), new
            {
                schemaVersion = 1, selector = "actual local Julia typed Choice; NOT generative chat classification",
                StoryboardRouteSelector.StateVersion, StoryboardRouteSelector.Instructions, StoryboardRouteSelector.UncertaintyRule,
                candidateOrder = new[] { "planning-fast", "planning-strong" },
                StoryboardRouteSelector.FastDescription, StoryboardRouteSelector.StrongDescription,
                policy = frozenPolicy, stateProjection = "exact brief + task=storyboard-planning + maxScenes + maxDurationSeconds; no endpoint/credentials/paths",
                constraints, configSha256 = new StoryboardRouteSelector(config with { Policy = frozenPolicy }, () => native, modelManifestSha256).ConfigurationSha256,
                modelManifestSha256, datasetSha256 = datasetSha,
                notice = "Author hypotheses, not measured generation quality or calibrated confidence. No real generation endpoint was called."
            });
        }
        var normal = observations.Where(item => !item.ReverseOrder).ToArray();
        var successful = normal.Where(item => item.Binding is not null).ToArray();
        var actualSelected = successful.Select(item => new
        {
            item.CaseId, item.ExpectedPolicy,
            route = item.Binding!.SelectedRoute
        }).ToArray();
        Json.Write(Path.Combine(outputDirectory, "evaluation.json"), new
        {
            schemaVersion = 1, dataset.Split, datasetSha256 = datasetSha, modelManifestSha256,
            contract = StoryboardRouteSelector.ContractSource, provider = StoryboardRouteSelector.ProviderSource,
            model = StoryboardRouteSelector.Model,
            assetLoadMilliseconds = recordedResponsesDirectory is null ? loadTimer.Elapsed.TotalMilliseconds : 0,
            recordedClientSetupMilliseconds = recordedResponsesDirectory is null ? 0 : loadTimer.Elapsed.TotalMilliseconds,
            runtime = new
            {
                framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                managed = AppDomain.CurrentDomain.GetAssemblies().Where(assembly => !assembly.IsDynamic
                    && (assembly.GetName().Name?.StartsWith("VideoProduction.", StringComparison.Ordinal) == true
                        || assembly.GetName().Name is "Microsoft.ML.OnnxRuntime" or "Tokenizers.HuggingFace"
                            or "System.Numerics.Tensors" or "Microsoft.Extensions.AI.Abstractions"))
                    .OrderBy(assembly => assembly.GetName().Name).Select(assembly => new
                    { name = assembly.GetName().Name, version = assembly.GetName().Version?.ToString(), sha256 = Files.Hash(assembly.Location) }).ToArray(),
                native = Directory.Exists(Path.Combine(AppContext.BaseDirectory, "runtimes"))
                    ? Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "runtimes"), "*", SearchOption.AllDirectories)
                        .Where(file => Path.GetExtension(file) is ".dll" or ".so" or ".dylib")
                        .Order().Select(file => new { file = Path.GetRelativePath(AppContext.BaseDirectory, file).Replace('\\', '/'), sha256 = Files.Hash(file) }).ToArray()
                    : []
            },
            policy, policySha256 = Files.Fingerprint(policy),
            execution = recordedResponsesDirectory is null ? "actual-local-native-inference" : "recorded-neutral-response-policy-replay",
            sourceNativeObservations = recordedResponsesDirectory is null ? 0 : observations.Count,
            newNativeInferenceCalls = recordedResponsesDirectory is null ? observations.Count : 0,
            cases = normal.Length, inferenceRequests = recordedResponsesDirectory is null ? observations.Count : 0,
            decisionClientRequests = observations.Count, errors = normal.Count(item => item.Binding is null),
            successful = successful.Length, selections = actualSelected,
            strongMisses = actualSelected.Count(item => item.ExpectedPolicy != "prefer-fast" && item.route == PlanningRouteId.Fast),
            fastOverrouting = actualSelected.Count(item => item.ExpectedPolicy == "prefer-fast" && item.route == PlanningRouteId.Strong),
            strongSelections = actualSelected.Count(item => item.route == PlanningRouteId.Strong),
            policyOverrides = successful.Count(item => item.Binding!.ObservedRoute != item.Binding.SelectedRoute),
            reverseChecks = observations.Where(item => item.ReverseOrder).Select(item => new
            {
                item.CaseId, item.ErrorCode,
                nativeChoiceChanged = item.Binding is not null && normal.Single(original => original.CaseId == item.CaseId).Binding?.ObservedRoute != item.Binding.ObservedRoute,
                finalChoiceChanged = item.Binding is not null && normal.Single(original => original.CaseId == item.CaseId).Binding is { } originalBinding
                    && originalBinding.SelectedRoute != item.Binding.SelectedRoute
            }).ToArray(),
            firstInferenceMilliseconds = recordedResponsesDirectory is null ? successful.FirstOrDefault()?.Binding?.DecisionMilliseconds : null,
            subsequentInferenceMedianMilliseconds = recordedResponsesDirectory is null
                ? successful.Skip(1).Select(item => item.Binding!.DecisionMilliseconds)
                    .Order().Skip(Math.Max(0, (successful.Length - 1) / 2)).Cast<double?>().FirstOrDefault() : null,
            policyReplayMedianMilliseconds = recordedResponsesDirectory is null ? null
                : successful.Select(item => item.Binding!.DecisionMilliseconds).Order().Skip(successful.Length / 2).Cast<double?>().FirstOrDefault(),
            ruleBaseline = new
            {
                rule = "strong if brief contains any of: preserve, constraints, evidence, uncertain, do not, without, not supplied, routing; else fast; ordinal ignore-case; frozen before evaluation",
                errors = successful.Count(item => (item.ExpectedPolicy == "prefer-fast") != (item.RuleRoute == "planning-fast"))
            },
            directStrongBaseline = new
            {
                route = "planning-strong", authorLabelDisagreements = normal.Count(item => item.ExpectedPolicy == "prefer-fast"),
                realGenerationCalls = 0, measuredQuality = false
            },
            recordingChatFixtureCalls = successful.Length, liveGenerationCalls = 0,
            liveComparison = "BLOCKED: no explicitly approved configured pair of distinct compatible generation endpoints",
            derivedDevelopmentPolicy = develop ? frozenPolicy : null,
            derivedPolicySelectionsNotActualForwarding = develop ? successful.Select(item => new
            { item.CaseId, route = Final(item.Binding!, frozenPolicy.MinimumProbability, frozenPolicy.MinimumMargin) }).ToArray() : null,
            grid, observedClaims = recordedResponsesDirectory is null
                ? "Actual native decision distributions and recording-client forwarding only; no cost/quality/film improvement established."
                : "Offline application of frozen policy to exact retained real Julia neutral responses; zero new native inference and zero real generation."
        });
        Console.WriteLine($"Retained {observations.Count} decision responses; new native calls={(recordedResponsesDirectory is null ? observations.Count : 0)}; real generation calls=0; output={outputDirectory}");

        async Task Observe(RoutingEvaluationCase item, bool reverse)
        {
            StoryboardRouteBinding? binding = null;
            string? errorCode = null;
            string? errorType = null;
            string? sourceTraceSha256 = null;
            if (native is RecordedDecisionClient recorded)
            {
                var sourceFile = Path.Combine(recordedResponsesDirectory!, item.Id + (reverse ? "-reverse" : "") + "-native-input.json");
                recorded.Set(sourceFile);
                sourceTraceSha256 = Files.Hash(sourceFile);
            }
            try { binding = await selector.Select(item.Brief, constraints, CancellationToken.None, reverse); }
            catch (Exception error) when (error is DecisionProtocolException or ArgumentException or NotSupportedException
                or VideoException or Microsoft.ML.OnnxRuntime.OnnxRuntimeException)
            {
                errorCode = error is VideoException video ? video.Code : "evaluation.native-error";
                errorType = error.GetType().FullName;
            }
            observations.Add(new(item.Id, item.ExpectedPolicy, reverse, Files.HashText(item.Brief), binding, errorCode, errorType,
                Rule(item.Brief), "planning-strong"));
            Json.Write(Path.Combine(outputDirectory, "observations.json"), observations);
            if (tracing.LastResponse is { } actual && binding is not null)
                Json.Write(Path.Combine(outputDirectory, item.Id + (reverse ? "-reverse" : "") + "-native-input.json"), new
                {
                    actualRequest = actual.Request,
                    actualResponse = actual,
                    rawRepresentationType = actual.RawRepresentation?.GetType().FullName,
                    sourceTraceSha256,
                    notice = recordedResponsesDirectory is null
                        ? "Actual native request/response before application uncertainty policy; authored non-sensitive evaluation brief only."
                        : "Exact retained real Julia response replayed through actual selector/frozen policy, not new native inference."
                });
            if (binding is not null && !reverse)
            {
                using var fast = new RecordingPlannerClient("fixture-fast");
                using var strong = new RecordingPlannerClient("fixture-strong");
                var selectedClient = binding.SelectedRoute == PlanningRouteId.Fast ? fast : strong;
                var model = binding.SelectedRoute == PlanningRouteId.Fast ? "fixture-fast" : "fixture-strong";
                using var router = Microsoft.Extensions.AI.RoutingChatClient.Create((_, ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    return ValueTask.FromResult<Microsoft.Extensions.AI.IChatClient>(new RouteOptionsChatClient(selectedClient, model));
                });
                var planned = await new ChatVideoPlanner(router, "must-be-overridden").Plan(item.Brief, constraints, default);
                Guard.Require(selectedClient.Calls == 1 && fast.Calls + strong.Calls == 1 && selectedClient.Options!.ModelId == model,
                    "evaluation.forwarding", "Recording client forwarding did not preserve the selected route/model.");
                Json.Write(Path.Combine(outputDirectory, item.Id + "-recording-fixture.json"), new
                {
                    fixtureOnly = true, liveGenerationCalls = 0, selectedModel = model,
                    briefSha256 = Files.HashText(item.Brief), fastCalls = fast.Calls, strongCalls = strong.Calls,
                    returnedModel = planned.ReturnedModelId
                });
            }
        }
    }

    private static PlanningRouteId Final(StoryboardRouteBinding binding, double probability, double margin)
    {
        if (binding.ObservedRoute is null) return binding.SelectedRoute;
        var selected = binding.Distribution.Single(item => item.Route == binding.ObservedRoute).Probability;
        var other = binding.Distribution.Single(item => item.Route != binding.ObservedRoute).Probability;
        return selected == other || selected < probability || selected - other < margin ? PlanningRouteId.Strong : binding.ObservedRoute.Value;
    }
    private static string Rule(string brief) => new[] { "preserve", "constraints", "evidence", "uncertain", "do not", "without", "not supplied", "routing" }
        .Any(word => brief.Contains(word, StringComparison.OrdinalIgnoreCase)) ? "planning-strong" : "planning-fast";

    private sealed class TracingDecisionClient(IDecisionClient inner) : IDecisionClient
    {
        public DecisionResponse? LastResponse { get; private set; }
        public async Task<DecisionResponse> GetResponseAsync(DecisionRequest request, DecisionOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            LastResponse = null;
            return LastResponse = await inner.GetResponseAsync(request, options, cancellationToken);
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => inner.GetService(serviceType, serviceKey);
        public void Dispose() { }
    }

    private sealed class RecordedDecisionClient : IDecisionClient
    {
        private DecisionResponse? response;
        public void Set(string file)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            response = JsonSerializer.Deserialize<DecisionResponse>(document.RootElement.GetProperty("actualResponse").GetRawText(), Json.Options)
                ?? throw new InvalidDataException("Retained native response is missing.");
        }
        public Task<DecisionResponse> GetResponseAsync(DecisionRequest request, DecisionOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(response ?? throw new InvalidDataException("No retained response selected."));
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
