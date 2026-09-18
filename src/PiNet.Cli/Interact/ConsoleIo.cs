namespace PiNet.Cli.Interact;

public interface IConsoleInput
{
    ConsoleKeyInfo? ReadKey();
}

public interface IConsoleOutput
{
    void Write(string text, ConsoleColor? color = null);
    void WriteLine(string text = "", ConsoleColor? color = null);
    void RenderEditorLine(string prompt, string buffer, int caret);
}

public sealed class ConsoleInput : IConsoleInput
{
    public ConsoleKeyInfo? ReadKey()
    {
        var previous = Console.TreatControlCAsInput;
        Console.TreatControlCAsInput = true;
        try
        {
            return Console.IsInputRedirected ? null : Console.ReadKey(intercept: true);
        }
        finally
        {
            Console.TreatControlCAsInput = previous;
        }
    }
}

public sealed class ConsoleOutput : IConsoleOutput
{
    public void Write(string text, ConsoleColor? color = null)
    {
        if (color is null)
        {
            Console.Write(text);
            return;
        }

        var previous = Console.ForegroundColor;
        Console.ForegroundColor = color.Value;
        try
        {
            Console.Write(text);
        }
        finally
        {
            Console.ForegroundColor = previous;
        }
    }

    public void WriteLine(string text = "", ConsoleColor? color = null)
    {
        Write(text + Environment.NewLine, color);
    }

    public void RenderEditorLine(string prompt, string buffer, int caret)
    {
        var prefix = $"\r{prompt}";
        Console.Write(prefix + buffer);
        var pad = Math.Max(0, Console.WindowWidth - Console.CursorLeft);
        Console.Write(new string(' ', Math.Min(pad, 1000)));
        Console.Write($"\r{prefix}");
        var column = prefix.Length + Math.Min(caret, buffer.Length);
        if (column < Console.WindowWidth)
            Console.CursorLeft = column;
    }
}