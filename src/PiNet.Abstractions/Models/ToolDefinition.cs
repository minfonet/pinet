namespace PiNet.Abstractions.Models;

public sealed record ToolParameterSchema
{
    public required string Type { get; init; }
    public string? Description { get; init; }
    public IReadOnlyDictionary<string, ToolParameterSchema>? Properties { get; init; }
    public IReadOnlyList<string>? Required { get; init; }
}

public sealed record ToolDefinition
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public ToolParameterSchema? Parameters { get; init; }
}