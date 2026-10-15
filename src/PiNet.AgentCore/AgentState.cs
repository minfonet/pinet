using PiNet.Abstractions.Chat;
using PiNet.Abstractions.Models;

namespace PiNet.AgentCore;

using ThinkingLevel = Abstractions.Chat.ThinkingLevel;

/// <summary>How queued steering/follow-up messages are delivered at a boundary.</summary>
public enum QueueMode
{
    /// <summary>Deliver at most one queued message per turn boundary (default).</summary>
    OneAtATime,

    /// <summary>Deliver every queued message at the same boundary, in FIFO order.</summary>
    All
}

public sealed class AgentState
{
    public string SystemPrompt { get; set; } = "You are a helpful assistant.";
    public string ProviderId { get; set; } = "openai";
    public string ModelId { get; set; } = "gpt-4o-mini";
    public Abstractions.Chat.ThinkingLevel ThinkingLevel { get; set; } = Abstractions.Chat.ThinkingLevel.Off;
    public QueueMode SteeringMode { get; set; } = QueueMode.OneAtATime;
    public QueueMode FollowUpMode { get; set; } = QueueMode.OneAtATime;
    public IReadOnlyList<AgentTool> Tools { get; set; } = [];
    public IReadOnlyList<ChatMessage> Messages { get; set; } = [];
    public bool IsStreaming { get; internal set; }
    public string? ErrorMessage { get; internal set; }

    /// <summary>Usage reported by the most recent <c>StreamEnd</c> (null until one arrives).</summary>
    public Usage? LastUsage { get; internal set; }

    /// <summary>Cumulative usage across every run on this state.</summary>
    public Usage TotalUsage { get; internal set; } = new(0, 0, 0, 0, new Cost(0, 0, 0, 0));

    internal void AppendMessage(ChatMessage message) =>
        Messages = [.. Messages, message];

    internal void RecordUsage(Usage usage)
    {
        LastUsage = usage;
        TotalUsage = new Usage(
            TotalUsage.InputTokens + usage.InputTokens,
            TotalUsage.OutputTokens + usage.OutputTokens,
            TotalUsage.CacheReadTokens + usage.CacheReadTokens,
            TotalUsage.CacheWriteTokens + usage.CacheWriteTokens,
            new Cost(
                TotalUsage.Cost.Input + usage.Cost.Input,
                TotalUsage.Cost.Output + usage.Cost.Output,
                TotalUsage.Cost.CacheRead + usage.Cost.CacheRead,
                TotalUsage.Cost.CacheWrite + usage.Cost.CacheWrite));
    }
}