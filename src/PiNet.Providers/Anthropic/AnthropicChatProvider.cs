using System.Text;
using System.Text.Json;
using PiNet.Abstractions.Auth;
using PiNet.Abstractions.Chat;
using PiNet.Abstractions.Models;
using PiNet.Abstractions.Providers;
using PiNet.Abstractions.Streaming;
using PiNet.Providers.Infrastructure;

namespace PiNet.Providers.Anthropic;

public sealed class AnthropicChatProvider : IChatProvider
{
    private const string ApiVersion = "2023-06-01";
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _providerId;
    private readonly string _name;

    public string Id => _providerId;
    public string Name => _name;
    public IReadOnlyList<ModelDefinition> Models { get; }
    public ICredentialResolver Credentials { get; }

    public AnthropicChatProvider(
        ICredentialStore? store = null,
        string baseUrl = "https://api.anthropic.com",
        string? apiKey = null,
        IReadOnlyList<ModelDefinition>? models = null,
        string providerId = "anthropic",
        string name = "Anthropic")
    {
        _providerId = providerId;
        _name = name;
        _baseUrl = baseUrl.TrimEnd('/');
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.Accept.Add(new("application/json"));
        _http.DefaultRequestHeaders.Accept.Add(new("text/event-stream"));
        Credentials = apiKey is null
            ? new EnvCredentialResolver("Anthropic API key", ["ANTHROPIC_API_KEY"], store ?? new InMemoryCredentialStore(), _baseUrl)
            : new PiNet.Providers.Auth.ConfigCredentialResolver(_baseUrl, apiKey, ["ANTHROPIC_API_KEY"]);
        Models = models ?? AnthropicCatalog.Models;
    }

