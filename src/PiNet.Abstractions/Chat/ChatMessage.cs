using System.Text.Json;

namespace PiNet.Abstractions.Chat;

public enum ThinkingLevel
{
    Off,
    Minimal,
    Low,
    Medium,
    High,
    XHigh,
    Max
}


public enum ChatRole
{
    System,
    User,
    Assistant,
    Tool
}

public abstract record ContentBlock;

public sealed record TextContent(string Text) : ContentBlock;

public sealed record ImageContent(string Data, string MimeType) : ContentBlock;

public sealed record ThinkingContent(string Thinking) : ContentBlock;

public sealed record ToolCallContent(string Id, string Name, JsonElement Arguments) : ContentBlock;

public sealed record ChatMessage(
    ChatRole Role,
    IReadOnlyList<ContentBlock> Content,
    string? ToolCallId = null,
    bool IsError = false,
    long Timestamp = 0)
{
    public static ChatMessage User(string text) =>
        new(ChatRole.User, [new TextContent(text)], Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    public static ChatMessage User(params ContentBlock[] blocks) =>
        new(ChatRole.User, blocks.Length == 0 ? [new TextContent(string.Empty)] : blocks,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    public static ChatMessage User(IReadOnlyList<ContentBlock> blocks) =>
        new(ChatRole.User, blocks.Count == 0 ? [new TextContent(string.Empty)] : blocks,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    public static ChatMessage Assistant(string text) =>
        new(ChatRole.Assistant, [new TextContent(text)], Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    public static ChatMessage Assistant(params ContentBlock[] blocks) =>
        new(ChatRole.Assistant, blocks.Length == 0 ? [new TextContent(string.Empty)] : blocks,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    public static ChatMessage Assistant(IReadOnlyList<ContentBlock> blocks) =>
        new(ChatRole.Assistant, blocks.Count == 0 ? [new TextContent(string.Empty)] : blocks,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    public static ChatMessage Tool(string toolCallId, string toolName, string text, bool isError = false) =>
        new(ChatRole.Tool, [new TextContent(text)], ToolCallId: toolCallId, IsError: isError,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    public string? TryGetText() =>
        Content.OfType<TextContent>().Select(t => t.Text).FirstOrDefault();

    public IReadOnlyList<ToolCallContent> GetToolCalls() =>
        Content.OfType<ToolCallContent>().ToArray();
}
