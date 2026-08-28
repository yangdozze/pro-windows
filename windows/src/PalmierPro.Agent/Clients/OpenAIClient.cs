using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;

namespace PalmierPro.Agent.Clients;

/// <summary>Streaming OpenAI Responses API client with Palmier Agent tool-call support.</summary>
public sealed class OpenAIClient : IAgentClient
{
    private static readonly Uri Endpoint = new("https://api.openai.com/v1/responses");
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly int _maxOutputTokens;

    public OpenAIClient(string apiKey, string model, int maxOutputTokens = 64_000, HttpClient? http = null)
    {
        _apiKey = apiKey;
        _model = string.IsNullOrWhiteSpace(model) ? AgentProvider.OpenAI.DefaultModel() : model.Trim();
        _maxOutputTokens = maxOutputTokens;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    }

    public async IAsyncEnumerable<AnthropicStreamEvent> StreamAsync(
        string system,
        IReadOnlyList<AnthropicToolSchema> tools,
        IReadOnlyList<object> messages,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
            throw new AnthropicClientException("No OpenAI API key is set.");

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Content = new StringContent(BuildRequest(system, tools, messages), Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        if ((int)response.StatusCode >= 400)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new AnthropicClientException(
                $"OpenAI API error ({(int)response.StatusCode}): {(body.Length > 500 ? body[..500] : body)}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        var sawTerminal = false;

        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null || !line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var payload = line["data:".Length..].Trim();
            if (payload.Length == 0 || payload == "[DONE]") continue;

            JsonNode? root;
            try { root = JsonNode.Parse(payload); }
            catch { continue; }
            if (root is null) continue;

            var type = root["type"]?.GetValue<string>();
            switch (type)
            {
                case "response.output_text.delta":
                case "response.refusal.delta":
                {
                    var delta = root["delta"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(delta))
                        yield return new AnthropicStreamEvent.TextDelta(delta);
                    break;
                }
                case "response.output_item.done":
                {
                    var item = root["item"];
                    if (item?["type"]?.GetValue<string>() != "function_call") break;
                    var id = item["call_id"]?.GetValue<string>() ?? "";
                    var name = item["name"]?.GetValue<string>() ?? "";
                    var args = item["arguments"]?.GetValue<string>() ?? "{}";
                    if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
                        yield return new AnthropicStreamEvent.ToolUseComplete(id, name, args);
                    break;
                }
                case "response.completed":
                case "response.incomplete":
                {
                    sawTerminal = true;
                    var responseNode = root["response"];
                    var reason = HasFunctionCall(responseNode)
                        ? AnthropicStopReason.ToolUse
                        : type == "response.incomplete"
                            ? AnthropicStopReason.MaxTokens
                            : AnthropicStopReason.EndTurn;
                    yield return new AnthropicStreamEvent.MessageStop(reason);
                    break;
                }
                case "response.cancelled":
                    throw new OperationCanceledException(ct);
                case "response.failed":
                case "error":
                {
                    sawTerminal = true;
                    var error = root["error"] ?? root["response"]?["error"];
                    var message = error?["message"]?.GetValue<string>() ?? "OpenAI stream failed.";
                    throw new AnthropicClientException(message);
                }
            }
        }

        if (!sawTerminal)
            throw new AnthropicClientException("The OpenAI stream ended before a terminal event.");
    }

    private string BuildRequest(
        string system,
        IReadOnlyList<AnthropicToolSchema> tools,
        IReadOnlyList<object> messages)
    {
        var root = new JsonObject
        {
            ["model"] = _model,
            ["store"] = false,
            ["stream"] = true,
            ["max_output_tokens"] = _maxOutputTokens,
            ["instructions"] = system,
            ["reasoning"] = new JsonObject { ["summary"] = "auto", ["effort"] = "medium" },
            ["input"] = ConvertMessages(messages),
        };

        if (tools.Count > 0)
        {
            var toolArray = new JsonArray();
            foreach (var tool in tools)
            {
                toolArray.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = JsonNode.Parse(tool.InputSchema.GetRawText()),
                    ["strict"] = false,
                });
            }
            root["tools"] = toolArray;
        }

        return root.ToJsonString();
    }

    private static JsonArray ConvertMessages(IReadOnlyList<object> messages)
    {
        var input = new JsonArray();
        foreach (var raw in messages)
        {
            var message = raw as JsonObject ?? JsonNode.Parse(raw.ToString() ?? "{}") as JsonObject;
            if (message is null) continue;
            var role = message["role"]?.GetValue<string>() ?? "user";
            var content = message["content"];

            if (content is JsonValue value && value.TryGetValue<string>(out var text))
            {
                AddMessage(input, role, text);
                continue;
            }

            if (content is not JsonArray blocks) continue;
            var textBuffer = new StringBuilder();
            foreach (var block in blocks)
            {
                var blockType = block?["type"]?.GetValue<string>();
                if (blockType == "text")
                {
                    textBuffer.Append(block?["text"]?.GetValue<string>() ?? "");
                    continue;
                }

                FlushText(input, role, textBuffer);
                if (blockType == "tool_use")
                {
                    input.Add(new JsonObject
                    {
                        ["type"] = "function_call",
                        ["call_id"] = block?["id"]?.GetValue<string>() ?? "",
                        ["name"] = block?["name"]?.GetValue<string>() ?? "",
                        ["arguments"] = (block?["input"] ?? new JsonObject()).ToJsonString(),
                    });
                }
                else if (blockType == "tool_result")
                {
                    input.Add(new JsonObject
                    {
                        ["type"] = "function_call_output",
                        ["call_id"] = block?["tool_use_id"]?.GetValue<string>() ?? "",
                        ["output"] = ConvertToolOutput(block?["content"]),
                    });
                }
            }
            FlushText(input, role, textBuffer);
        }
        return input;
    }

    private static void FlushText(JsonArray input, string role, StringBuilder buffer)
    {
        if (buffer.Length == 0) return;
        AddMessage(input, role, buffer.ToString());
        buffer.Clear();
    }

    private static void AddMessage(JsonArray input, string role, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        input.Add(new JsonObject
        {
            ["type"] = "message",
            ["role"] = role,
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = role == "assistant" ? "output_text" : "input_text",
                    ["text"] = text,
                },
            },
        });
    }

    private static JsonNode ConvertToolOutput(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
            return JsonValue.Create(text)!;
        if (node is not JsonArray blocks)
            return JsonValue.Create(node?.ToJsonString() ?? "")!;

        var output = new JsonArray();
        foreach (var block in blocks)
        {
            var type = block?["type"]?.GetValue<string>();
            if (type == "text")
            {
                output.Add(new JsonObject
                {
                    ["type"] = "input_text",
                    ["text"] = block?["text"]?.GetValue<string>() ?? "",
                });
            }
            else if (type == "image")
            {
                var source = block?["source"];
                var mediaType = source?["media_type"]?.GetValue<string>() ?? "image/png";
                var data = source?["data"]?.GetValue<string>() ?? "";
                output.Add(new JsonObject
                {
                    ["type"] = "input_image",
                    ["image_url"] = $"data:{mediaType};base64,{data}",
                });
            }
        }
        return output;
    }

    private static bool HasFunctionCall(JsonNode? response)
    {
        if (response?["output"] is not JsonArray output) return false;
        return output.Any(item => item?["type"]?.GetValue<string>() == "function_call");
    }
}
