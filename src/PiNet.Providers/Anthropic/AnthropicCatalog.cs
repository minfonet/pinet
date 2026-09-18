using PiNet.Abstractions.Models;

namespace PiNet.Providers.Anthropic;

public static class AnthropicCatalog
{
    public static IReadOnlyList<ModelDefinition> Models { get; } =
    [
        new ModelDefinition
        {
            Provider = "anthropic", Id = "claude-sonnet-4-20250514", Name = "Claude Sonnet 4",
            SupportsReasoning = true, SupportsVision = true, ContextWindow = 200000,
            Cost = new CostModel(Input: 0.003m, Output: 0.015m, CacheRead: 0.0003m, CacheWrite: 0.00375m)
        },
        new ModelDefinition
        {
            Provider = "anthropic", Id = "claude-haiku-4-20250514", Name = "Claude Haiku 4",
            SupportsReasoning = true, SupportsVision = true, ContextWindow = 200000,
            Cost = new CostModel(Input: 0.001m, Output: 0.005m, CacheRead: 0.0001m, CacheWrite: 0.00125m)
        },
        new ModelDefinition
        {
            Provider = "anthropic", Id = "claude-sonnet-4-5", Name = "Claude Sonnet 4.5",
            SupportsReasoning = true, SupportsVision = true, ContextWindow = 500000,
            Cost = new CostModel(Input: 0.003m, Output: 0.015m, CacheRead: 0.0003m, CacheWrite: 0.0045m)
        },
        new ModelDefinition
        {
            Provider = "anthropic", Id = "claude-opus-4-1", Name = "Claude Opus 4.1",
            SupportsReasoning = true, SupportsVision = true, ContextWindow = 500000,
            Cost = new CostModel(Input: 0.015m, Output: 0.075m, CacheRead: 0.0015m, CacheWrite: 0.01875m)
        }
    ];
}