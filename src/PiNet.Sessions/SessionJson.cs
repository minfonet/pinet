using System.Text;
using System.Text.Json;
using PiNet.Abstractions.Chat;

namespace PiNet.Sessions;

public static class SessionJson
{
    public static readonly JsonWriterOptions WriterOptions = new() { Indented = false };
    private static readonly JsonDocumentOptions DocOptions = new() { AllowTrailingCommas = false };

    public static void WriteEntry(Utf8JsonWriter writer, SessionEntry entry)
    {
        writer.WriteStartObject();
        writer.WriteString("id", entry.Id);
        if (entry.ParentId is null) writer.WriteNull("parentId");
        else writer.WriteString("parentId", entry.ParentId);
        writer.WriteString("role", RoleToJson(entry.Role));
        writer.WriteNumber("timestamp", entry.Timestamp);
        writer.WriteBoolean("isError", entry.IsError);
        writer.WriteStartArray("content");
        foreach (var block in entry.Content)
            WriteBlock(writer, block);
        writer.WriteEndArray();
        if (entry.ToolCallId is null) writer.WriteNull("toolCallId");
        else writer.WriteString("toolCallId", entry.ToolCallId);
        writer.WriteStartObject("metadata");
        if (entry.Metadata is not null)
            foreach (var (k, v) in entry.Metadata)
                writer.WriteString(k, v);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    public static SessionEntry ReadEntry(string line)
    {
        using var doc = JsonDocument.Parse(line, DocOptions);
        var root = doc.RootElement;

        string? StringOrNull(string name) =>
            root.TryGetProperty(name, out var p) && p.ValueKind != JsonValueKind.Null ? p.GetString() : null;

        var content = new List<ContentBlock>();
        foreach (var block in root.GetProperty("content").EnumerateArray())
            content.Add(ReadBlock(block));

        var metadata = new Dictionary<string, string>();
        if (root.TryGetProperty("metadata", out var meta) && meta.ValueKind == JsonValueKind.Object)
            foreach (var prop in meta.EnumerateObject())
                metadata[prop.Name] = prop.Value.GetString() ?? string.Empty;

        return new SessionEntry(
            root.GetProperty("id").GetString()!,
            RoleFromJson(root.GetProperty("role").GetString()!),
            content,
            ParentId: StringOrNull("parentId"),
            ToolCallId: StringOrNull("toolCallId"),
            IsError: root.TryGetProperty("isError", out var ie) && ie.GetBoolean(),
            Timestamp: root.TryGetProperty("timestamp", out var ts) ? ts.GetInt64() : 0,
            Metadata: metadata.Count == 0 ? null : metadata);
    }

    private static void WriteBlock(Utf8JsonWriter writer, ContentBlock block)
    {
        writer.WriteStartObject();
        switch (block)
        {
            case TextContent t:
                writer.WriteString("type", "text");
                writer.WriteString("text", t.Text);
                break;
            case ThinkingContent th:
                writer.WriteString("type", "thinking");
                writer.WriteString("thinking", th.Thinking);
                break;
            case ImageContent img:
                writer.WriteString("type", "image");
                writer.WriteString("data", img.Data);
                writer.WriteString("mimeType", img.MimeType);
                break;
            case ToolCallContent tc:
                writer.WriteString("type", "tool_call");
                writer.WriteString("id", tc.Id);
                writer.WriteString("name", tc.Name);
                writer.WritePropertyName("arguments");
                tc.Arguments.WriteTo(writer);
                break;
        }
        writer.WriteEndObject();
    }

    private static ContentBlock ReadBlock(JsonElement block)
    {
        var type = block.GetProperty("type").GetString();
        return type switch
        {
            "text" => new TextContent(block.GetProperty("text").GetString() ?? string.Empty),
            "thinking" => new ThinkingContent(block.GetProperty("thinking").GetString() ?? string.Empty),
            "image" => new ImageContent(
                block.GetProperty("data").GetString() ?? string.Empty,
                block.GetProperty("mimeType").GetString() ?? "application/octet-stream"),
            "tool_call" => new ToolCallContent(
                block.GetProperty("id").GetString() ?? string.Empty,
                block.GetProperty("name").GetString() ?? string.Empty,
                block.GetProperty("arguments").Clone()),
            _ => throw new JsonException($"Unknown content block type '{type}'.")
        };
    }

    private static string RoleToJson(ChatRole role) => role switch
    {
        ChatRole.System => "system",
        ChatRole.User => "user",
        ChatRole.Assistant => "assistant",
        _ => "tool"
    };

    private static ChatRole RoleFromJson(string role) => role switch
    {
        "system" => ChatRole.System,
        "user" => ChatRole.User,
        "assistant" => ChatRole.Assistant,
        _ => ChatRole.Tool
    };

    public static async Task WriteEntryLineAsync(Stream stream, SessionEntry entry, CancellationToken ct = default)
    {
        await using var writer = new Utf8JsonWriter(stream, WriterOptions);
        WriteEntry(writer, entry);
        await writer.FlushAsync(ct);
        var eol = Encoding.UTF8.GetBytes("\n");
        await stream.WriteAsync(eol, ct);
        await stream.FlushAsync(ct);
    }

    public static SessionEntry ReadEntryLine(string line) => ReadEntry(line);
}