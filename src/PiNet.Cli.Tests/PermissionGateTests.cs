using PiNet.Abstractions.Providers;
using PiNet.Cli.Interact;
using PiNet.Permissions;
using PiNet.Providers;
using PiNet.Sessions;
using Xunit;

namespace PiNet.Cli.Tests;

/// <summary>
/// AC1–AC4 + SR-05.04/05/07/08 — the permission gate in the interactive loop,
/// driven by scripted key sequences. The resolver is built in-memory (no file
/// IO, no PI_NET_HOME), so these tests never touch the real home directory.
/// </summary>
public class PermissionGateTests
{
    private sealed class Fixture : IDisposable
    {
        public string Cwd { get; }
        public string Root { get; }
        public string Outside { get; }
        public ModelRegistry Registry { get; } = new();
        public ChatService Chat { get; }
        public SessionManager Manager { get; }
        public ScriptedProvider Provider { get; }
        private readonly IDisposable _disposer;

        public Fixture()
        {
            var (cwd, root, disposer) = TestFixtures.TempDirs();
            Cwd = cwd;
            Root = root;
            Outside = Path.GetFullPath(Path.Combine(cwd, ".."));
            _disposer = disposer;
            Chat = new ChatService(Registry);
            Manager = new SessionManager(root);
            Provider = new ScriptedProvider("faux");
            Registry.RegisterProvider(Provider);
        }

        public void Dispose() => _disposer.Dispose();
    }

    private static InteractiveLoop NewLoop(
        Fixture fx,
        ScriptedInput input,
        CapturingOutput output,
        IPermissionResolver? permissions = null,
        IPermissionPrompt? prompt = null) =>
        new(chat: fx.Chat,
            manager: fx.Manager,
            cwd: fx.Cwd,
            providerId: "faux",
            modelId: "faux-model",
            input: input,
            output: output,
            noSession: true,
            permissions: permissions,
            permissionPrompt: prompt);

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    // --- AC1: first bash asks; a/s/d are honored ----------------------------

    [Fact]
    public async Task FirstBash_Prompts_AllowOnce_RunsTool()
    {
        using var fx = new Fixture();
        fx.Provider.EnqueueEvents(ToolCallScript.Bash("echo hello"))
               .Enqueue("done");

        var resolver = new RulePermissionResolver([], []);
        var output = new CapturingOutput();
        var input = new ScriptedInput(
        [
            ..Keys.Type("go"), Keys.Enter,
            Keys.Char('a'), // allow once
            ..Keys.Type("/quit"), Keys.Enter
        ]);
        var loop = NewLoop(fx, input, output, resolver,
            new ConsolePermissionPrompt(input, output));

        var code = await loop.RunAsync();

        Assert.Equal(0, code);
        Assert.Contains("Permission required — bash", output.All);
        Assert.Contains("$ echo hello", output.All);
        Assert.Contains("> ok: hello", output.All); // tool actually ran
        Assert.Equal(2, fx.Provider.StreamCalls); // LLM continued after the tool
    }

    [Fact]
    public async Task FirstBash_Prompts_Deny_BlocksTool_AgentContinues()
    {
        using var fx = new Fixture();
        fx.Provider.EnqueueEvents(ToolCallScript.Bash("echo secret"))
               .Enqueue("understood");

        var resolver = new RulePermissionResolver([], []);
        var output = new CapturingOutput();
        var input = new ScriptedInput(
        [
            ..Keys.Type("go"), Keys.Enter,
            Keys.Char('d'), // deny
            ..Keys.Type("/quit"), Keys.Enter
        ]);
        var loop = NewLoop(fx, input, output, resolver,
            new ConsolePermissionPrompt(input, output));

        var code = await loop.RunAsync();

        Assert.Equal(0, code);
        Assert.Contains("Permission required — bash", output.All);
        Assert.Contains(output.RedLines, l => l.Contains("Blocked: Denied by user"));
        Assert.DoesNotContain("> ok: secret", output.All); // tool never ran
        Assert.Equal(2, fx.Provider.StreamCalls); // agent kept going after the block
    }

