using System.Text.Json;
using PiNet.Abstractions.Chat;
using PiNet.Abstractions.Models;
using PiNet.Abstractions.Streaming;

namespace PiNet.AgentCore;

public enum ToolExecutionMode
{
    Parallel,
    Sequential
}

public sealed record ToolResult(string Content, bool IsError = false, bool Terminate = false)
{
    public static ToolResult Ok(string content) => new(content);
    public static ToolResult Error(string message) => new(message, IsError: true);
    public static ToolResult Done(string message) => new(message, Terminate: true);
}

public sealed record ToolExecutionResult(ToolCallContent ToolCall, ToolResult Result)
{
    public bool ShouldTerminate => Result.Terminate;
}

public sealed class AgentTool
{
    public required string Name { get; init; }
    public string? Label { get; init; }
    public required string Description { get; init; }
    public ToolParameterSchema? Parameters { get; init; }
    public ToolExecutionMode ExecutionMode { get; init; } = ToolExecutionMode.Parallel;
    public required Func<string, JsonElement, CancellationToken, IProgress<StreamEvent>?, Task<ToolResult>> Execute { get; init; }
}