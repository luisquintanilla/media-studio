using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using VideoProduction;
using VideoProduction.Decisions.Experimental;
using VideoProduction.Providers;

internal static class RoutingTests
{
    public static StoryboardRoutingConfiguration Config(StoryboardRoutingPolicy? policy = null) =>
        new("unused-model-fixture", policy ?? new("fixture-policy-not-calibrated", .7, .2, new string('a', 64)),
            [Route(PlanningRouteId.Fast, "fixture-fast"), Route(PlanningRouteId.Strong, "fixture-strong")]);
    public static PlanningRouteConfiguration Route(PlanningRouteId id, string model) =>
        new(id, new("http://localhost:9999/v1/", model, null), true, true, true, 6000, 4096);

    public static async Task<int> Run(string root)
    {
        var count = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); count++; }
        async Task Reject(Func<Task> action, string? code = null)
        {
            try { await action(); }
            catch (Exception error) when (error is not InvalidOperationException { Message: "Expected routing error" })
            {
                if (code is not null) Check(error is VideoException video && video.Code == code,
                    $"Expected {code}, got {error.GetType().Name}/{(error as VideoException)?.Code}");
                else count++;
                return;
            }
            throw new InvalidOperationException("Expected routing error");
        }
        var config = Config();
        using var native = new FixtureDecisionClient(.95);
        var selector = new StoryboardRouteSelector(config, () => native, new string('b', 64));
        using var fast = new RecordingPlannerClient("fixture-fast");
        using var strong = new RecordingPlannerClient("fixture-strong");
        var routes = new Dictionary<PlanningRouteId, (string, Func<IChatClient>)> {
            [PlanningRouteId.Fast] = ("fixture-fast", () => fast),
            [PlanningRouteId.Strong] = ("fixture-strong", () => strong)
        };
        var planner = new RoutedVideoPlanner(selector, routes);
        var constraints = new VideoConstraints(30, 3);
        const string brief = "Plan a three-scene beginner explanation of a token.";
        var planned = await planner.Plan(brief, constraints, default);
        Check(fast.Calls == 1 && strong.Calls == 0 && native.Calls == 1, "Local decision invokes only its selected fixture planner");
        Check(planned.Routing!.SelectedRoute == PlanningRouteId.Fast && planned.Routing.Distribution.Length == 2
            && planned.ConfiguredModel == "fixture-fast" && planned.ReturnedModelId == "fixture-fast",
            "Full native distribution and actual selected configured/returned model are retained");
        Check(fast.Input!.Last().Text.Contains(brief, StringComparison.Ordinal)
            && native.LastRequest!.State.GetProperty("brief").GetString() == brief,
            "Original free-form brief reaches both decision state and planner unchanged");
        Check(fast.Options is { MaxOutputTokens: 4096, Tools: null, ModelId: "fixture-fast" }
            && fast.Options.ResponseFormat is ChatResponseFormatJson, "Selected route retains JSON/no-tools/output guards");
        native.FastProbability = .05;
        var strongPlan = await planner.Plan(brief, constraints, default);
        Check(strongPlan.Routing!.SelectedRoute == PlanningRouteId.Strong && strong.Calls == 1 && fast.Calls == 1,
            "Strong native choice does not invoke fast fixture");
        native.FastProbability = .55;
        var uncertain = await selector.Select(brief, constraints, default);
        Check(uncertain.ObservedRoute == PlanningRouteId.Fast && uncertain.SelectedRoute == PlanningRouteId.Strong
            && uncertain.Reason == "valid-uncertainty-approved-strong" && uncertain.Distribution[0].Probability == .55,
            "Valid uncertainty retains observed choice/distribution and explicitly uses approved stronger route");
        native.FastProbability = .5;
        var noMarginSelector = new StoryboardRouteSelector(Config(new("fixture-tie", .5, 0, new string('a', 64))),
            () => native, new string('b', 64));
        Check((await noMarginSelector.Select(brief, constraints, default)).SelectedRoute == PlanningRouteId.Strong,
            "An exact probability tie is explicitly uncertain even with a zero-margin operating point");
        native.FastProbability = .55;
        var reverse = await selector.Select(brief, constraints, default, reverseCandidates: true);
        Check(native.LastRequest!.Questions[0] is ChoiceDecisionQuestion question
            && question.Candidates[0].Id == "planning-strong" && reverse.Distribution[0].Route == PlanningRouteId.Strong,
            "Reverse-order native test preserves stable route IDs and complete ordered distribution");
        var receipt = JsonSerializer.Serialize(uncertain, StoryboardRoutingJson.Default.StoryboardRouteBinding);
        var roundtrip = JsonSerializer.Deserialize(receipt, StoryboardRoutingJson.Default.StoryboardRouteBinding)!;
        Check(JsonSerializer.Serialize(roundtrip, StoryboardRoutingJson.Default.StoryboardRouteBinding) == receipt,
            "Application-owned source-generated receipt metadata roundtrips without upstream private context");
        native.FastProbability = .95;
        var job = Path.Combine(root, "routed-binding");
        Directory.CreateDirectory(job);
        await planner.PlanBound(job, brief, constraints, default);
        var boundBytes = File.ReadAllBytes(Path.Combine(job, "route-binding.json"));
        var nativeCalls = native.Calls;
        native.FastProbability = .05;
        var retry = await planner.PlanBound(job, brief, constraints, default);
        Check(native.Calls == nativeCalls && retry.Routing!.SelectedRoute == PlanningRouteId.Fast
            && File.ReadAllBytes(Path.Combine(job, "route-binding.json")).SequenceEqual(boundBytes),
            "Durable retry reuses exact persisted route before generation, without rerouting or rewriting receipt");
        var bindingFile = Path.Combine(job, "route-binding.json");
        var originalBinding = JsonSerializer.Deserialize(boundBytes, StoryboardRoutingJson.Default.StoryboardRouteBinding)!;
        File.WriteAllText(bindingFile, JsonSerializer.Serialize(originalBinding with { SelectedRoute = PlanningRouteId.Strong },
            StoryboardRoutingJson.Default.StoryboardRouteBinding));
        var callsBeforeCorrupt = fast.Calls + strong.Calls;
        await Reject(() => planner.PlanBound(job, brief, constraints, default), "routing.binding-conflict");
        Check(callsBeforeCorrupt == fast.Calls + strong.Calls, "Inconsistent stored policy selection does not call a generator");
        File.WriteAllBytes(bindingFile, boundBytes);
        var changed = new StoryboardRouteSelector(config with { Policy = config.Policy with { Version = "changed" } },
            () => native, new string('b', 64));
        var changedPlanner = new RoutedVideoPlanner(changed, routes);
        var generatorCalls = fast.Calls + strong.Calls;
        await Reject(() => changedPlanner.PlanBound(job, brief, constraints, default), "routing.binding-conflict");
        await Reject(() => planner.PlanBound(job, brief + " Changed.", constraints, default), "routing.binding-conflict");
        Check(generatorCalls == fast.Calls + strong.Calls, "Changed config/request fails before planner call, not a fresh route choice");
        var soleConfig = config with { Routes = [config.Routes[0], config.Routes[1] with { Available = false }] };
        var noModelLoads = 0;
        var sole = new StoryboardRouteSelector(soleConfig, () =>
        {
            noModelLoads++;
            throw new FileNotFoundException("Missing model must not matter for a deterministic sole route.");
        }, new string('b', 64));
        var soleChoice = await sole.Select(brief, constraints, default);
        Check(soleChoice.SelectedRoute == PlanningRouteId.Fast && soleChoice.SelectionMode == "deterministic-no-inference"
            && soleChoice.Distribution.Length == 0 && noModelLoads == 0, "One eligible route is deterministic and does not load a model");
        var none = new StoryboardRouteSelector(config with { Routes = config.Routes.Select(route => route with { Available = false }).ToArray() },
            () => throw new Exception("Must not load"), new string('b', 64));
        await Reject(() => none.Select(brief, constraints, default), "routing.no-eligible");
        var remoteConfig = config with {
            Routes = config.Routes.Select(route => route with { Planner = route.Planner with { Endpoint = "https://example.com/v1/", AllowRemote = true } }).ToArray()
        };
        var remote = new StoryboardRouteSelector(remoteConfig, () => native, new string('b', 64));
        Check(remote.Eligibility(brief).All(route => route.Reason == "remote-not-approved"),
            "Per-route remote opt-in alone cannot override application privacy eligibility");
        foreach (var bad in new[]
        {
            config.Routes[1] with { SupportsJson = false },
            config.Routes[1] with { AllowsNoTools = false },
            config.Routes[1] with { MaxPromptCharacters = 1 },
            config.Routes[1] with { MaxOutputTokens = 100 },
            config.Routes[1] with { Available = false }
        })
        {
            var filtered = new StoryboardRouteSelector(config with { Routes = [config.Routes[0], bad] },
                () => throw new Exception("No inference for one eligible"), new string('b', 64));
            Check((await filtered.Select(brief, constraints, default)).SelectedRoute == PlanningRouteId.Fast,
                "Capability/context/budget/availability filters cannot be relaxed by the decision");
        }
        foreach (var failure in new[] { "unknown", "missing", "reordered", "wrong-state", "nan", "wrong-selected", "runtime" })
        {
            native.Failure = failure;
            generatorCalls = fast.Calls + strong.Calls;
            await Reject(() => planner.Plan(brief, constraints, default));
            Check(generatorCalls == fast.Calls + strong.Calls, $"Invalid {failure} decision invokes neither planner");
        }
        native.Failure = null;
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            generatorCalls = fast.Calls + strong.Calls;
            nativeCalls = native.Calls;
            await Reject(() => planner.Plan(brief, constraints, cancellation.Token));
            Check(generatorCalls == fast.Calls + strong.Calls && nativeCalls == native.Calls,
                "Pre-cancelled routing calls neither decision nor generator");
        }
        native.CancelAfterResponse = true;
        using (var cancellation = new CancellationTokenSource())
        {
            native.Cancellation = cancellation;
            generatorCalls = fast.Calls + strong.Calls;
            await Reject(() => planner.Plan(brief, constraints, cancellation.Token));
            Check(generatorCalls == fast.Calls + strong.Calls, "Cancellation during decision prevents generation");
        }
        native.CancelAfterResponse = false;
        await Reject(() => selector.Select(new string('x', 6001), constraints, default), "routing.input");
        var originalOptions = new ChatOptions {
            ModelId = "wrong-original-model", MaxOutputTokens = 4096,
            ResponseFormat = ChatResponseFormat.Json
        };
        var messages = new[] { new ChatMessage(ChatRole.User, brief) };
        var wrapper = new RouteOptionsChatClient(fast, "fixture-fast");
        using (var router = RoutingChatClient.Create((context, token) =>
        {
            token.ThrowIfCancellationRequested();
            Check(context.Messages.SequenceEqual(messages), "Official router selector sees repeatable original messages");
            return ValueTask.FromResult<IChatClient>(wrapper);
        }))
        {
            await router.GetResponseAsync(messages, originalOptions);
            Check(originalOptions.ModelId == "wrong-original-model" && !ReferenceEquals(originalOptions, fast.Options)
                && fast.Options!.ModelId == "fixture-fast", "Normal forwarding overrides cloned route ModelId, not caller options");
            var updates = new List<ChatResponseUpdate>();
            await foreach (var update in router.GetStreamingResponseAsync(messages, originalOptions)) updates.Add(update);
            Check(fast.StreamCalls == 1 && updates.Count == 1 && updates[0].Text == "fixture-stream"
                && originalOptions.ModelId == "wrong-original-model", "Existing streaming surface forwards chosen client/options without a new product API");
        }
        wrapper.Dispose();
        Check(!fast.Disposed && !strong.Disposed, "Router and route wrappers do not dispose caller-owned clients");
        fast.Fail = true;
        generatorCalls = strong.Calls;
        await Reject(() => wrapper.GetResponseAsync(messages, originalOptions));
        Check(strong.Calls == generatorCalls, "Generation failure is not failover");
        fast.Fail = false;
        await Reject(() => wrapper.GetResponseAsync(messages, new ChatOptions {
            ModelId = "x", MaxOutputTokens = 10000,
            ResponseFormat = ChatResponseFormat.Json
        }), "routing.options");
        await Reject(() => wrapper.GetResponseAsync(messages, new ChatOptions {
            MaxOutputTokens = 4096,
            ResponseFormat = ChatResponseFormat.Text
        }), "routing.options");
        await Reject(() => wrapper.GetResponseAsync(messages, new ChatOptions {
            MaxOutputTokens = 4096,
            ResponseFormat = ChatResponseFormat.Json, AdditionalProperties = new() { ["override"] = "not-allowed" }
        }), "routing.options");
        var lazyLoads = 0;
        var created = new List<RecordingPlannerClient>();
        using (var optional = new OptionalRoutedPlanner(config, () => { lazyLoads++; return new FixtureDecisionClient(.95); },
            new string('b', 64), clientFactory: route =>
            {
                var client = new RecordingPlannerClient(route.Model);
                created.Add(client);
                return client;
            }))
        {
            Check(lazyLoads == 0 && created.Count == 0, "Constructing optional routed planner creates no native or chat dependencies");
            optional.EnsureConfigured();
            Check(lazyLoads == 0 && created.Count == 0, "Submission configuration check does not load native assets or generator clients");
            await optional.Plan(brief, constraints, default);
            Check(lazyLoads == 1 && created.Count == 1 && !created[0].Disposed,
                "Only actual two-route planning loads one reused native client and the selected chat client");
        }
        Check(created[0].Disposed, "Configured optional planner disposes only clients it created");
        using (var conflicting = new OptionalRoutedPlanner(config, () => throw new Exception("No load"), new string('b', 64),
            conflictingPlanner: true))
            await Reject(() => Task.Run(conflicting.EnsureConfigured), "routing.config");

        var bypass = new ThrowingPlanner();
        var fixtureRequest = new VideoRequest("Caller free-form request", constraints, FixtureChatClient.Outline());
        var fixtureFont = Path.Combine(root, "fixture-font.ttf");
        var fixtureLicense = Path.Combine(root, "fixture-license.txt");
        File.WriteAllText(fixtureFont, "Test-only pinned bytes, never used to render or infer.");
        File.WriteAllText(fixtureLicense, "Test-only license bytes.");
        var fixtureTools = Path.Combine(root, "fixture-production-tools.json");
        Json.Write(fixtureTools, new Tools(root, "node", "node", "node", Speech: new SpeechConfig("azure-speech", "af_heart", "en-us")));
        PrivateFiles.Protect(fixtureTools);
        var generation = new GenerationConfiguration(fixtureTools, fixtureFont, Files.Hash(fixtureFont),
            fixtureLicense, Files.Hash(fixtureLicense));
        var supplied = new ProductionVideoPipeline(bypass, generation);
        supplied.EnsureConfigured(fixtureRequest);
        Check(bypass.Calls == 0, "Production caller plan configuration bypasses routed planner entirely");
        var noAsr = generation with {
            TranscriberConfigFile = Path.Combine(root, "deliberately-missing-unused-asr.json"),
            NarrationPolicy = new(1, AuthoredNarrationPolicy.ScriptPhrases)
        };
        new ProductionVideoPipeline(bypass, noAsr).EnsureConfigured(fixtureRequest);
        Check(bypass.Calls == 0, "Script captions with QA off do not require or read configured but unused ASR dependencies");
        await Reject(() => Task.Run(() => new ProductionVideoPipeline(bypass, noAsr with { NarrationPolicy = noAsr.NarrationPolicy with { AnalyzeSpeech = true } }).EnsureConfigured(fixtureRequest)),
            "generation.transcriber-config-missing");
        await Reject(() => Task.Run(() => new ProductionVideoPipeline(bypass, noAsr with { NarrationPolicy = null }).EnsureConfigured(fixtureRequest)), "generation.transcriber-config-missing");
        Check(!Json.Text(generation).Contains("narrationPolicy", StringComparison.Ordinal),
            "Legacy generation configuration omits null policy without changing fingerprints");
        var callerJobRoot = Path.Combine(root, "caller-bypass");
        Directory.CreateDirectory(callerJobRoot);
        var missingTools = Path.Combine(root, "explicitly-missing-tools.json");
        var callerPipeline = new ProductionVideoPipeline(bypass, generation with { ToolsFile = missingTools });
        await Reject(() => callerPipeline.Execute(callerJobRoot, fixtureRequest, _ => { }, default), "generation.tools-missing");
        Check(bypass.Calls == 0 && !File.Exists(Path.Combine(callerJobRoot, "planning.json")),
            "Prerequisite failure precedes planning evidence and still never invokes the caller-plan-bypassed planner");
        Check(Files.Fingerprint(new VideoRequest(brief, constraints)) == Files.HashText(Json.Text(new { prompt = brief, constraints, plan = (VideoOutline?)null })),
            "No new routing fields change legacy video request identity");
        var oldOutline = FixtureChatClient.Outline();
        Check(Json.Text(new PlannedVideo(oldOutline, "legacy")) == Json.Text(new
        {
            outline = oldOutline, planningMode = "legacy", clientType = (string?)null, configuredModel = (string?)null,
            returnedModelId = (string?)null, originalPrompt = (string?)null, constraints = (VideoConstraints?)null
        }), "Null routing/usage fields preserve the exact legacy planning JSON shape");
        return count;
    }

    private sealed class ThrowingPlanner : IVideoPlanner
    {
        public int Calls;
        public void EnsureConfigured() { Calls++; throw new Exception("Caller plan must bypass planner"); }
        public Task<PlannedVideo> Plan(string prompt, VideoConstraints constraints, CancellationToken ct)
        { Calls++; throw new Exception("Caller plan must bypass planner"); }
    }
}

