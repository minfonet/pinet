using PiNet.Abstractions.Auth;
using PiNet.Abstractions.Models;
using PiNet.Abstractions.Providers;
using PiNet.Abstractions.Streaming;
using PiNet.Cli.Interact;

namespace PiNet.Cli.Tests;

public static class Keys
{
    public static ConsoleKeyInfo Char(char c) => new(c, KeyFor(c), false, false, false);

    private static ConsoleKey KeyFor(char c) => c switch
    {
        // '.' would otherwise collide with ConsoleKey.Delete (both 46) and be
        // swallowed by the line editor; real terminals report OemPeriod.
        '.' => ConsoleKey.OemPeriod,
        _ => (ConsoleKey)c
    };
    public static ConsoleKeyInfo Key(ConsoleKey key, ConsoleModifiers modifiers = 0) =>
        new('\0', key, (modifiers & ConsoleModifiers.Shift) != 0,
            (modifiers & ConsoleModifiers.Alt) != 0,
            (modifiers & ConsoleModifiers.Control) != 0);

    public static IEnumerable<ConsoleKeyInfo> Type(string text) => text.Select(Char);

    public static ConsoleKeyInfo Ctrl(ConsoleKey key) =>
        Key(key, ConsoleModifiers.Control);

    public static ConsoleKeyInfo Alt(ConsoleKey key) =>
        Key(key, ConsoleModifiers.Alt);

    public static ConsoleKeyInfo Enter => Key(ConsoleKey.Enter);
    public static ConsoleKeyInfo CtrlC => Ctrl(ConsoleKey.C);
    public static ConsoleKeyInfo Up => Key(ConsoleKey.UpArrow);
    public static ConsoleKeyInfo Down => Key(ConsoleKey.DownArrow);
}

public sealed class ScriptedInput : IConsoleInput
{
    private readonly Queue<ConsoleKeyInfo> _keys;
    private readonly object _gate = new();

    /// <param name="interactive">
    /// True models a live terminal where keystrokes can arrive mid-turn
    /// (enables the 7a.2 pump); the default keeps sequential semantics.
    /// </param>
    public ScriptedInput(IEnumerable<ConsoleKeyInfo> keys, bool interactive = false)
    {
        _keys = new(keys);
        Interactive = interactive;
    }

    public bool Interactive { get; }

    public int Pending
    {
        get { lock (_gate) return _keys.Count; }
    }

    public bool KeyAvailable
    {
        get { lock (_gate) return _keys.Count > 0; }
    }

    public ConsoleKeyInfo? ReadKey()
    {
        lock (_gate)
            return _keys.Count > 0 ? _keys.Dequeue() : null;
    }

    /// <summary>Appends keys while the loop is running (mid-turn input, 7a.2).</summary>
    public void Push(params ConsoleKeyInfo[] keys)
    {
        lock (_gate)
            foreach (var key in keys)
                _keys.Enqueue(key);
    }
}

public sealed class CapturingOutput : IConsoleOutput
{
    public sealed record Emitted(string Text, ConsoleColor? Color);
    public sealed record EditorLine(string Prompt, string Buffer, int Caret);
    public List<Emitted> Writen { get; } = [];
    public List<EditorLine> EditorLines { get; } = [];
    private string _pending = string.Empty;

    public void Write(string text, ConsoleColor? color = null)
    {
        lock (Writen)
        {
            _pending += text;
            Writen.Add(new Emitted(text, color));
        }
    }

    public void WriteLine(string text = "", ConsoleColor? color = null) => Write(text + "\n", color);

    public void RenderEditorLine(string prompt, string buffer, int caret)
    {
        lock (Writen)
            EditorLines.Add(new EditorLine(prompt, buffer, caret));
    }

    public string All
    {
        get { lock (Writen) return string.Join(string.Empty, Writen.Select(w => w.Text)); }
    }

    public IReadOnlyList<EditorLine> EditorLinesSnapshot
    {
        get { lock (Writen) return EditorLines.ToArray(); }
    }

    public IEnumerable<string> RedLines
    {
        get { lock (Writen) return Writen.Where(w => w.Color == ConsoleColor.Red).Select(w => w.Text).ToArray(); }
    }

    public IEnumerable<string> YellowLines
    {
        get { lock (Writen) return Writen.Where(w => w.Color == ConsoleColor.Yellow).Select(w => w.Text).ToArray(); }
    }
}

/// <summary>Provider whose first generation never completes until cancelled.</summary>
public sealed class HangingProvider : IChatProvider
{
    public string Id => "hang";
    public string Name => "Hanging test provider";
    public IReadOnlyList<ModelDefinition> Models { get; } =
    [
        new ModelDefinition
        {
            Provider = "hang", Id = "h", Name = "Hang model",
            SupportsReasoning = false, SupportsVision = false, ContextWindow = 1000
        }
    ];
    public ICredentialResolver Credentials { get; } = NoneCredentials.Instance;

    public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, AuthInfo auth)
    {
        yield return new StreamStart(Id, request.ModelId, "hang-req-1");
        await Task.Delay(Timeout.Infinite, request.CancellationToken);
    }

    private sealed class NoneCredentials : ICredentialResolver
    {
        public static readonly NoneCredentials Instance = new();
        public ValueTask<AuthInfo> ResolveAsync(CancellationToken ct = default) =>
            new(new AuthInfo("none", null));
    }
}

public static class TestFixtures
{
    public static (string Cwd, string Root, IDisposable Disposer) TempDirs(string prefix = "pinet-test-")
    {
        var baseDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N")[..8])).FullName;
        var cwd = Directory.CreateDirectory(Path.Combine(baseDir, "cwd")).FullName;
        var root = Path.Combine(baseDir, "sessions");
        return (cwd, root, new DisposableDirectory(baseDir));
    }

    private sealed class DisposableDirectory : IDisposable
    {
        private readonly string _path;
        public DisposableDirectory(string path) => _path = path;
        public void Dispose() => Directory.Delete(_path, recursive: true);
    }

    public static IReadOnlyList<string> SessionEntryLines(string root, string cwd, string sessionId)
    {
        var manager = new Sessions.SessionManager(root);
        var path = manager.SessionFilePath(cwd, sessionId);
        return File.Exists(path) ? File.ReadAllLines(path) : [];
    }
}
