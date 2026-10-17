using PiNet.Abstractions.Chat;
using PiNet.Abstractions.Providers;
using PiNet.Cli.Interact;
using PiNet.Cli.Settings;
using PiNet.Providers;
using PiNet.Sessions;
using Xunit;

namespace PiNet.Cli.Tests;

/// <summary>
/// AC1/AC3/AC4 + SR-04.01/05/06/07/08/09 — the interactive loop driven by
/// scripted key sequences against a captured IConsoleOutput.
/// </summary>
public class InteractiveLoopTests
{
    // Internal so the serial /settings + PI_NET_HOME tests (SettingsTests) can reuse it.
    internal sealed class Fixture : IDisposable
    {
        public string Cwd { get; }
        public string Root { get; }
        public ModelRegistry Registry { get; } = new();
        public ChatService Chat { get; }
        public SessionManager Manager { get; }
        private readonly IDisposable _disposer;

        public Fixture()
        {
            var (cwd, root, disposer) = TestFixtures.TempDirs();
            Cwd = cwd;
            Root = root;
            _disposer = disposer;
            Chat = new ChatService(Registry);
            Manager = new SessionManager(root);
        }

        public void Dispose() => _disposer.Dispose();
    }

    // Internal so the serial /settings + PI_NET_HOME tests (SettingsTests) can reuse it.
    internal static InteractiveLoop NewLoop(
        Fixture fx,
        ScriptedInput input,
        CapturingOutput output,
        string providerId = "faux",
        string modelId = "faux-model",
        bool noSession = false,
        string? sessionRef = null,
        string? forkRef = null,
        EffectiveSettings? settings = null) =>
        new(chat: fx.Chat,
            manager: fx.Manager,
            cwd: fx.Cwd,
            providerId: providerId,
            modelId: modelId,
            input: input,
            output: output,
            noSession: noSession,
            sessionRef: sessionRef,
            forkRef: forkRef,
            settings: settings);

    // --- AC1: multi-prompt session persists across turns -------------------

    [Fact]
    public async Task MultiPrompt_Session_AccumulatesContext_AndPersists()
    {
        using var fx = new Fixture();
        var provider = new ScriptedProvider("faux", "Hello!", "Hi again!");
        fx.Registry.RegisterProvider(provider);

        var input = new ScriptedInput(
        [
            ..Keys.Type("hello"), Keys.Enter,
            ..Keys.Type("again"), Keys.Enter,
            ..Keys.Type("/quit"), Keys.Enter
        ]);
        var output = new CapturingOutput();
        var loop = NewLoop(fx, input, output);

        var code = await loop.RunAsync();

        Assert.Equal(0, code);
        Assert.Equal(2, provider.StreamCalls);

        // Context accumulates: the second request sees the whole first turn.
        var secondRequest = provider.Requests[1].Messages;
        Assert.Contains(secondRequest, m => m.Role == ChatRole.User && m.TryGetText() == "hello");
        Assert.Contains(secondRequest, m => m.Role == ChatRole.Assistant && m.TryGetText() == "Hello!");
        Assert.Contains(secondRequest, m => m.Role == ChatRole.User && m.TryGetText() == "again");

        // Persisted in the JSONL file: user + assistant per turn.
        var files = fx.Manager.ListSessions(fx.Cwd);
        Assert.Single(files);
        var lines = TestFixtures.SessionEntryLines(fx.Root, fx.Cwd, files[0].SessionId);
        Assert.Equal(4, lines.Count);
        Assert.Contains("session ", output.All);
    }

    [Fact]
    public async Task BlankLine_IsIgnored_AndRePrompts()
    {
        using var fx = new Fixture();
        var provider = new ScriptedProvider("faux", "ok");
        fx.Registry.RegisterProvider(provider);

        var input = new ScriptedInput(
        [
            Keys.Enter, // blank line: ignored
            ..Keys.Type("hi"), Keys.Enter,
            ..Keys.Type("/quit"), Keys.Enter
        ]);
        var loop = NewLoop(fx, input, new CapturingOutput());

        var code = await loop.RunAsync();

        Assert.Equal(0, code);
        Assert.Equal(1, provider.StreamCalls);
    }

    // --- AC4: Ctrl+C semantics ----------------------------------------------

    [Fact]
    public async Task CtrlC_OnEmptyPrompt_ExitsWithCodeZero()
    {
        using var fx = new Fixture();
        fx.Registry.RegisterProvider(new ScriptedProvider("faux"));

        var input = new ScriptedInput([Keys.CtrlC]);
        var output = new CapturingOutput();
        var loop = NewLoop(fx, input, output);

        var code = await loop.RunAsync();

        Assert.Equal(0, code);
        Assert.Contains("^C", output.All);
    }

    [Fact]
    public async Task CtrlC_OnNonEmptyPrompt_ClearsLine_AndContinues()
    {
        using var fx = new Fixture();
        var provider = new ScriptedProvider("faux", "ok");
        fx.Registry.RegisterProvider(provider);

        var input = new ScriptedInput(
        [
            ..Keys.Type("abc"), Keys.CtrlC,
            ..Keys.Type("/quit"), Keys.Enter
        ]);
        var output = new CapturingOutput();
        var loop = NewLoop(fx, input, output);

        var code = await loop.RunAsync();

        Assert.Equal(0, code);
        Assert.Equal(0, provider.StreamCalls); // cleared line never became a prompt
        Assert.Contains("^C", output.All);
    }

