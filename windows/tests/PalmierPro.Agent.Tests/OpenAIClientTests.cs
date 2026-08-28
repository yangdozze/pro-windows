using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using PalmierPro.Agent.Clients;
using Xunit;

namespace PalmierPro.Agent.Tests;

public sealed class OpenAIClientTests
{
    [Fact]
    public async Task StreamsTextAndToolCallsThroughResponsesApi()
    {
        const string sse = """
            data: {"type":"response.output_text.delta","delta":"hello"}

            data: {"type":"response.output_item.done","item":{"type":"function_call","call_id":"call-1","name":"get_timeline","arguments":"{}"}}

            data: {"type":"response.completed","response":{"status":"completed","output":[{"type":"function_call"}]}}

            """;
        var handler = new RecordingHandler(sse);
        var client = new OpenAIClient("sk-test", "gpt-test", http: new HttpClient(handler));
        var messages = new object[]
        {
            new JsonObject { ["role"] = "user", ["content"] = "show the timeline" },
        };

        var events = new List<AnthropicStreamEvent>();
        await foreach (var item in client.StreamAsync("system", [], messages, CancellationToken.None))
            events.Add(item);

        Assert.Collection(events,
            item => Assert.Equal("hello", Assert.IsType<AnthropicStreamEvent.TextDelta>(item).Text),
            item =>
            {
                var tool = Assert.IsType<AnthropicStreamEvent.ToolUseComplete>(item);
                Assert.Equal("call-1", tool.Id);
                Assert.Equal("get_timeline", tool.Name);
                Assert.Equal("{}", tool.InputJson);
            },
            item => Assert.Equal(
                AnthropicStopReason.ToolUse,
                Assert.IsType<AnthropicStreamEvent.MessageStop>(item).StopReason));

        Assert.Equal("https://api.openai.com/v1/responses", handler.RequestUri);
        Assert.Equal("Bearer sk-test", handler.Authorization);
        var body = JsonNode.Parse(handler.Body)!;
        Assert.Equal("gpt-test", body["model"]?.GetValue<string>());
        Assert.True(body["stream"]?.GetValue<bool>());
        Assert.Equal("show the timeline", body["input"]?[0]?["content"]?[0]?["text"]?.GetValue<string>());
    }

    private sealed class RecordingHandler(string responseBody) : HttpMessageHandler
    {
        public string? RequestUri { get; private set; }
        public string? Authorization { get; private set; }
        public string Body { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri?.AbsoluteUri;
            Authorization = request.Headers.Authorization?.ToString();
            Body = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "text/event-stream"),
            };
        }
    }
}
