using System.Text.Json;
using PiNet.Abstractions.Chat;
using PiNet.AgentCore;
using PiNet.Cli.Interact;
using Xunit;

namespace PiNet.Cli.Tests;

/// <summary>
/// Live rendering dedup: deltas stream inline once and completed lines must not repeat
/// content that is already on screen.
/// </summary>
public class AgentStreamPrinterTests
{
    private static ToolCallContent Tc(string id, string name) =>
        new(id, name, JsonDocument.Parse("{}").RootElement);

    private static CapturingOutput Render(params AgentEvent[] events)
    {
        var output = new CapturingOutput();
        var printer = new AgentStreamPrinter(output);
        var state = LineState.Empty;
        foreach (var evt in events)
        {
            var step = AgentConsoleRenderer.Step(state, evt);
            state = step.State;
            printer.Print(step);
        }

        return output;
    }

    [Fact]
    public void ThoughtThenText_IsNotDuplicated()
    {
        var output = Render(
            new MessageUpdateEvent("think", IsThinking: true),
            new MessageUpdateEvent("ans"),
            new MessageUpdateEvent("wer"),
            new TurnEndEvent(ChatMessage.Assistant("answer"), []));

        Assert.Equal("think\nanswer\n\n", output.All);
    }

    [Fact]
    public void PlainAnswer_IsNotDuplicated()
    {
        var output = Render(
            new MessageUpdateEvent("hola"),
            new TurnEndEvent(ChatMessage.Assistant("hola"), []));

        Assert.Equal("hola\n\n", output.All);
    }

    [Fact]
    public void ToolLifecycle_KeepsLabelsAndStreamsPartialOutput()
    {
        var tc = Tc("t1", "read_file");
        var output = Render(
            new MessageUpdateEvent("pensando", IsThinking: true),
            new ToolExecutionStartEvent(tc),
            new ToolExecutionEndEvent(tc, ToolResult.Ok("# Pi.NET")));

        Assert.Equal("pensando\n> Tool: read_file\n> ok: # Pi.NET\n", output.All);
    }

    [Fact]
    public void ToolPartialUpdates_StreamBeforeResult()
    {
        var tc = Tc("t1", "bash");
        var output = Render(
            new ToolExecutionStartEvent(tc),
            new ToolExecutionUpdateEvent(tc, "line1"),
            new ToolExecutionUpdateEvent(tc, "line2"),
            new ToolExecutionEndEvent(tc, ToolResult.Ok("line1line2")));

        Assert.Equal("line1line2\n> Tool: bashline1line2\n> ok: line1line2\n", output.All);
    }
}