    [Fact]
    public async Task CtrlC_DuringTurn_Cancels_ReturnsToPrompt_AndDoesNotPersist()
    {
        using var fx = new Fixture();
        var provider = new HangingSignalProvider("hang");
        fx.Registry.RegisterProvider(provider);

        var input = new ScriptedInput([..Keys.Type("hang please"), Keys.Enter]);
        var output = new CapturingOutput();
        var loop = NewLoop(fx, input, output, providerId: "hang", modelId: "hang-model");

        var loopTask = loop.RunAsync();
        await provider.Started; // turn is in progress
        loop.CancelTurn();
        var code = await loopTask;

        Assert.Equal(0, code);
        Assert.Contains("(cancelled)", output.All);
        // Partial turn is not persisted: session file exists (created at startup) but is empty.
        var files = fx.Manager.ListSessions(fx.Cwd);
        Assert.Single(files);
        Assert.Empty(TestFixtures.SessionEntryLines(fx.Root, fx.Cwd, files[0].SessionId));
    }

    // --- AC3: slash commands -------------------------------------------------

    [Fact]
    public async Task UnknownCommand_PrintsError_AndLoopContinues()
    {
        using var fx = new Fixture();
        fx.Registry.RegisterProvider(new ScriptedProvider("faux"));

        var input = new ScriptedInput(
        [
            ..Keys.Type("/wat"), Keys.Enter,
            ..Keys.Type("/quit"), Keys.Enter
        ]);
        var output = new CapturingOutput();
        var loop = NewLoop(fx, input, output);

        var code = await loop.RunAsync();

        Assert.Equal(0, code);
        Assert.Contains(output.RedLines, l => l.Contains("Unknown command: /wat"));
    }

    [Fact]
    public async Task SlashlessExit_IsPlainPrompt_NotCommand()
    {
        using var fx = new Fixture();
        var provider = new ScriptedProvider("faux", "ok");
        fx.Registry.RegisterProvider(provider);

        var input = new ScriptedInput(
        [
            ..Keys.Type("exit"), Keys.Enter, // plain prompt, not /exit
            ..Keys.Type("/quit"), Keys.Enter
        ]);
        var loop = NewLoop(fx, input, new CapturingOutput());

        var code = await loop.RunAsync();

        Assert.Equal(0, code);
        Assert.Equal(1, provider.StreamCalls);
        Assert.Contains(provider.Requests[0].Messages,
            m => m.Role == ChatRole.User && m.TryGetText() == "exit");
    }

    [Fact]
    public async Task Help_PrintsCommandList()
    {
        using var fx = new Fixture();
        fx.Registry.RegisterProvider(new ScriptedProvider("faux"));

        var input = new ScriptedInput(
        [
            ..Keys.Type("/help"), Keys.Enter,
            ..Keys.Type("/quit"), Keys.Enter
        ]);
        var output = new CapturingOutput();
        var loop = NewLoop(fx, input, output);

        await loop.RunAsync();

        Assert.Contains("Commands:", output.All);
        Assert.Contains("/resume [id|path]", output.All);
        Assert.Contains("/model [provider/id]", output.All);
    }

    [Fact]
    public async Task HelpForCommand_PrintsSingleEntry()
    {
        using var fx = new Fixture();
        fx.Registry.RegisterProvider(new ScriptedProvider("faux"));

        var input = new ScriptedInput(
        [
            ..Keys.Type("/help quit"), Keys.Enter,
            ..Keys.Type("/quit"), Keys.Enter
        ]);
        var output = new CapturingOutput();
        var loop = NewLoop(fx, input, output);

        await loop.RunAsync();

        Assert.Contains("/quit, /exit", output.All);
    }

    [Fact]
    public async Task NewSession_DisablesPersistence_ForSubsequentTurns()
    {
        using var fx = new Fixture();
        fx.Registry.RegisterProvider(new ScriptedProvider("faux", "A", "B"));

        var input = new ScriptedInput(
        [
            ..Keys.Type("first"), Keys.Enter,
            ..Keys.Type("/new"), Keys.Enter,
            ..Keys.Type("second"), Keys.Enter,
            ..Keys.Type("/quit"), Keys.Enter
        ]);
        var output = new CapturingOutput();
        var loop = NewLoop(fx, input, output);

        var code = await loop.RunAsync();

        Assert.Equal(0, code);
        Assert.Contains("(new session: persistence disabled)", output.All);
        var files = fx.Manager.ListSessions(fx.Cwd);
        Assert.Single(files);
        // Only the first turn was persisted (2 entries), the second was ephemeral.
        var lines = TestFixtures.SessionEntryLines(fx.Root, fx.Cwd, files[0].SessionId);
        Assert.Equal(2, lines.Count);
    }

