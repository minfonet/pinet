using PiNet.Permissions;
using Xunit;

namespace PiNet.Permissions.Tests;

public class RuleMatchingTests
{
    [Fact]
    public void ExactToolName_Matches()
    {
        var rule = new PermissionRule("bash", null, ToolPermission.Ask);
        Assert.True(rule.Matches("bash", null));
        Assert.False(rule.Matches("read_file", null));
    }

    [Fact]
    public void ToolGlob_Matches()
    {
        Assert.True(new PermissionRule("*", null, ToolPermission.Deny).Matches("bash", null));
        Assert.True(new PermissionRule("write_*", null, ToolPermission.Deny).Matches("write_file", "src/a.txt"));
        Assert.False(new PermissionRule("write_*", null, ToolPermission.Deny).Matches("read_file", "src/a.txt"));
    }

    [Fact]
    public void PathPattern_MatchesRelativePaths()
    {
        var rule = new PermissionRule("write_file", "secrets/*", ToolPermission.Deny);
        Assert.True(rule.Matches("write_file", "secrets/key.txt"));
        Assert.True(rule.Matches("write_file", "secrets/deep/key.txt")); // '*' crosses '/'
        Assert.False(rule.Matches("write_file", "src/foo.cs"));
    }

    [Fact]
    public void PathPatternRule_NeverMatchesCallWithoutPath()
    {
        var rule = new PermissionRule("bash", "whatever", ToolPermission.Deny);
        Assert.False(rule.Matches("bash", null));
    }

    [Fact]
    public void NullPathPattern_MatchesAnyCall()
    {
        var rule = new PermissionRule("bash", null, ToolPermission.Ask);
        Assert.True(rule.Matches("bash", null));
        Assert.True(rule.Matches("bash", "some/path.txt"));
    }

    [Fact]
    public void StarMatchesEverything()
    {
        Assert.True(new PermissionRule("*", "*", ToolPermission.Deny).Matches("anything", "a/b/c"));
        Assert.True(Glob.Match("*", "", StringComparison.Ordinal));
        Assert.False(Glob.Match("a*", "", StringComparison.Ordinal));
        Assert.True(Glob.Match("", "", StringComparison.Ordinal));
    }
}