internal sealed class FixtureDecisionClient(double fastProbability) : IDecisionClient
{
    public double FastProbability { get; set; } = fastProbability;
    public string? Failure { get; set; }
    public int Calls { get; private set; }
    public DecisionRequest? LastRequest { get; private set; }
    public bool CancelAfterResponse { get; set; }
    public CancellationTokenSource? Cancellation { get; set; }
    public Task<DecisionResponse> GetResponseAsync(DecisionRequest request, DecisionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        LastRequest = request;
        if (Failure == "runtime") throw new InvalidOperationException("Explicit fixture native runtime failure");
        var question = (ChoiceDecisionQuestion)request.Questions.Single();
        var candidates = question.Candidates.ToArray();
        if (Failure == "reordered") Array.Reverse(candidates);
        if (Failure == "wrong-state") request = new DecisionRequest(JsonSerializer.SerializeToElement(new { brief = "wrong" }), request.Questions);
        if (Failure == "reordered") request = new DecisionRequest(request.State,
            [new ChoiceDecisionQuestion(question.Id, question.Instructions, candidates)]);
        var probabilities = candidates.Select(candidate => new DecisionProbability(candidate.Id,
            candidate.Id == "planning-fast" ? FastProbability : 1 - FastProbability)).ToArray();
        if (Failure == "nan") probabilities[0] = new(probabilities[0].Id, double.NaN);
        if (Failure == "missing") probabilities = probabilities[..1];
        var selected = Failure == "unknown" ? "unconfigured"
            : Failure == "wrong-selected" ? (FastProbability > .5 ? "planning-strong" : "planning-fast")
            : FastProbability >= .5 ? "planning-fast" : "planning-strong";
        var response = new DecisionResponse(request,
            [new ChoiceDecisionAnswer(question.Id, selected, probabilities)]);
        if (CancelAfterResponse) Cancellation!.Cancel();
        return Task.FromResult(response);
    }
    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

internal sealed class RecordingPlannerClient(string model) : IChatClient
{
    public int Calls { get; private set; }
    public int StreamCalls { get; private set; }
    public bool Disposed { get; private set; }
    public bool Fail { get; set; }
    public ChatOptions? Options { get; private set; }
    public ChatMessage[]? Input { get; private set; }
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        Input = messages.ToArray();
        Options = options;
        if (Fail) throw new InvalidOperationException("Fixture generator failure");
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Json.Text(FixtureChatClient.Outline()))) { ModelId = model });
    }
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StreamCalls++;
        Input = messages.ToArray();
        Options = options;
        await Task.CompletedTask;
        yield return new ChatResponseUpdate(ChatRole.Assistant, "fixture-stream");
    }
    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() => Disposed = true;
}
