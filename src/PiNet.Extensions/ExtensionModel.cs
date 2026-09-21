using System.Text.Json;

namespace PiNet.Extensions;

public interface IExtension
{
    void Register(IExtensionContext ctx);
}

public interface IExtensionContext
{
    string Cwd { get; }
    void RegisterTool(ExtensionToolSpec tool);
    void RegisterCommand(string name, ExtensionCommandHandler handler);
}

public sealed record ExtensionToolSpec(
    string Name,
    string Description,
    string? ParametersJson,
    Func<string, JsonElement, CancellationToken, Task<ExtensionToolResult>> Execute);

public sealed record ExtensionToolResult(string Content, bool IsError = false, bool Terminate = false);

public delegate Task<bool> ExtensionCommandHandler(string args, IExtensionCommandContext ctx);

public interface IExtensionCommandContext
{
    void WriteLine(string text);
    Task SendToAgentAsync(string prompt);
}

/// <summary>
/// Neutral JSON-schema shape for extension tool parameters. It mirrors the
/// recursive { type, description, properties, required } layout of the agent's
/// ToolParameterSchema; the CLI maps one to the other at adapter time.
/// </summary>
public sealed record ExtensionParameterSchema
{
    public required string Type { get; init; }
    public string? Description { get; init; }
    public IReadOnlyDictionary<string, ExtensionParameterSchema>? Properties { get; init; }
    public IReadOnlyList<string>? Required { get; init; }
}

public static class ToolSchemaParser
{
    public static ExtensionParameterSchema? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return ParseNode(doc.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ExtensionParameterSchema? ParseNode(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object)
            return null;

        var type = el.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(type))
            return null;

        var description = el.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
            ? d.GetString()
            : null;

        Dictionary<string, ExtensionParameterSchema>? properties = null;
        if (el.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
        {
            properties = new Dictionary<string, ExtensionParameterSchema>();
            foreach (var prop in props.EnumerateObject())
            {
                var parsed = ParseNode(prop.Value);
                if (parsed is not null)
                    properties[prop.Name] = parsed;
            }
        }

        List<string>? required = null;
        if (el.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array)
        {
            required = [];
            foreach (var item in req.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                    required.Add(item.GetString()!);
            }
        }

        return new ExtensionParameterSchema
        {
            Type = type,
            Description = description,
            Properties = properties,
            Required = required
        };
    }
}