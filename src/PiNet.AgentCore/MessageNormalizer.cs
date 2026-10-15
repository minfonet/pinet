using PiNet.Abstractions.Chat;

namespace PiNet.AgentCore;

/// <summary>
/// 7d — cross-provider handoff. Adapts the conversation history to what the
/// target provider accepts: assistant thinking blocks only round-trip to
/// Anthropic-style APIs; OpenAI-style chat completions must not receive them.
/// Normalization happens on the LLM-bound copy (via <c>ConvertToLlm</c>); the
/// persisted session always keeps the original entries.
/// </summary>
public static class MessageNormalizer
{
    /// <summary>True when the provider accepts assistant thinking blocks in request history.</summary>
    public static bool SupportsThinkingHistory(string providerId) =>
        providerId.Contains("anthropic", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the messages as the target provider may receive them. When nothing
    /// needs to change the caller's list/instances are returned untouched.
    /// </summary>
    public static IReadOnlyList<ChatMessage> ForProvider(IReadOnlyList<ChatMessage> messages, string providerId)
    {
        if (SupportsThinkingHistory(providerId))
            return messages;
        if (!messages.Any(m => m.Content.Any(b => b is ThinkingContent)))
            return messages;
        return messages.Select(StripThinking).ToArray();
    }

    private static ChatMessage StripThinking(ChatMessage message)
    {
        if (!message.Content.Any(b => b is ThinkingContent))
            return message;
        var content = message.Content.Where(b => b is not ThinkingContent).ToArray();
        return message with
        {
            // A message must keep at least one block for provider wire formats.
            Content = content.Length == 0 ? [new TextContent(string.Empty)] : content
        };
    }
}
