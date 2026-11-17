#if OPENAI_DECISIONS
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using OpenAI.Decisions;
using VideoProduction.Decisions;
using VideoProduction.Decisions.Experimental;
using NeutralScoreLevel = VideoProduction.Decisions.Experimental.DecisionScoreLevel;

#pragma warning disable OPENAI001, VPD001
internal static class OpenAiDecisionTests
{
    public static async Task TypedHttpFixtures()
    {
        using var handler = new FixtureHandler();
        using var http = new HttpClient(handler);
        var sdk = new DecisionsClient(new ApiKeyCredential("synthetic-offline-key"), new DecisionsClientOptions
        {
            Endpoint = new Uri("https://offline.invalid/v1"),
            Transport = new HttpClientPipelineTransport(http), RetryPolicy = new ClientRetryPolicy(0)
        });
        using var client = new OpenAiDecisionClient(sdk, "synthetic-decisions-fixture");
        handler.Answers = """[{"type":"predicate","name":"absent-number"}]""";
        var sdkDefault = await sdk.CreateDecisionAsync(new DecisionCreationOptions
        {
            Model = "synthetic-decisions-fixture",
            InputItems = { new DecisionsUserMessageItem
            {
                Content = { new DecisionsInputJsonContentPart([BinaryData.FromString("{}")]) }
            } },
            Questions = { new DecisionsPredicateQuestion { Name = "absent-number" } }
        });
        Check.That(sdkDefault.Value.Answers[0] is DecisionsPredicateAnswer { Probability: 0 },
            "Pinned SDK defaults an absent numeric property to zero; raw presence validation is necessary.");
        handler.Answers = FixtureHandler.ValidAnswers;
        using var state = JsonDocument.Parse("""{"text":"fixture only"}""");
        var request = new DecisionRequest(state.RootElement,
        [
            new BinaryDecisionQuestion("predicate-id", "The fixture is explicit."),
            new ChoiceDecisionQuestion("choice-id", "Choose the task.", [
                new DecisionCandidate("opaque-alpha", "First"), new DecisionCandidate("opaque-beta", "Second")]),
            new ScoreDecisionQuestion("score-id", "Rate the fixture.", [
                new NeutralScoreLevel("low-id", "Low"), new NeutralScoreLevel("high-id", "High")])
        ]);
        var result = await client.GetResponseAsync(request);
        Check.That(result.Answers.Count == 3, "Typed SDK batch is complete.");
        Check.That(Math.Abs(((BinaryDecisionAnswer)result.Answers[0]).TrueProbability - 0.83) < 0.000001,
            "Predicate is true probability (SDK float retained as double).");
        var choice = (ChoiceDecisionAnswer)result.Answers[1];
        Check.That(choice.SelectedCandidateId == "opaque-beta" && choice.Probabilities.Count == 2,
            "Exact opaque choice IDs and complete distribution survive.");
        Check.That(Math.Abs((double)choice.AdditionalProperties!["nativeConfidence"]! - 0.91) < 0.000001
            && Math.Abs(choice.Probabilities.Single(p => p.Id == "opaque-beta").Value - 0.7) < 0.000001,
            "Native confidence is separate from selected probability.");
        var score = (ScoreDecisionAnswer)result.Answers[2];
        Check.That(score.Score == 0.75 && score.ExpectedScore == 0.75 && score.Probabilities[1].Id == "high-id",
            "Score is expected ordinal value, not selected level.");
        Check.That(result.Provenance?.ModelId == "synthetic-decisions-fixture-returned"
            && result.Usage?.TotalTokenCount == 17, "Actual returned model and usage survive.");
        using var posted = JsonDocument.Parse(handler.LastRequest!);
        Check.That(posted.RootElement.GetProperty("questions")[1].GetProperty("choices")[0]
            .GetProperty("value").GetString() == "opaque-alpha", "Typed SDK request preserves caller IDs.");
        Check.That(posted.RootElement.GetProperty("input")[0].GetProperty("content")[0]
            .GetProperty("type").GetString() == "input_json", "State uses actual typed SDK JSON content.");

        handler.Answers = """[{"type":"refusal","name":"predicate-id"}]""";
        await Check.ThrowsAsync<DecisionProtocolException>(() => client.GetResponseAsync(request));
        handler.Answers = """
            [{"type":"predicate","name":"predicate-id","probability":0.83},
             {"type":"refusal","name":"choice-id"},
             {"type":"score","name":"score-id","score":0.75,"confidence":0.9,
              "probabilities":[{"value":0,"label":"low-id","probability":0.25},{"value":1,"label":"high-id","probability":0.75}]}]
            """;
        await Check.ThrowsAsync<DecisionRefusedException>(() => client.GetResponseAsync(request));
        handler.Answers = FixtureHandler.ValidAnswers.Replace("\"opaque-alpha\",\"probability\":0.3",
            "\"unknown-id\",\"probability\":0.3", StringComparison.Ordinal);
        await Check.ThrowsAsync<DecisionProtocolException>(() => client.GetResponseAsync(request));
        handler.Answers = FixtureHandler.ValidAnswers.Replace("\"score\":0.75", "\"score\":0.25", StringComparison.Ordinal);
        await Check.ThrowsAsync<DecisionProtocolException>(() => client.GetResponseAsync(request));
        foreach (var missing in new[]
        {
            (Answer: 0, Field: "probability", Distribution: false),
            (Answer: 1, Field: "confidence", Distribution: false),
            (Answer: 1, Field: "probability", Distribution: true),
            (Answer: 2, Field: "score", Distribution: false),
            (Answer: 2, Field: "confidence", Distribution: false),
            (Answer: 2, Field: "value", Distribution: true),
            (Answer: 2, Field: "probability", Distribution: true)
        })
        {
            var changed = System.Text.Json.Nodes.JsonNode.Parse(FixtureHandler.ValidAnswers)!.AsArray();
            if (missing.Distribution)
            {
                var probabilities = changed[missing.Answer]!["probabilities"]!.AsArray();
                probabilities[0]!["probability"] = 0;
                probabilities[1]!["probability"] = 1;
                if (missing.Answer == 2) changed[2]!["score"] = 1;
                probabilities[0]!.AsObject().Remove(missing.Field);
            }
            else changed[missing.Answer]!.AsObject().Remove(missing.Field);
            handler.Answers = changed.ToJsonString();
            await Check.ThrowsAsync<DecisionProtocolException>(() => client.GetResponseAsync(request));
        }
        foreach (var name in new[] { "", "wrong-question" })
        {
            var changed = System.Text.Json.Nodes.JsonNode.Parse(FixtureHandler.ValidAnswers)!.AsArray();
            changed[0] = System.Text.Json.Nodes.JsonNode.Parse(name.Length == 0
                ? """{"type":"refusal"}""" : """{"type":"refusal","name":"wrong-question"}""");
            handler.Answers = changed.ToJsonString();
            await Check.ThrowsAsync<DecisionProtocolException>(() => client.GetResponseAsync(request));
        }
        handler.Status = HttpStatusCode.InternalServerError;
        await Check.ThrowsAsync<DecisionClientException>(() => client.GetResponseAsync(request));
        Check.That(handler.Calls == 16, "Each SDK fixture request was intercepted; no retry or network inference occurred.");
    }