    [Fact]
    public async Task ResumeWithArg_LoadsSession()
    {
        using var fx = new Fixture();
        fx.Registry.RegisterProvider(new ScriptedProvider("faux"));

        var seed = fx.Manager.CreateSession(fx.Cwd,
        [
            ChatMessage.User("old"),
            ChatMessage.Assistant("Old reply")
        ]);

        var input = new ScriptedInput(
        [
            ..Keys.Type($"/resume {seed.Id}"), Keys.Enter,
            ..Keys.Type("/quit"), Keys.Enter
        ]);
        var output = new CapturingOutput();
        var loop = NewLoop(fx, input, output);

        var code = await loop.RunAsync();

        Assert.Equal(0, code);
        Assert.Contains($"session {seed.Id} (2 entries)", output.All);
    }

    [Fact]
    public async Task ResumeWithInvalidId_PrintsError_KeepsCurrentSession()
    {
        using var fx = new Fixture();
        fx.Registry.RegisterProvider(new ScriptedProvider("faux"));

        var input = new ScriptedInput(
        [
            ..Keys.Type("/resume no-such-session"), Keys.Enter,
            ..Keys.Type("/quit"), Keys.Enter
        ]);
        var output = new CapturingOutput();
        var loop = NewLoop(fx, input, output);

        var code = await loop.RunAsync();

        Assert.Equal(0, code);
        Assert.Contains(output.RedLines, l => l.Contains("no-such-session"));
    }

    [Fact]
    public async Task ResumePicker_ListsSessions_AndSelectsByNumber()
    {
        using var fx = new Fixture();
        fx.Registry.RegisterProvider(new ScriptedProvider("faux"));

        var seed = fx.Manager.CreateSession(fx.Cwd,
        [
            ChatMessage.User("old"),
            ChatMessage.Assistant("Old reply")
        ]);

        var input = new ScriptedInput(
        [
            ..Keys.Type("/resume"), Keys.Enter,
            Keys.Char('1'), Keys.Enter, // picker selection
            ..Keys.Type("/quit"), Keys.Enter
        ]);
        var output = new CapturingOutput();
        // noSession: the loop creates no startup session file, so the picker
        // lists exactly the seeded session (deterministic ordering).
        var loop = NewLoop(fx, input, output, noSession: true);

        var code = await loop.RunAsync();

        Assert.Equal(0, code);
        Assert.Contains($"1. {seed.Id}  (2 entries,", output.All);
        Assert.Contains($"session {seed.Id} (2 entries)", output.All);
    }

    [Fact]
    public async Task ForkWithArg_CreatesNewSession()
    {
        using var fx = new Fixture();
        fx.Registry.RegisterProvider(new ScriptedProvider("faux"));

        var seed = fx.Manager.CreateSession(fx.Cwd,
        [
            ChatMessage.User("old"),
            ChatMessage.Assistant("Old reply")
        ]);

        var input = new ScriptedInput(
        [
            ..Keys.Type($"/fork {seed.Id}"), Keys.Enter,
            ..Keys.Type("/quit"), Keys.Enter
        ]);
        var output = new CapturingOutput();
        var loop = NewLoop(fx, input, output);

        var code = await loop.RunAsync();

        Assert.Equal(0, code);
        Assert.Contains("[fork] session", output.All);
        Assert.NotEqual(seed.Id, output.All.Split("session ")[^1].Split(' ')[0]);
        // seed + startup session + fork
        Assert.Equal(3, fx.Manager.ListSessions(fx.Cwd).Count);
    }

    [Fact]
    public async Task ForkWithoutArg_PrintsUsage()
    {
        using var fx = new Fixture();
        fx.Registry.RegisterProvider(new ScriptedProvider("faux"));

        var input = new ScriptedInput(
        [
            ..Keys.Type("/fork"), Keys.Enter,
            ..Keys.Type("/quit"), Keys.Enter
        ]);
        var output = new CapturingOutput();
        var loop = NewLoop(fx, input, output);

        var code = await loop.RunAsync();

        Assert.Equal(0, code);
        Assert.Contains("Usage: /fork <session id|path>", output.All);
    }

    [Fact]
    public async Task Model_SwitchesProviderAndModel_ForSubsequentTurns()
    {
        using var fx = new Fixture();
        var p1 = new ScriptedProvider("p1");
        var p2 = new ScriptedProvider("p2", "from p2");
        fx.Registry.RegisterProvider(p1);
        fx.Registry.RegisterProvider(p2);

        var input = new ScriptedInput(
        [
            ..Keys.Type("/model p2/p2-model"), Keys.Enter,
            ..Keys.Type("hi"), Keys.Enter,
            ..Keys.Type("/quit"), Keys.Enter
        ]);
        var output = new CapturingOutput();
        var loop = NewLoop(fx, input, output, providerId: "p1", modelId: "p1-model");

        var code = await loop.RunAsync();

        Assert.Equal(0, code);
        Assert.Contains("(model: p2/p2-model)", output.All);
        Assert.Equal(0, p1.StreamCalls);
        Assert.Equal(1, p2.StreamCalls);
    }