    public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, AuthInfo auth)
    {
        if (string.IsNullOrWhiteSpace(auth.ApiKey))
        {
            yield return new StreamError(
                "No Anthropic API key found. Set ANTHROPIC_API_KEY or configure your credentials.");
            yield break;
        }

        var blocks = new List<AnthropicBlockAccumulator>();
        Usage usage = new(0, 0, 0, 0, new Cost(0, 0, 0, 0));

        yield return new StreamStart(Id, request.ModelId, null);

        var json = JsonSerializer.Serialize(BuildPayload(request), JsonOpts);
        using var payload = new StringContent(json, Encoding.UTF8, "application/json");
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v1/messages")
        {
            Content = payload
        };
        httpRequest.Headers.TryAddWithoutValidation("x-api-key", auth.ApiKey);
        httpRequest.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);

        HttpResponseMessage? response = null;
        try
        {
            response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, request.CancellationToken);
        }
        catch (OperationCanceledException) when (request.CancellationToken.IsCancellationRequested)
        {
        }

        if (response is null)
        {
            yield return new StreamError("Aborted by user.", IsAborted: true);
            yield break;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(request.CancellationToken);
                yield return new StreamError($"Anthropic error {(int)response.StatusCode}: {body}");
                yield break;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(request.CancellationToken);
            using var reader = new StreamReader(stream);
            var sawDone = false;

            await foreach (var sse in SseParser.ParseAsync(reader, request.CancellationToken))
            {
                if (request.CancellationToken.IsCancellationRequested)
                {
                    yield return new StreamError("Aborted by user.", IsAborted: true);
                    yield break;
                }

                JsonElement chunk;
                try { chunk = JsonDocument.Parse(sse.Data).RootElement; }
                catch (JsonException) { continue; }

                var type = chunk.TryGetProperty("type", out var t) ? t.GetString() : sse.Event;
                switch (type)
                {
                    case "message_start":
                    {
                        if (chunk.TryGetProperty("message", out var message))
                            usage = ReadInitialUsage(message);
                        break;
                    }
                    case "content_block_start":
                    {
                        var index = chunk.TryGetProperty("index", out var i) ? i.GetInt32() : blocks.Count;
                        if (chunk.TryGetProperty("content_block", out var cb))
                        {
                            var blockType = cb.TryGetProperty("type", out var bt) ? bt.GetString() : null;
                            var acc = new AnthropicBlockAccumulator { Index = index, Kind = blockType };
                            if (blockType == "tool_use")
                            {
                                acc.Id = cb.TryGetProperty("id", out var id) ? id.GetString() : null;
                                acc.Name = cb.TryGetProperty("name", out var name) ? name.GetString() : null;
                                acc.Json = new StringBuilder();
                            }
                            else if (blockType == "text")
                            {
                                acc.Text = new StringBuilder();
                            }
                            else if (blockType == "thinking")
                            {
                                acc.Thinking = new StringBuilder();
                                acc.Kind = "thinking";
                            }

                            while (blocks.Count <= index)
                                blocks.Add(new AnthropicBlockAccumulator { Index = blocks.Count });
                            blocks[index] = acc;
                        }
                        break;
                    }
                    case "content_block_delta":
                    {
                        var index = chunk.TryGetProperty("index", out var i) ? i.GetInt32() : 0;
                        if (chunk.TryGetProperty("delta", out var delta))
                        {
                            var deltaType = delta.TryGetProperty("type", out var dt) ? dt.GetString() : null;
                            if (blocks.Count <= index)
                            {
                                blocks.Add(new AnthropicBlockAccumulator { Index = index });
                            }
                            var acc = blocks[index];

                            switch (deltaType)
                            {
                                case "text_delta":
                                {
                                    var text = delta.TryGetProperty("text", out var t2) ? t2.GetString() : null;
                                    if (!string.IsNullOrEmpty(text))
                                    {
                                        (acc.Text ??= new StringBuilder()).Append(text);
                                        yield return new TextDelta(text);
                                    }
                                    break;
                                }
                                case "thinking_delta":
                                {
                                    var thinking = delta.TryGetProperty("thinking", out var t3) ? t3.GetString() : null;
                                    if (!string.IsNullOrEmpty(thinking))
                                    {
                                        (acc.Thinking ??= new StringBuilder()).Append(thinking);
                                        yield return new ThinkingDelta(thinking);
                                    }
                                    break;
                                }
                                case "input_json_delta":
                                {
                                    var partial = delta.TryGetProperty("partial_json", out var p) ? p.GetString() : null;
                                    if (!string.IsNullOrEmpty(partial))
                                    {
                                        (acc.Json ??= new StringBuilder()).Append(partial);
                                        yield return new ToolCallDelta(index, partial);
                                    }
                                    break;
                                }
                            }
                        }
                        break;
                    }
                    case "message_delta":
                    {
                        if (chunk.TryGetProperty("usage", out var mu))
                        {
                            var output = mu.TryGetProperty("output_tokens", out var o) ? o.GetInt32() : usage.OutputTokens;
                            usage = usage with { OutputTokens = output };
                        }
                        break;
                    }
                    case "message_stop":
                        sawDone = true;
                        yield return new StreamEnd("stop", usage, BuildAssistantMessage(blocks));
                        break;
                    case "error":
                    {
                        var message = chunk.TryGetProperty("error", out var e) &&
                                      e.TryGetProperty("message", out var em)
                            ? em.GetString()
                            : chunk.GetRawText();
                        yield return new StreamError(message ?? "Anthropic error");
                        yield break;
                    }
                }
            }

            if (!sawDone)
                yield return new StreamEnd("stop", usage, BuildAssistantMessage(blocks));
        }
    }

    private static ChatMessage BuildAssistantMessage(List<AnthropicBlockAccumulator> blocks)
    {
        var content = new List<ContentBlock>();
        foreach (var acc in blocks.OrderBy(b => b.Index))
        {
            switch (acc.Kind)
            {
                case "text":
                    content.Add(new TextContent(acc.Text?.ToString() ?? string.Empty));
                    break;
                case "thinking":
                    content.Add(new ThinkingContent(acc.Thinking?.ToString() ?? string.Empty));
                    break;
                case "tool_use":
                {
                    var argsJson = acc.Json?.ToString() ?? "{}";
                    JsonElement args;
                    try { args = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson).RootElement; }
                    catch (JsonException) { args = JsonDocument.Parse("{}").RootElement; }
                    content.Add(new ToolCallContent(acc.Id ?? Guid.NewGuid().ToString(), acc.Name ?? "tool", args));
                    break;
                }
            }
        }

        return new ChatMessage(ChatRole.Assistant, content, Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    private static Usage ReadInitialUsage(JsonElement message)
    {
        var input = message.TryGetProperty("usage", out var u) && u.TryGetProperty("input_tokens", out var i) ? i.GetInt32() : 0;
        var output = 0;
        var cacheRead = 0;
        var cacheWrite = 0;
        if (message.TryGetProperty("usage", out var u2))
        {
            output = u2.TryGetProperty("output_tokens", out var o) ? o.GetInt32() : 0;
            cacheRead = u2.TryGetProperty("cache_read_input_tokens", out var cr) ? cr.GetInt32() : 0;
            cacheWrite = u2.TryGetProperty("cache_creation_input_tokens", out var cw) ? cw.GetInt32() : 0;
        }

        return new Usage(input, output, cacheRead, cacheWrite, new Cost(0, 0, 0, 0));
    }

    private static object BuildPayload(ChatRequest request) => new
    {
        model = request.ModelId,
        system = request.SystemPrompt,
        max_tokens = request.MaxTokens ?? 4096,
        stream = true,
        temperature = request.Temperature ?? 1,
        messages = BuildMessages(request),
        tools = BuildTools(request.Tools)
    };

    private static List<object> BuildMessages(ChatRequest request)
    {
        var messages = new List<object>();
        var toolResultBuffer = new List<object>();

        void FlushToolResults()
        {
            if (toolResultBuffer.Count > 0)
            {
                messages.Add(new { role = "user", content = toolResultBuffer.ToArray() });
                toolResultBuffer.Clear();
            }
        }

        foreach (var msg in request.Messages)
        {
            switch (msg.Role)
            {
                case ChatRole.User:
                    FlushToolResults();
                    messages.Add(new { role = "user", content = BuildUserContent(msg.Content) });
                    break;
                case ChatRole.Assistant:
                    FlushToolResults();
                    messages.Add(new { role = "assistant", content = BuildAssistantContent(msg.Content) });
                    break;
                case ChatRole.Tool:
                    toolResultBuffer.Add(new
                    {
                        type = "tool_result",
                        tool_use_id = msg.ToolCallId,
                        content = msg.TryGetText() ?? string.Empty,
                        is_error = msg.IsError
                    });
                    break;
            }
        }

        FlushToolResults();
        return messages;
    }

    private static object BuildUserContent(IReadOnlyList<ContentBlock> content)
    {
        var parts = new List<object>();
        foreach (var block in content)
        {
            switch (block)
            {
                case TextContent t:
                    parts.Add(new { type = "text", text = t.Text });
                    break;
                case ImageContent img:
                    parts.Add(new { type = "image", source = new { type = "base64", media_type = img.MimeType, data = img.Data } });
                    break;
            }
        }

        return parts.Count == 0 ? new[] { new { type = "text", text = string.Empty } } : parts;
    }

    private static object BuildAssistantContent(IReadOnlyList<ContentBlock> content)
    {
        var parts = new List<object>();
        foreach (var block in content)
        {
            switch (block)
            {
                case TextContent t:
                    parts.Add(new { type = "text", text = t.Text });
                    break;
                case ToolCallContent tc:
                    parts.Add(new
                    {
                        type = "tool_use",
                        id = tc.Id,
                        name = tc.Name,
                        input = tc.Arguments
                    });
                    break;
            }
        }

        return parts;
    }

    private static object? BuildTools(IReadOnlyList<ToolDefinition>? tools)
    {
        if (tools is null || tools.Count == 0)
            return null;

        return tools.Select(t => new
        {
            name = t.Name,
            description = t.Description,
            input_schema = BuildSchema(t.Parameters)
        }).ToArray();
    }

    private static object BuildSchema(ToolParameterSchema? schema) =>
        schema is null
            ? new { type = "object", properties = new Dictionary<string, object>() }
            : new
            {
                type = schema.Type,
                properties = schema.Properties?.ToDictionary(
                    kv => kv.Key,
                    kv => BuildSchema(kv.Value)),
                required = schema.Required
            };

    private sealed class AnthropicBlockAccumulator
    {
        public int Index { get; init; }
        public string? Kind { get; set; }
        public string? Id { get; set; }
        public string? Name { get; set; }
        public StringBuilder? Text { get; set; }
        public StringBuilder? Thinking { get; set; }
        public StringBuilder? Json { get; set; }
    }
}