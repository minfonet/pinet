using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PiNet.Abstractions.Auth;
using PiNet.Abstractions.Chat;
using PiNet.Abstractions.Models;
using PiNet.Abstractions.Providers;
using PiNet.Abstractions.Streaming;
using PiNet.Providers.Infrastructure;

namespace PiNet.Providers.OpenAI;

public sealed class OpenAIChatProvider : IChatProvider
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _providerId;
    private readonly string _name;

    public string Id => _providerId;
    public string Name => _name;
    public IReadOnlyList<ModelDefinition> Models { get; }
    public ICredentialResolver Credentials { get; }

    public OpenAIChatProvider(
        ICredentialStore? store = null,
        string baseUrl = "https://api.openai.com/v1",
        string? apiKey = null,
        IReadOnlyList<ModelDefinition>? models = null,
        string providerId = "openai",
        string name = "OpenAI")
    {
        _providerId = providerId;
        _name = name;
        _baseUrl = baseUrl.TrimEnd('/');
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.Accept.Add(new("application/json"));
        _http.DefaultRequestHeaders.Accept.Add(new("text/event-stream"));
        Credentials = apiKey is null
            ? new EnvCredentialResolver("OpenAI API key", ["OPENAI_API_KEY", "OPENAI_KEY"], store ?? new InMemoryCredentialStore(), _baseUrl)
            : new PiNet.Providers.Auth.ConfigCredentialResolver(_baseUrl, apiKey, ["OPENAI_API_KEY", "OPENAI_KEY"]);
        Models = models ?? OpenAICatalog.Models;
    }

    /// <summary>Test-only constructor allowing an injected HTTP transport.</summary>
    internal OpenAIChatProvider(HttpMessageHandler handler, string baseUrl = "http://localhost/v1", string providerId = "test")
        : this(store: null, baseUrl: baseUrl, apiKey: "test-key", models: null, providerId: providerId, name: "Test")
    {
        _http.Dispose();
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, AuthInfo auth)
    {
        if (string.IsNullOrWhiteSpace(auth.ApiKey))
        {
            yield return new StreamError(
                "No OpenAI API key found. Set OPENAI_API_KEY or configure your credentials.");
            yield break;
        }

        var toolBuffer = new Dictionary<int, OpenAIToolAccumulator>();
        Usage usage = new(0, 0, 0, 0, new Cost(0, 0, 0, 0));

        yield return new StreamStart(Id, request.ModelId, null);

        var json = BuildPayloadJson(request);
        using var payload = new StringContent(json, Encoding.UTF8, "application/json");
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/chat/completions")
        {
            Content = payload
        };
        httpRequest.Headers.TryAddWithoutValidation("Authorization", $"Bearer {auth.ApiKey}");

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
                yield return new StreamError($"OpenAI error {(int)response.StatusCode}: {body}");
                yield break;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(request.CancellationToken);
            using var reader = new StreamReader(stream);
            var sawFinish = false;

            await foreach (var sse in SseParser.ParseAsync(reader, request.CancellationToken))
            {
                if (request.CancellationToken.IsCancellationRequested)
                {
                    yield return new StreamError("Aborted by user.", IsAborted: true);
                    yield break;
                }

                if (sse.Data == "[DONE]")
                    break;

                JsonElement chunk;
                try { chunk = JsonDocument.Parse(sse.Data).RootElement; }
                catch (JsonException) { continue; }

                if (chunk.TryGetProperty("error", out var err))
                {
                    yield return new StreamError(err.TryGetProperty("message", out var m) ? m.GetString()! : err.GetRawText());
                    yield break;
                }

                if (chunk.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                {
                    var choice = choices[0];

                    if (choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String)
                    {
                        sawFinish = true;
                        var reason = finish.GetString() ?? "stop";
                        // SR-01.05: close every buffered call before the unified StreamEnd.
                        foreach (var (endIndex, detail) in FinishToolCalls(toolBuffer))
                            yield return new ToolCallEnd(endIndex, detail);
                        yield return new StreamEnd(reason, usage, BuildAssistantMessage(toolBuffer, reason));
                    }

                    if (choice.TryGetProperty("delta", out var delta))
                    {
                        if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                        {
                            var text = content.GetString();
                            if (!string.IsNullOrEmpty(text))
                                yield return new TextDelta(text);
                        }

                        if (delta.TryGetProperty("reasoning_content", out var r) && r.ValueKind == JsonValueKind.String)
                        {
                            var thinking = r.GetString();
                            if (!string.IsNullOrEmpty(thinking))
                                yield return new ThinkingDelta(thinking);
                        }

                        if (delta.TryGetProperty("tool_calls", out var toolCalls))
                        {
                            foreach (var tc in toolCalls.EnumerateArray())
                            {
                                var index = tc.TryGetProperty("index", out var idx) ? idx.GetInt32() : 0;
                                if (!toolBuffer.TryGetValue(index, out var acc))
                                {
                                    acc = new OpenAIToolAccumulator { Index = index };
                                    toolBuffer[index] = acc;
                                }

                                if (tc.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String)
                                    acc.Id ??= idProp.GetString();
                                if (tc.TryGetProperty("function", out var fn) &&
                                    fn.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String)
                                    acc.Name ??= nameProp.GetString();

                                // SR-01.05: announce the call as soon as its identity is known,
                                // then stream argument fragments until the stream ends.
                                if (!acc.Started && acc.Id is not null && acc.Name is not null)
                                {
                                    acc.Started = true;
                                    yield return new ToolCallStart(index, acc.Id, acc.Name);
                                }

                                if (tc.TryGetProperty("function", out var fnArgs) &&
                                    fnArgs.TryGetProperty("arguments", out var argsProp) && argsProp.ValueKind == JsonValueKind.String)
                                {
                                    var fragment = argsProp.GetString();
                                    if (!string.IsNullOrEmpty(fragment))
                                    {
                                        acc.Arguments.Append(fragment);
                                        yield return new ToolCallDelta(index, fragment);
                                    }
                                }
                            }
                        }
                    }
                }

                if (chunk.TryGetProperty("usage", out var usageProp) && usageProp.ValueKind == JsonValueKind.Object)
                    usage = ReadUsage(usageProp);
            }

            if (!sawFinish)
            {
                foreach (var (endIndex, detail) in FinishToolCalls(toolBuffer))
                    yield return new ToolCallEnd(endIndex, detail);
                yield return new StreamEnd("stop", usage, BuildAssistantMessage(toolBuffer, "stop"));
            }
        }
    }

    private static IEnumerable<(int Index, ToolCallDetail Detail)> FinishToolCalls(
        Dictionary<int, OpenAIToolAccumulator> toolBuffer) =>
        toolBuffer.Values
            .OrderBy(t => t.Index)
            .Select(acc => (acc.Index, new ToolCallDetail(
                acc.Id ??= Guid.NewGuid().ToString(),
                acc.Name ??= "tool",
                acc.Arguments.ToString())));

    private static ChatMessage BuildAssistantMessage(Dictionary<int, OpenAIToolAccumulator> toolBuffer, string stopReason)
    {
        var blocks = new List<ContentBlock>();
        var toolCalls = new List<ToolCallContent>();
        string? text = null;

        foreach (var (_, detail) in FinishToolCalls(toolBuffer))
            toolCalls.Add(new ToolCallContent(detail.Id, detail.Name, ParseArguments(detail.ArgumentsJson)));

        if (toolCalls.Count > 0)
            blocks.AddRange(toolCalls);
        else
            text = string.Empty;

        _ = stopReason;
        return new ChatMessage(ChatRole.Assistant, blocks, Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    private static JsonElement ParseArguments(string json)
    {
        try { return JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json).RootElement; }
        catch (JsonException) { return JsonDocument.Parse("{}").RootElement; }
    }

    private static Usage ReadUsage(JsonElement usageProp)
    {
        var input = usageProp.TryGetProperty("prompt_tokens", out var i) ? i.GetInt32() : 0;
        var output = usageProp.TryGetProperty("completion_tokens", out var o) ? o.GetInt32() : 0;
        var cacheRead = 0;
        if (usageProp.TryGetProperty("prompt_tokens_details", out var d) &&
            d.TryGetProperty("cached_tokens", out var c) && c.ValueKind == JsonValueKind.Number)
            cacheRead = c.GetInt32();

        return new Usage(input, output, cacheRead, 0, new Cost(0, 0, 0, 0));
    }

    /// <summary>Builds the <c>/chat/completions</c> payload (internal for wire-level tests).</summary>
    internal static string BuildPayloadJson(ChatRequest request) =>
        JsonSerializer.Serialize(BuildPayload(request), JsonOpts);

    private static object BuildPayload(ChatRequest request) => new
    {
        model = request.ModelId,
        stream = true,
        messages = BuildMessages(request),
        tools = BuildTools(request.Tools),
        temperature = request.Temperature ?? 1,
        max_tokens = request.MaxTokens ?? 4096,
        reasoning_effort = ThinkingMapper.ToOpenAiReasoningEffort(request.ThinkingLevel)
    };

    private static List<object> BuildMessages(ChatRequest request)
    {
        var messages = new List<object>();

        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
            messages.Add(new { role = "system", content = request.SystemPrompt });

        foreach (var msg in request.Messages)
        {
            switch (msg.Role)
            {
                case ChatRole.User:
                {
                    var parts = new List<object>();
                    foreach (var block in msg.Content)
                    {
                        switch (block)
                        {
                            case TextContent t:
                                parts.Add(new { type = "text", text = t.Text });
                                break;
                            case ImageContent img:
                                parts.Add(new { type = "image_url", image_url = new { url = $"data:{img.MimeType};base64,{img.Data}" } });
                                break;
                        }
                    }

                    messages.Add(parts.Count == 0
                        ? new { role = "user", content = new object[] { new { type = "text", text = string.Empty } } }
                        : new { role = "user", content = parts });
                    break;
                }
                case ChatRole.Assistant:
                {
                    var text = msg.TryGetText();
                    var toolCalls = msg.GetToolCalls();

                    if (toolCalls.Count == 0)
                    {
                        messages.Add(new { role = "assistant", content = text ?? string.Empty });
                    }
                    else
                    {
                        var callObjs = toolCalls.Select(c => new
                        {
                            id = c.Id,
                            type = "function",
                            function = new { name = c.Name, arguments = c.Arguments.GetRawText() }
                        }).ToArray();

                        messages.Add(new { role = "assistant", content = text, tool_calls = callObjs });
                    }
                    break;
                }
                case ChatRole.Tool:
                {
                    var text = msg.TryGetText() ?? string.Empty;
                    messages.Add(new { role = "tool", tool_call_id = msg.ToolCallId, content = text, is_error = msg.IsError });
                    break;
                }
            }
        }

        return messages;
    }

    private static object? BuildTools(IReadOnlyList<ToolDefinition>? tools)
    {
        if (tools is null || tools.Count == 0)
            return null;

        return tools.Select(t => new
        {
            type = "function",
            function = new
            {
                name = t.Name,
                description = t.Description,
                parameters = BuildSchema(t.Parameters)
            }
        }).ToArray();
    }

    private static object BuildSchema(ToolParameterSchema? schema) =>
        schema is null
            ? new { type = "object", properties = new Dictionary<string, object>(), required = Array.Empty<string>() }
            : new
            {
                type = schema.Type,
                description = schema.Description,
                properties = schema.Properties?.ToDictionary(
                    kv => kv.Key,
                    kv => BuildSchema(kv.Value)),
                required = schema.Required
            };

    private sealed class OpenAIToolAccumulator
    {
        public int Index { get; init; }
        public string? Id { get; set; }
        public string? Name { get; set; }
        public bool Started { get; set; }
        public StringBuilder Arguments { get; } = new();
    }
}