    [Fact]
    public async Task Model_InvalidKeepsCurrentModel()
    {
        using var fx = new Fixture();
        var p1 = new ScriptedProvider("p1", "ok");
        fx.Registry.RegisterProvider(p1);

        var input = new ScriptedInput(
        [
            ..Keys.Type("/model nope/nope"), Keys.Enter,
            ..Keys.Type("hi"), Keys.Enter,
            ..Keys.Type("/quit"), Keys.Enter
        ]);
        var output = new CapturingOutput();
        var loop = NewLoop(fx, input, output, providerId: "p1", modelId: "p1-model");

        var code = await loop.RunAsync();

        Assert.Equal(0, code);
        Assert.Contains(output.RedLines, l => l.Contains("Unknown model: nope/nope"));
        Assert.Equal(1, p1.StreamCalls); // still handled by p1
    }

    [Fact]
    public async Task Model_NoArg_ListsNumberedModels_AndPicksByNumber()
    {
        using var fx = new Fixture();
        var p1 = new ScriptedProvider("p1");
        var p2 = new ScriptedProvider("p2", "from p2");
        fx.Registry.RegisterProvider(p1);
        fx.Registry.RegisterProvider(p2);

        var input = new ScriptedInput(
        [
            ..Keys.Type("/model"), Keys.Enter,
            Keys.Char('2'), Keys.Enter, // picker selection -> p2/p2-model
            ..Keys.Type("hi"), Keys.Enter,
            ..Keys.Type("/quit"), Keys.Enter
        ]);
        var output = new CapturingOutput();
        var loop = NewLoop(fx, input, output, providerId: "p1", modelId: "p1-model");

        var code = await loop.RunAsync();

        Assert.Equal(0, code);
        Assert.Contains("Available models:", output.All);
        Assert.Contains("  1. p1/p1-model", output.All);
        Assert.Contains("  2. p2/p2-model", output.All);
        Assert.Contains("(model: p2/p2-model)", output.All);
        Assert.Equal(0, p1.StreamCalls);
        Assert.Equal(1, p2.StreamCalls);
    }

    [Fact]
    public async Task Model_Picker_AcceptsTypedProviderId_AndModelsAlias()
    {
        using var fx = new Fixture();
        var p1 = new ScriptedProvider("p1", "ok");
        var p2 = new ScriptedProvider("p2", "from p2");
        fx.Registry.RegisterProvider(p1);
        fx.Registry.RegisterProvider(p2);

        var input = new ScriptedInput(
        [
            ..Keys.Type("/models"), Keys.Enter, // alias of /model opens the picker
            ..Keys.Type("p2/p2-model"), Keys.Enter, // typed selection instead of a number
            ..Keys.Type("hi"), Keys.Enter,
            ..Keys.Type("/quit"), Keys.Enter
        ]);
        var output = new CapturingOutput();
        var loop = NewLoop(fx, input, output, providerId: "p1", modelId: "p1-model");

        var code = await loop.RunAsync();

        Assert.Equal(0, code);
        Assert.Contains("Available models:", output.All);
        Assert.Contains("(model: p2/p2-model)", output.All);
        Assert.Equal(1, p2.StreamCalls);
    }

    [Fact]
    public async Task Model_Picker_Cancel_KeepsCurrentModel()
    {
        using var fx = new Fixture();
        var p1 = new ScriptedProvider("p1", "ok");
        fx.Registry.RegisterProvider(p1);

        var input = new ScriptedInput(
        [
            ..Keys.Type("/model"), Keys.Enter,
            ..Keys.Type("q"), Keys.Enter, // cancel the picker
            ..Keys.Type("hi"), Keys.Enter,
            ..Keys.Type("/quit"), Keys.Enter
        ]);
        var output = new CapturingOutput();
        var loop = NewLoop(fx, input, output, providerId: "p1", modelId: "p1-model");

        var code = await loop.RunAsync();

        Assert.Equal(0, code);
        Assert.Contains("Available models:", output.All);
        Assert.DoesNotContain("(model:", output.All);
        Assert.Equal(1, p1.StreamCalls);
    }

    [Fact]
    public async Task Model_Picker_RejectsInvalidNumber()
    {
        using var fx = new Fixture();
        var p1 = new ScriptedProvider("p1", "ok");
        fx.Registry.RegisterProvider(p1);

        var input = new ScriptedInput(
        [
            ..Keys.Type("/model"), Keys.Enter,
            ..Keys.Type("99"), Keys.Enter,
            ..Keys.Type("hi"), Keys.Enter,
            ..Keys.Type("/quit"), Keys.Enter
        ]);
        var output = new CapturingOutput();
        var loop = NewLoop(fx, input, output, providerId: "p1", modelId: "p1-model");

        var code = await loop.RunAsync();

        Assert.Equal(0, code);
        Assert.Contains(output.RedLines, l => l.Contains("Invalid selection: 99"));
        Assert.Equal(1, p1.StreamCalls);
    }

    // --- SR-04.06: --no-session ----------------------------------------------

    [Fact]
    public async Task NoSession_DisablesPersistenceEntirely()
    {
        using var fx = new Fixture();
        fx.Registry.RegisterProvider(new ScriptedProvider("faux", "ok"));

        var input = new ScriptedInput(
        [
            ..Keys.Type("hi"), Keys.Enter,
            ..Keys.Type("/quit"), Keys.Enter
        ]);
        var loop = NewLoop(fx, input, new CapturingOutput(), noSession: true);

        var code = await loop.RunAsync();

        Assert.Equal(0, code);
        Assert.Empty(fx.Manager.ListSessions(fx.Cwd));
    }

