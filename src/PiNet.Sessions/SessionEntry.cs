using PiNet.Abstractions.Chat;

namespace PiNet.Sessions;

public sealed record SessionEntry(
    string Id,
    ChatRole Role,
    IReadOnlyList<ContentBlock> Content,
    string? ParentId = null,
    string? ToolCallId = null,
    bool IsError = false,
    long Timestamp = 0,
    IReadOnlyDictionary<string, string>? Metadata = null)
{
    public static SessionEntry FromMessage(ChatMessage message, string? parentId = null, string? id = null) =>
        new(
            id ?? SessionId.New(),
            message.Role,
            message.Content,
            ParentId: parentId,
            ToolCallId: message.ToolCallId,
            IsError: message.IsError,
            Timestamp: message.Timestamp,
            Metadata: null);

    public ChatMessage ToMessage() =>
        new(Role, Content, ToolCallId: ToolCallId, IsError: IsError, Timestamp: Timestamp);
}