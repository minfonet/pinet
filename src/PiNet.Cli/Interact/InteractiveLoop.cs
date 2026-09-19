using PiNet.Abstractions.Chat;
using PiNet.AgentCore;
using PiNet.AgentCore.Tools;
using PiNet.Permissions;
using PiNet.Providers;
using PiNet.Sessions;

namespace PiNet.Cli.Interact;

public sealed class InteractiveLoop
{
    private readonly ChatService _chat;
    private readonly SessionManager _manager;
    private readonly string _cwd;
    private readonly IConsoleInput _input;
    private readonly IConsoleOutput _out;
    private readonly Action<CancellationTokenSource>? _turnCancellation;
    private readonly IPermissionResolver? _permissions;
    private readonly IPermissionPrompt? _permissionPrompt;

    private readonly LineEditor _editor = new();
    private readonly LineHistory _history = new();

    private string _provider;
    private string _model;
    private Session? _session;
    private Agent? _agent;
    private TurnRunner? _runner;
    private CancellationTokenSource? _activeTurn;
    private LineState _renderState = LineState.Empty;

    public InteractiveLoop(
        ChatService chat,
        SessionManager manager,
        string cwd,
        string providerId,
        string modelId,
        IConsoleInput input,
        IConsoleOutput output,
        bool noSession = false,
        string? sessionRef = null,
        string? forkRef = null,
        bool continueLatest = false,
        Action<CancellationTokenSource>? turnCancellation = null,
        IPermissionResolver? permissions = null,
        IPermissionPrompt? permissionPrompt = null)
    {
        _chat = chat;
        _manager = manager;
        _cwd = cwd;
        _provider = providerId;
        _model = modelId;
        _input = input;
        _out = output;
        NoSession = noSession;
        SessionRef = sessionRef;
        ForkRef = forkRef;
        ContinueLatest = continueLatest;
        _turnCancellation = turnCancellation;
        _permissions = permissions;
        _permissionPrompt = permissionPrompt;
    }

    private bool NoSession { get; }
    private string? SessionRef { get; }
    private string? ForkRef { get; }
    private bool ContinueLatest { get; }

    public void CancelTurn() => _activeTurn?.Cancel();

    public async Task<int> RunAsync(CancellationToken hostCt = default)
    {
        var seed = await StartupAsync(hostCt);
        RebuildAgent(_provider, _model, seed);

        while (!hostCt.IsCancellationRequested)
        {
            _out.RenderEditorLine(Prompt(), _editor.Buffer, _editor.Caret);
            var key = _input.ReadKey();
            if (key is null)
            {
                _out.WriteLine();
                return 0;
            }

            var control = key.Value.Modifiers.HasFlag(ConsoleModifiers.Control);
            if (control && key.Value.Key == ConsoleKey.C)
            {
                if (_editor.Buffer.Length == 0)
                {
                    _out.WriteLine("^C");
                    return 0;
                }
                _editor.Reset();
                _out.WriteLine("^C");
                continue;
            }
            if (control && key.Value.Key is ConsoleKey.D or ConsoleKey.Z)
            {
                if (_editor.Buffer.Length == 0)
                {
                    _out.WriteLine("^D");
                    return 0;
                }
                continue;
            }

            if (key.Value.Key == ConsoleKey.UpArrow)
            {
                if (!_history.IsNavigating)
                    _history.SetDraft(_editor.Buffer);
                if (_history.MoveBack())
                    _editor.SetBuffer(_history.Current);
                continue;
            }
            if (key.Value.Key == ConsoleKey.DownArrow)
            {
                if (_history.MoveForward())
                    _editor.SetBuffer(_history.Current);
                continue;
            }

            _editor.Apply(key.Value);
            if (!_editor.Complete)
                continue;

            var line = _editor.Submit();
            if (string.IsNullOrWhiteSpace(line))
                continue;

            _history.Add(line);
            if (!await HandleAsync(line, hostCt))
                return 0;
            _renderState = LineState.Empty;
        }

        return 0;
    }

    private async Task<List<ChatMessage>> StartupAsync(CancellationToken ct)
    {
        var seed = new List<ChatMessage>();
        if (NoSession)
            return seed;

        if (ForkRef is not null)
        {
            _session = await _manager.ForkAsync(_cwd, ForkRef, ct: ct);
            _out.WriteLine($"[fork of {ForkRef}] session {_session.Id}");
        }
        else if (SessionRef is not null)
        {
            _session = await _manager.LoadSessionAsync(_cwd, SessionRef, ct);
            _out.WriteLine($"session {_session.Id}");
        }
        else if (ContinueLatest)
        {
            _session = await _manager.LoadLatestAsync(_cwd, ct) ?? _manager.CreateSession(_cwd);
            _out.WriteLine($"session {_session.Id}");
        }
        else
        {
            _session = _manager.CreateSession(_cwd);
            _out.WriteLine($"session {_session.Id}");
        }

        seed.AddRange(_session.ToMessages());
        if (seed.Count > 0)
            _out.WriteLine($"[resumed with {seed.Count} prior message(s)]");
        return seed;
    }