    // --- 7b: /compact (manual compaction) -------------------------------------

    private static IReadOnlyList<ChatMessage> CompactSeed()
    {
        return [
            ChatMessage.User("question one"),
            ChatMessage.Assistant("answer one"),
            ChatMessage.User("question two"),
            ChatMessage.Assistant("answer two"),
            ChatMessage.User("question three"),
            ChatMessage.Assistant("answer three")
        ];
    }

    private static async Task<IReadOnlyList<SessionEntry>> ReadEntriesAsync(SessionManager manager, string cwd, string sessionId) =>
        await SessionStore.ReadAllAsync(manager.SessionFilePath(cwd, sessionId));

    private static string JoinText(IReadOnlyList<SessionEntry> entries) =>
        string.Join("\n", entries.Select(e => e.ToMessage().TryGetText() ?? string.Empty));

    [Fact]
    public async Task Compact_ForcesCompaction_BelowThreshold_PersistsSummary_AndKeepsHistory()
    {
        using var fx = new Fixture();
        var provider = new ScriptedProvider("faux")
            .Enqueue("SUMMARY: first three messages")
            .Enqueue("Hello from model");
        fx.Registry.RegisterProvider(provider);

        var session = fx.Manager.CreateSession(fx.Cwd, CompactSeed());

        var input = new ScriptedInput(
        [
            ..Keys.Type("/compact"), Keys.Enter,
            ..Keys.Type("hello"), Keys.Enter,
            ..Keys.Type("/quit"), Keys.Enter
        ]);
        var output = new CapturingOutput();
        var loop = NewLoop(fx, input, output, sessionRef: session.Id);

        var code = await loop.RunAsync();

        Assert.Equal(0, code);
        Assert.Equal(2, provider.StreamCalls); // 1 compaction + 1 hello turn
        Assert.Contains("(compacted 2 message(s) into a summary)", output.All);

        var files = fx.Manager.ListSessions(fx.Cwd);
        Assert.Single(files);
        var entries = await ReadEntriesAsync(fx.Manager, fx.Cwd, files[0].SessionId);
        // Full original history retained; summary additive; hello turn persisted.
        Assert.Equal(9, entries.Count);
        var all = JoinText(entries);
        Assert.Contains("question one", all);
        Assert.Contains("answer three", all);
        Assert.Contains("SUMMARY: first three messages", all);
        Assert.Contains("Hello from model", all);
        Assert.Contains("<summary of earlier conversation>", all);
    }

    [Fact]
    public async Task CompactWithCustomInstruction_InstructionReachesSummarizer()
    {
        using var fx = new Fixture();
        var provider = new ScriptedProvider("faux").Enqueue("CUSTOM-SUMMARY");
        fx.Registry.RegisterProvider(provider);

        var session = fx.Manager.CreateSession(fx.Cwd, CompactSeed());

        var input = new ScriptedInput(
        [
            ..Keys.Type("/compact summarize the tooling and decisions"), Keys.Enter,
            ..Keys.Type("/quit"), Keys.Enter
        ]);
        var output = new CapturingOutput();
        var loop = NewLoop(fx, input, output, sessionRef: session.Id);

        var code = await loop.RunAsync();

        Assert.Equal(0, code);
        Assert.Equal(1, provider.StreamCalls);
        Assert.Contains("(compacted 2 message(s) into a summary)", output.All);

        // The custom instruction is sent to the summarizer (first request),
        // together with the conversation to be summarized.
        var compactionPrompt = provider.Requests[0].Messages[0].TryGetText();
        Assert.Contains("summarize the tooling and decisions", compactionPrompt);
        Assert.Contains("question one", compactionPrompt);
    }

    [Fact]
    public async Task CompactThenModelSwitch_SummaryFoldsAcrossSwitch()
    {
        using var fx = new Fixture();
        var p1 = new ScriptedProvider("p1").Enqueue("S1: first summary");
        var p2 = new ScriptedProvider("p2").Enqueue("S2: folded summary");
        fx.Registry.RegisterProvider(p1);
        fx.Registry.RegisterProvider(p2);

        var session = fx.Manager.CreateSession(fx.Cwd, CompactSeed());

        var input = new ScriptedInput(
        [
            ..Keys.Type("/compact"), Keys.Enter,
            ..Keys.Type("/model p2/p2-model"), Keys.Enter,
            ..Keys.Type("/compact"), Keys.Enter,
            ..Keys.Type("/quit"), Keys.Enter
        ]);
        var output = new CapturingOutput();
        var loop = NewLoop(fx, input, output, providerId: "p1", modelId: "p1-model", sessionRef: session.Id);

        var code = await loop.RunAsync();

        Assert.Equal(0, code);
        Assert.Contains("(model: p2/p2-model)", output.All);
        Assert.Equal(1, p1.StreamCalls);
        Assert.Equal(1, p2.StreamCalls);

        var files = fx.Manager.ListSessions(fx.Cwd);
        Assert.Single(files);
        var entries = await ReadEntriesAsync(fx.Manager, fx.Cwd, files[0].SessionId);
        // 6 seed + 2 summaries; original history retained.
        Assert.Equal(8, entries.Count);
        var all = JoinText(entries);
        Assert.Contains("S1: first summary", all);
        Assert.Contains("S2: folded summary", all);

        // The second compaction (p2) must have folded the first summary in.
        var secondPrompt = p2.Requests[0].Messages[0].TryGetText();
        Assert.Contains("S1: first summary", secondPrompt);
    }

