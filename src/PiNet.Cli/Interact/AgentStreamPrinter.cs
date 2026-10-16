using System.Text;

namespace PiNet.Cli.Interact;

/// <summary>
/// Live console rendering of agent stream steps (SR-04.10). Streamed deltas are written
/// inline as they arrive; when a completed line only repeats text already on screen it is
/// terminated with a newline instead of printing the same content a second time.
/// </summary>
public sealed class AgentStreamPrinter
{
    private readonly IConsoleOutput _out;
    private readonly StringBuilder _streamed = new();

    public AgentStreamPrinter(IConsoleOutput output)
    {
        _out = output;
    }

    public void Print(RenderStep step)
    {
        var pending = _streamed.ToString();

        foreach (var line in step.Completed)
        {
            if (pending.Length > 0)
            {
                // The inline text is already on screen: end its line first.
                _out.WriteLine();
                _streamed.Remove(0, pending.Length);
                if (line.Text != pending)
                    _out.WriteLine(line.Text, line.Color);
                pending = string.Empty;
            }
            else
            {
                _out.WriteLine(line.Text, line.Color);
            }
        }

        if (step.Inline is not null)
        {
            _out.Write(step.Inline.Text, step.Inline.Color);
            _streamed.Append(step.Inline.Text);
        }
    }
}
