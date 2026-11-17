using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Microsoft.Extensions.AI;
using VideoProduction;
using VideoProduction.Decisions.Experimental;
using VideoProduction.Providers;

internal static class ChatProviderTests
{
    private const string Claude = "fixture-claude";
    private const string Key = "synthetic-not-a-credential";
    private static PlannerConfiguration Connection => new(
        "https://fixture.services.ai.azure.com/anthropic/", Claude, null, AllowRemote: true,
        Provider: ChatProviderKind.Anthropic);

    public static async Task<int> Run(string root)
    {
        int count = 0;
        void Check(bool value, string message) { count++; if (!value) throw new Exception(message); }
        async Task Reject(Func<Task> action, string? code = null)
        {
            try { await action(); }
            catch (Exception error) when (error is not InvalidOperationException { Message: "Expected provider rejection." })
            {
                Check(code is null || error is VideoException v && v.Code == code, "Unexpected rejection: " + error.GetType().Name);
                return;
            }
            throw new InvalidOperationException("Expected provider rejection.");
        }
        Task Construct(PlannerConfiguration config, string? key = Key)
        {
            using var client = ChatVideoPlanner.CreateClient(config, explicitApiKey: key);
            return Task.CompletedTask;
        }

        var old = new PlannerConfiguration("http://localhost:9000/v1", "fixture", "unused-key-file");
        string serialized = Json.Text(old);
        Check(old.Provider == ChatProviderKind.OpenAI && !serialized.Contains("\"provider\""),
            "OpenAI remains the omitted legacy default, preserving configuration fingerprints.");
        Check(JsonSerializer.Deserialize<PlannerConfiguration>(serialized, Json.Options)!.Provider == ChatProviderKind.OpenAI,
            "Legacy settings reload as OpenAI.");
        Check(Json.Text(Connection).Contains("\"provider\": \"anthropic\"") ||
            Json.Text(Connection).Contains("\"provider\":\"anthropic\""), "Provider kind is an explicit readable enum.");
        await Reject(() => Task.Run(() => JsonSerializer.Deserialize<PlannerConfiguration>(
            serialized.TrimEnd('}') + ",\"provider\":\"unknown\"}", Json.Options)));
        await Reject(() => Task.Run(() => JsonSerializer.Deserialize<PlannerConfiguration>(
            serialized.TrimEnd('}') + ",\"provider\":42}", Json.Options)));
        await Reject(() => Construct(Connection with { Provider = (ChatProviderKind)42 }), "planner.config");
        foreach (var endpoint in new[] { "https://example.com/anthropic", "https://fixture.openai.azure.com/anthropic",
            "http://fixture.services.ai.azure.com/anthropic", "https://fixture.services.ai.azure.com/openai/v1",
            "https://fixture.services.ai.azure.com/anthropic?key=x", "https://user@fixture.services.ai.azure.com/anthropic" })
            await Reject(() => Construct(Connection with { Endpoint = endpoint }), "planner.config");
        await Reject(() => Construct(Connection with { AllowRemote = false }), "planner.config");
        await Reject(() => Construct(Connection with { Authentication = "azure-cli" }), "planner.config");
        await Reject(() => Construct(Connection with { Authentication = "api-key" }, null), "planner.config");
        await Reject(() => Construct(Connection with { ApiKeySecret = "invalid" }), "planner.config");
        await Reject(() => Task.Run(() => ChatVideoPlanner.CreateOpenAIClient(Connection, explicitApiKey: Key)), "planner.config");
        using (var idle = ChatVideoPlanner.CreateClient(Connection with { Authentication = "azure-cli" }))
            Check(idle.GetService<ChatClientMetadata>()?.DefaultModelId == Claude,
                "Entra Claude client construction/metadata performs no token acquisition or model call.");

        using var handler = new Transport();
        HttpClient Http() => new(handler, disposeHandler: false);
        using (var client = ChatVideoPlanner.CreateClient(Connection, explicitApiKey: Key, anthropicHttpClient: Http()))
        {
            var response = await client.GetResponseAsync([new(ChatRole.System, "Be precise."), new(ChatRole.User, "Explain cancellation.")],
                new() { MaxOutputTokens = 123 });
            Check(response.Text == "fixture answer" && response.ModelId == Claude &&
                response.Usage?.InputTokenCount == 10 && response.Usage.OutputTokenCount == 5,
                "Official Anthropic MEAI adapter preserves text/model/token usage.");
            var body = handler.Body;
            Check(handler.Path == "/anthropic/v1/messages" && handler.ApiKey is null && handler.Authorization == "Bearer " + Key &&
                handler.Version == "2026-10-21", "Foundry Anthropic uses Messages and the pinned SDK's explicit-key bearer/version headers.");
            Check(body.GetProperty("model").GetString() == Claude && body.GetProperty("max_tokens").GetInt32() == 123 &&
                body.GetProperty("messages")[0].GetProperty("role").GetString() == "user" &&
                body.GetProperty("system").GetRawText().Contains("Be precise."), "System/user roles and bounds reach real SDK serialization.");
            handler.Mode = "tool";
            var tool = AIFunctionFactory.Create((string title) => title, "propose_title");
            response = await client.GetResponseAsync([new(ChatRole.User, "Propose a title.")],
                new() { MaxOutputTokens = 123, Tools = [tool] });
            Check(response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>()
                .Single().Name == "propose_title" && response.FinishReason == ChatFinishReason.ToolCalls &&
                handler.Body.GetProperty("tools")[0].GetProperty("name").GetString() == "propose_title",
                "Typed tools cross the official adapter as inert proposals, without executing the function.");
            handler.Mode = "text";
            await client.GetResponseAsync([.. response.Messages,
                new(ChatRole.Tool, [new FunctionResultContent("fixture-call", new { accepted = true })])], new() { MaxOutputTokens = 123 });
            Check(handler.Body.GetRawText().Contains("\"tool_result\"") &&
                handler.Body.GetRawText().Contains("fixture-call"), "Tool acknowledgment is serialized through the native Messages protocol.");
            using var schema = JsonDocument.Parse("""{"type":"object","properties":{"answer":{"type":"string"}},"required":["answer"],"additionalProperties":false}""");
            await client.GetResponseAsync([new(ChatRole.User, "Return an answer object.")],
                new() { MaxOutputTokens = 123, ResponseFormat = ChatResponseFormat.ForJsonSchema(schema.RootElement) });
            Check(handler.Body.GetRawText().Contains("\"json_schema\"") && handler.Body.GetRawText().Contains("\"answer\""),
                "Explicit structured output schema reaches the official adapter, not an OpenAI wire body.");
            handler.Mode = "stream";
            var text = new StringBuilder();
            await foreach (var update in client.GetStreamingResponseAsync([new(ChatRole.User, "Stream.")], new() { MaxOutputTokens = 123 }))
                text.Append(update.Text);
            Check(text.ToString() == "fixture stream", "Claude SSE streams through IChatClient.");
            handler.Mode = "error";
            int before = handler.Calls;
            await Reject(() => client.GetResponseAsync([new(ChatRole.User, "Fail.")], new() { MaxOutputTokens = 123 }));
            Check(handler.Calls == before + 1, "Provider errors do not retry generation or switch providers.");
            using var cancel = new CancellationTokenSource();
            cancel.Cancel(); before = handler.Calls;
            await Reject(() => client.GetResponseAsync([new(ChatRole.User, "Cancelled.")], new() { MaxOutputTokens = 123 }, cancel.Token));
            Check(handler.Calls == before, "Pre-cancelled work never reaches transport.");
        }
        handler.Mode = "text";
        var credential = new Credential();
        using (var client = ChatVideoPlanner.CreateClient(Connection with { Authentication = "azure-cli" },
            anthropicHttpClient: Http(), anthropicCredential: credential))
        {
            Check(credential.Calls == 0, "Injected Entra credential remains lazy.");
            await client.GetResponseAsync([new(ChatRole.User, "Entra fixture.")], new() { MaxOutputTokens = 64 });
            Check(credential.Calls == 1 && credential.Scope == "https://cognitiveservices.azure.com/.default" &&
                handler.Authorization == "Bearer synthetic-token" && handler.ApiKey is null,
                "Anthropic SDK credential bridge requests exact Foundry scope and uses bearer auth, without API-key fallback.");
        }
        var failedCredential = new Credential { Fail = true };
        int previous = handler.Calls;
        using (var client = ChatVideoPlanner.CreateClient(Connection with { Authentication = "azure-cli" },
            anthropicHttpClient: Http(), anthropicCredential: failedCredential))
            await Reject(() => client.GetResponseAsync([new(ChatRole.User, "No credential fallback.")], new() { MaxOutputTokens = 64 }));
        Check(handler.Calls == previous, "Credential failure stops before HTTP; no ambient key or alternate provider is used.");
        var keyFile = Path.Combine(root, "anthropic-fixture-key");
        File.WriteAllText(keyFile, Key); PrivateFiles.Protect(keyFile);
        using (var client = ChatVideoPlanner.CreateClient(Connection with { ApiKeyFile = keyFile }, anthropicHttpClient: Http()))
            Check((await client.GetResponseAsync([new(ChatRole.User, "Private key.")], new() { MaxOutputTokens = 64 })).ModelId == Claude,
                "Explicit owner-private key-file mode uses the same adapter.");

        using var gptHandler = new Transport { OpenAI = true };
        using var gptHttp = new HttpClient(gptHandler);
        var gpt = new PlannerConfiguration("https://fixture.services.ai.azure.com/openai/v1/", "fixture-gpt", null,
            AllowRemote: true, Responses: true);
        ChatRouteReceipt? receipt = null;
        var scorer = new Scorer();
        var config = new ChatRoutingConfiguration(new("fixture-mixed-not-calibrated", LocalOnly: false),
            [new("gpt", "Reviewed GPT fixture route.", gpt, true, 24000, 4096),
             new("claude", "Reviewed Claude fixture route.", Connection, true, 24000, 4096)],
            new("unused-local-scorer-directory", null));
        IChatClient Factory(PlannerConfiguration c) => c.Provider == ChatProviderKind.Anthropic
            ? ChatVideoPlanner.CreateClient(c, explicitApiKey: Key, anthropicHttpClient: Http())
            : ChatVideoPlanner.CreateClient(c, new HttpClientPipelineTransport(gptHttp), Key);
        using (var router = DecisionRoutingChatClient.FromConfiguration(config, "mixed-provider-fixture", r => receipt = r, () => scorer, Factory))
        {
            var response = await router.GetResponseAsync([new(ChatRole.User, "Answer.")], new() { MaxOutputTokens = 64 });
            Check(response.ModelId == Claude && receipt is { SelectedRoute: "claude", SelectedModel: Claude } &&
                receipt.Distribution.Length == 2 && gptHandler.Calls == 0, "Mixed named pool dispatches only Claude and records its model/distribution.");
            scorer.Claude = false;
            response = await router.GetResponseAsync([new(ChatRole.User, "Answer.")], new() { MaxOutputTokens = 64 });
            Check(response.ModelId == "fixture-gpt" && receipt is { SelectedRoute: "gpt", SelectedModel: "fixture-gpt" } &&
                gptHandler.Path == "/openai/v1/responses", "The same pool selects real OpenAI Responses IChatClient serialization.");
        }
        var ownedHandler = new Transport();
        using (var client = ChatVideoPlanner.CreateClient(Connection, explicitApiKey: Key,
            anthropicHttpClient: new HttpClient(ownedHandler)))
            await client.GetResponseAsync([new(ChatRole.User, "Lifetime.")], new() { MaxOutputTokens = 64 });
        Check(ownedHandler.Disposed, "Disposing IChatClient releases the owned SDK and HTTP transport.");
        return count;
    }

