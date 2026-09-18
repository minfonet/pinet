using System.Text.Json;
using PiNet.Abstractions.Chat;
using PiNet.AgentCore;
using PiNet.Cli.Interact;
using Xunit;

namespace PiNet.Cli.Tests;

/// <summary>SR-04.10 — streaming rendering (pure folding, single source of truth).</summary>
public class AgentConsoleRendererTests
{
    private static ToolCallContent Tc(string id, string name) =>
        new(id, name, JsonDocument.Parse("{}").RootElement);

    [Fact]
    public void Fold_TextDeltas_BecomeOneLine()
    {
        var lines = AgentConsoleRenderer.Fold(
        [
            new MessageUpdateEvent("hello "),
            new MessageUpdateEvent("world")
        ]);
        Assert.Equal([new RenderLine("hello world")], lines);
    }

    [Fact]
    public void Fold_ThinkingDeltas_BecomeDarkGrayLine()
    {
        var lines = AgentConsoleRenderer.Fold(
        [
            new MessageUpdateEvent("think ", IsThinking: true),
            new MessageUpdateEvent("more", IsThinking: true)
        ]);
        Assert.Equal([new RenderLine("think more", ConsoleColor.DarkGray)], lines);
    }

    [Fact]
    public void Fold_ThinkingThenText_FlushesThinkingLineFirst()
    {
        var lines = AgentConsoleRenderer.Fold(
        [
            new MessageUpdateEvent("think", IsThinking: true),
            new MessageUpdateEvent("answer")
        ]);
        Assert.Equal(
        [
            new RenderLine("think", ConsoleColor.DarkGray),
            new RenderLine("answer")
        ], lines);
    }

    [Fact]
    public void Fold_ToolStart_Update_End_ProduceLabeledLines()
    {
        var tc = Tc("t1", "read");
        var lines = AgentConsoleRenderer.Fold(
        [
            new ToolExecutionStartEvent(tc),
            new ToolExecutionUpdateEvent(tc, "partial"),
            new ToolExecutionEndEvent(tc, ToolResult.Ok("result"))
        ]);
        Assert.Equal(
        [
            new RenderLine("> Tool: readpartial"),
            new RenderLine("> ok: result", ConsoleColor.Green)
        ], lines);
    }

    [Fact]
    public void Fold_ToolError_IsRed()
    {
        var tc = Tc("t1", "bash");
        var lines = AgentConsoleRenderer.Fold(
        [
            new ToolExecutionStartEvent(tc),
            new ToolExecutionEndEvent(tc, ToolResult.Error("boom"))
        ]);
        Assert.Equal(
        [
            new RenderLine("> Tool: bash"),
            new RenderLine("> error: boom", ConsoleColor.Red)
        ], lines);
    }

    [Fact]
    public void Fold_ToolResult_TruncatedTo500Chars()
    {
        var tc = Tc("t1", "read");
        var longResult = new string('x', 600);
        var lines = AgentConsoleRenderer.Fold(
        [
            new ToolExecutionStartEvent(tc),
            new ToolExecutionEndEvent(tc, ToolResult.Ok(longResult))
        ]);
        Assert.Equal(
            $"> ok: {new string('x', 500)}...",
            lines[^1].Text);
        Assert.Equal(AgentConsoleRenderer.MaxToolResultLength, 500);
    }

    [Fact]
    public void Fold_TurnEndWithoutTools_AppendsBlankLine()
    {
        var lines = AgentConsoleRenderer.Fold(
        [
            new MessageUpdateEvent("done"),
            new TurnEndEvent(ChatMessage.Assistant("done"), [])
        ]);
        Assert.Equal(
        [
            new RenderLine("done"),
            new RenderLine(string.Empty)
        ], lines);
    }

    [Fact]
    public void Fold_LifecycleEvents_AreIgnored()
    {
        var lines = AgentConsoleRenderer.Fold(
        [
            new AgentStartEvent(),
            new TurnStartEvent(),
            new MessageStartEvent(ChatMessage.Assistant("")),
            new AgentEndEvent()
        ]);
        Assert.Empty(lines);
    }

    [Fact]
    public void Fold_IsDeterministic_SameEventsSameLines()
    {
        // SR-04.10: one-shot --agent and the interactive loop share this folding,
        // so an identical event sequence must always fold identically.
        var events = new List<AgentEvent>
        {
            new MessageUpdateEvent("think", IsThinking: true),
            new MessageUpdateEvent("answer"),
            new ToolExecutionStartEvent(Tc("t1", "read")),
            new ToolExecutionEndEvent(Tc("t1", "read"), ToolResult.Ok("ok"))
        };
        Assert.Equal(AgentConsoleRenderer.Fold(events), AgentConsoleRenderer.Fold(events));
    }
}