    [Fact]
    public async Task AllowSession_SecondBashInSameTurn_DoesNotPromptAgain()
    {
        using var fx = new Fixture();
        fx.Provider.EnqueueEvents(ToolCallScript.Bash("echo one"))
                   .EnqueueEvents(ToolCallScript.Bash("echo two", "call-2"))
                   .Enqueue("all done");

        var resolver = new RulePermissionResolver([], []);
        var output = new CapturingOutput();
        var input = new ScriptedInput(
        [
            ..Keys.Type("go"), Keys.Enter,
            Keys.Char('s'), // allow for session
            ..Keys.Type("/quit"), Keys.Enter
        ]);
        var loop = NewLoop(fx, input, output, resolver,
            new ConsolePermissionPrompt(input, output));

        var code = await loop.RunAsync();

        Assert.Equal(0, code);
        Assert.Equal(1, CountOccurrences(output.All, "Permission required"));
        Assert.Contains("> ok: one", output.All);
        Assert.Contains("> ok: two", output.All);
        Assert.Contains(resolver.SessionRules,
            r => r.ToolPattern == "bash" && r.Permission == ToolPermission.Allow);
    }

    // --- AC2: /trust persists project trust; subsequent bash is free --------

    [Fact]
    public async Task Trust_WritesProjectConfig_SubsequentBashDoesNotPrompt()
    {
        using var fx = new Fixture();
        fx.Provider.EnqueueEvents(ToolCallScript.Bash("echo hi"))
                   .Enqueue("denied ok")
                   .EnqueueEvents(ToolCallScript.Bash("echo hi again", "call-2"))
                   .Enqueue("done");

        var resolver = new RulePermissionResolver([], []);
        var output = new CapturingOutput();
        var input = new ScriptedInput(
        [
            ..Keys.Type("go"), Keys.Enter,
            Keys.Char('d'), // deny the first bash
            ..Keys.Type("/trust"), Keys.Enter,
            ..Keys.Type("go again"), Keys.Enter,
            ..Keys.Type("/quit"), Keys.Enter
        ]);
        var loop = NewLoop(fx, input, output, resolver,
            new ConsolePermissionPrompt(input, output));

        var code = await loop.RunAsync();

        Assert.Equal(0, code);
        Assert.Equal(1, CountOccurrences(output.All, "Permission required"));
        Assert.Contains("(project trusted: bash allowed without prompt)", output.All);
        Assert.Contains("> ok: hi again", output.All); // second bash ran without prompt
        Assert.True(TrustStore.IsTrusted(fx.Cwd));
        var json = File.ReadAllText(PermissionPaths.ProjectConfigPath(fx.Cwd));
        Assert.Contains("\"bash\"", json);
        Assert.Contains("\"allow\"", json);
    }

    [Fact]
    public async Task TrustOff_RemovesTrustRule()
    {
        using var fx = new Fixture();

        var output = new CapturingOutput();
        var input = new ScriptedInput(
        [
            ..Keys.Type("/trust"), Keys.Enter,
            ..Keys.Type("/trust off"), Keys.Enter,
            ..Keys.Type("/quit"), Keys.Enter
        ]);
        var loop = NewLoop(fx, input, output,
            new RulePermissionResolver([], []), new AutoApprovePrompt());

        var code = await loop.RunAsync();

        Assert.Equal(0, code);
        Assert.Contains("(project trusted: bash allowed without prompt)", output.All);
        Assert.Contains("(project no longer trusted)", output.All);
        Assert.False(TrustStore.IsTrusted(fx.Cwd));
    }

    // --- AC3: --approve (AutoApprovePrompt) and non-interactive fallback -----

