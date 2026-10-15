using System.Text.Json;
using PiNet.Abstractions.Chat;

namespace PiNet.AgentCore.Tests;

/// <summary>
/// Unit tests for <see cref="TokenEstimator"/> (AC7b.1: "TokenEstimator grows with
/// text and counts tool-call JSON and images.").
/// </summary>
public sealed class TokenEstimatorTests
{
    [Fact]
    public void EmptyList_IsZero()
    {
        ChatMessage[] empty = [];
        Assert.Equal(0, TokenEstimator.Estimate(empty));
    }

    [Fact]
    public void EmptyTextMessage_IsMessageOverheadOnly()
    {
        // Per-message overhead (4) plus zero text.
        Assert.True(TokenEstimator.Estimate(ChatMessage.User("")) >= 4,
            $"Expected at least the per-message overhead, got {TokenEstimator.Estimate(ChatMessage.User(""))}.");
    }

    [Fact]
    public void GrowsWithLongerText()
    {
        var shortEst = TokenEstimator.Estimate(ChatMessage.User("hi"));
        var longEst = TokenEstimator.Estimate(ChatMessage.User(new string('x', 2000)));
        Assert.True(longEst > shortEst, $"Expected longer text to cost more ({longEst} vs {shortEst}).");
    }

    [Fact]
    public void ImageBlock_IsLargeFixedCost()
    {
        var empty = TokenEstimator.Estimate(ChatMessage.User(""));
        var withImage = TokenEstimator.Estimate(
            new ChatMessage(ChatRole.User, [new ImageContent("data:image/png;base64,AAAA", "image/png")], Timestamp: 0));

        // The image block contributes a fixed ~1000-token cost that dominates the estimate.
        var diff = withImage - empty;
        Assert.True(diff is >= 995 and <= 1010,
            $"Expected the image to add ~1000 tokens, but it added {diff}.");
    }

    [Fact]
    public void ToolCallContent_CountsArgumentJson()
    {
        var smallJson = "{}";
        var bigJson = "{\"cmd\":\"" + new string('x', 400) + "\"}";

        var small = new ChatMessage(ChatRole.Assistant,
            [new ToolCallContent("t1", "bash", JsonDocument.Parse(smallJson).RootElement)], Timestamp: 0);
        var big = new ChatMessage(ChatRole.Assistant,
            [new ToolCallContent("t1", "bash", JsonDocument.Parse(bigJson).RootElement)], Timestamp: 0);

        var smallEst = TokenEstimator.Estimate(small);
        var bigEst = TokenEstimator.Estimate(big);
        Assert.True(bigEst > smallEst, $"Expected richer args to cost more ({bigEst} vs {smallEst}).");
    }

    [Fact]
    public void ThinkingContent_CountsText()
    {
        var empty = TokenEstimator.Estimate(ChatMessage.Assistant(""));
        var withThinking = TokenEstimator.Estimate(
            new ChatMessage(ChatRole.Assistant, [new ThinkingContent(new string('t', 500))], Timestamp: 0));

        Assert.True(withThinking > empty, $"Expected thinking text to cost more ({withThinking} vs {empty}).");
    }
}
