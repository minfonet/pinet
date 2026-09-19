using System.Text;
using PiNet.Abstractions.Streaming;
using PiNet.AgentCore;
using PiNet.AgentCore.Tools;
using PiNet.Cli.Interact;
using PiNet.Permissions;
using PiNet.Providers;
using PiNet.Sessions;

namespace PiNet.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
            return RunAsync(args).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RunAsync(string[] args)
    {
        var providerId = "openai";
        var modelId = "gpt-4o-mini";
        var printMode = false;
        var agentMode = false;
        var interactive = false;
        string? sessionRef = null;
        string? forkRef = null;
        var continueLatest = false;
        var noSession = false;
        string? apiKeyOverride = null;
        var approve = false;
        var noApprove = false;
        var prompts = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--provider" when i + 1 < args.Length:
                    providerId = args[++i];
                    break;
                case "--model" when i + 1 < args.Length:
                    modelId = args[++i];
                    break;
                case "-p" or "--print":
                    printMode = true;
                    break;
                case "--agent":
                    agentMode = true;
                    break;
                case "--session" when i + 1 < args.Length:
                    sessionRef = args[++i];
                    break;
                case "--interactive" or "-i":
                    interactive = true;
                    break;
                case "--fork" when i + 1 < args.Length:
                    forkRef = args[++i];
                    break;
                case "-c" or "--continue":
                    continueLatest = true;
                    break;
                case "--no-session":
                    noSession = true;
                    break;
                case "--api-key" when i + 1 < args.Length:
                    apiKeyOverride = args[++i];
                    break;
                case "--approve":
                    approve = true;
                    break;
                case "--no-approve":
                    noApprove = true;
                    break;
                case "--list-models":
                    ListModels();
                    return 0;
                case "--help" or "-h":
                    PrintHelp();
                    return 0;
                default:
                    prompts.Add(args[i]);
                    break;
            }
        }

        var prompt = string.Join(' ', prompts);

        if (interactive)
        {
            if (Console.IsInputRedirected)
            {
                Console.Error.WriteLine("Interactive mode requires an interactive terminal.");
                return 1;
            }
            return await InteractiveModeAsync(providerId, modelId, sessionRef, forkRef, continueLatest, noSession, approve, noApprove);
        }

        if (!Console.IsInputRedirected && string.IsNullOrWhiteSpace(prompt))
        {
            PrintHelp();
            return 0;
        }

        if (Console.IsInputRedirected)
        {
            var stdin = await Console.In.ReadToEndAsync();
            if (!string.IsNullOrWhiteSpace(stdin))
                prompt = string.IsNullOrWhiteSpace(prompt) ? stdin.TrimEnd() : $"{stdin.TrimEnd()}\n\n{prompt}";
        }

        if (string.IsNullOrWhiteSpace(prompt))
        {
            Console.Error.WriteLine("No prompt provided.");
            return 1;
        }

        if (printMode)
            return await PrintModeAsync(providerId, modelId, prompt, apiKeyOverride);

        if (agentMode)
            return await AgentModeAsync(providerId, modelId, prompt, apiKeyOverride, sessionRef, forkRef, continueLatest, noSession, approve, noApprove);

        // No mode flag: default to one-shot print mode.
        return await PrintModeAsync(providerId, modelId, prompt, apiKeyOverride);
    }

    private static async Task<int> PrintModeAsync(string providerId, string modelId, string prompt, string? apiKeyOverride)
    {
        var registry = DefaultModelRegistry.Create();
        var chat = new ChatService(registry);

        var model = chat.GetModel(providerId, modelId);
        if (model is null)
        {
            Console.Error.WriteLine($"Unknown model: {providerId}/{modelId}. Available: {string.Join(", ", chat.GetModels().Select(m => $"{m.Provider}/{m.Id}"))}");
            return 1;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        var messages = new List<PiNet.Abstractions.Chat.ChatMessage>
        {
            PiNet.Abstractions.Chat.ChatMessage.User(prompt)
        };

        await foreach (var evt in chat.GenerateAsync(providerId, modelId, CliConfig.DefaultSystemPrompt, messages, ct: cts.Token))
        {
            switch (evt)
            {
                case TextDelta t:
                    Console.Write(t.Delta);
                    break;
                case ThinkingDelta th:
                    Console.Write($"\n[{th.Delta}]");
                    break;
                case ToolCallStart tc:
                    Console.Write($"\n[Tool: {tc.Name}] ");
                    break;
                case ToolCallDelta td:
                    Console.Write(td.ArgumentsJsonFragment);
                    break;
                case ToolCallEnd tce:
                    Console.Write($"\n");
                    break;
                case StreamEnd end:
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.Write($"\n\nTokens: ↑{end.Usage.InputTokens} ↓{end.Usage.OutputTokens}");
                    if (end.Usage.CacheReadTokens > 0) Console.Write($" R{end.Usage.CacheReadTokens}");
                    if (end.Usage.CacheWriteTokens > 0) Console.Write($" W{end.Usage.CacheWriteTokens}");
                    Console.Write($" | Cost: ${end.Usage.Cost.Total:F4}");
                    Console.ResetColor();
                    Console.WriteLine();
                    break;
                case StreamError err:
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.Error.WriteLine($"\nError: {err.Message}");
                    Console.ResetColor();
                    return err.IsAborted ? 130 : 1;
            }
        }

        return 0;
    }

    private static async Task<int> InteractiveModeAsync(
        string providerId,
        string modelId,
        string? sessionRef,
        string? forkRef,
        bool continueLatest,
        bool noSession,
        bool approve,
        bool noApprove)
    {
        var registry = DefaultModelRegistry.Create();
        var chat = new ChatService(registry);

        if (chat.GetModel(providerId, modelId) is null)
        {
            Console.Error.WriteLine($"Unknown model: {providerId}/{modelId}. Available: {string.Join(", ", chat.GetModels().Select(m => $"{m.Provider}/{m.Id}"))}");
            return 1;
        }

        using var cts = new CancellationTokenSource();
        var (resolver, permissionPrompt) = await BuildPermissionsAsync(approve, noApprove, interactive: true, cts.Token);
        var loop = new InteractiveLoop(
            chat,
            new SessionManager(),
            Environment.CurrentDirectory,
            providerId,
            modelId,
            new ConsoleInput(),
            new ConsoleOutput(),
            noSession,
            sessionRef,
            forkRef,
            continueLatest,
            turnCancellation: turnCts =>
            {
                Console.TreatControlCAsInput = false;
                Console.CancelKeyPress += (_, e) =>
                {
                    e.Cancel = true;
                    turnCts.Cancel();
                };
            },
            permissions: resolver,
            permissionPrompt: permissionPrompt);

        return await loop.RunAsync(cts.Token);
    }

    /// <summary>
    /// Builds the permission resolver + prompt for a run (spec 05 §3.7):
    /// --approve auto-approves; non-interactive stdin falls back to deny-all.
    /// </summary>
    private static async Task<(RulePermissionResolver, IPermissionPrompt)> BuildPermissionsAsync(
        bool approve, bool noApprove, bool interactive, CancellationToken ct)
    {
        var resolver = await RulePermissionResolver.LoadAsync(Environment.CurrentDirectory, ct);
        if (noApprove)
            resolver.AddSessionRule(new PermissionRule("bash", null, ToolPermission.Ask));

        IPermissionPrompt prompt = approve
            ? new AutoApprovePrompt()
            : interactive
                ? new ConsolePermissionPrompt(new ConsoleInput(), new ConsoleOutput())
                : new DenyAllPrompt(Console.Error);
        return (resolver, prompt);
    }

    private static async Task<int> AgentModeAsync(
        string providerId,
        string modelId,
        string prompt,
        string? apiKeyOverride,
        string? sessionRef,
        string? forkRef,
        bool continueLatest,
        bool noSession,
        bool approve,
        bool noApprove)
    {
        var registry = DefaultModelRegistry.Create();
        var chat = new ChatService(registry);

        var model = chat.GetModel(providerId, modelId);
        if (model is null)
        {
            Console.Error.WriteLine($"Unknown model: {providerId}/{modelId}. Available: {string.Join(", ", chat.GetModels().Select(m => $"{m.Provider}/{m.Id}"))}");
            return 1;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        var cwd = Environment.CurrentDirectory;
        var initialMessages = new List<PiNet.Abstractions.Chat.ChatMessage>();
        Session? session = null;
        SessionManager? manager = null;

        if (!noSession && (sessionRef is not null || continueLatest || forkRef is not null))
        {
            manager = new SessionManager();
            if (forkRef is not null)
            {
                session = await manager.ForkAsync(cwd, forkRef);
                Console.WriteLine($"[fork of {forkRef}] session {session.Id}");
            }
            else if (sessionRef is not null)
            {
                session = await manager.LoadSessionAsync(cwd, sessionRef);
                Console.WriteLine($"session {session.Id}");
            }
            else
            {
                session = await manager.LoadLatestAsync(cwd) ?? manager.CreateSession(cwd);
                Console.WriteLine($"session {session.Id}");
            }

            initialMessages.AddRange(session.ToMessages());
            Console.WriteLine($"[resumed with {initialMessages.Count} prior message(s)]");
        }

        var (resolver, permissionPrompt) = await BuildPermissionsAsync(approve, noApprove, interactive: !Console.IsInputRedirected, cts.Token);

        var agent = new Agent(new AgentOptions
        {
            Runner = chat,
            BeforeToolCall = PermissionGate.Create(resolver, permissionPrompt, cwd),
            InitialState = new AgentState
            {
                SystemPrompt = CliConfig.DefaultSystemPrompt,
                ProviderId = providerId,
                ModelId = modelId,
                Tools = BuiltInTools.All(),
                Messages = initialMessages
            }
        });

        using var runner = new TurnRunner(agent, cwd, manager ?? new SessionManager(), noSession);
        if (session is not null)
            runner.AttachSession(session);

        var renderState = LineState.Empty;
        runner.AgentEventSink += async (evt, _) =>
        {
            var step = AgentConsoleRenderer.Step(renderState, evt);
            renderState = step.State;
            if (step.Inline is not null)
                WriteColored(step.Inline.Text, step.Inline.Color);
            foreach (var completed in step.Completed)
                WriteColoredLine(completed.Text, completed.Color);
            await Task.CompletedTask;
        };

        var result = await runner.RunAsync(prompt, cts.Token);
        if (result.Aborted)
            return 130;
        if (!result.Ok)
        {
            Console.Error.WriteLine($"Error: {result.Error}");
            return 1;
        }

        if (result.NewMessageCount > 0 && result.SessionId is not null)
            Console.WriteLine($"[saved {result.NewMessageCount} new message(s) to session {result.SessionId}]");

        return 0;
    }

    private static void WriteColored(string text, ConsoleColor? color)
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

    private static void WriteColoredLine(string text, ConsoleColor? color)
    {
        WriteColored(text, color);
        Console.WriteLine();
    }

    private static void ListModels()
    {
        var registry = DefaultModelRegistry.Create();
        var chat = new ChatService(registry);
        Console.WriteLine("Available models:");
        foreach (var group in chat.GetModels().GroupBy(m => m.Provider))
        {
            Console.WriteLine($"  [{group.Key}]");
            foreach (var m in group)
                Console.WriteLine($"    {m.Id} — {m.Name} (context: {m.ContextWindow}, reasoning: {(m.SupportsReasoning ? "yes" : "no")})");
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
        Pi.NET — terminal coding harness (Stage 1)

        Usage:
          pinet --provider <provider> --model <model> -p "your prompt"
          cat file.txt | pinet -p "Summarize this"

        Options:
          --provider <name>    Provider id (openai, anthropic, faux)
          --model <id>         Model id
          -p, --print          Print response and exit
          --agent              Run as an agent with built-in tools (read_file, write_file, edit_file, bash)
          -i, --interactive    Interactive prompt loop with slash-commands
          --session <id|path>  Resume a saved session
          --fork <id|path>     Fork a session and continue from its active branch
          -c, --continue       Resume the most recent session for this directory
          --no-session         Disable session persistence
          --approve            Auto-approve permission prompts for this run
          --no-approve         Ask for permission even if config would allow (bash)
          --api-key <key>      API key (overrides env vars)
          --list-models        List available models
          -h, --help           Show this help
        """);
    }
}