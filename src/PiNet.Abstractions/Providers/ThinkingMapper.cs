using PiNet.Abstractions.Chat;

namespace PiNet.Abstractions.Providers;

/// <summary>
/// Maps the provider-agnostic <see cref="ThinkingLevel"/> to provider-specific
/// wire parameters (spec 08 §5, sub-stage 7c).
/// </summary>
public static class ThinkingMapper
{
    /// <summary>
    /// OpenAI <c>reasoning_effort</c> value for a level, or <see langword="null"/>
    /// when thinking is off (the field must not be sent).
    /// </summary>
    public static string? ToOpenAiReasoningEffort(ThinkingLevel? level) => level switch
    {
        null or ThinkingLevel.Off => null,
        ThinkingLevel.Minimal => "minimal",
        ThinkingLevel.Low => "low",
        ThinkingLevel.Medium => "medium",
        _ => "high" // High / XHigh / Max clamp to the highest supported effort
    };

    /// <summary>
    /// Anthropic thinking budget (<c>thinking.budget_tokens</c>) for a level, or
    /// <see langword="null"/> when thinking is off or cannot be enabled because
    /// <paramref name="maxTokens"/> is too small. The API requires
    /// <c>budget_tokens &gt;= 1024</c> and <c>budget_tokens &lt; max_tokens</c>.
    /// </summary>
    public static int? ToAnthropicBudgetTokens(ThinkingLevel? level, int maxTokens)
    {
        if (level is null or ThinkingLevel.Off)
            return null;
        if (maxTokens <= 1024)
            return null; // no room for a valid budget below max_tokens

        var wanted = level switch
        {
            ThinkingLevel.Minimal => 1024,
            ThinkingLevel.Low => 2048,
            ThinkingLevel.Medium => 4096,
            ThinkingLevel.High => 8192,
            ThinkingLevel.XHigh => 16384,
            ThinkingLevel.Max => 32768,
            _ => 4096
        };
        return Math.Min(wanted, maxTokens - 1);
    }
}
