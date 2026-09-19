using System.Text.Json;
using PiNet.Permissions;
using Xunit;

namespace PiNet.Permissions.Tests;

public class ConfigTests
{
    [Fact]
    public void Parse_ValidJson()
    {
        var cfg = PermissionConfig.Parse("<test>", """
            { "rules": [
                { "tool": "bash", "permission": "ask" },
                { "tool": "write_file", "path": "secrets/*", "permission": "deny" }
            ] }
            """);
        Assert.Equal(2, cfg.Rules.Count);
        Assert.Equal("bash", cfg.Rules[0].ToolPattern);
        Assert.Equal(ToolPermission.Ask, cfg.Rules[0].Permission);
        Assert.Equal("secrets/*", cfg.Rules[1].PathPattern);
        Assert.Equal(ToolPermission.Deny, cfg.Rules[1].Permission);
    }

    [Fact]
    public void Parse_EmptyRules()
    {
        var cfg = PermissionConfig.Parse("<test>", """{ "rules": [] }""");
        Assert.Empty(cfg.Rules);
    }

    [Fact]
    public void Parse_MalformedJson_ThrowsWithPath()
    {
        var ex = Assert.Throws<PermissionConfigException>(() => PermissionConfig.Parse("<test>", "{ not json"));
        Assert.Contains("invalid JSON", ex.Message);
        Assert.Equal("<test>", ex.Path);
    }

    [Fact]
    public void Parse_InvalidPermission_Throws()
    {
        Assert.Throws<PermissionConfigException>(
            () => PermissionConfig.Parse("<test>", """{ "rules": [ { "tool": "bash", "permission": "maybe" } ] }"""));
    }

    [Fact]
    public void Parse_MissingTool_Throws()
    {
        Assert.Throws<PermissionConfigException>(
            () => PermissionConfig.Parse("<test>", """{ "rules": [ { "permission": "ask" } ] }"""));
    }

    [Fact]
    public void ToJson_RoundTrips()
    {
        var cfg = new PermissionConfig
        (
            [
                new PermissionRule("bash", null, ToolPermission.Ask),
                new PermissionRule("write_file", "src/*", ToolPermission.Deny)
            ]
        );
        var parsed = PermissionConfig.Parse("<test>", cfg.ToJson());
        Assert.Equal(cfg.Rules, parsed.Rules);
    }

    [Fact]
    public async Task Load_MissingFile_ReturnsEmpty()
    {
        using var dirs = new TempDirs();
        var cfg = await PermissionConfig.LoadAsync(Path.Combine(dirs.Cwd, "nope.json"));
        Assert.Empty(cfg.Rules);
    }

    [Fact]
    public async Task Load_Save_RoundTrips()
    {
        using var dirs = new TempDirs();
        var path = Path.Combine(dirs.Cwd, "permissions.json");
        var cfg = new PermissionConfig([new PermissionRule("bash", null, ToolPermission.Allow)]);
        await PermissionConfig.SaveAsync(cfg, path);
        var loaded = await PermissionConfig.LoadAsync(path);
        Assert.Equal(cfg.Rules, loaded.Rules);
    }

    [Fact]
    public async Task GlobalConfigPath_RespectsPI_NET_HOME()
    {
        using var dirs = new TempDirs();
        var original = Environment.GetEnvironmentVariable("PI_NET_HOME");
        try
        {
            Environment.SetEnvironmentVariable("PI_NET_HOME", dirs.Cwd);
            Assert.Equal(Path.Combine(dirs.Cwd, "permissions.json"), PermissionPaths.GlobalConfigPath);

            // LoadAsync against cwd with no project config picks up the global one.
            await PermissionConfig.SaveAsync(
                new PermissionConfig([new PermissionRule("bash", null, ToolPermission.Deny)]),
                Path.Combine(dirs.Cwd, "permissions.json"));
            var resolver = await RulePermissionResolver.LoadAsync(dirs.Outside);
            var d = resolver.Resolve("bash", null, dirs.Outside);
            Assert.Equal(ToolPermission.Deny, d.Permission);
            Assert.Equal("global", d.Source);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PI_NET_HOME", original);
        }
    }
}
