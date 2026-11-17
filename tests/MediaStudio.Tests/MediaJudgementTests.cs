using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using VideoProduction;
using VideoProduction.Decisions.Experimental;
using VideoProduction.Providers;

internal static class MediaJudgementTests
{
    public static async Task<int> Run(string root)
    {
        var count = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); count++; }
        async Task Reject<T>(Func<Task> action) where T : Exception
        {
            try { await action(); throw new Exception("Expected " + typeof(T).Name); }
            catch (T) { count++; }
        }
        using var fixture = new FixtureDecision();
        using var judgements = new MediaJudgements(() => fixture);
        var teaching = new TeachingIntent("Explain revisions", "Working edits", "Approval binds saved bytes",
            "Show the data/authority relationship", "Synthetic mechanism example, not a performance benchmark");
        var title = new ProposedScene(TemplateKind.Title, "Revision", "", "Only saved revisions authorize work.", Teaching: teaching);
        Check((await judgements.SuggestVisual(title, default)).SelectedId == "plain" && fixture.Calls == 0,
            "A sole eligible plain title never invokes inference.");
        var flow = title with { Kind = TemplateKind.Diagram, Text = "Working edits\nSaved revision\nApproved production" };
        var eligible = MediaJudgements.EligibleVisuals(flow);
        Check(eligible.Select(c => c.Id).SequenceEqual(new[] { "plain", "architectureFlowEmphasis", "beforeAfterComparison" }),
            "Renderer eligibility filters code focus from a diagram before inference.");
        fixture.Selected = "architectureFlowEmphasis";
        var suggestion = await judgements.SuggestVisual(flow, default);
        Check(suggestion.Treatment?.Preset == VisualPreset.ArchitectureFlowEmphasis && suggestion.Distribution.Length == 3,
            "Bounded choice retains exact eligible treatment and complete distribution.");
        Check(flow.Visual is null && fixture.Last!.State.GetProperty("guidance").GetProperty("sha256").GetString() == TeachingGuidance.Identity.Sha256,
            "Visual inference receives typed guidance identity and never mutates the authored scene.");
        Check(MediaJudgements.EligibleVisuals(flow with { Text = new string('x', 100) }).Length == 1,
            "Overflow cannot become an eligible preset merely because a model selects it.");
        fixture.Failure = "unknown";
        await Reject<DecisionProtocolException>(() => judgements.SuggestVisual(flow, default));
        fixture.Failure = "wrong-state";
        await Reject<DecisionProtocolException>(() => judgements.SuggestVisual(flow, default));
        fixture.Failure = "provider";
        await Reject<DecisionClientException>(() => judgements.SuggestVisual(flow, default));
        fixture.Failure = null;
        var hash = new string('a', 64);
        var observation = new TranscriptLearning(1, "synthetic.wav", hash, 16000, 16000, "derived.wav", hash,
            16000, 16000, "Actual supplied report clock", "synthetic-typed-fixture-not-STT", "fixture", "fixture", hash,
            [], hash, hash, "Only saved revisions authorize work.", "", "Every edit authorizes work.",
            [new(0, 1000, 0, 16000, "Every edit authorizes work.", Evidence.AsrSegment)],
            Transcription.Compare("Only saved revisions authorize work.", "Every edit authorizes work."));
        var advisory = await judgements.Triage(observation, default);
        Check(advisory.RequiresReview && advisory.MeaningChangeProbability == .9
            && advisory.EvidenceBoundary.Contains("not acoustic verification", StringComparison.Ordinal),
            "Native triage remains advisory and cannot clear deterministic review or certify audio.");
        var calls = fixture.Calls;
        Check((await judgements.Triage(observation with { ObservedText = observation.IntendedScript }, default)).Mode
            == "exact-normalized-text-no-inference" && fixture.Calls == calls, "Exact text agreement skips native inference.");
        await Reject<VideoException>(() => judgements.Triage(observation with { ObservedText = "" }, default));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Reject<OperationCanceledException>(() => judgements.SuggestVisual(flow, cancel.Token));
        Check(fixture.Calls == calls, "Cancellation and missing evidence do not infer.");

