using System.Net;
using System.Text;
using Microsoft.Extensions.AI;
using VideoProduction.Providers.Bench;

namespace MediaStudio.Tests.Illustrations;

public sealed class ChatBenchTests
{
    [Fact]
    public void RoutingCasesMatchTheFixtureFile()
    {
        var fixtures = Routing.RoutingAccuracy.Load().Cases;
        Assert.Equal(fixtures.Select(f => (f.Id, f.Purpose, f.Prompt, f.Expected, f.Json, f.Tools, f.MaxOutputTokens, f.Padding)),
            ChatBenchCommands.RoutingCases.Select(c => (c.Id, c.Purpose, c.Prompt, c.Expected, c.Json, c.Tools, c.MaxOutputTokens, c.Padding)));
        Assert.Equal(10, ChatBenchCommands.AuthoringPrompts.Count);
    }

    [Theory]
    [InlineData("""{"title":"Tides","scenes":[{"title":"Moon","narration":"The moon pulls.","visual":"Earth and moon"}]}""", true)]
    [InlineData("```json\n{\"title\":\"T\",\"scenes\":[{\"title\":\"a\",\"narration\":\"b\",\"visual\":\"c\"}]}\n```", true)]
    [InlineData("""{"title":"Tides","scenes":[]}""", false)]
    [InlineData("""{"title":"Tides","scenes":[{"title":"Moon","narration":"","visual":"x"}]}""", false)]
    [InlineData("not json", false)]
    public void StoryboardValidity(string json, bool valid) => Assert.Equal(valid, ChatBenchCommands.ValidStoryboard(json));

    [Fact]
    public async Task MeasuresRoutingAgreementJsonValidityAndJudge()
    {
        using var model = new ScriptedChat(messages =>
        {
            var system = messages[0].Text;
            if (system.StartsWith("You route", StringComparison.Ordinal))
                return messages[1].Text.Contains("Hi!", StringComparison.Ordinal) ? """{"tier":"fast"}""" : """{"tier":"strong"}""";
            return messages[1].Text.Contains("tides", StringComparison.Ordinal)
                ? """{"title":"Tides","scenes":[{"title":"Moon","narration":"The moon pulls.","visual":"Earth and moon"}]}"""
                : "{}";
        });
        using var judge = new ScriptedChat(_ => """{"score":8,"issues":[]}""");
        var result = await ChatBenchCommands.MeasureAsync("fake", model, judge, routing: true, prompts: 2);

        // "strong" everywhere except the greeting: every strong case plus the greeting agree, the other five fast cases do not.
        Assert.Equal(ChatBenchCommands.RoutingCases.Count - 5, result.RoutingAgreed);
        Assert.Equal(2, result.AuthoringPrompts);
        Assert.Equal(1, result.JsonValid);
        Assert.Equal(0.8, result.JudgeScore!.Value, 3);
        Assert.Equal(1, judge.Calls);
        Assert.NotNull(result.TokensPerSecond);
    }

    [Fact]
    public async Task ListsOllamaChatModelsWithoutEmbeddingModels()
    {
        using var http = new HttpClient(new JsonHandler("""
            {"models":[{"name":"qwen3.5:4b","details":{"families":["qwen35"]}},{"name":"embeddinggemma:latest","details":{"families":["gemma3"]}},
            {"name":"all-minilm:l6","details":{"families":["bert"]}},{"name":"gpt-oss:20b"}]}
            """));
        var models = await ChatBenchCommands.OllamaChatModelsAsync(http, new Uri("http://localhost:11434/"));
        Assert.Equal(["gpt-oss:20b", "qwen3.5:4b"], models);
    }

    [Fact]
    public async Task UnknownOptionPrintsUsage()
    {
        using var writer = new StringWriter();
        Assert.True(ChatBenchCommands.Handles(["bench", "local"]));
        Assert.False(ChatBenchCommands.Handles(["bench", "tts"]));
        Assert.Equal(2, await ChatBenchCommands.RunAsync(["bench", "local", "--bogus"], writer));
        Assert.Contains("Usage:", writer.ToString(), StringComparison.Ordinal);
    }

    private sealed class ScriptedChat(Func<IList<ChatMessage>, string> reply) : IChatClient
    {
        public int Calls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply(messages.ToList()))) {
                Usage = new UsageDetails { OutputTokenCount = 40 }
            });
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "B-roll is supporting footage.");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class JsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("/api/tags", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
}
