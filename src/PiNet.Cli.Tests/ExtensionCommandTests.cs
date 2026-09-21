using System.Text.Json;
using PiNet.Abstractions.Chat;
using PiNet.Cli.Extensions;
using PiNet.Extensions;
using Xunit;

namespace PiNet.Cli.Tests;

/// <summary>
/// Spec 06: extension commands and tools wired through the interactive loop,
/// including collision behaviour with built-ins.
/// </summary>
[Collection("Extensions")]
public class ExtensionCommandTests
{
    private sealed class TestExtension : IExtension
    {
        public void Register(IExtensionContext ctx)
        {
            ctx.RegisterTool(new ExtensionToolSpec(
                "hello",
                "Say hello",
                """{"type":"object","properties":{"name":{"type":"string"}},"required":["name"]}""",
                (callId, args, ct) => Task.FromResult(new ExtensionToolResult("Hello from extension!"))));

            ctx.RegisterCommand("hello", async (args, c) =>
            {
                c.WriteLine("hello from command");
                await c.SendToAgentAsync("queued: " + args);
                return true;
            });
        }
    }

    private sealed class CollisionExtension : IExtension
    {
        public void Register(IExtensionContext ctx)
        {
            ctx.RegisterTool(new ExtensionToolSpec("write_file", "Built-in collision", null,
                (callId, args, ct) => Task.FromResult(new ExtensionToolResult("boom"))));
            ctx.RegisterCommand("quit", (args, c) => Task.FromResult(true));
        }
    }

    [Fact]
    public async Task ExtensionSlash_RunsHandler_AndQueuesTurn()
    {
        using var env = new ExtTestEnv();
        using var fx = new LoopFixture(env);
        var provider = new ScriptedProvider("faux", "ok");
        fx.Registry.RegisterProvider(provider);

        var extensions = ExtensionHost.LoadInMemory(fx.Cwd, new TestExtension());
        using var _ = extensions;
        var input = new ScriptedInput(
        [
            ..Keys.Type("/hello some args"), Keys.Enter,
            Keys.CtrlC
        ]);
        var output = new CapturingOutput();
        var loop = LoopFixture.NewLoop(fx, input, output, extensions: extensions);

        await loop.RunAsync();

        Assert.Contains("hello from command", output.All);
        var request = Assert.Single(provider.Requests);
        Assert.Contains(request.Messages, m => m.Role == ChatRole.User && m.TryGetText() == "queued: some args");
    }

    [Fact]
    public async Task Extension_NoArgs_PassesEmptyArgs()
    {
        using var env = new ExtTestEnv();
        using var fx = new LoopFixture(env);
        var provider = new ScriptedProvider("faux", "ok");
        fx.Registry.RegisterProvider(provider);

        using var extensions = ExtensionHost.LoadInMemory(fx.Cwd, new TestExtension());
        var input = new ScriptedInput(
        [
            ..Keys.Type("/hello"), Keys.Enter,
            Keys.CtrlC
        ]);
        var loop = LoopFixture.NewLoop(fx, input, new CapturingOutput(), extensions: extensions);

        await loop.RunAsync();

        var request = Assert.Single(provider.Requests);
        Assert.Contains(request.Messages, m => m.Role == ChatRole.User && m.TryGetText() == "queued: ");
    }

    [Fact]
    public async Task ExtensionTool_IsExposedToAgent()
    {
        using var env = new ExtTestEnv();
        using var fx = new LoopFixture(env);
        var provider = new ScriptedProvider("faux", "ok");
        fx.Registry.RegisterProvider(provider);

        using var extensions = ExtensionHost.LoadInMemory(fx.Cwd, new TestExtension());
        var input = new ScriptedInput(
        [
            ..Keys.Type("plain turn"), Keys.Enter,
            Keys.CtrlC
        ]);
        var loop = LoopFixture.NewLoop(fx, input, new CapturingOutput(), extensions: extensions);

        await loop.RunAsync();

        var request = Assert.Single(provider.Requests);
        Assert.NotNull(request.Tools);
        Assert.Contains(request.Tools, t => t.Name == "hello");
        var mapped = request.Tools.Single(t => t.Name == "hello");
        Assert.Equal("Say hello", mapped.Description);
        Assert.NotNull(mapped.Parameters);
        Assert.Equal("object", mapped.Parameters!.Type);
        Assert.True(mapped.Parameters!.Properties!.ContainsKey("name"));
    }

    [Fact]
    public async Task BuiltInCommandCollision_Quit_StillExits()
    {
        using var env = new ExtTestEnv();
        using var fx = new LoopFixture(env);
        var provider = new ScriptedProvider("faux", "ok");
        fx.Registry.RegisterProvider(provider);

        using var extensions = ExtensionHost.LoadInMemory(fx.Cwd, new CollisionExtension());
        var input = new ScriptedInput(
        [
            ..Keys.Type("/quit"), Keys.Enter
        ]);
        var output = new CapturingOutput();
        var loop = LoopFixture.NewLoop(fx, input, output, extensions: extensions);

        var code = await loop.RunAsync();

        Assert.Equal(0, code);
        Assert.Empty(provider.Requests);
        Assert.DoesNotContain("hello from command", output.All);
    }

    [Fact]
    public async Task BuiltInToolNameCollision_AddsWarning_SkipsTool()
    {
        using var env = new ExtTestEnv();
        using var fx = new LoopFixture(env);
        var provider = new ScriptedProvider("faux", "ok");
        fx.Registry.RegisterProvider(provider);

        using var extensions = ExtensionHost.LoadInMemory(fx.Cwd, new CollisionExtension());
        var input = new ScriptedInput(
        [
            ..Keys.Type("plain turn"), Keys.Enter,
            Keys.CtrlC
        ]);
        var output = new CapturingOutput();
        var loop = LoopFixture.NewLoop(fx, input, output, extensions: extensions);

        await loop.RunAsync();

        Assert.Contains("name conflicts with a built-in tool", output.All);
    }

    [Fact]
    public async Task UnknownCommand_PrintsError()
    {
        using var env = new ExtTestEnv();
        using var fx = new LoopFixture(env);
        var provider = new ScriptedProvider("faux", "ok");
        fx.Registry.RegisterProvider(provider);

        var input = new ScriptedInput(
        [
            ..Keys.Type("/nope"), Keys.Enter,
            Keys.CtrlC
        ]);
        var output = new CapturingOutput();
        var loop = LoopFixture.NewLoop(fx, input, output);

        await loop.RunAsync();

        Assert.Contains(output.RedLines, l => l.Contains("Unknown command: /nope"));
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task ExtensionToolSpec_ExecutesReturningContent()
    {
        var executed = false;
        var spec = new ExtensionToolSpec("t", "d", null,
            (callId, args, ct) =>
            {
                executed = true;
                return Task.FromResult(new ExtensionToolResult("done"));
            });
        var tool = ExtensionToolAdapter.ToAgentTool(spec);

        var result = await tool.Execute("call-1", JsonDocument.Parse("{}").RootElement, CancellationToken.None, null);

        Assert.True(executed);
        Assert.Equal("done", result.Content);
        Assert.False(result.IsError);
        Assert.False(result.Terminate);
    }
}