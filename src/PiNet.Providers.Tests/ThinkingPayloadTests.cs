using System.Text.Json;
using PiNet.Abstractions.Chat;
using PiNet.Abstractions.Providers;
using PiNet.Providers.Anthropic;
using PiNet.Providers.OpenAI;
using Xunit;

namespace PiNet.Providers.Tests;

/// <summary>
/// 7c — thinking level → provider wire payload: OpenAI <c>reasoning_effort</c>
/// and Anthropic <c>thinking.enabled</c> + <c>budget_tokens</c>.
/// </summary>
public class ThinkingPayloadTests
{
    private static ChatRequest Request(ThinkingLevel? level, int? maxTokens = null) => new()
    {
        ProviderId = "test",
        ModelId = "test-model",
        SystemPrompt = "You are a test assistant.",
        Messages = [ChatMessage.User("hi")],
        ThinkingLevel = level,
        MaxTokens = maxTokens
    };

    // --- OpenAI: reasoning_effort ---------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData(ThinkingLevel.Off)]
    public void OpenAi_ThinkingOff_SendsNoReasoningEffort(ThinkingLevel? level)
    {
        using var doc = JsonDocument.Parse(OpenAIChatProvider.BuildPayloadJson(Request(level)));
        Assert.False(doc.RootElement.TryGetProperty("reasoning_effort", out _));
    }

    [Theory]
    [InlineData(ThinkingLevel.Minimal, "minimal")]
    [InlineData(ThinkingLevel.Low, "low")]
    [InlineData(ThinkingLevel.Medium, "medium")]
    [InlineData(ThinkingLevel.High, "high")]
    [InlineData(ThinkingLevel.XHigh, "high")]
    [InlineData(ThinkingLevel.Max, "high")]
    public void OpenAi_ThinkingLevel_MapsToReasoningEffort(ThinkingLevel level, string expected)
    {
        using var doc = JsonDocument.Parse(OpenAIChatProvider.BuildPayloadJson(Request(level)));
        Assert.Equal(expected, doc.RootElement.GetProperty("reasoning_effort").GetString());
    }

    // --- Anthropic: thinking.enabled + budget_tokens --------------------

    [Theory]
    [InlineData(null)]
    [InlineData(ThinkingLevel.Off)]
    public void Anthropic_ThinkingOff_SendsNoThinking(ThinkingLevel? level)
    {
        using var doc = JsonDocument.Parse(AnthropicChatProvider.BuildPayloadJson(Request(level)));
        Assert.False(doc.RootElement.TryGetProperty("thinking", out _));
    }

    [Fact]
    public void Anthropic_ThinkingOn_SendsEnabledWithBudgetBelowMaxTokens()
    {
        using var doc = JsonDocument.Parse(AnthropicChatProvider.BuildPayloadJson(Request(ThinkingLevel.High)));
        var thinking = doc.RootElement.GetProperty("thinking");
        Assert.Equal("enabled", thinking.GetProperty("type").GetString());
        var budget = thinking.GetProperty("budget_tokens").GetInt32();
        var maxTokens = doc.RootElement.GetProperty("max_tokens").GetInt32();
        Assert.True(budget >= 1024, $"budget {budget} must be >= 1024");
        Assert.True(budget < maxTokens, $"budget {budget} must be < max_tokens {maxTokens}");
    }

    [Fact]
    public void Anthropic_MediumWithLargeMaxTokens_UsesLevelBudget()
    {
        using var doc = JsonDocument.Parse(
            AnthropicChatProvider.BuildPayloadJson(Request(ThinkingLevel.Medium, maxTokens: 100_000)));
        Assert.Equal(4096, doc.RootElement.GetProperty("thinking").GetProperty("budget_tokens").GetInt32());
    }

    [Fact]
    public void Anthropic_HighLevel_ClampsBudgetBelowMaxTokens()
    {
        using var doc = JsonDocument.Parse(
            AnthropicChatProvider.BuildPayloadJson(Request(ThinkingLevel.Max, maxTokens: 4096)));
        Assert.Equal(4095, doc.RootElement.GetProperty("thinking").GetProperty("budget_tokens").GetInt32());
    }

    [Fact]
    public void Anthropic_TinyMaxTokens_DisablesThinking()
    {
        using var doc = JsonDocument.Parse(
            AnthropicChatProvider.BuildPayloadJson(Request(ThinkingLevel.Max, maxTokens: 1024)));
        Assert.False(doc.RootElement.TryGetProperty("thinking", out _));
    }

    // --- shared mapper ---------------------------------------------------

    [Fact]
    public void Mapper_ClampsBudgetsToTheAllowedRange()
    {
        Assert.Null(ThinkingMapper.ToAnthropicBudgetTokens(null, 4096));
        Assert.Null(ThinkingMapper.ToAnthropicBudgetTokens(ThinkingLevel.Off, 4096));
        Assert.Null(ThinkingMapper.ToAnthropicBudgetTokens(ThinkingLevel.Max, 1024));
        Assert.Equal(1024, ThinkingMapper.ToAnthropicBudgetTokens(ThinkingLevel.Minimal, 4096));
        Assert.Equal(4095, ThinkingMapper.ToAnthropicBudgetTokens(ThinkingLevel.Max, 4096));
        Assert.Equal(32768, ThinkingMapper.ToAnthropicBudgetTokens(ThinkingLevel.Max, 1_000_000));
    }

    [Fact]
    public void Mapper_OpenAiEffort_IsNullOnlyWhenOff()
    {
        Assert.Null(ThinkingMapper.ToOpenAiReasoningEffort(null));
        Assert.Null(ThinkingMapper.ToOpenAiReasoningEffort(ThinkingLevel.Off));
        Assert.Equal("high", ThinkingMapper.ToOpenAiReasoningEffort(ThinkingLevel.Max));
    }
}