    [Fact]
    public async Task Compact_TooLittleContent_ReportsAlreadyCompact()
    {
        using var fx = new Fixture();
        var provider = new ScriptedProvider("faux");
        fx.Registry.RegisterProvider(provider);

        var session = fx.Manager.CreateSession(fx.Cwd, [ChatMessage.User("just one message")]);

        var input = new ScriptedInput(
        [
            ..Keys.Type("/compact"), Keys.Enter,
            ..Keys.Type("/quit"), Keys.Enter
        ]);
        var output = new CapturingOutput();
        var loop = NewLoop(fx, input, output, sessionRef: session.Id);

        await loop.RunAsync();

        Assert.Contains("(context already compact)", output.All);
        Assert.Equal(0, provider.StreamCalls);
        var entries = await ReadEntriesAsync(fx.Manager, fx.Cwd, session.Id);
        Assert.Single(entries); // nothing added
    }

    // --- 7c: /thinking (reasoning level) ---------------------------------------

    [Fact]
    public async Task Thinking_Command_SetsLevel_ForNextRequest()
    {
        using var fx = new Fixture();
        var provider = new ScriptedProvider("faux", "ok");
        fx.Registry.RegisterProvider(provider);

        var input = new ScriptedInput(
        [
            ..Keys.Type("/thinking high"), Keys.Enter,
            ..Keys.Type("hello"), Keys.Enter,
            ..Keys.Type("/quit"), Keys.Enter
        ]);
        var output = new CapturingOutput();
        var loop = NewLoop(fx, input, output);

        var code = await loop.RunAsync();

        Assert.Equal(0, code);
        Assert.Contains("(thinking: High)", output.All);
        Assert.Single(provider.Requests);
        Assert.Equal(ThinkingLevel.High, provider.Requests[0].ThinkingLevel);
    }

    [Fact]
    public async Task Thinking_Command_NoArg_ShowsCurrentLevel()
    {
        using var fx = new Fixture();
        fx.Registry.RegisterProvider(new ScriptedProvider("faux"));

        var input = new ScriptedInput(
        [
            ..Keys.Type("/thinking"), Keys.Enter,
            ..Keys.Type("/quit"), Keys.Enter
        ]);
        var output = new CapturingOutput();
        var loop = NewLoop(fx, input, output);

        await loop.RunAsync();

        Assert.Contains("(thinking: Off)", output.All);
    }

    [Fact]
    public async Task Thinking_Command_InvalidLevel_IsRejected_AndLevelUnchanged()
    {
        using var fx = new Fixture();
        var provider = new ScriptedProvider("faux", "ok");
        fx.Registry.RegisterProvider(provider);

        var input = new ScriptedInput(
        [
            ..Keys.Type("/thinking banana"), Keys.Enter,
            ..Keys.Type("hello"), Keys.Enter,
            ..Keys.Type("/quit"), Keys.Enter
        ]);
        var output = new CapturingOutput();
        var loop = NewLoop(fx, input, output);

        await loop.RunAsync();

        Assert.Contains(output.RedLines, l => l.Contains("Unknown thinking level"));
        Assert.Equal(ThinkingLevel.Off, provider.Requests[0].ThinkingLevel);
    }

    [Fact]
    public async Task Thinking_Level_Survives_ModelSwitch()
    {
        using var fx = new Fixture();
        var p1 = new ScriptedProvider("p1", "one");
        var p2 = new ScriptedProvider("p2", "two");
        fx.Registry.RegisterProvider(p1);
        fx.Registry.RegisterProvider(p2);

        var input = new ScriptedInput(
        [
            ..Keys.Type("/thinking medium"), Keys.Enter,
            ..Keys.Type("first"), Keys.Enter,
            ..Keys.Type("/model p2/p2-model"), Keys.Enter,
            ..Keys.Type("second"), Keys.Enter,
            ..Keys.Type("/quit"), Keys.Enter
        ]);
        var output = new CapturingOutput();
        var loop = NewLoop(fx, input, output, providerId: "p1", modelId: "p1-model");

        await loop.RunAsync();

        Assert.Equal(ThinkingLevel.Medium, p1.Requests[0].ThinkingLevel);
        Assert.Equal(ThinkingLevel.Medium, p2.Requests[0].ThinkingLevel);
    }

    // --- 7d: cross-provider handoff --------------------------------------------

    private static IReadOnlyList<ChatMessage> ThinkingSeed() =>
    [
        ChatMessage.User("earlier question"),
        new ChatMessage(ChatRole.Assistant,
            [new ThinkingContent("private reasoning"), new TextContent("earlier answer")], Timestamp: 1000)
    ];

