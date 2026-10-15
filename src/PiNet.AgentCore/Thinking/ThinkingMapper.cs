using PiNet.Abstractions.Chat;

namespace PiNet.AgentCore.Thinking;

/// <summary>
/// Maps <see cref="ThinkingLevel"/> to provider-specific thinking parameters.
/// </summary>
public static class ThinkingMapper
{
    /// <summary>
    /// Maps thinking level to provider-specific payload object.
    /// Returns null if thinking should be disabled.
    /// </summary>
    public static object? Map(ThinkingLevel level, string providerId, int? maxTokens = null)
    {
        if (level == ThinkingLevel.Off)
            return null;

        var pid = providerId?.ToLowerInvariant() ?? string.Empty;

        // OpenAI reasoning models use reasoning_effort
        if (pid == "openai" || pid.StartsWith("openai-") || pid.Contains("openai"))
        {
            var effort = level switch
            {
                ThinkingLevel.Minimal => "minimal",
                ThinkingLevel.Low => "low",
                ThinkingLevel.Medium => "medium",
                ThinkingLevel.High => "high",
                ThinkingLevel.XHigh => "high",
                ThinkingLevel.Max => "high",
                _ => "medium"
            };
            return new { reasoning_effort = effort };
        }

        // Anthropic uses thinking.enabled + budget_tokens
        if (pid == "anthropic" || pid.StartsWith("anthropic-") || pid.Contains("anthropic"))
        {
            var budget = GetAnthropicBudget(level, maxTokens);
            // Only enable thinking if we have a positive budget
            if (budget <= 0)
                return null;
            // Ensure budget < maxTokens if both specified
            if (maxTokens.HasValue && budget >= maxTokens.Value)
            {
                budget = Math.Max(1, maxTokens.Value - 1);
            }
            return new { thinking = new { type = "thought", enabled = true, budget_tokens = budget } };
        }

        // For other providers, return generic mapping
        return new { level = level.ToString().ToLowerInvariant(), enabled = true };
    }

    private static int GetAnthropicBudget(ThinkingLevel level, int? maxTokens)
    {
        // Conservative defaults as per spec guidance
        var defaultBudget = level switch
        {
            ThinkingLevel.Minimal => 1000,
            ThinkingLevel.Low => 2000,
            ThinkingLevel.Medium => 4000,
            ThinkingLevel.High => 8000,
            ThinkingLevel.XHigh => 12000,
            ThinkingLevel.Max => 16000,
            _ => 4000
        };
        if (!maxTokens.HasValue) return defaultBudget;
        // Ensure at most 80% of max tokens for thinking, leave room for response
        var maxBudget = Math.Max(1, (int)(maxTokens.Value * 0.8));
        return Math.Min(defaultBudget, maxBudget);
    }
}
