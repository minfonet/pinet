using PiNet.Abstractions.Chat;
using Xunit;

namespace PiNet.AgentCore.Tests;

/// <summary>7d — cross-provider handoff: history normalization via ConvertToLlm.</summary>
public class MessageNormalizerTests
{
    private static ChatMessage AssistantWithThinking() => new(ChatRole.Assistant,
        [
            new ThinkingContent("internal reasoning"),
            new TextContent("visible answer"),
            new ToolCallContent("call-1", "read", Helpers.Args("""{"path":"a.txt"}"""))
        ], Timestamp: 1);

    [Fact]
    public void AnthropicTarget_KeepsThinking()
    {
        var messages = new List<ChatMessage> { AssistantWithThinking() };

        var result = MessageNormalizer.ForProvider(messages, "anthropic");

        Assert.Same(messages, result);
        Assert.Contains(result[0].Content, b => b is ThinkingContent);
    }

    [Fact]
    public void OpenAiTarget_DropsThinking_KeepsRest()
    {
        var messages = new List<ChatMessage> { AssistantWithThinking(), ChatMessage.User("next") };

        var result = MessageNormalizer.ForProvider(messages, "openai");

        Assert.NotSame(messages, result);
        Assert.DoesNotContain(result[0].Content, b => b is ThinkingContent);
        Assert.Contains(result[0].Content, b => b is TextContent { Text: "visible answer" });
        Assert.Contains(result[0].Content, b => b is ToolCallContent { Id: "call-1" });
        Assert.Same(messages[1], result[1]);

        // Original history (what gets persisted) is untouched.
        Assert.Contains(messages[0].Content, b => b is ThinkingContent);
    }

    [Fact]
    public void NoThinking_ReturnsInputUnchanged()
    {
        var messages = new List<ChatMessage> { ChatMessage.User("hi"), ChatMessage.Assistant("yo") };

        var result = MessageNormalizer.ForProvider(messages, "openai");

        Assert.Same(messages, result);
    }

    [Fact]
    public void ThinkingOnlyAssistant_BecomesEmptyTextBlock()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.Assistant, [new ThinkingContent("only thoughts")], Timestamp: 1)
        };

        var result = MessageNormalizer.ForProvider(messages, "openai");

        var block = Assert.Single(result[0].Content);
        Assert.Equal(string.Empty, Assert.IsType<TextContent>(block).Text);
    }

    [Fact]
    public void CustomAnthropicCompatibleProvider_KeepsThinking()
    {
        var messages = new List<ChatMessage> { AssistantWithThinking() };

        Assert.Same(messages, MessageNormalizer.ForProvider(messages, "my-anthropic-gateway"));
    }
}