    private void RebuildAgent(string providerId, string modelId, IReadOnlyList<ChatMessage> messages)
    {
        _runner?.Dispose();

        _agent = new Agent(new AgentOptions
        {
            Runner = _chat,
            BeforeToolCall = _permissions is not null && _permissionPrompt is not null
                ? PermissionGate.Create(_permissions, _permissionPrompt, _cwd)
                : null,
            InitialState = new AgentState
            {
                SystemPrompt = CliConfig.DefaultSystemPrompt,
                ProviderId = providerId,
                ModelId = modelId,
                Tools = BuiltInTools.All(),
                Messages = new List<ChatMessage>(messages)
            }
        });

        _runner = new TurnRunner(_agent, _cwd, _manager, NoSession);
        if (_session is not null)
            _runner.AttachSession(_session);
        _runner.AgentEventSink += RenderEventAsync;
    }

    private string Prompt() => $"{_provider}/{_model} > ";

    private async Task RenderEventAsync(AgentEvent evt, CancellationToken ct)
    {
        var step = AgentConsoleRenderer.Step(_renderState, evt);
        _renderState = step.State;
        if (step.Inline is not null)
            _out.Write(step.Inline.Text, step.Inline.Color);
        foreach (var completed in step.Completed)
            _out.WriteLine(completed.Text, completed.Color);
        await Task.CompletedTask;
    }

    private async Task<bool> HandleAsync(string line, CancellationToken hostCt)
    {
        if (!line.TrimStart().StartsWith('/'))
        {
            await RunTurnAsync(line, hostCt);
            return true;
        }

        var command = SlashCommandParser.Parse(line);
        switch (command.Kind)
        {
            case SlashCommandKind.Help:
                PrintHelp(command.Arg);
                return true;
            case SlashCommandKind.Quit:
                return false;
            case SlashCommandKind.NewSession:
                StartEphemeral();
                return true;
            case SlashCommandKind.Resume:
                await ResumeSessionAsync(command.Arg, hostCt);
                return true;
            case SlashCommandKind.Fork:
                await ForkSessionAsync(command.Arg, hostCt);
                return true;
            case SlashCommandKind.Model:
                SwitchModel(command.Arg);
                return true;
            case SlashCommandKind.Trust:
                await TrustProjectAsync(command.Arg, hostCt);
                return true;
            default:
                _out.WriteLine($"Unknown command: /{command.Name}", ConsoleColor.Red);
                return true;
        }
    }

    private void StartEphemeral()
    {
        var current = _agent?.State.Messages ?? [];
        _session = null;
        RebuildAgent(_provider, _model, current);
        _out.WriteLine("(new session: persistence disabled)");
    }

    private async Task ResumeSessionAsync(string? arg, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            var sessions = _manager.ListSessions(_cwd);
            if (sessions.Count == 0)
            {
                _out.WriteLine("No saved sessions for this directory.", ConsoleColor.DarkGray);
                return;
            }

            for (var i = 0; i < sessions.Count; i++)
                _out.WriteLine($"  {i + 1}. {sessions[i].SessionId}  ({sessions[i].EntryCount} entries, {AgeOf(sessions[i].LastWriteUtc)})");

            var picked = await ReadLineAsync($"pick (1-{sessions.Count} or id) [q to cancel] > ");
            if (picked is null || picked.Trim().ToLowerInvariant() == "q")
                return;

            var selection = picked.Trim();
            if (int.TryParse(selection, out var index) && index >= 1 && index <= sessions.Count)
                selection = sessions[index - 1].SessionId;
            arg = selection;
        }

