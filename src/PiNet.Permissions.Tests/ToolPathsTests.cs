using System.Text.Json;
using PiNet.Permissions;
using Xunit;

namespace PiNet.Permissions.Tests;

public class ToolPathsTests
{
    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;

    [Theory]
    [InlineData("read_file", """{"path":"src/a.txt"}""", "src/a.txt")]
    [InlineData("write_file", """{"path":"src/b.txt"}""", "src/b.txt")]
    [InlineData("edit_file", """{"path":"src/c.txt"}""", "src/c.txt")]
    [InlineData("bash", """{"command":"ls"}""", null)]
    [InlineData("unknown_tool", """{"path":"x"}""", null)]
    public void ExtractPath(string tool, string argsJson, string? expected)
    {
        Assert.Equal(expected, ToolPaths.ExtractPath(tool, Args(argsJson)));
    }

    [Theory]
    [InlineData("read_file", """{}""", null)]
    [InlineData("read_file", """{"path":123}""", null)]
    public void ExtractPath_MissingOrWrongType_ReturnsNull(string tool, string argsJson, string? expected)
    {
        Assert.Equal(expected, ToolPaths.ExtractPath(tool, Args(argsJson)));
    }

    [Fact]
    public void ExtractCommand_Bash()
    {
        Assert.Equal("ls -la", ToolPaths.ExtractCommand("bash", Args("""{"command":"ls -la"}""")));
    }

    [Fact]
    public void ExtractCommand_NonBash_ReturnsNull()
    {
        Assert.Null(ToolPaths.ExtractCommand("read_file", Args("""{"path":"a"}""")));
        Assert.Null(ToolPaths.ExtractCommand("bash", Args("""{}""")));
    }
}