    private sealed class FixtureHandler : HttpMessageHandler
    {
        public const string ValidAnswers = """
            [{"type":"predicate","name":"predicate-id","probability":0.83},
             {"type":"choice","name":"choice-id","choice":"opaque-beta","confidence":0.91,
              "probabilities":[{"value":"opaque-alpha","probability":0.3},{"value":"opaque-beta","probability":0.7}]},
             {"type":"score","name":"score-id","score":0.75,"confidence":0.9,
              "probabilities":[{"value":0,"label":"low-id","probability":0.25},{"value":1,"label":"high-id","probability":0.75}]}]
            """;
        public string Answers { get; set; } = ValidAnswers;
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string? LastRequest { get; private set; }
        public int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Check.That(request.RequestUri == new Uri("https://offline.invalid/v1/decisions")
                && request.Method == HttpMethod.Post, "Actual SDK uses the decisions HTTP endpoint.");
            Check.That(request.Headers.Authorization?.Parameter == "synthetic-offline-key",
                "Only a synthetic credential enters the intercepted transport.");
            LastRequest = await request.Content!.ReadAsStringAsync(ct);
            return new(Status)
            {
                Content = new StringContent($$$"""
                    {"model":"synthetic-decisions-fixture-returned","answers":{{{Answers}}},
                     "usage":{"input_tokens":17,"output_tokens":0,"total_tokens":17}}
                    """, Encoding.UTF8, "application/json")
            };
        }
    }
}
#endif
