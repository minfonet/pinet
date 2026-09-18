using PiNet.Abstractions.Chat;

namespace PiNet.AgentCore;

public abstract record AgentEvent;

public sealed record AgentStartEvent : AgentEvent;
public sealed record AgentEndEvent : AgentEvent;
public sealed record TurnStartEvent : AgentEvent;

public sealed record MessageStartEvent(ChatMessage Message) : AgentEvent;
public sealed record MessageUpdateEvent(string Delta, bool IsThinking = false) : AgentEvent;
public sealed record MessageEndEvent(ChatMessage Message) : AgentEvent;

public sealed record ToolExecutionStartEvent(ToolCallContent ToolCall) : AgentEvent;
public sealed record ToolExecutionUpdateEvent(ToolCallContent ToolCall, string Partial) : AgentEvent;
public sealed record ToolExecutionEndEvent(ToolCallContent ToolCall, ToolResult Result) : AgentEvent;

public sealed record TurnEndEvent(ChatMessage Message, IReadOnlyList<ToolExecutionResult> ToolResults) : AgentEvent;