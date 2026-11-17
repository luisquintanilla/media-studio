using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using Microsoft.Extensions.AI;
using OpenAI;
using Xunit;

namespace MediaStudio.Tests;

// Gate for the pinned OpenAI + Microsoft.Extensions.AI.OpenAI pair: streaming must work through the
// published packages without a local compatibility patch (see docs/troubleshooting.md).
[Trait("Category", "OpenAICompat")]
public sealed class OpenAICompatibilityTests
{
    private const string ResponsesSse =
        "event: response.created\n" +
        "data: {\"type\":\"response.created\",\"sequence_number\":0,\"response\":{\"id\":\"resp_1\",\"object\":\"response\",\"created_at\":1,\"status\":\"in_progress\",\"model\":\"gpt-test\",\"output\":[]}}\n\n" +
        "event: response.output_item.added\n" +
        "data: {\"type\":\"response.output_item.added\",\"sequence_number\":1,\"output_index\":0,\"item\":{\"type\":\"message\",\"id\":\"msg_1\",\"role\":\"assistant\",\"status\":\"in_progress\",\"content\":[]}}\n\n" +
        "event: response.output_text.delta\n" +
        "data: {\"type\":\"response.output_text.delta\",\"sequence_number\":2,\"output_index\":0,\"content_index\":0,\"item_id\":\"msg_1\",\"delta\":\"Hello \"}\n\n" +
        "event: response.output_text.delta\n" +
        "data: {\"type\":\"response.output_text.delta\",\"sequence_number\":3,\"output_index\":0,\"content_index\":0,\"item_id\":\"msg_1\",\"delta\":\"responses\"}\n\n" +
        "event: response.output_item.done\n" +
        "data: {\"type\":\"response.output_item.done\",\"sequence_number\":4,\"output_index\":0,\"item\":{\"type\":\"message\",\"id\":\"msg_1\",\"role\":\"assistant\",\"status\":\"completed\",\"content\":[{\"type\":\"output_text\",\"text\":\"Hello responses\",\"annotations\":[]}]}}\n\n" +
        "event: response.completed\n" +
        "data: {\"type\":\"response.completed\",\"sequence_number\":5,\"response\":{\"id\":\"resp_1\",\"object\":\"response\",\"created_at\":1,\"status\":\"completed\",\"model\":\"gpt-test\",\"output\":[{\"type\":\"message\",\"id\":\"msg_1\",\"role\":\"assistant\",\"status\":\"completed\",\"content\":[{\"type\":\"output_text\",\"text\":\"Hello responses\",\"annotations\":[]}]}],\"usage\":{\"input_tokens\":3,\"output_tokens\":2,\"total_tokens\":5}}}\n\n";

    private const string ChatSse =
        "data: {\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"gpt-test\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"Hello \"},\"finish_reason\":null}]}\n\n" +
        "data: {\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"gpt-test\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"chat\"},\"finish_reason\":null}]}\n\n" +
        "data: {\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"gpt-test\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" +
        "data: [DONE]\n\n";

    [Fact]
    public async Task ResponsesStreamingWorksThroughPublishedPackages()
    {
        var handler = new RecordedSseHandler(ResponsesSse);
#pragma warning disable OPENAI001
        using var client = CreateClient(handler).GetResponsesClient().AsIChatClient("gpt-test");
#pragma warning restore OPENAI001

        var text = await Collect(client);

        Assert.Equal("Hello responses", text);
        Assert.Equal(1, handler.Calls);
        Assert.EndsWith("/responses", handler.LastPath);
    }

    [Fact]
    public async Task ChatCompletionsStreamingWorksThroughPublishedPackages()
    {
        var handler = new RecordedSseHandler(ChatSse);
        using var client = CreateClient(handler).GetChatClient("gpt-test").AsIChatClient();

        var text = await Collect(client);

        Assert.Equal("Hello chat", text);
        Assert.Equal(1, handler.Calls);
        Assert.EndsWith("/chat/completions", handler.LastPath);
    }

    private static OpenAIClient CreateClient(HttpMessageHandler handler) =>
        new(new ApiKeyCredential("synthetic-not-a-credential"), new OpenAIClientOptions {
            Endpoint = new Uri("http://127.0.0.1:9/v1"),
            Transport = new HttpClientPipelineTransport(new HttpClient(handler)),
            RetryPolicy = new ClientRetryPolicy(0),
        });

    private static async Task<string> Collect(IChatClient client)
    {
        var text = new StringBuilder();
        await foreach (var update in client.GetStreamingResponseAsync("Say hello."))
            text.Append(update.Text);
        return text.ToString();
    }

    private sealed class RecordedSseHandler(string sse) : HttpMessageHandler
    {
        public int Calls;
        public string LastPath = "";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastPath = request.RequestUri!.AbsolutePath;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
            });
        }
    }
}
