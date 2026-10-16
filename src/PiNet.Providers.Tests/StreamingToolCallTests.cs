using System.Net;
using System.Text;
using PiNet.Abstractions.Chat;
using PiNet.Abstractions.Providers;
using PiNet.Abstractions.Streaming;
using PiNet.Providers.Anthropic;
using PiNet.Providers.OpenAI;
using Xunit;

namespace PiNet.Providers.Tests;

/// <summary>
/// SR-01.05 — tool calls stream progressively: ToolCallStart, ToolCallDelta fragments and a
/// final ToolCallEnd for real wire formats (OpenAI-compatible SSE, e.g. llama.cpp, and
/// Anthropic message streams). Regression: the Stage 1 providers used to emit only
/// ToolCallDelta, so the agent loop could never assemble an executable tool call.
/// </summary>
public class StreamingToolCallTests
{
    private sealed class SseHandler(string sse) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
            };
            return Task.FromResult(response);
        }
    }

    private static ChatRequest Request(string providerId, string modelId) => new()
    {
        ProviderId = providerId,
        ModelId = modelId,
        SystemPrompt = "You are a helpful assistant.",
        Messages = [ChatMessage.User("read README.md")],
    };

    private static async Task<List<StreamEvent>> CollectAsync(IAsyncEnumerable<StreamEvent> stream)
    {
        var events = new List<StreamEvent>();
        await foreach (var evt in stream)
            events.Add(evt);
        return events;
    }

    // Fragmented arguments exactly like the chunks llama.cpp streams: id and name in the first
    // chunk, argument JSON split across several chunks, finish_reason in the last one.
    private const string OpenAiToolSse = """
        data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"read_file","arguments":"{"}}]}}]}

        data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"\"path\":\""}}]}}]}

        data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"README.md"}}]}}]}

        data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"\"}"}}]}}]}

        data: {"choices":[{"delta":{},"finish_reason":"tool_calls"}]}

        data: [DONE]

        """;

    [Fact]
    public async Task OpenAI_FragmentedToolCall_EmitsStartDeltasEnd()
    {
        var provider = new OpenAIChatProvider(new SseHandler(OpenAiToolSse));

        var events = await CollectAsync(provider.StreamAsync(Request("openai", "gpt-4o-mini"), new AuthInfo("test-key", null)));

        var start = Assert.Single(events.OfType<ToolCallStart>());
        Assert.Equal(0, start.Index);
        Assert.Equal("call_1", start.Id);
        Assert.Equal("read_file", start.Name);

        var fragments = events.OfType<ToolCallDelta>().Select(d => d.ArgumentsJsonFragment).ToList();
        Assert.Equal("{\"path\":\"README.md\"}", string.Concat(fragments));

        var end = Assert.Single(events.OfType<ToolCallEnd>());
        Assert.Equal(0, end.Index);
        Assert.Equal("call_1", end.ToolCall.Id);
        Assert.Equal("read_file", end.ToolCall.Name);
        Assert.Equal("{\"path\":\"README.md\"}", end.ToolCall.ArgumentsJson);

        var streamEnd = Assert.Single(events.OfType<StreamEnd>());
        Assert.Equal("tool_calls", streamEnd.StopReason);

        // Ordering: Start, then fragments, then End, then StreamEnd.
        Assert.True(events.IndexOf(start) < events.IndexOf(events.OfType<ToolCallDelta>().First()));
        Assert.True(events.IndexOf(events.OfType<ToolCallDelta>().Last()) < events.IndexOf(end));
        Assert.True(events.IndexOf(end) < events.IndexOf(streamEnd));

        var block = Assert.IsType<ToolCallContent>(Assert.Single(streamEnd.Message.Content));
        Assert.Equal("call_1", block.Id);
        Assert.Equal("read_file", block.Name);
        Assert.Equal("README.md", block.Arguments.GetProperty("path").GetString());
    }

    [Fact]
    public async Task OpenAI_WithoutFinishReason_ClosesToolCallsBeforeStreamEnd()
    {
        const string sse = """
            data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_9","function":{"name":"bash","arguments":"{\"command\":\"ls\"}"}}]}}]}

            data: [DONE]

            """;
        var provider = new OpenAIChatProvider(new SseHandler(sse));

        var events = await CollectAsync(provider.StreamAsync(Request("openai", "gpt-4o-mini"), new AuthInfo("test-key", null)));

        var end = Assert.Single(events.OfType<ToolCallEnd>());
        Assert.Equal("bash", end.ToolCall.Name);
        Assert.Equal("{\"command\":\"ls\"}", end.ToolCall.ArgumentsJson);

        var streamEnd = Assert.Single(events.OfType<StreamEnd>());
        Assert.Equal("stop", streamEnd.StopReason);
        Assert.True(events.IndexOf(end) < events.IndexOf(streamEnd));
    }

    [Fact]
    public async Task OpenAI_TwoToolCalls_AreClosedInIndexOrder()
    {
        const string sse = """
            data: {"choices":[{"delta":{"tool_calls":[{"index":1,"id":"b","function":{"name":"bash","arguments":"{}"}},{"index":0,"id":"a","function":{"name":"read_file","arguments":"{}"}}]}}]}

            data: {"choices":[{"delta":{},"finish_reason":"tool_calls"}]}

            data: [DONE]

            """;
        var provider = new OpenAIChatProvider(new SseHandler(sse));

        var events = await CollectAsync(provider.StreamAsync(Request("openai", "gpt-4o-mini"), new AuthInfo("test-key", null)));

        var ends = events.OfType<ToolCallEnd>().ToList();
        Assert.Equal(new[] { 0, 1 }, ends.Select(e => e.Index));
        Assert.Equal(new[] { "a", "b" }, ends.Select(e => e.ToolCall.Id));
    }

    [Fact]
    public async Task OpenAI_PlainText_EmitsNoToolCallEvents()
    {
        const string sse = """
            data: {"choices":[{"delta":{"content":"hola"}}]}

            data: {"choices":[{"delta":{},"finish_reason":"stop"}]}

            data: [DONE]

            """;
        var provider = new OpenAIChatProvider(new SseHandler(sse));

        var events = await CollectAsync(provider.StreamAsync(Request("openai", "gpt-4o-mini"), new AuthInfo("test-key", null)));

        Assert.Empty(events.OfType<ToolCallStart>());
        Assert.Empty(events.OfType<ToolCallEnd>());
        Assert.Contains(events, e => e is TextDelta { Delta: "hola" });
    }

    private const string AnthropicToolSse = """
        event: message_start
        data: {"type":"message_start","message":{"usage":{"input_tokens":10}}}

        event: content_block_start
        data: {"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"toolu_1","name":"read_file"}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"path\":\""}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"README.md\"}"}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":0}

        event: message_stop
        data: {"type":"message_stop"}

        """;

    [Fact]
    public async Task Anthropic_ToolUseBlock_EmitsStartDeltasEnd()
    {
        var provider = new AnthropicChatProvider(new SseHandler(AnthropicToolSse));

        var events = await CollectAsync(provider.StreamAsync(Request("anthropic", "claude-sonnet-4-5"), new AuthInfo("test-key", null)));

        var start = Assert.Single(events.OfType<ToolCallStart>());
        Assert.Equal(0, start.Index);
        Assert.Equal("toolu_1", start.Id);
        Assert.Equal("read_file", start.Name);

        var fragments = events.OfType<ToolCallDelta>().Select(d => d.ArgumentsJsonFragment).ToList();
        Assert.Equal("{\"path\":\"README.md\"}", string.Concat(fragments));

        var end = Assert.Single(events.OfType<ToolCallEnd>());
        Assert.Equal("toolu_1", end.ToolCall.Id);
        Assert.Equal("read_file", end.ToolCall.Name);
        Assert.Equal("{\"path\":\"README.md\"}", end.ToolCall.ArgumentsJson);

        var streamEnd = Assert.Single(events.OfType<StreamEnd>());
        Assert.True(events.IndexOf(start) < events.IndexOf(events.OfType<ToolCallDelta>().First()));
        Assert.True(events.IndexOf(end) < events.IndexOf(streamEnd));

        var block = Assert.IsType<ToolCallContent>(Assert.Single(streamEnd.Message.Content));
        Assert.Equal("README.md", block.Arguments.GetProperty("path").GetString());
    }

    [Fact]
    public async Task Anthropic_MessageStopWithoutBlockStop_StillClosesToolCall()
    {
        const string sse = """
            event: content_block_start
            data: {"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"toolu_2","name":"bash"}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"command\":\"ls\"}"}}

            event: message_stop
            data: {"type":"message_stop"}

            """;
        var provider = new AnthropicChatProvider(new SseHandler(sse));

        var events = await CollectAsync(provider.StreamAsync(Request("anthropic", "claude-sonnet-4-5"), new AuthInfo("test-key", null)));

        var end = Assert.Single(events.OfType<ToolCallEnd>());
        Assert.Equal("toolu_2", end.ToolCall.Id);
        Assert.Equal("{\"command\":\"ls\"}", end.ToolCall.ArgumentsJson);

        var streamEnd = Assert.Single(events.OfType<StreamEnd>());
        Assert.True(events.IndexOf(end) < events.IndexOf(streamEnd));
    }
}
