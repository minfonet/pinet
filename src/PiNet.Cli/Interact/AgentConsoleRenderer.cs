using PiNet.AgentCore;

namespace PiNet.Cli.Interact;

public sealed record RenderLine(string Text, ConsoleColor? Color = null);

public sealed record LineState(string Text, ConsoleColor? Color, bool IsTool)
{
    public static LineState Empty { get; } = new(string.Empty, null, false);
}

public sealed record RenderStep(LineState State, IReadOnlyList<RenderLine> Completed, RenderLine? Inline);

public static class AgentConsoleRenderer
{
    public const int MaxToolResultLength = 500;

    public static IReadOnlyList<RenderLine> Fold(IReadOnlyList<AgentEvent> events)
    {
        var lines = new List<RenderLine>();
        var state = LineState.Empty;
        foreach (var evt in events)
        {
            var step = Step(state, evt);
            state = step.State;
            lines.AddRange(step.Completed);
        }

        if (state.Text.Length > 0)
            lines.Add(new RenderLine(state.Text, state.Color));
        return lines;
    }

    public static RenderStep Step(LineState state, AgentEvent evt)
    {
        switch (evt)
        {
            case MessageUpdateEvent { IsThinking: true } th when state.IsTool || state.Color != ConsoleColor.DarkGray:
                return new RenderStep(
                    new LineState(th.Delta, ConsoleColor.DarkGray, false),
                    Flush(state),
                    new RenderLine(th.Delta, ConsoleColor.DarkGray));
            case MessageUpdateEvent { IsThinking: true } th:
                return new RenderStep(state with { Text = state.Text + th.Delta }, [],
                    new RenderLine(th.Delta, ConsoleColor.DarkGray));
            case MessageUpdateEvent t when state.IsTool || state.Color is not null:
                return new RenderStep(
                    new LineState(t.Delta, null, false),
                    Flush(state),
                    new RenderLine(t.Delta));
            case MessageUpdateEvent t:
                return new RenderStep(state with { Text = state.Text + t.Delta }, [],
                    new RenderLine(t.Delta));
            case ToolExecutionStartEvent start:
                return new RenderStep(
                    new LineState($"> Tool: {start.ToolCall.Name}", null, true),
                    Flush(state),
                    null);
            case ToolExecutionUpdateEvent upd when state.IsTool:
                return new RenderStep(state with { Text = state.Text + upd.Partial }, [],
                    new RenderLine(upd.Partial));
            case ToolExecutionUpdateEvent upd:
                return new RenderStep(new LineState(upd.Partial, null, true),
                    Flush(state),
                    new RenderLine(upd.Partial));
            case ToolExecutionEndEvent end:
                var resultColor = end.Result.IsError ? ConsoleColor.Red : ConsoleColor.Green;
                var label = end.Result.IsError ? "> error" : "> ok";
                var resultLine = new RenderLine($"{label}: {Truncate(end.Result.Content, MaxToolResultLength)}", resultColor);
                var lines = new List<RenderLine>();
                if (state.IsTool)
                {
                    if (state.Text.Length > 0)
                        lines.Add(new RenderLine(state.Text));
                }
                else
                {
                    lines.AddRange(Flush(state));
                }
                lines.Add(resultLine);
                return new RenderStep(LineState.Empty, lines, null);
            case TurnEndEvent { ToolResults.Count: 0 }:
                var completed = new List<RenderLine>();
                completed.AddRange(Flush(state));
                completed.Add(new RenderLine(string.Empty));
                return new RenderStep(LineState.Empty, completed, null);
            default:
                return new RenderStep(state, [], null);
        }
    }

    private static IReadOnlyList<RenderLine> Flush(LineState state) =>
        state.Text.Length == 0
            ? []
            : [new RenderLine(state.Text, state.Color)];

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "...";
}