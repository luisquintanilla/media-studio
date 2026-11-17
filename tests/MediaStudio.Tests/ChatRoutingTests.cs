using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using VideoProduction;
using VideoProduction.Decisions.Experimental;
using VideoProduction.Providers;

internal static class ChatRoutingTests
{
    public static async Task<int> Run(string root)
    {
        var assertions = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); assertions++; }
        async Task Reject(Func<Task> action, string? code = null)
        {
            try { await action(); }
            catch (Exception e) when (e is VideoException or DecisionProtocolException or OperationCanceledException)
            {
                if (code is not null) Check(e is VideoException v && v.Code == code, $"Expected {code}, received {e}");
                else assertions++;
                return;
            }
            throw new Exception("Routing unexpectedly accepted invalid input/state");
        }
        var policy = new ChatRoutePolicy("fixture-not-calibrated");
        NamedChatRoute Route(string name) => new(name, $"Reviewed {name} provider criteria.",
            new("http://localhost:9000/v1/", name + "-model", null), true, 24000, 4096);
        var config = new ChatRoutingConfiguration(policy, [Route("fast"), Route("strong"), Route("specialist")],
            new("unused-pinned-model-directory", null));
        var scorer = new Scorer { Probabilities = [.85, .1, .05] };
        var created = new Dictionary<string, Client>();
        IChatClient Create(PlannerConfiguration c) => created[c.Model] = new Client(c.Model);
        ChatRouteReceipt? last = null;
        var callerOptions = new ChatOptions { ModelId = "caller-model-is-not-a-route", MaxOutputTokens = 512 };
        IEnumerable<ChatMessage> Once()
        {
            yield return new(ChatRole.System, "Answer a general programming question.");
            yield return new(ChatRole.User, "Explain cancellation in C#.");
        }
        using (var router = DecisionRoutingChatClient.FromConfiguration(config, "general-chat", r => last = r, () => scorer, Create))
        {
            Check(created.Count == 0 && scorer.Calls == 0, "Constructing policy router performs no provider/model work");
            var response = await router.GetResponseAsync(Once(), callerOptions);
            Check(response.Text == "fast-model" && created.Count == 1 && scorer.Calls == 1, "Named three-provider pool routes general chat");
            Check(last!.SelectedRoute == "fast" && last.Distribution.Length == 3 && last.Precision == DecisionPrecision.HighPrecision,
                "Actual complete domain/distribution/precision and choice retained");
            Check(scorer.Last!.State.GetProperty("purpose").GetString() == "general-chat"
                && scorer.Last.State.GetProperty("conversation").GetString()!.Contains("Explain cancellation in C#."),
                "Actual non-storyboard conversation reaches the decision, no keyword classifier or storyboard state");
            Check(callerOptions.ModelId == "caller-model-is-not-a-route" && created["fast-model"].Model == "fast-model",
                "Selected model overrides cloned options only");
            Check(response.AdditionalProperties?[DecisionRoutingChatClient.ReceiptKey] is ChatRouteReceipt { SelectedModel: "fast-model" },
                "Per-response receipt identifies actual selected provider, not the legacy model placeholder");
            scorer.Probabilities = [.45, .35, .2];
            await router.GetResponseAsync(Once(), callerOptions);
            Check(last!.SelectedRoute == "strong" && last.ObservedRoute == "fast"
                && last.Reason == "low-confidence-escalated", "Low confidence escalates to the next stronger eligible tier");
            scorer.Probabilities = [.1, .1, .8];
            await foreach (var update in router.GetStreamingResponseAsync(Once(), callerOptions))
                Check(update.Text == "specialist-model", "Streaming forwards exactly chosen specialist");
            var before = created.Values.Sum(c => c.Calls);
            scorer.Fail = true;
            await Reject(() => router.GetResponseAsync(Once(), callerOptions));
            Check(created.Values.Sum(c => c.Calls) == before, "Scorer failure is not generation fallback");
            scorer.Fail = false;
            scorer.WrongState = true;
            await Reject(() => router.GetResponseAsync(Once(), callerOptions));
            Check(created.Values.Sum(c => c.Calls) == before, "Wrong-state response is rejected before dispatch");
            scorer.WrongState = false;
            await Reject(() => router.GetResponseAsync([new(ChatRole.User, [new DataContent(new byte[] { 1 }, "image/png")])], callerOptions), "routing.options");
            await Reject(() => router.GetResponseAsync(Once(), withOptions(o => o.AdditionalProperties = new() { ["override"] = true })), "routing.options");
            await Reject(() => router.GetResponseAsync(Once(), withOptions(o => o.MaxOutputTokens = 5000)), "routing.options");
            await Reject(() => router.GetResponseAsync(Once(), withOptions(o => o.ConversationId = "hidden-remote-history")), "routing.options");
            await Reject(() => router.GetResponseAsync(Once(), withOptions(o => o.Instructions = new string('x', 24000))), "routing.input");
            await Reject(() => router.GetResponseAsync(Enumerable.Repeat(new ChatMessage(ChatRole.User, "bounded"), 129), callerOptions), "routing.options");
            await Reject(() => router.GetResponseAsync([new(ChatRole.User, "NUL\0")], callerOptions), "routing.input");
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            var calls = scorer.Calls;
            await Reject(() => router.GetResponseAsync(Once(), callerOptions, cancel.Token));
            Check(scorer.Calls == calls, "Pre-cancelled invocation never scores");
        }
        Check(created.Values.All(c => c.Disposed) && scorer.Disposed, "Router disposes only its owned lazy clients and scorer");

        var noLoads = 0;
        using (var sole = DecisionRoutingChatClient.FromConfiguration(config with { Routes = [Route("fast")] }, "svg-illustration", r => last = r,
            () => { noLoads++; throw new Exception("No scorer may be loaded"); }, c => new Client(c.Model)))
        {
            await sole.GetResponseAsync([new(ChatRole.User, "Author a bounded SVG.")], callerOptions);
            Check(noLoads == 0 && last!.Distribution.Length == 0 && last.Reason == "sole-eligible",
                "Same router supports the actual non-JSON SVG authoring path without inferred decisions for a singleton");
        }
        var filters = new[]
        {
            config with { Routes = config.Routes.Select(r => r with { Available = false }).ToArray() },
            config with { Routes = config.Routes.Select(r => r with { Connection = r.Connection with { Endpoint = "https://example.com/v1/", AllowRemote = true } }).ToArray() },
            config with { Routes = config.Routes.Select(r => r with { MaxInputCharacters = 1 }).ToArray() },
            config with { Routes = config.Routes.Select(r => r with { MaxOutputTokens = 1 }).ToArray() },
            config with { Policy = policy with { RequireJson = true }, Routes = config.Routes.Select(r => r with { SupportsJson = false }).ToArray() }
        };
        foreach (var invalid in filters)
        {
            using var router = DecisionRoutingChatClient.FromConfiguration(invalid, "general-chat", _ => throw new Exception("No receipt on no-eligible"),
                () => throw new Exception("No scorer"), _ => throw new Exception("No generator"));
            await Reject(() => router.GetResponseAsync(Once(),
                new ChatOptions { MaxOutputTokens = 512, ResponseFormat = ChatResponseFormat.Json }), "routing.no-eligible");
        }
        using (var json = DecisionRoutingChatClient.FromConfiguration(config with { Policy = policy with { RequireJson = true } },
            "json-answer", _ => { }, () => new Scorer(), c => new Client(c.Model)))
            await Reject(() => json.GetResponseAsync(Once(), callerOptions), "routing.options");
        using (var escalate = DecisionRoutingChatClient.FromConfiguration(config, "general-chat", r => last = r,
            () => new Scorer { Probabilities = [.4, .35, .25] }, c => new Client(c.Model)))
            Check((await escalate.GetResponseAsync(Once(), callerOptions)).Text == "strong-model" && last!.ObservedRoute == "fast",
                "Uncertain choice is escalated, never rejected");
        using (var privacy = DecisionRoutingChatClient.FromConfiguration(config with { Decision = new(null, new("https://example.com/v1/", "decider", null, true)) },
            "general-chat", _ => { }, () => throw new Exception("No cloud scorer"), _ => throw new Exception("No generator")))
            await Reject(() => privacy.GetResponseAsync(Once(), callerOptions), "routing.scorer-privacy");

        var flaky = new Dictionary<string, Client>();
        using (var failover = DecisionRoutingChatClient.FromConfiguration(config, "general-chat", r => last = r, () => new Scorer(),
            c => flaky[c.Model] = new Client(c.Model) { Transient = c.Model == "fast-model" }))
        {
            var response = await failover.GetResponseAsync(Once(), callerOptions);
            Check(response.Text == "strong-model" && last is { SelectedRoute: "strong", FailedOverFrom: "fast", Reason: "failover" }
                && response.AdditionalProperties?[DecisionRoutingChatClient.ReceiptKey] is ChatRouteReceipt { FailedOverFrom: "fast" },
                "Transient local failure fails over to the next stronger eligible tier and records it");
        }
        var cloudPool = config with {
            Routes = [Route("fast"), Route("strong") with { Connection = Route("strong").Connection with
            { Endpoint = "https://example.com/v1/", AllowRemote = true } }]
        };
        using (var localOnly = DecisionRoutingChatClient.FromConfiguration(cloudPool, "general-chat", _ => { }, () => new Scorer(),
            c => c.Model == "fast-model" ? new Client(c.Model) { Transient = true } : throw new Exception("Cloud tier must not be created")))
            await Reject(() => localOnly.GetResponseAsync(Once(), callerOptions), "routing.local-unavailable");
        using (var generationFailure = DecisionRoutingChatClient.FromConfiguration(config, "general-chat", _ => { }, () => new Scorer(),
            _ => new Client("fast") { Fail = true }))
            await Reject(() => generationFailure.GetResponseAsync(Once(), callerOptions), "fixture.generation");

        using (var receiptFailure = DecisionRoutingChatClient.FromConfiguration(config, "general-chat",
            _ => throw new VideoException("fixture.receipt", "Cannot durably record."),
            () => new Scorer(), _ => throw new Exception("Receipt failure cannot construct generator")))
            await Reject(() => receiptFailure.GetResponseAsync(Once(), callerOptions), "fixture.receipt");
        using (var cancellation = new CancellationTokenSource())
        using (var cancelledScore = DecisionRoutingChatClient.FromConfiguration(config, "general-chat", _ => throw new Exception("No receipt after cancel"),
            () => new Scorer { AfterScore = cancellation.Cancel }, _ => throw new Exception("No generation after cancel")))
            await Reject(() => cancelledScore.GetResponseAsync(Once(), callerOptions, cancellation.Token));
        using (var ineligibleFallback = DecisionRoutingChatClient.FromConfiguration(config with { Routes = config.Routes.Select(r => r.Name == "strong" ? r with { Available = false } : r).ToArray() },
            "general-chat", r => last = r, () => new Scorer { Probabilities = [.55, .45] }, c => new Client(c.Model)))
            Check((await ineligibleFallback.GetResponseAsync(Once(), callerOptions)).Text == "specialist-model",
                "Escalation skips ineligible tiers");
        var planRoot = Path.Combine(root, "general-planner");
        Directory.CreateDirectory(planRoot);
        var planScores = 0;
        var planner = new PolicyRoutedVideoPlanner(config,
            () => { planScores++; return new Scorer(); },
            c => new Client(c.Model) { ResponseText = Json.Text(FixtureChatClient.Outline()) });
        var planned = await planner.PlanBound(planRoot, "Explain cancellation in C#.", new(90, 8), default);
        Check(planned.ChatRouting?.SelectedRoute == "fast" && planned.ConfiguredModel == "fast-model"
            && File.Exists(Path.Combine(planRoot, "chat-route.json")), "Actual planner adapter records the selected general chat route");
        await planner.PlanBound(planRoot, "Different brief.", new(90, 8), default);
        Check(planScores == 2, "A retry is routed afresh; receipts are telemetry, not bindings");

        var svgScorer = new Scorer();
        Client? svgClient = null;
        ChatSvgAttempt? svgAttempt = null;
        using (var svgRouter = DecisionRoutingChatClient.FromConfiguration(config, "svg-illustration", r => last = r,
            () => svgScorer, c => svgClient = new Client(c.Model) { ResponseText = "<bad/>" }))
        using (var image = new ChatSvgImageGenerator(svgRouter, "legacy-placeholder",
            new(root, "unused-no-render", root, "unused", "unused"), r => svgAttempt = r))
        {
            await Reject(() => image.GenerateAsync(new("Explain fast and slow memory.")), "image.svg");
            Check(svgScorer.Last!.State.GetProperty("purpose").GetString() == "svg-illustration"
                && svgClient?.Model == "fast-model" && last!.SelectedModel == "fast-model",
                "Actual IImageGenerator path uses general scorer and chosen IChatClient; strict invalid-SVG rejection remains");
            Check(svgAttempt?.ConfiguredModel == "fast-model",
                "Even rejected SVG attempt evidence names actual selected configured model, not the legacy placeholder");
        }
        var mutable = config with { Routes = config.Routes.ToArray() };
        using (var snapshot = DecisionRoutingChatClient.FromConfiguration(mutable, "general-chat", _ => { },
            () => new Scorer(), c => new Client(c.Model)))
        {
            mutable.Routes[0] = mutable.Routes[0] with { Available = false };
            Check((await snapshot.GetResponseAsync(Once(), callerOptions)).Text == "fast-model",
                "A caller's later configuration mutation cannot change the router-owned approved pool");
        }
        var cover = new MediaCoverConfiguration(Route("fast").Connection,
            new(root, "unused", root, "unused", new string('a', 64)));
        Check(!Json.Text(cover).Contains("\"routing\"", StringComparison.Ordinal)
            && Files.Fingerprint(cover) != Files.Fingerprint(cover with { Routing = config }),
            "Absent cover routing preserves legacy serialized identity; opting in changes exact production configuration fingerprint");
        return assertions;

        ChatOptions withOptions(Action<ChatOptions> change)
        {
            var options = callerOptions.Clone();
            change(options);
            return options;
        }
    }

    private sealed class Scorer : IDecisionClient
    {
        public double[] Probabilities { get; set; } = [.9, .06, .04];
        public bool WrongState, Fail, Disposed;
        public Action? AfterScore;
        public int Calls;
        public DecisionRequest? Last;
        public Task<DecisionResponse> GetResponseAsync(DecisionRequest request, DecisionOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++; Last = request;
            if (Fail) throw new DecisionProtocolException("Fixture failure");
            var q = (ChoiceDecisionQuestion)request.Questions.Single();
            var selected = Array.IndexOf(Probabilities, Probabilities.Max());
            if (WrongState) request = new(JsonSerializer.SerializeToElement("wrong"), request.Questions);
            var response = new DecisionResponse(request,
                [new ChoiceDecisionAnswer(q.Id, q.Candidates[selected].Id,
                    q.Candidates.Select((c, i) => new DecisionProbability(c.Id, Probabilities[i])).ToArray())]);
            AfterScore?.Invoke();
            return Task.FromResult(response);
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() => Disposed = true;
    }
    private sealed class Client(string model) : IChatClient
    {
        public bool Disposed, Fail, Transient;
        public int Calls;
        public string? Model;
        public string? ResponseText;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++; Model = options?.ModelId;
            if (Fail) throw new VideoException("fixture.generation", "No failover.");
            if (Transient) throw new HttpRequestException("fixture connection refused");
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, ResponseText ?? model)) { ModelId = model });
        }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++; Model = options?.ModelId;
            await Task.Yield();
            yield return new(ChatRole.Assistant, model);
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() => Disposed = true;
    }
}
