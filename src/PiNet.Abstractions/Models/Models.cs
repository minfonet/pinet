namespace PiNet.Abstractions.Models;

public sealed record Cost(decimal Input, decimal Output, decimal CacheRead, decimal CacheWrite)
{
    public decimal Total => Input + Output + CacheRead + CacheWrite;
}

public sealed record Usage(
    int InputTokens,
    int OutputTokens,
    int CacheReadTokens,
    int CacheWriteTokens,
    Cost Cost);

public sealed record ModelDefinition
{
    public required string Provider { get; init; }
    public required string Id { get; init; }
    public required string Name { get; init; }
    public bool SupportsReasoning { get; init; }
    public bool SupportsVision { get; init; }
    public long ContextWindow { get; init; }
    public CostModel Cost { get; init; } = new();
}

public sealed record CostModel(decimal Input = 0, decimal Output = 0, decimal CacheRead = 0, decimal CacheWrite = 0);