#if OPENAI_DECISIONS
        var key = Path.Combine(root, "synthetic-decision-key");
        File.WriteAllText(key, "synthetic-intercepted-only");
        PrivateFiles.Protect(key);
        using var handler = new NativeHandler();
        using var http = new HttpClient(handler);
        var native = new OpenAiDecisionsConfiguration("https://offline.invalid/v1", "synthetic-native", key, true);
        using var nativeClient = VideoProduction.Providers.Decisions.DecisionClientFactory.CreateStrict(new(null, native), new HttpClientPipelineTransport(http));
        var config = RoutingTests.Config() with { OpenAi = native };
        var selector = new StoryboardRouteSelector(config, () => nativeClient, new string('b', 64));
        using var chat = new FixtureChat();
        var routes = config.Routes.ToDictionary(r => r.Id, r => (r.Planner.Model, (Func<IChatClient>)(() => chat)));
        var planner = new RoutedVideoPlanner(selector, routes);
        var boundRoot = Path.Combine(root, "native-route-binding");
        Directory.CreateDirectory(boundRoot);
        var planned = await planner.PlanBound(boundRoot, "Explain the revision-bound approval mechanism.", new(30, 3), default);
        Check(planned.PlanningMode == "server-typed-openai-decision-routed-IChatClient"
            && planned.Routing!.SelectionMode == "typed-openai-decision"
            && planned.Routing.NativeConfigurationSha256 == Files.Fingerprint(native),
            "Real typed SDK factory composes into the real router with native mode and exact configuration binding.");
        _ = await planner.PlanBound(boundRoot, "Explain the revision-bound approval mechanism.", new(30, 3), default);
        Check(handler.Calls == 1 && chat.Calls == 2, "Persisted route replay does not invoke native inference again.");
        Check(chat.System!.Contains(TeachingGuidance.Instructions, StringComparison.Ordinal)
            && chat.System.Contains("Optional \"teaching\"", StringComparison.Ordinal),
            "Actual planner receives the one canonical instruction body and supported typed teaching contract.");
        handler.Fast = .50; handler.Strong = .49;
        var roundedRoot = Path.Combine(root, "native-rounded-route");
        Directory.CreateDirectory(roundedRoot);
        var rounded = await planner.PlanBound(roundedRoot, "Explain a qualified revision comparison.", new(30, 3), default);
        _ = await planner.PlanBound(roundedRoot, "Explain a qualified revision comparison.", new(30, 3), default);
        Check(handler.Calls == 2 && rounded.Routing!.Provenance?.Precision == DecisionPrecision.TwoDecimalPlaces,
            "An SDK-rounded 0.50/0.49 distribution persists and replays with its exact declared precision, not a widened global tolerance.");
        var bindingFile = Path.Combine(roundedRoot, "route-binding.json");
        var original = File.ReadAllText(bindingFile);
        Json.Write(bindingFile, rounded.Routing! with
        {
            Distribution = [new(PlanningRouteId.Fast, .5), new(PlanningRouteId.Strong, .4)]
        });
        await Reject<VideoException>(() => planner.PlanBound(roundedRoot, "Explain a qualified revision comparison.", new(30, 3), default));
        Check(handler.Calls == 2, "Malformed persisted mass is rejected without rerouting/inference.");
        File.WriteAllText(bindingFile, original);
        handler.Strong = .4;
        await Reject<VideoException>(() => planner.Plan("Explain a malformed synthetic distribution.", new(30, 3), default));
#else
        // Native Decisions need OpenAI 2.15; without it the typed provider must fail explicitly, never fall back.
        await Reject<VideoException>(() => Task.FromResult(VideoProduction.Providers.Decisions.DecisionClientFactory.CreateStrict(new(null,
            new OpenAiDecisionsConfiguration("https://offline.invalid/v1", "synthetic-native", null, true)))));
#endif
        return count;
    }

    private sealed class FixtureDecision : IDecisionClient
    {
        public int Calls { get; private set; }
        public string Selected { get; set; } = "plain";
        public string? Failure { get; set; }
        public DecisionRequest? Last { get; private set; }
        public Task<DecisionResponse> GetResponseAsync(DecisionRequest request, DecisionOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls++; Last = request;
            if (Failure == "provider") throw new DecisionClientException("Synthetic explicit failure");
            var question = request.Questions.Single();
            DecisionAnswer answer = question is ChoiceDecisionQuestion choice
                ? new ChoiceDecisionAnswer(question.Id, Failure == "unknown" ? "unknown" : Selected,
                    choice.Candidates.Select(c => new DecisionProbability(c.Id, c.Id == Selected ? 1 : 0)).ToArray())
                : new BinaryDecisionAnswer(question.Id, .9);
            return Task.FromResult(new DecisionResponse(Failure == "wrong-state"
                ? new(JsonSerializer.SerializeToElement(new { wrong = true }), request.Questions) : request, [answer]));
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
    private sealed class FixtureChat : IChatClient
    {
        public int Calls { get; private set; }
        public string? System { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls++; System = messages.First().Text;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                Json.Text(new VideoOutline("Typed fixture", [new(TemplateKind.Title, "Approval", "", "Saved revisions bind approval.")])))) { ModelId = options?.ModelId });
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
    private sealed class NativeHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public double Fast { get; set; } = .95;
        public double Strong { get; set; } = .05;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            if (request.RequestUri != new Uri("https://offline.invalid/v1/decisions")
                || request.Headers.Authorization?.Parameter != "synthetic-intercepted-only")
                throw new InvalidOperationException("Only the exact synthetic intercepted SDK request is allowed.");
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var name = payload.RootElement.GetProperty("questions")[0].GetProperty("name").GetString();
            return new(HttpStatusCode.OK) {
                Content = new StringContent($$"""
                    {"model":"synthetic-native-returned","answers":[{"type":"choice","name":"{{name}}",
                    "choice":"planning-fast","confidence":0.95,"probabilities":[
                    {"value":"planning-fast","probability":{{JsonSerializer.Serialize(Fast)}}},{"value":"planning-strong","probability":{{JsonSerializer.Serialize(Strong)}}}]}]}
                    """, Encoding.UTF8, "application/json")
            };
        }
    }
}