    private sealed class Credential : TokenCredential
    {
        public int Calls;
        public string? Scope;
        public bool Fail;
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            GetTokenAsync(requestContext, cancellationToken).AsTask().GetAwaiter().GetResult();
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Calls++; Scope = requestContext.Scopes.Single();
            if (Fail) throw new Azure.Identity.AuthenticationFailedException("Synthetic credential failure.");
            return ValueTask.FromResult(new AccessToken("synthetic-token", DateTimeOffset.UtcNow.AddHours(1)));
        }
    }
    private sealed class Scorer : IDecisionClient
    {
        public bool Claude = true;
        public Task<DecisionResponse> GetResponseAsync(DecisionRequest request, DecisionOptions? options = null, CancellationToken cancellationToken = default)
        {
            var q = (ChoiceDecisionQuestion)request.Questions.Single();
            string selected = Claude ? "claude" : "gpt";
            return Task.FromResult(new DecisionResponse(request, [new ChoiceDecisionAnswer(q.Id, selected,
                q.Candidates.Select(c => new DecisionProbability(c.Id, c.Id == selected ? .9 : .1)).ToArray())]));
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
    private sealed class Transport : HttpMessageHandler
    {
        public int Calls;
        public string Mode = "text";
        public bool OpenAI;
        public bool Disposed;
        public string? Path, Authorization, ApiKey, Version;
        public JsonElement Body;
        protected override void Dispose(bool disposing)
        {
            if (disposing) Disposed = true;
            base.Dispose(disposing);
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Calls++;
            Path = request.RequestUri!.AbsolutePath; Authorization = request.Headers.Authorization?.ToString();
            ApiKey = request.Headers.TryGetValues("x-api-key", out var keys) ? keys.Single() : null;
            Version = request.Headers.TryGetValues("anthropic-version", out var versions) ? versions.Single() : null;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Body = body.RootElement.Clone();
            if (Mode == "error") return new(HttpStatusCode.BadRequest) {
                Content = new StringContent(
                """{"type":"error","error":{"type":"invalid_request_error","message":"Synthetic error."}}""", Encoding.UTF8, "application/json")
            };
            if (Mode == "stream") return new(HttpStatusCode.OK) { Content = new StringContent("""
                event: message_start
                data: {"type":"message_start","message":{"id":"fixture-stream","type":"message","role":"assistant","model":"fixture-claude","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":10,"output_tokens":0}}}

                event: content_block_start
                data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

                event: content_block_delta
                data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"fixture stream"}}

                event: content_block_stop
                data: {"type":"content_block_stop","index":0}

                event: message_delta
                data: {"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":5}}

                event: message_stop
                data: {"type":"message_stop"}


                """, Encoding.UTF8, "text/event-stream") };
            string response = OpenAI ? """
                {"id":"fixture-response","object":"response","created_at":1,"status":"completed","model":"fixture-gpt","output":[{"type":"message","id":"fixture-msg","role":"assistant","status":"completed","content":[{"type":"output_text","text":"fixture gpt answer","annotations":[]}]}],"usage":{"input_tokens":10,"output_tokens":5,"total_tokens":15}}
                """ : Mode == "tool" ? """
                {"id":"fixture-tool","type":"message","role":"assistant","model":"fixture-claude","content":[{"type":"tool_use","id":"fixture-call","name":"propose_title","input":{"title":"A fixture title"}}],"stop_reason":"tool_use","stop_sequence":null,"usage":{"input_tokens":10,"output_tokens":5}}
                """ : """
                {"id":"fixture-message","type":"message","role":"assistant","model":"fixture-claude","content":[{"type":"text","text":"fixture answer"}],"stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":10,"output_tokens":5}}
                """;
            return new(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }
}
