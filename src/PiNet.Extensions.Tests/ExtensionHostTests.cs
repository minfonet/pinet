using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace PiNet.Extensions.Tests;

public class ExtensionHostTests
{
    private const string SampleExtension = """
        using System.Threading;
        using System.Threading.Tasks;
        using PiNet.Extensions;

        public sealed class HelloExtension : IExtension
        {
            public void Register(IExtensionContext ctx)
            {
                ctx.RegisterTool(new ExtensionToolSpec(
                    "hello",
                    "Say hello",
                    null,
                    (callId, args, ct) => Task.FromResult(new ExtensionToolResult("Hello from extension!"))));
                ctx.RegisterCommand("hello", (args, c) =>
                {
                    c.WriteLine("hello from command");
                    return Task.FromResult(true);
                });
            }
        }
        """;

    private const string BrokenExtension = """
        using PiNet.Extensions;

        public sealed class BadExtension : IExtension
        {
            public void Register(IExtensionContext ctx) {
                this will not compile
            }
        }
        """;

    [Fact]
    public void LoadInMemory_RegistersToolsAndCommands()
    {
        using var dirs = new TestDirs();
        var input = new InMemoryExtension();
        var host = ExtensionHost.LoadInMemory(dirs.Cwd, input);

        try
        {
            Assert.Contains(host.Tools, t => t.Name == "mem-tool" && t.Description == "Mem tool");
            Assert.Contains(host.Commands.Keys, n => n == "mem-cmd");
            Assert.Empty(host.Warnings);
        }
        finally
        {
            host.Dispose();
        }
    }

    [Fact]
    public void LoadInMemory_DuplicateTool_ReportsWarning()
    {
        using var dirs = new TestDirs();
        var dup = new DuplicateToolExtension();
        var host = ExtensionHost.LoadInMemory(dirs.Cwd, dup, dup);

        try
        {
            Assert.Single(host.Tools);
            Assert.Contains(host.Warnings, w => w.Contains("duplicate"));
        }
        finally
        {
            host.Dispose();
        }
    }

    [Fact]
    public async Task CommandCollision_WithBuiltInName_SkippedByAdapter_NotHost()
    {
        using var dirs = new TestDirs();
        var host = ExtensionHost.LoadInMemory(dirs.Cwd, new CommandCollisionExtension());
        try
        {
            Assert.Contains(host.Commands.Keys, n => n == "quit");
        }
        finally
        {
            host.Dispose();
        }
    }

    [Fact]
    public async Task CompileAndLoad_SampleCsExtension()
    {
        if (!DotnetSdkAvailable())
            return;

        using var dirs = new TestDirs();
        TestDirs.WriteFile(dirs.Home, "extensions/hello.cs", SampleExtension);

        using var host = await ExtensionHost.LoadAsync(dirs.Cwd);

        Assert.DoesNotContain(host.Warnings, w => w.Contains("disabled"));
        Assert.Contains(host.Tools, t => t.Name == "hello" && t.Description == "Say hello");
        Assert.Contains(host.Commands.Keys, n => n == "hello");

        var tool = host.Tools.Single(t => t.Name == "hello");
        var result = await tool.Execute("call-1", JsonDocument.Parse("{}").RootElement, CancellationToken.None);
        Assert.Equal("Hello from extension!", result.Content);
    }

    [Fact]
    public async Task CompileFailure_ProducesWarnings_NotCrash()
    {
        if (!DotnetSdkAvailable())
            return;

        using var dirs = new TestDirs();
        TestDirs.WriteFile(dirs.Home, "extensions/broken.cs", BrokenExtension);

        using var host = await ExtensionHost.LoadAsync(dirs.Cwd);

        Assert.NotEmpty(host.Warnings);
        Assert.DoesNotContain(host.Tools, t => t.Name == "broken");
    }

    [Fact]
    public async Task ContentHashCache_IsReused()
    {
        if (!DotnetSdkAvailable())
            return;

        using var dirs = new TestDirs();
        TestDirs.WriteFile(dirs.Home, "extensions/hello.cs", SampleExtension);

        using (var first = await ExtensionHost.LoadAsync(dirs.Cwd))
        {
            Assert.Contains(first.Tools, t => t.Name == "hello");
        }

        var cacheDir = Path.Combine(dirs.Home, "cache", "extensions");
        Assert.True(Directory.Exists(cacheDir), "cache dir should exist after a compile");
        var before = Directory.GetFiles(cacheDir, "*.dll").Length;
        Assert.Equal(1, before);

        using (var second = await ExtensionHost.LoadAsync(dirs.Cwd))
        {
            Assert.Contains(second.Tools, t => t.Name == "hello");
        }

        Assert.Equal(before, Directory.GetFiles(cacheDir, "*.dll").Length);
    }

    [Fact]
    public async Task NoExtensionFiles_YieldsEmptyHost()
    {
        using var dirs = new TestDirs();

        using var host = await ExtensionHost.LoadAsync(dirs.Cwd);

        Assert.Empty(host.Tools);
        Assert.Empty(host.Commands);
    }

    private static bool DotnetSdkAvailable()
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    ArgumentList = { "--list-sdks" },
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            process.Start();
            process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private sealed class InMemoryExtension : IExtension
    {
        public void Register(IExtensionContext ctx)
        {
            ctx.RegisterTool(new ExtensionToolSpec("mem-tool", "Mem tool", null,
                (callId, args, ct) => Task.FromResult(new ExtensionToolResult("ok"))));
            ctx.RegisterCommand("mem-cmd", (args, c) =>
            {
                c.WriteLine("ran");
                return Task.FromResult(true);
            });
        }
    }

    private sealed class DuplicateToolExtension : IExtension
    {
        public void Register(IExtensionContext ctx)
        {
            ctx.RegisterTool(new ExtensionToolSpec("dup", "First", null,
                (callId, args, ct) => Task.FromResult(new ExtensionToolResult("1"))));
            ctx.RegisterTool(new ExtensionToolSpec("dup", "Second", null,
                (callId, args, ct) => Task.FromResult(new ExtensionToolResult("2"))));
        }
    }

    private sealed class CommandCollisionExtension : IExtension
    {
        public void Register(IExtensionContext ctx)
        {
            ctx.RegisterTool(new ExtensionToolSpec("hello", "Hello", null,
                (callId, args, ct) => Task.FromResult(new ExtensionToolResult("hi"))));
            ctx.RegisterCommand("quit", (args, c) =>
            {
                c.WriteLine("extension quit");
                return Task.FromResult(true);
            });
        }
    }
}