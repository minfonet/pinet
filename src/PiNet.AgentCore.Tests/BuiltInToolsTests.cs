using System.Text.Json;
using PiNet.Abstractions.Chat;
using PiNet.AgentCore.Tools;

namespace PiNet.AgentCore.Tests;

public class BuiltInToolsTests
{
    [Fact]
    public async Task ReadFile_ReturnsUtf8Content()
    {
        var dir = NewTempDir();
        try
        {
            var path = Path.Combine(dir, "input.txt");
            await File.WriteAllTextAsync(path, "content-123\nsecond line");

            var result = await InvokeArgs(BuiltInTools.ReadFile, new { path });

            Assert.False(result.IsError);
            Assert.Equal("content-123\nsecond line", result.Content);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task ReadFile_MissingFile_ReturnsError()
    {
        var dir = NewTempDir();
        try
        {
            var path = Path.Combine(dir, "nope.txt");
            var result = await InvokeArgs(BuiltInTools.ReadFile, new { path });

            Assert.True(result.IsError);
            Assert.Contains("not found", result.Content, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task WriteFile_CreatesParentDirectories_AndWritesContent()
    {
        var dir = NewTempDir();
        try
        {
            var path = Path.Combine(dir, "a", "b", "out.txt");
            var result = await InvokeArgs(BuiltInTools.WriteFile, new { path, content = "written!" });

            Assert.False(result.IsError);
            Assert.Equal("written!", await File.ReadAllTextAsync(path));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task EditFile_ReplacesSingleUniqueOccurrence()
    {
        var dir = NewTempDir();
        try
        {
            var path = Path.Combine(dir, "doc.txt");
            await File.WriteAllTextAsync(path, "hello world hello");

            var result = await InvokeArgs(BuiltInTools.EditFile,
                new { path, old_string = "world", new_string = "there" });

            Assert.False(result.IsError);
            Assert.Equal("hello there hello", await File.ReadAllTextAsync(path));

            var missing = await InvokeArgs(BuiltInTools.EditFile,
                new { path, old_string = "zzz", new_string = "x" });
            Assert.True(missing.IsError);
            Assert.Contains("could not find", missing.Content, StringComparison.OrdinalIgnoreCase);

            var multiple = await InvokeArgs(BuiltInTools.EditFile,
                new { path, old_string = "hello", new_string = "x" });
            Assert.True(multiple.IsError);
            Assert.Contains("requires exactly one match", multiple.Content, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Bash_EchoCommand_ReturnsCapturedOutput()
    {
        var result = await InvokeArgs(BuiltInTools.Bash, new { command = "echo hello-pinet" });

        Assert.False(result.IsError);
        Assert.Contains("hello-pinet", result.Content);
    }

    [Fact]
    public async Task Bash_NonZeroExit_ReturnsErrorWithExitCode()
    {
        var result = await InvokeArgs(BuiltInTools.Bash, new { command = "exit 3" });

        Assert.True(result.IsError);
        Assert.Contains("3", result.Content);
    }

    [Fact]
    public async Task Ac1_AgentReadsFile_ThenWritesAnotherBasedOnIt_FullMultiTurnLoop()
    {
        var dir = NewTempDir();
        try
        {
            var seed = Path.Combine(dir, "seed.txt");
            await File.WriteAllTextAsync(seed, "original content");
            var output = Path.Combine(dir, "out", "copy.txt");

            static string PathArgs(string path) =>
                JsonSerializer.Serialize(new { path });

            var runner = new ScriptedRunner()
                .Enqueue(r => ScriptedRunner.ToolTurn("call_1", "read_file", PathArgs(seed)))
                .Enqueue(r =>
                {
                    var content = r.Messages.Last(m => m.Role == ChatRole.Tool).ReadText();
                    return ScriptedRunner.ToolTurn("call_2", "write_file",
                        JsonSerializer.Serialize(new { path = output, content = $"based-on: {content}" }));
                })
                .Enqueue(_ => ScriptedRunner.Answer("file copied"));

            var agent = Helpers.NewAgent(runner, tools: BuiltInTools.All());
            var events = Helpers.Record(agent);

            await agent.PromptAsync("copy seed.txt");

            Assert.Equal(3, runner.GenerateCalls);
            Assert.True(File.Exists(output));
            Assert.Equal("based-on: original content", await File.ReadAllTextAsync(output));
            Assert.Equal(2, events.Count(e => e is ToolExecutionEndEvent));
        }
        finally { Directory.Delete(dir, true); }
    }

    private static Task<ToolResult> InvokeArgs(AgentTool tool, object args) =>
        tool.Execute("test-call", JsonSerializer.SerializeToElement(args), default, null);

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pinet-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}