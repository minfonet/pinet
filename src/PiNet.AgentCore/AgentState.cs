using PiNet.Abstractions.Chat;

namespace PiNet.AgentCore;

public enum ThinkingLevel
{
    Off,
    Minimal,
    Low,
    Medium,
    High,
    XHigh,
    Max
}

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
    public ThinkingLevel ThinkingLevel { get; set; } = ThinkingLevel.Off;
    public QueueMode SteeringMode { get; set; } = QueueMode.OneAtATime;
    public QueueMode FollowUpMode { get; set; } = QueueMode.OneAtATime;
    public IReadOnlyList<AgentTool> Tools { get; set; } = [];
    public IReadOnlyList<ChatMessage> Messages { get; set; } = [];
    public bool IsStreaming { get; internal set; }
    public string? ErrorMessage { get; internal set; }

    internal void AppendMessage(ChatMessage message) =>
        Messages = [.. Messages, message];
}