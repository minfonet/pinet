using System.Text.Json;

namespace PiNet.Permissions;

/// <summary>
/// Extracts permission-relevant subjects (paths, commands) from built-in tool
/// arguments. Unknown tools yield null.
/// </summary>
public static class ToolPaths
{
    /// <summary>
    /// The 'path' argument of read_file/write_file/edit_file; null for bash,
    /// unknown tools, or missing/non-string arguments.
    /// </summary>
    public static string? ExtractPath(string toolName, JsonElement arguments)
    {
        if (toolName is not ("read_file" or "write_file" or "edit_file"))
            return null;
        return StringProperty(arguments, "path");
    }

    /// <summary>The 'command' argument of bash; null otherwise.</summary>
    public static string? ExtractCommand(string toolName, JsonElement arguments)
    {
        if (toolName is not "bash")
            return null;
        return StringProperty(arguments, "command");
    }

    private static string? StringProperty(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object
        && arguments.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
