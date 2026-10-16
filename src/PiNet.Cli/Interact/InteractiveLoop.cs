using System.Text;
using System.Text.RegularExpressions;
using PiNet.Abstractions.Chat;
using PiNet.AgentCore;
using PiNet.AgentCore.Tools;
using PiNet.Cli.Extensions;
using PiNet.Cli.Settings;
using PiNet.Extensions;
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
    private readonly InputArbiter? _inputArbiter;
    private readonly Action<CancellationTokenSource>? _turnCancellation;
    private readonly IPermissionResolver? _permissions;
    private readonly IPermissionPrompt? _permissionPrompt;
    private readonly ExtensionHost? _extensions;
    private readonly SkillRegistry? _skills;
    private readonly IReadOnlyList<PromptTemplate> _templates;
    private readonly string? _systemPrompt;
    private readonly bool _autoCompact;
    private readonly double _compactThreshold;
    private readonly int _compactPreserveRecent;
    private readonly IReadOnlyList<AgentTool> _agentTools;
    private readonly List<string> _extensionWarnings;
    private readonly EffectiveSettings? _settings;
    private readonly bool _verbose;

    // Mutable so /thinking can change it mid-session; survives model switches.
    private ThinkingLevel _thinkingLevel;

    private readonly LineEditor _editor = new();
    private readonly LineHistory _history = new();

    private string _provider;
    private string _model;
    private Session? _session;
    private Agent? _agent;
    private TurnRunner? _runner;
    private ContextCompactor? _compactor;
    private CancellationTokenSource? _activeTurn;
    private LineState _renderState = LineState.Empty;

    private string SystemPromptText => _systemPrompt ?? CliConfig.DefaultSystemPrompt;

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
        IPermissionPrompt? permissionPrompt = null,
        ExtensionHost? extensions = null,
        SkillRegistry? skills = null,
        IReadOnlyList<PromptTemplate>? templates = null,
        string? systemPrompt = null,
        bool autoCompact = false,
        double compactThreshold = 0.75,
        int compactPreserveRecent = 4,
        EffectiveSettings? settings = null,
        ThinkingLevel? thinkingLevel = null,
        InputArbiter? inputArbiter = null)
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
        _extensions = extensions;
        _skills = skills;
        _templates = templates ?? [];
        _systemPrompt = systemPrompt;
        _autoCompact = autoCompact;
        _compactThreshold = compactThreshold;
        _compactPreserveRecent = compactPreserveRecent;
        _extensionWarnings = [];
        _settings = settings;
        _thinkingLevel = thinkingLevel ?? settings?.ThinkingLevel ?? ThinkingLevel.Off;
        _verbose = settings?.Verbose ?? false;
        _inputArbiter = inputArbiter;
        _agentTools = AdaptExtensionTools();
    }

    private bool NoSession { get; }
    private string? SessionRef { get; }
    private string? ForkRef { get; }
    private bool ContinueLatest { get; }

    private IReadOnlyList<AgentTool> AdaptExtensionTools()
    {
        if (_extensions is null || _extensions.Tools.Count == 0)
            return [];
        var builtInNames = BuiltInTools.All().Select(t => t.Name).ToArray();
        var warnings = new List<string>();
        var tools = ExtensionToolAdapter.BuildAll(builtInNames, _extensions.Tools, warnings);
        var list = (List<string>)_extensionWarnings;
        list.AddRange(warnings);
        return tools;
    }

    public void CancelTurn() => _activeTurn?.Cancel();

    public async Task<int> RunAsync(CancellationToken hostCt = default)
    {
        var seed = await StartupAsync(hostCt);
        RebuildAgent(_provider, _model, seed);
        PrintWarnings();

        if (_verbose)
        {
            var thinking = _agent?.State.ThinkingLevel ?? ThinkingLevel.Off;
            _out.WriteLine($"[verbose] {_provider}/{_model} · thinking={thinking}", ConsoleColor.DarkGray);
            if (_settings is not null)
                _out.WriteLine($"[verbose] settings: global={_settings.GlobalPath} project={_settings.ProjectPath}", ConsoleColor.DarkGray);
        }

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

        var tools = new List<AgentTool>(BuiltInTools.All());
        tools.AddRange(_agentTools);

        var systemPrompt = SystemPromptText;
        var contextWindow = _chat.GetModel(providerId, modelId)?.ContextWindow ?? 0;

        var settings = new CompactionSettings
        {
            ContextWindow = contextWindow,
            Threshold = _compactThreshold,
            PreserveRecentMessages = _compactPreserveRecent
        };

        // Preserve compaction state across a rebuild only when the live context is the
        // same in-memory message list (a mid-session model switch, or an ephemeral
        // toggle). When the context is reloaded from a session, the previous context's
        // summary no longer applies, so start fresh.
        var keepCompactorState = _compactor is not null &&
            ReferenceEquals(messages, _agent?.State.Messages);
        var existingSummary = _compactor?.Summary;
        var existingCount = _compactor?.SummarizedCount ?? 0;

        _compactor = keepCompactorState
            ? new ContextCompactor(
                new SessionCompactor(_chat, providerId, modelId),
                settings,
                existingSummary,
                existingCount)
            : new ContextCompactor(new SessionCompactor(_chat, providerId, modelId), settings);
        var compactor = _compactor;

        _agent = new Agent(new AgentOptions
        {
            Runner = _chat,
            // 7d — history blocks the target provider cannot accept (e.g. thinking
            // blocks handed from Anthropic to OpenAI) are normalized away on the
            // Llm-bound copy; the persisted session keeps the originals.
            ConvertToLlm = msgs => MessageNormalizer.ForProvider(msgs, providerId),
            TransformContext = _autoCompact
                ? (msgs, ct) =>
                {
                    compactor.LastPromptTokens = _agent?.State.LastUsage is { } u ? u.InputTokens : null;
                    return compactor.TransformAsync(msgs, systemPrompt, ct);
                }
                : null,
            BeforeToolCall = _permissions is not null && _permissionPrompt is not null
                ? PermissionGate.Create(_permissions, _permissionPrompt, _cwd)
                : null,
            InitialState = new AgentState
            {
                SystemPrompt = systemPrompt,
                ProviderId = providerId,
                ModelId = modelId,
                Tools = tools,
                Messages = new List<ChatMessage>(messages),
                // Set by --thinking / /thinking (or the settings default, spec 7g).
                ThinkingLevel = _thinkingLevel
            }
        });

        _runner = new TurnRunner(_agent, _cwd, _manager, NoSession);
        if (_session is not null)
        {
            _runner.AttachSession(_session);
            // The in-memory context may be a compacted view whose length differs from
            // the persisted session entry count; the watermark must index the live
            // context, not the full history.
            _runner.MarkPersisted(_agent.State.Messages.Count);
        }
        _runner.AgentEventSink += RenderEventAsync;
    }

    private void PrintWarnings()
    {
        if (_extensions is not null)
            foreach (var warning in _extensions.Warnings)
                _out.WriteLine(warning, ConsoleColor.DarkGray);
        foreach (var warning in _extensionWarnings)
            _out.WriteLine(warning, ConsoleColor.DarkGray);
        if (_skills is not null)
            foreach (var warning in _skills.Warnings)
                _out.WriteLine(warning, ConsoleColor.DarkGray);
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
            case SlashCommandKind.Compact:
                await CompactAsync(command.Arg, hostCt);
                return true;
            case SlashCommandKind.Settings:
                await SettingsCommandAsync(command.Arg, hostCt);
                return true;
            case SlashCommandKind.Thinking:
                ThinkingCommand(command.Arg);
                return true;
            case SlashCommandKind.Export:
                await ExportCommandAsync(command.Arg, hostCt);
                return true;
            case SlashCommandKind.Import:
                await ImportCommandAsync(command.Arg, hostCt);
                return true;
            case SlashCommandKind.Skill:
                await DispatchSkillAsync(command.Name, command.Arg, hostCt);
                return true;
            default:
                return await DispatchExtensionishAsync(command.Name, command.Arg, hostCt);
        }
    }

    private async Task DispatchSkillAsync(string name, string? args, CancellationToken hostCt)
    {
        if (_skills is null)
        {
            _out.WriteLine("Skills are disabled.", ConsoleColor.Red);
            return;
        }
        var skill = _skills.Find(name);
        if (skill is null)
        {
            _out.WriteLine($"Unknown skill: {name} (see /help)", ConsoleColor.Red);
            return;
        }

        var body = SkillFrontmatter.ReadBody(skill.Path);
        var prompt = $"[Skill: {skill.Name} — {skill.Path}]{Environment.NewLine}{body}";
        if (!string.IsNullOrWhiteSpace(args))
            prompt += $"{Environment.NewLine}{Environment.NewLine}User: {args}";
        await RunTurnAsync(prompt, hostCt);
    }

    private async Task<bool> DispatchExtensionishAsync(string name, string? args, CancellationToken hostCt)
    {
        if (_extensions is not null && _extensions.Commands.TryGetValue(name, out var handler))
        {
            var queued = new List<string>();
            var ctx = new LoopCommandContext(_out, queued.Add);
            await handler(args ?? string.Empty, ctx);
            foreach (var prompt in queued)
                await RunTurnAsync(prompt, hostCt);
            return true;
        }

        var template = _templates.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        if (template is not null)
        {
            var expanded = TemplateExpander.Expand(template, SplitArgs(args));
            _editor.SetBuffer(expanded);
            _out.WriteLine($"(template /{template.Name}: press Enter to send)", ConsoleColor.DarkGray);
            return true;
        }

        _out.WriteLine($"Unknown command: /{name} (see /help)", ConsoleColor.Red);
        return true;
    }

    private static IReadOnlyList<string> SplitArgs(string? args) =>
        string.IsNullOrWhiteSpace(args) ? [] : args.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary> /settings (view) or /settings key value (edit the global file). </summary>
    private async Task SettingsCommandAsync(string? arg, CancellationToken hostCt)
    {
        if (_settings is null)
        {
            _out.WriteLine("No settings loaded.", ConsoleColor.Red);
            return;
        }

        if (string.IsNullOrWhiteSpace(arg))
        {
            PrintSettingsSummary();
            return;
        }

        var parts = SplitArgs(arg);
        if (parts.Count < 2)
        {
            _out.WriteLine("Usage: /settings key value   (or /settings to view)");
            return;
        }

        var key = parts[0];
        var value = parts[1];
        var warning = SettingsManager.SetGlobal(key, value);
        if (warning is not null)
            _out.WriteLine($"[settings] {warning}", ConsoleColor.Red);
        else
            _out.WriteLine($"(global {key} = {value} -> {_settings.GlobalPath})");
    }

    private void PrintSettingsSummary()
    {
        var s = _settings;
        if (s is null)
        {
            _out.WriteLine("No settings loaded.", ConsoleColor.Red);
            return;
        }

        _out.WriteLine("Effective settings (project wins over global):");
        _out.WriteLine($"  provider       {(s.Provider is null ? "openai" : s.Provider)}    [{SourceOf(s.Project?.Provider, s.Global?.Provider)}]");
        _out.WriteLine($"  model          {(s.Model is null ? "gpt-4o-mini" : s.Model)}    [{SourceOf(s.Project?.Model, s.Global?.Model)}]");
        _out.WriteLine($"  thinking       {(s.ThinkingLevel is null ? "Off" : s.ThinkingLevel)}    [{SourceOf(s.Project?.ThinkingLevel, s.Global?.ThinkingLevel)}]");
        _out.WriteLine($"  compact        threshold {s.CompactThreshold:F2} / keep {s.CompactPreserveRecent}    [{SourceOf(s.Project?.CompactThreshold, s.Global?.CompactThreshold)}]");
        _out.WriteLine($"  autoCompact    {(s.AutoCompact ? "on" : "off")}    [{SourceOf(s.Project?.AutoCompact, s.Global?.AutoCompact)}]");
        _out.WriteLine($"  verbose        {(s.Verbose ? "on" : "off")}    [{SourceOf(s.Project?.Verbose, s.Global?.Verbose)}]");
        _out.WriteLine($"  global    {s.GlobalPath}{(s.Global is null ? "  (missing)" : "")}");
        _out.WriteLine($"  project   {s.ProjectPath}{(s.Project is null ? "  (missing)" : "")}");
        foreach (var warning in s.Warnings)
            _out.WriteLine($"  ! {warning}", ConsoleColor.Red);
    }

    private static string SourceOf<T>(T? project, T? global) =>
        project is not null ? "project" : global is not null ? "global" : "default";

    /// <summary>/thinking [level] — view or set the reasoning level for subsequent requests.</summary>
    private void ThinkingCommand(string? arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            _out.WriteLine($"(thinking: {_thinkingLevel})");
            return;
        }

        if (!Enum.TryParse<ThinkingLevel>(arg.Trim(), ignoreCase: true, out var level))
        {
            _out.WriteLine(
                $"Unknown thinking level '{arg}' (use Off, Minimal, Low, Medium, High, XHigh, Max).",
                ConsoleColor.Red);
            return;
        }

        _thinkingLevel = level;
        if (_agent is not null)
            _agent.State.ThinkingLevel = level;
        _out.WriteLine($"(thinking: {level})");
    }

    /// <summary>7f — /export jsonl|html &lt;path&gt;: JSONL = all entries, HTML = active branch.</summary>
    private async Task ExportCommandAsync(string? arg, CancellationToken ct)
    {
        var parts = (arg ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || parts[0].ToLowerInvariant() is not ("jsonl" or "html"))
        {
            _out.WriteLine("Usage: /export jsonl|html <path>", ConsoleColor.Red);
            return;
        }

        var format = parts[0].ToLowerInvariant();
        var session = CurrentSessionSnapshot();
        if (session is null)
        {
            _out.WriteLine("[export] nothing to export yet.", ConsoleColor.DarkGray);
            return;
        }

        var path = parts.Length > 1
            ? string.Join(' ', parts[1..]).Trim('"')
            : $"pinet-{session.Id}.{format}";
        if (!Path.IsPathRooted(path))
            path = Path.Combine(_cwd, path);

        try
        {
            if (format == "jsonl")
                await SessionExport.ExportJsonlAsync(session, path, ct);
            else
                await SessionExport.ExportHtmlAsync(session, path, ct);
            _out.WriteLine($"[export] {session.Entries.Count} entries → {path}");
        }
        catch (Exception ex)
        {
            _out.WriteLine($"[export] {ex.Message}", ConsoleColor.Red);
        }
    }

    /// <summary>7f — /import &lt;path.jsonl&gt;: loads the file into a new session and continues it.</summary>
    private async Task ImportCommandAsync(string? arg, CancellationToken ct)
    {
        var path = (arg ?? string.Empty).Trim().Trim('"');
        if (path.Length == 0)
        {
            _out.WriteLine("Usage: /import <path.jsonl>", ConsoleColor.Red);
            return;
        }
        if (!Path.IsPathRooted(path))
            path = Path.Combine(_cwd, path);
        if (!File.Exists(path))
        {
            _out.WriteLine($"[import] file not found: {path}", ConsoleColor.Red);
            return;
        }

        try
        {
            var imported = await SessionExport.ImportJsonlAsync(path, ct);
            if (imported.Entries.Count == 0)
            {
                _out.WriteLine($"[import] no entries found in {path}.", ConsoleColor.Red);
                return;
            }

            // Persist under a fresh id so future turns append to the imported session.
            _session = imported;
            await _manager.SaveAsync(imported, _cwd, ct);
            RebuildAgent(_provider, _model, imported.ToMessages());
            _out.WriteLine($"[import] session {imported.Id} ({imported.Entries.Count} entries) ← {path}");
        }
        catch (Exception ex)
        {
            // Malformed files fail cleanly: the current session is left untouched.
            _out.WriteLine($"[import] {ex.Message}", ConsoleColor.Red);
        }
    }

    /// <summary>Active session, or a snapshot of the live (ephemeral) context.</summary>
    private Session? CurrentSessionSnapshot()
    {
        if (_session is not null)
            return _session;
        var messages = _agent?.State.Messages;
        if (messages is null || messages.Count == 0)
            return null;
        var snapshot = new Session { Id = SessionId.New() };
        foreach (var message in messages)
            snapshot.AppendMessage(message);
        return snapshot;
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
        var trimmed = (arg ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            // Show available models grouped by provider
            var models = _chat.GetModels();
            var grouped = models.GroupBy(m => m.Provider, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key);
            _out.WriteLine("Available models (/model provider/id):");
            foreach (var g in grouped)
            {
                _out.WriteLine($"  [{g.Key}]");
                foreach (var m in g.OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase))
                    _out.WriteLine($"    {m.Id}  ({m.Name})");
            }
            return;
        }

        var parts = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            _out.WriteLine($"Usage: /model <provider>/<id> (or /model to list)", ConsoleColor.Red);
            return;
        }

        var provider = parts[0];
        var model = parts[1];
        if (_chat.GetModel(provider, model) is null)
        {
            _out.WriteLine($"Unknown model: {provider}/{model}", ConsoleColor.Red);
            return;
        }

        _provider = provider;
        _model = model;
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

    private async Task CompactAsync(string? arg, CancellationToken ct)
    {
        if (_compactor is null || _agent is null)
        {
            _out.WriteLine("Compact is not available.", ConsoleColor.Red);
            return;
        }

        try
        {
            var messages = _agent.State.Messages;
            var outcome = await _compactor.CompactAsync(messages, SystemPromptText, arg, force: true, ct);
            if (!outcome.Compacted)
            {
                _out.WriteLine("(context already compact)", ConsoleColor.DarkGray);
                return;
            }

            // Persist the summary as a new, additive session entry so the compaction
            // survives restarts; the full original history stays in the JSONL file.
            if (_session is not null)
            {
                if (_compactor.Summary is { } summary)
                    _session.AppendMessage(summary);
                await _manager.AppendAsync(_session, _cwd, ct);
            }

            _agent.State.Messages = new List<ChatMessage>(outcome.Messages).AsReadOnly();
            _runner?.MarkPersisted(outcome.Messages.Count);
            _compactor.Reset();

            _out.WriteLine($"(compacted {outcome.Summarized} message(s) into a summary)", ConsoleColor.DarkGray);
        }
        catch (Exception ex)
        {
            _out.WriteLine($"Compact failed: {ex.Message}", ConsoleColor.Red);
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
        // 7a.2 — live terminals capture keystrokes while the turn is in flight
        // (Enter → steer, Alt+Enter → follow-up); scripted/redirected input keeps
        // its sequential semantics.
        var pump = _input.Interactive ? PumpTurnInputAsync(cts.Token) : null;
        try
        {
            // 7e — @path tokens become image attachments; unresolvable tokens stay as text.
            var (text, images) = await ExpandImagesAsync(prompt);
            images = VisionFilter(images);
            var result = images.Count == 0
                ? await _runner!.RunAsync(text, cts.Token)
                : await _runner!.RunAsync(ImageMessage(text, images), cts.Token);

            if (result.Aborted)
                _out.WriteLine("(cancelled)", ConsoleColor.DarkGray);
            else if (!result.Ok)
                _out.WriteLine($"Error: {result.Error}", ConsoleColor.Red);
            else if (result.NewMessageCount > 0)
                _out.WriteLine($"[saved {result.NewMessageCount} message(s) to session {result.SessionId}]", ConsoleColor.DarkGray);
        }
        finally
        {
            cts.Cancel();
            if (pump is not null)
            {
                // Text typed but not submitted before the turn ended goes back to
                // the editor so it is not lost.
                var leftover = await pump;
                if (leftover.Length > 0)
                    _editor.SetBuffer(leftover);
            }
            _activeTurn = null;
        }
    }

    /// <summary>
    /// 7a.2 — reads keystrokes while a turn is in flight: Enter enqueues a
    /// steering message, Alt+Enter a follow-up, Ctrl+C cancels the turn. Pauses
    /// while an <see cref="InputArbiter"/> scope (permission prompt) owns the
    /// input. Returns whatever was typed but not submitted when the turn ended.
    /// </summary>
    private async Task<string> PumpTurnInputAsync(CancellationToken ct)
    {
        var buffer = new StringBuilder();
        while (!ct.IsCancellationRequested)
        {
            if (_inputArbiter is { IsBusy: true } || !_input.KeyAvailable)
            {
                try
                {
                    await Task.Delay(TurnInputPollMs, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                continue;
            }

            var key = _input.ReadKey();
            if (key is null)
                break;

            var value = key.Value;
            if (value.Modifiers.HasFlag(ConsoleModifiers.Control) && value.Key == ConsoleKey.C)
            {
                buffer.Clear();
                _activeTurn?.Cancel();
                continue;
            }
            if (value.Key == ConsoleKey.Backspace)
            {
                if (buffer.Length > 0)
                    buffer.Length--;
                continue;
            }
            if (value.Key == ConsoleKey.Enter)
            {
                var line = buffer.ToString().Trim();
                buffer.Clear();
                if (line.Length == 0)
                    continue;
                if (value.Modifiers.HasFlag(ConsoleModifiers.Alt))
                {
                    _agent?.FollowUp(line);
                    _out.WriteLine($"(follow-up: {line})", ConsoleColor.DarkGray);
                }
                else
                {
                    _agent?.Steer(line);
                    _out.WriteLine($"(steering: {line})", ConsoleColor.DarkGray);
                }
                continue;
            }
            if (!value.Modifiers.HasFlag(ConsoleModifiers.Control) &&
                value.KeyChar >= 0x20 && value.KeyChar != 0x7f)
                buffer.Append(value.KeyChar);
        }

        return buffer.ToString();
    }

    private const int TurnInputPollMs = 15;

    /// <summary>7e — user message carrying text (when present) plus image attachments.</summary>
    private static ChatMessage ImageMessage(string text, List<ImageContent> images)
    {
        var content = new List<ContentBlock>(images.Count + 1);
        if (text.Length > 0)
            content.Add(new TextContent(text));
        content.AddRange(images);
        return ChatMessage.User(content);
    }

    /// <summary>7e — drops attachments when the active model cannot accept images.</summary>
    private List<ImageContent> VisionFilter(List<ImageContent> images)
    {
        if (images.Count == 0 || _chat.GetModel(_provider, _model)?.SupportsVision == true)
            return images;
        _out.WriteLine(
            $"[image] {_provider}/{_model} does not accept image input; {images.Count} attachment(s) skipped.",
            ConsoleColor.Yellow);
        return [];
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
                "compact" => "/compact [instructions]  — manually compact the session context",
                "thinking" => "/thinking <level>  — set the reasoning level (Off/Minimal/Low/Medium/High/XHigh/Max)",
                "export" => "/export jsonl|html <path>  — write the session to a file",
                "import" => "/import <path.jsonl>  — load a session file and continue it",
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
        _out.WriteLine("  /compact [instructions] manually compact the session context");
        _out.WriteLine("  /thinking <level>      set the reasoning level (Off/Minimal/Low/Medium/High/XHigh/Max)");
        _out.WriteLine("  /export jsonl|html <path> export the session (all entries / active branch)");
        _out.WriteLine("  /import <path.jsonl>   import a session file and continue it");

        if (_extensions is not null && _extensions.Commands.Count > 0)
        {
            _out.WriteLine("Extension commands:");
            foreach (var name in _extensions.Commands.Keys.OrderBy(k => k, StringComparer.Ordinal))
                _out.WriteLine($"  /{name}                  [extension]");
        }

        if (_skills is not null && _skills.Skills.Count > 0)
        {
            _out.WriteLine("Skills:");
            foreach (var skill in _skills.Skills.OrderBy(s => s.Name, StringComparer.Ordinal))
                _out.WriteLine($"  /skill:{skill.Name}        {skill.Description}  [skill]");
        }

        if (_templates.Count > 0)
        {
            _out.WriteLine("Templates:");
            foreach (var template in _templates.OrderBy(t => t.Name, StringComparer.Ordinal))
                _out.WriteLine($"  /{template.Name} {template.ArgumentHint}  {template.Description}  [template]");
        }
    }

    private sealed class LoopCommandContext(IConsoleOutput output, Action<string> queue) : IExtensionCommandContext
    {
        public void WriteLine(string text) => output.WriteLine(text);
        public Task SendToAgentAsync(string prompt) =>
            Task.Run(() => queue(prompt));
    }

    private static string AgeOf(DateTime utc)
    {
        var age = DateTime.UtcNow - utc;
        return age.TotalMinutes < 1 ? "just now"
            : age.TotalHours < 1 ? $"{(int)age.TotalMinutes}m ago"
            : age.TotalDays < 1 ? $"{(int)age.TotalHours}h ago"
            : $"{(int)age.TotalDays}d ago";
    }

    private static string InferMime(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".svg" => "image/svg+xml",
            _ => "application/octet-stream"
        };
    }

    /// <summary>
    /// 7e — expands <c>@path.png</c> tokens into base64 <see cref="ImageContent"/>
    /// attachments (paths resolved against the session cwd). The prompt text is
    /// preserved verbatim except for the extracted tokens; unresolvable tokens
    /// stay in the text unchanged.
    /// </summary>
    private async Task<(string Text, List<ImageContent> Images)> ExpandImagesAsync(string text)
    {
        var images = new List<ImageContent>();
        if (string.IsNullOrEmpty(text))
            return (text ?? string.Empty, images);

        var result = new StringBuilder();
        var last = 0;
        foreach (Match match in ImageTokenRegex.Matches(text))
        {
            var path = match.Groups[1].Value;
            var full = Path.IsPathRooted(path) ? path : Path.Combine(_cwd, path);
            ImageContent? image = null;
            try
            {
                if (File.Exists(full))
                {
                    var bytes = await File.ReadAllBytesAsync(full);
                    image = new ImageContent(Convert.ToBase64String(bytes), InferMime(full));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _out.WriteLine($"[image] {path}: {ex.Message} — sending as text.", ConsoleColor.Yellow);
            }

            if (image is null)
                continue; // unresolvable: the token stays in the text untouched

            AppendSegment(result, text, last, match.Index);
            last = match.Index + match.Length;
            images.Add(image);
        }

        if (images.Count == 0)
            return (text, images);

        AppendSegment(result, text, last, text.Length);
        return (result.ToString().Trim(), images);
    }

    private static readonly Regex ImageTokenRegex = new(
        @"@(\S+?\.(?:png|jpg|jpeg|gif|webp|bmp|svg))(?=\s|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Appends <c>text[start..end]</c>, avoiding doubled whitespace at extraction seams.</summary>
    private static void AppendSegment(StringBuilder sb, string text, int start, int end)
    {
        if (sb.Length > 0 && char.IsWhiteSpace(sb[^1]))
            while (start < end && char.IsWhiteSpace(text[start]))
                start++;
        sb.Append(text, start, end - start);
    }
}
