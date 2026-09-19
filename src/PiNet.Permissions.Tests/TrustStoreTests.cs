using PiNet.Permissions;
using Xunit;

namespace PiNet.Permissions.Tests;

public class TrustStoreTests
{
    [Fact]
    public async Task Trust_CreatesProjectRule()
    {
        using var dirs = new TempDirs();
        var changed = await TrustStore.TrustAsync(dirs.Cwd);
        Assert.True(changed);
        Assert.True(TrustStore.IsTrusted(dirs.Cwd));

        var cfg = await PermissionConfig.LoadAsync(PermissionPaths.ProjectConfigPath(dirs.Cwd));
        Assert.Contains(cfg.Rules, r => r.ToolPattern == "bash" && r.Permission == ToolPermission.Allow);
    }

    [Fact]
    public async Task Trust_IsIdempotent()
    {
        using var dirs = new TempDirs();
        Assert.True(await TrustStore.TrustAsync(dirs.Cwd));
        Assert.False(await TrustStore.TrustAsync(dirs.Cwd));
    }

    [Fact]
    public async Task Trust_MergesWithExistingRules()
    {
        using var dirs = new TempDirs();
        await PermissionConfig.SaveAsync(new PermissionConfig(
            [new PermissionRule("write_file", "secrets/*", ToolPermission.Deny)]
        ), PermissionPaths.ProjectConfigPath(dirs.Cwd));

        await TrustStore.TrustAsync(dirs.Cwd);
        var cfg = await PermissionConfig.LoadAsync(PermissionPaths.ProjectConfigPath(dirs.Cwd));
        Assert.Equal(2, cfg.Rules.Count);
        Assert.Contains(cfg.Rules, r => r.ToolPattern == "write_file" && r.Permission == ToolPermission.Deny);
        Assert.Contains(cfg.Rules, r => r.ToolPattern == "bash" && r.Permission == ToolPermission.Allow);
    }

    [Fact]
    public async Task Untrust_RemovesRule()
    {
        using var dirs = new TempDirs();
        await TrustStore.TrustAsync(dirs.Cwd);
        Assert.True(await TrustStore.UntrustAsync(dirs.Cwd));
        Assert.False(TrustStore.IsTrusted(dirs.Cwd));
        Assert.False(await TrustStore.UntrustAsync(dirs.Cwd)); // idempotent
    }

    [Fact]
    public async Task Trust_AffectsResolverAfterReload()
    {
        using var dirs = new TempDirs();
        var resolver = await RulePermissionResolver.LoadAsync(dirs.Cwd);
        Assert.Equal(ToolPermission.Ask, resolver.Resolve("bash", null, dirs.Cwd).Permission);

        await TrustStore.TrustAsync(dirs.Cwd);
        resolver.SetProjectRules((await PermissionConfig.LoadAsync(PermissionPaths.ProjectConfigPath(dirs.Cwd))).Rules);
        var d = resolver.Resolve("bash", null, dirs.Cwd);
        Assert.Equal(ToolPermission.Allow, d.Permission);
        Assert.Equal("project", d.Source);
    }
}