        try
        {
            var session = await _manager.LoadSessionAsync(_cwd, arg, ct);
            _session = session;
            RebuildAgent(_provider, _model, session.ToMessages());
            _out.WriteLine($"session {session.Id} ({session.Entries.Count} entries)");
        }
        catch (SessionResolutionException ex)
        {
            _out.WriteLine(ex.Message, ConsoleColor.Red);
        }
    }

    private async Task ForkSessionAsync(string? arg, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            _out.WriteLine("Usage: /fork <session id|path>", ConsoleColor.Red);
            return;
        }

        try
        {
            var fork = await _manager.ForkAsync(_cwd, arg, ct: ct);
            _session = fork;
            RebuildAgent(_provider, _model, fork.ToMessages());
            _out.WriteLine($"[fork] session {fork.Id}");
        }
        catch (SessionResolutionException ex)
        {
            _out.WriteLine(ex.Message, ConsoleColor.Red);
        }
    }

    private void SwitchModel(string? arg)
    {
        var parts = (arg ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || _chat.GetModel(parts[0], parts[1]) is null)
        {
            _out.WriteLine($"Unknown model: {arg ?? string.Empty}", ConsoleColor.Red);
            return;
        }

        _provider = parts[0];
        _model = parts[1];
        RebuildAgent(_provider, _model, _agent?.State.Messages ?? []);
        _out.WriteLine($"(model: {_provider}/{_model})");
    }

    private async Task TrustProjectAsync(string? arg, CancellationToken ct)
    {
        if (_permissions is null)
        {
            _out.WriteLine("Permissions are not enabled.", ConsoleColor.Red);
            return;
        }

        try
        {
            var turningOff = arg is not null && arg.Trim().Equals("off", StringComparison.OrdinalIgnoreCase);
            var changed = turningOff
                ? await TrustStore.UntrustAsync(_cwd, ct)
                : await TrustStore.TrustAsync(_cwd, ct);
            _permissions.SetProjectRules(await LoadProjectRulesAsync(ct));

            if (turningOff)
                _out.WriteLine(changed ? "(project no longer trusted)" : "(project was not trusted)");
            else
                _out.WriteLine(changed
                    ? "(project trusted: bash allowed without prompt)"
                    : "(project already trusted)");
        }
        catch (PermissionConfigException ex)
        {
            _out.WriteLine(ex.Message, ConsoleColor.Red);
        }
    }

    private async Task<IReadOnlyList<PermissionRule>> LoadProjectRulesAsync(CancellationToken ct)
    {
        var path = PermissionPaths.ProjectConfigPath(_cwd);
        if (!File.Exists(path))
            return [];
        return (await PermissionConfig.LoadAsync(path, ct)).Rules;
    }

    private async Task RunTurnAsync(string prompt, CancellationToken hostCt)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(hostCt);
        _activeTurn = cts;
        _turnCancellation?.Invoke(cts);
        try
        {
            var result = await _runner!.RunAsync(prompt, cts.Token);
            if (result.Aborted)
                _out.WriteLine("(cancelled)", ConsoleColor.DarkGray);
            else if (!result.Ok)
                _out.WriteLine($"Error: {result.Error}", ConsoleColor.Red);
            else if (result.NewMessageCount > 0)
                _out.WriteLine($"[saved {result.NewMessageCount} message(s) to session {result.SessionId}]", ConsoleColor.DarkGray);
        }
        finally
        {
            _activeTurn = null;
        }
    }

    private async Task<string?> ReadLineAsync(string prompt)
    {
        var editor = new LineEditor();
        while (true)
        {
            _out.RenderEditorLine(prompt, editor.Buffer, editor.Caret);
            var key = _input.ReadKey();
            if (key is null)
                return null;
            if (key.Value.Modifiers.HasFlag(ConsoleModifiers.Control) && key.Value.Key == ConsoleKey.C)
            {
                editor.Reset();
                _out.WriteLine("^C");
                return null;
            }
            editor.Apply(key.Value);
            if (!editor.Complete)
                continue;
            return editor.Submit();
        }
    }

    private void PrintHelp(string? arg)
    {
        if (!string.IsNullOrWhiteSpace(arg))
        {
            var named = arg.TrimStart('/').ToLowerInvariant() switch
            {
                "help" => "/help [command]  — show this help",
                "quit" or "exit" => "/quit, /exit  — exit the session",
                "new" => "/new  — start a fresh ephemeral session",
                "resume" => "/resume [id|path]  — resume a saved session",
                "fork" => "/fork <id|path>  — fork a session and continue it",
                "model" => "/model <provider>/<id>  — switch models",
                "trust" => "/trust [off]  — trust this project (bash without prompt) / untrust",
                "unknown" => "unknown command (try /help)",
                _ => null
            };
            _out.WriteLine(named ?? $"Unknown command: /{arg}", named is null ? ConsoleColor.Red : (ConsoleColor?)null);
            return;
        }

        _out.WriteLine("Commands:");
        _out.WriteLine("  /help [command]        show this help");
        _out.WriteLine("  /quit, /exit           exit the session");
        _out.WriteLine("  /new                   start a fresh ephemeral session");
        _out.WriteLine("  /resume [id|path]      resume a saved session");
        _out.WriteLine("  /fork <id|path>        fork a session and continue it");
        _out.WriteLine("  /model <provider>/<id> switch models");
        _out.WriteLine("  /trust [off]           trust this project (bash without prompt) / untrust");
    }

    private static string AgeOf(DateTime utc)
    {
        var age = DateTime.UtcNow - utc;
        return age.TotalMinutes < 1 ? "just now"
            : age.TotalHours < 1 ? $"{(int)age.TotalMinutes}m ago"
            : age.TotalDays < 1 ? $"{(int)age.TotalHours}h ago"
            : $"{(int)age.TotalDays}d ago";
    }
}