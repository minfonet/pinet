using PiNet.Abstractions.Models;

namespace PiNet.Providers.OpenAI;

public static class OpenAICatalog
{
    public static IReadOnlyList<ModelDefinition> Models { get; } =
    [
        new ModelDefinition
        {
            Provider = "openai", Id = "gpt-4o-mini", Name = "GPT-4o mini",
            SupportsReasoning = false, SupportsVision = true, ContextWindow = 128000,
            Cost = new CostModel(Input: 0.00015m, Output: 0.0006m)
        },
        new ModelDefinition
        {
            Provider = "openai", Id = "gpt-4o", Name = "GPT-4o",
            SupportsReasoning = false, SupportsVision = true, ContextWindow = 128000,
            Cost = new CostModel(Input: 0.0025m, Output: 0.01m)
        },
        new ModelDefinition
        {
            Provider = "openai", Id = "gpt-4.1-mini", Name = "GPT-4.1 mini",
            SupportsReasoning = false, SupportsVision = true, ContextWindow = 1000000,
            Cost = new CostModel(Input: 0.0004m, Output: 0.0016m)
        },
        new ModelDefinition
        {
            Provider = "openai", Id = "gpt-4.1", Name = "GPT-4.1",
            SupportsReasoning = false, SupportsVision = true, ContextWindow = 1000000,
            Cost = new CostModel(Input: 0.002m, Output: 0.008m)
        },
        new ModelDefinition
        {
            Provider = "openai", Id = "gpt-5-mini", Name = "GPT-5 mini",
            SupportsReasoning = true, SupportsVision = true, ContextWindow = 400000,
            Cost = new CostModel(Input: 0.00025m, Output: 0.002m, CacheRead: 0.00005m, CacheWrite: 0.000375m)
        },
        new ModelDefinition
        {
            Provider = "openai", Id = "gpt-5", Name = "GPT-5",
            SupportsReasoning = true, SupportsVision = true, ContextWindow = 400000,
            Cost = new CostModel(Input: 0.00125m, Output: 0.01m, CacheRead: 0.00025m, CacheWrite: 0.001875m)
        },
        new ModelDefinition
        {
            Provider = "openai", Id = "o3-mini", Name = "o3 mini",
            SupportsReasoning = true, SupportsVision = true, ContextWindow = 200000,
            Cost = new CostModel(Input: 0.0011m, Output: 0.0044m, CacheRead: 0.00055m)
        }
    ];
}