    [Fact]
    public async Task HandoffToOpenAi_DropsThinking_FromRequest_ButKeepsSession()
    {
        using var fx = new Fixture();
        var provider = new ScriptedProvider("faux", "ok");
        fx.Registry.RegisterProvider(provider);
        var session = fx.Manager.CreateSession(fx.Cwd, ThinkingSeed());

        var input = new ScriptedInput(
        [
            ..Keys.Type("continue"), Keys.Enter,
            ..Keys.Type("/quit"), Keys.Enter
        ]);
        var output = new CapturingOutput();
        var loop = NewLoop(fx, input, output, sessionRef: session.Id);

        await loop.RunAsync();

        var sent = provider.Requests[0].Messages;
        Assert.DoesNotContain(sent, m => m.Content.Any(b => b is ThinkingContent));
        Assert.Contains(sent, m => m.Role == ChatRole.Assistant && m.TryGetText() == "earlier answer");

        // The persisted session keeps the original thinking entry.
        var entries = await ReadEntriesAsync(fx.Manager, fx.Cwd, session.Id);
        Assert.Contains(entries, e => e.Content.Any(b => b is ThinkingContent));
    }

    [Fact]
    public async Task HandoffToAnthropicStyleProvider_KeepsThinking()
    {
        using var fx = new Fixture();
        var provider = new ScriptedProvider("anthropic-faux", "ok");
        fx.Registry.RegisterProvider(provider);
        var session = fx.Manager.CreateSession(fx.Cwd, ThinkingSeed());

        var input = new ScriptedInput(
        [
            ..Keys.Type("continue"), Keys.Enter,
            ..Keys.Type("/quit"), Keys.Enter
        ]);
        var output = new CapturingOutput();
        var loop = NewLoop(fx, input, output,
            providerId: "anthropic-faux", modelId: "anthropic-faux-model", sessionRef: session.Id);

        await loop.RunAsync();

        var sent = provider.Requests[0].Messages;
        Assert.Contains(sent, m => m.Content.Any(b => b is ThinkingContent { Thinking: "private reasoning" }));
    }

    // --- 7e: image input (@path expansion + vision guard) -----------------------

    private static void WritePng(string cwd, string name)
    {
        // Bytes don't matter for the wire assertion; name/mime/base64 do.
        File.WriteAllBytes(Path.Combine(cwd, name), [0x89, (byte)'P', (byte)'N', (byte)'G']);
    }

    [Fact]
    public async Task ImageToken_BecomesImageBlock_WhenModelSupportsVision()
    {
        using var fx = new Fixture();
        var provider = new ScriptedProvider("faux", supportsVision: true, "ok");
        fx.Registry.RegisterProvider(provider);
        WritePng(fx.Cwd, "cat.png");

        var input = new ScriptedInput(
        [
            ..Keys.Type("describe @cat.png please"), Keys.Enter,
            ..Keys.Type("/quit"), Keys.Enter
        ]);
        var output = new CapturingOutput();
        var loop = NewLoop(fx, input, output);

        await loop.RunAsync();

        var user = provider.Requests[0].Messages.Last(m => m.Role == ChatRole.User);
        var image = Assert.Single(user.Content.OfType<ImageContent>());
        Assert.Equal("image/png", image.MimeType);
        Assert.Equal(
            Convert.ToBase64String(File.ReadAllBytes(Path.Combine(fx.Cwd, "cat.png"))),
            image.Data);
        Assert.Equal("describe please", user.TryGetText());
    }

    [Fact]
    public async Task ImageToken_NonVisionModel_SkipsAttachment_WithMessage()
    {
        using var fx = new Fixture();
        var provider = new ScriptedProvider("faux", "ok"); // SupportsVision = false
        fx.Registry.RegisterProvider(provider);
        WritePng(fx.Cwd, "cat.png");

        var input = new ScriptedInput(
        [
            ..Keys.Type("describe @cat.png"), Keys.Enter,
            ..Keys.Type("/quit"), Keys.Enter
        ]);
        var output = new CapturingOutput();
        var loop = NewLoop(fx, input, output);

        await loop.RunAsync();

        Assert.Contains(output.YellowLines, l => l.Contains("does not accept image input"));
        var user = provider.Requests[0].Messages.Last(m => m.Role == ChatRole.User);
        Assert.Empty(user.Content.OfType<ImageContent>());
        Assert.Equal("describe", user.TryGetText());
    }

    [Fact]
    public async Task ImageToken_MissingFile_StaysAsText()
    {
        using var fx = new Fixture();
        var provider = new ScriptedProvider("faux", supportsVision: true, "ok");
        fx.Registry.RegisterProvider(provider);

        var input = new ScriptedInput(
        [
            ..Keys.Type("look at @missing.png"), Keys.Enter,
            ..Keys.Type("/quit"), Keys.Enter
        ]);
        var output = new CapturingOutput();
        var loop = NewLoop(fx, input, output);

        await loop.RunAsync();

        var user = provider.Requests[0].Messages.Last(m => m.Role == ChatRole.User);
        Assert.Empty(user.Content.OfType<ImageContent>());
        Assert.Equal("look at @missing.png", user.TryGetText());
    }

    // --- 7f: /export and /import ------------------------------------------------