    [Fact]
    public async Task AutoApprove_RunsBashWithoutPrompt()
    {
        using var fx = new Fixture();
        fx.Provider.EnqueueEvents(ToolCallScript.Bash("echo auto"))
               .Enqueue("done");

        var output = new CapturingOutput();
        var input = new ScriptedInput(
        [
            ..Keys.Type("go"), Keys.Enter,
            ..Keys.Type("/quit"), Keys.Enter
        ]);
        var loop = NewLoop(fx, input, output,
            new RulePermissionResolver([], []), new AutoApprovePrompt());

        var code = await loop.RunAsync();

        Assert.Equal(0, code);
        Assert.DoesNotContain("Permission required", output.All);
        Assert.Contains("> ok: auto", output.All);
    }

    [Fact]
    public async Task DenyAllPrompt_NonInteractive_BlocksBash()
    {
        using var fx = new Fixture();
        fx.Provider.EnqueueEvents(ToolCallScript.Bash("echo nope"))
               .Enqueue("done");

        var stderr = new System.IO.StringWriter();
        var output = new CapturingOutput();
        var input = new ScriptedInput(
        [
            ..Keys.Type("go"), Keys.Enter,
            ..Keys.Type("/quit"), Keys.Enter
        ]);
        var loop = NewLoop(fx, input, output,
            new RulePermissionResolver([], []), new DenyAllPrompt(stderr));

        var code = await loop.RunAsync();

        Assert.Equal(0, code);
        Assert.Contains("non-interactive", stderr.ToString());
        Assert.Contains(output.RedLines, l => l.Contains("Blocked: Denied by user"));
        Assert.DoesNotContain("> ok: nope", output.All);
    }

    // --- AC4: path-scoped denies and the out-of-cwd safety floor ------------

    [Fact]
    public async Task WriteOutsideCwd_AlwaysBlocked_EvenWithAutoApprove()
    {
        using var fx = new Fixture();
        var outsideFile = Path.Combine(fx.Outside, "outside.txt");
        fx.Provider.EnqueueEvents(ToolCallScript.WriteFile(outsideFile, "x"))
               .Enqueue("done");

        var output = new CapturingOutput();
        var input = new ScriptedInput(
        [
            ..Keys.Type("go"), Keys.Enter,
            ..Keys.Type("/quit"), Keys.Enter
        ]);
        var loop = NewLoop(fx, input, output,
            new RulePermissionResolver([], []), new AutoApprovePrompt());

        var code = await loop.RunAsync();

        Assert.Equal(0, code);
        Assert.Contains(output.RedLines, l => l.Contains("outside the working directory"));
        Assert.DoesNotContain("> ok:", output.All);
        Assert.False(File.Exists(outsideFile)); // file was never written
    }

    [Fact]
    public async Task PathScopedProjectDeny_BlocksMatchingPath_Only()
    {
        using var fx = new Fixture();
        var secretFile = Path.Combine(fx.Cwd, "secrets", "creds.txt");
        var okFile = Path.Combine(fx.Cwd, "notes.txt");
        fx.Provider.EnqueueEvents(ToolCallScript.WriteFile(secretFile, "x", "call-1"))
                   .Enqueue("denied ok")
                   .EnqueueEvents(ToolCallScript.WriteFile(okFile, "y", "call-2"))
                   .Enqueue("done");

        var resolver = new RulePermissionResolver([],
        [
            new PermissionRule("write_file", "secrets/*", ToolPermission.Deny)
        ]);
        var output = new CapturingOutput();
        var input = new ScriptedInput(
        [
            ..Keys.Type("go"), Keys.Enter,
            ..Keys.Type("go again"), Keys.Enter,
            ..Keys.Type("/quit"), Keys.Enter
        ]);
        var loop = NewLoop(fx, input, output, resolver, new AutoApprovePrompt());

        var code = await loop.RunAsync();

        Assert.Equal(0, code);
        Assert.Contains(output.RedLines, l => l.Contains("denied by project rule"));
        Assert.False(File.Exists(secretFile)); // denied path never written
        Assert.True(File.Exists(okFile)); // non-matching path was allowed
    }
}
