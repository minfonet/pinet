using System.Text.Json;

namespace PiNet.Abstractions.Chat;

/// <summary>
/// Cheap, provider-agnostic prompt-size estimator. Roughly four characters per
/// token plus a small per-message overhead; used as the fallback when real
/// usage counts are unavailable. Not a tokenizer.
/// </summary>
public static class TokenEstimator
{
    private const int PerMessageOverhead = 4;
    private const int ImageTokens = 1000;

    public static int Estimate(IReadOnlyList<ChatMessage> messages)
    {
        var tokens = 0;
        foreach (var message in messages)
            tokens += Estimate(message);
        return tokens;
    }

    public static int Estimate(ChatMessage message)
    {
        var tokens = PerMessageOverhead;
        foreach (var block in message.Content)
            tokens += block switch
            {
                TextContent t => Tokens(t.Text),
                ThinkingContent th => Tokens(th.Thinking),
                ToolCallContent tc => Tokens(tc.Name) + Tokens(RawArguments(tc)),
                ImageContent => ImageTokens,
                _ => 0
            };
        return tokens;
    }

    private static string RawArguments(ToolCallContent toolCall) =>
        toolCall.Arguments.ValueKind == JsonValueKind.Undefined
            ? string.Empty
            : toolCall.Arguments.GetRawText();

    private static int Tokens(string text) =>
        string.IsNullOrEmpty(text) ? 0 : (text.Length + 3) / 4;
}
