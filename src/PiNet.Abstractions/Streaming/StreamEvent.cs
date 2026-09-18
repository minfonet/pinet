using PiNet.Abstractions.Chat;
using PiNet.Abstractions.Models;

namespace PiNet.Abstractions.Streaming;

public abstract record StreamEvent;

public sealed record StreamStart(string Provider, string ModelId, string? RequestId) : StreamEvent;

public sealed record TextDelta(string Delta) : StreamEvent;

public sealed record ThinkingDelta(string Delta) : StreamEvent;

public sealed record ToolCallStart(int Index, string Id, string Name) : StreamEvent;

public sealed record ToolCallDelta(int Index, string ArgumentsJsonFragment) : StreamEvent;

public sealed record ToolCallEnd(int Index, ToolCallDetail ToolCall) : StreamEvent;

public sealed record StreamEnd(string StopReason, Usage Usage, ChatMessage Message) : StreamEvent;

public sealed record StreamError(string Message, bool IsAborted = false) : StreamEvent;

public sealed record ToolCallDetail(string Id, string Name, string ArgumentsJson);