    [Fact]
    public async Task Export_Jsonl_ThenImport_RoundTrips_AndContinuesSession()
    {
        using var fx = new Fixture();
        var provider = new ScriptedProvider("faux", "answer");
        fx.Registry.RegisterProvider(provider);

        var input = new ScriptedInput(
        [
            ..Keys.Type("hello world"), Keys.Enter,
            ..Keys.Type("/export jsonl out/session.jsonl"), Keys.Enter,
            ..Keys.Type("/quit"), Keys.Enter
        ]);
        var output = new CapturingOutput();
        var loop = NewLoop(fx, input, output);

        await loop.RunAsync();

        var exportedPath = Path.Combine(fx.Cwd, "out", "session.jsonl");
        Assert.True(File.Exists(exportedPath));
        Assert.Contains("[export] 2 entries", output.All);
        Assert.Equal(2, File.ReadAllLines(exportedPath).Length); // user + assistant

        // Import into a fresh loop and continue the conversation.
        var input2 = new ScriptedInput(
        [
            ..Keys.Type("/import out/session.jsonl"), Keys.Enter,
            ..Keys.Type("again"), Keys.Enter,
            ..Keys.Type("/quit"), Keys.Enter
        ]);
        var output2 = new CapturingOutput();
        var loop2 = NewLoop(fx, input2, output2);

        await loop2.RunAsync();

        Assert.Contains("[import] session", output2.All);
        var sent = provider.Requests[1].Messages;
        Assert.Contains(sent, m => m.Role == ChatRole.User && m.TryGetText() == "hello world");
        Assert.Contains(sent, m => m.Role == ChatRole.Assistant && m.TryGetText() == "answer");
        Assert.Contains(sent, m => m.Role == ChatRole.User && m.TryGetText() == "again");
    }

    [Fact]
    public async Task Export_Html_WritesStandaloneFile()
    {
        using var fx = new Fixture();
        var provider = new ScriptedProvider("faux", "answer");
        fx.Registry.RegisterProvider(provider);

        var input = new ScriptedInput(
        [
            ..Keys.Type("hello html"), Keys.Enter,
            ..Keys.Type("/export html page.html"), Keys.Enter,
            ..Keys.Type("/quit"), Keys.Enter
        ]);
        var output = new CapturingOutput();
        var loop = NewLoop(fx, input, output);

        await loop.RunAsync();

        var html = await File.ReadAllTextAsync(Path.Combine(fx.Cwd, "page.html"));
        Assert.StartsWith("<!DOCTYPE html>", html);
        Assert.Contains("hello html", html);
        Assert.EndsWith("</html>" + Environment.NewLine, html);
    }

    [Fact]
    public async Task Export_MissingFormat_PrintsUsage()
    {
        using var fx = new Fixture();
        fx.Registry.RegisterProvider(new ScriptedProvider("faux"));

        var input = new ScriptedInput(
        [
            ..Keys.Type("/export nope out.txt"), Keys.Enter,
            ..Keys.Type("/quit"), Keys.Enter
        ]);
        var output = new CapturingOutput();
        var loop = NewLoop(fx, input, output);

        await loop.RunAsync();

        Assert.Contains(output.RedLines, l => l.Contains("Usage: /export jsonl|html"));
    }

    [Fact]
    public async Task Import_MalformedFile_FailsCleanly_WithoutCorruptingCurrentSession()
    {
        using var fx = new Fixture();
        var provider = new ScriptedProvider("faux", "ok");
        fx.Registry.RegisterProvider(provider);
        await File.WriteAllTextAsync(Path.Combine(fx.Cwd, "broken.jsonl"), "{not json\n");

        var input = new ScriptedInput(
        [
            ..Keys.Type("first turn"), Keys.Enter,
            ..Keys.Type("/import broken.jsonl"), Keys.Enter,
            ..Keys.Type("second turn"), Keys.Enter,
            ..Keys.Type("/quit"), Keys.Enter
        ]);
        var output = new CapturingOutput();
        var loop = NewLoop(fx, input, output);

        var code = await loop.RunAsync();

        Assert.Equal(0, code);
        Assert.Contains(output.RedLines, l => l.Contains("[import]"));
        // The pre-import session kept working: both turns persisted there.
        var files = fx.Manager.ListSessions(fx.Cwd);
        var busiest = files.OrderByDescending(f => f.EntryCount).First();
        var entries = await ReadEntriesAsync(fx.Manager, fx.Cwd, busiest.SessionId);
        var all = JoinText(entries);
        Assert.Contains("first turn", all);
        Assert.Contains("second turn", all);
    }

    [Fact]
    public async Task Import_MissingFile_ReportsNotFound()
    {
        using var fx = new Fixture();
        fx.Registry.RegisterProvider(new ScriptedProvider("faux"));

        var input = new ScriptedInput(
        [
            ..Keys.Type("/import nope.jsonl"), Keys.Enter,
            ..Keys.Type("/quit"), Keys.Enter
        ]);
        var output = new CapturingOutput();
        var loop = NewLoop(fx, input, output);

        await loop.RunAsync();

        Assert.Contains(output.RedLines, l => l.Contains("file not found"));
    }
}
