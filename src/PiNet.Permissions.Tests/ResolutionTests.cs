using PiNet.Permissions;
using Xunit;

namespace PiNet.Permissions.Tests;

public class ResolutionTests
{
    private static RulePermissionResolver Resolver(
        IReadOnlyList<PermissionRule>? global = null,
        IReadOnlyList<PermissionRule>? project = null)
        => new(global ?? [], project ?? []);

    // --- Default policy (SR-05.01) ---

    [Fact]
    public void Default_Bash_Asks()
    {
        using var dirs = new TempDirs();
        var d = Resolver().Resolve("bash", null, dirs.Cwd);
        Assert.Equal(ToolPermission.Ask, d.Permission);
        Assert.Equal("default", d.Source);
    }

    [Fact]
    public void Default_ReadFile_Allows()
    {
        using var dirs = new TempDirs();
        var d = Resolver().Resolve("read_file", Path.Combine(dirs.Cwd, "a.txt"), dirs.Cwd);
        Assert.Equal(ToolPermission.Allow, d.Permission);
        Assert.Equal("default", d.Source);
    }

    [Fact]
    public void Default_WriteFileInsideCwd_Allows()
    {
        using var dirs = new TempDirs();
        var d = Resolver().Resolve("write_file", Path.Combine(dirs.Cwd, "src", "a.txt"), dirs.Cwd);
        Assert.Equal(ToolPermission.Allow, d.Permission);
        Assert.Equal("default", d.Source);
    }

    [Fact]
    public void Default_ReadOutsideCwd_Allows()
    {
        using var dirs = new TempDirs();
        var d = Resolver().Resolve("read_file", Path.Combine(dirs.Outside, "a.txt"), dirs.Cwd);
        Assert.Equal(ToolPermission.Allow, d.Permission);
    }

    // --- Safety floor (SR-05.02) ---

    [Fact]
    public void SafetyFloor_WriteOutsideCwd_Denies()
    {
        using var dirs = new TempDirs();
        var d = Resolver().Resolve("write_file", Path.Combine(dirs.Outside, "evil.txt"), dirs.Cwd);
        Assert.Equal(ToolPermission.Deny, d.Permission);
        Assert.Equal("safety", d.Source);
        Assert.Contains("evil.txt", d.Reason);
    }

    [Fact]
    public void SafetyFloor_EditOutsideCwd_Denies()
    {
        using var dirs = new TempDirs();
        var d = Resolver().Resolve("edit_file", Path.GetTempPath() + "evil.txt", dirs.Cwd);
        Assert.Equal(ToolPermission.Deny, d.Permission);
        Assert.Equal("safety", d.Source);
    }

    [Fact]
    public void SafetyFloor_CannotBeOverriddenBySessionGrant()
    {
        using var dirs = new TempDirs();
        var r = Resolver();
        r.AddSessionRule(new PermissionRule("write_file", null, ToolPermission.Allow));
        var d = r.Resolve("write_file", Path.Combine(dirs.Outside, "evil.txt"), dirs.Cwd);
        Assert.Equal(ToolPermission.Deny, d.Permission);
        Assert.Equal("safety", d.Source);
    }

    [Fact]
    public void SafetyFloor_RelativeEscapePath_Denies()
    {
        using var dirs = new TempDirs();
        var d = Resolver().Resolve("write_file", Path.Combine("..", "outside", "evil.txt"), dirs.Cwd);
        Assert.Equal(ToolPermission.Deny, d.Permission);
        Assert.Equal("safety", d.Source);
    }

    // --- Layer precedence and deny-stickiness (SR-05.04) ---

    [Fact]
    public void DenyIsSticky_GlobalDenyBeatsSessionAllow()
    {
        using var dirs = new TempDirs();
        var r = Resolver(global: [new PermissionRule("bash", null, ToolPermission.Deny)]);
        r.AddSessionRule(new PermissionRule("bash", null, ToolPermission.Allow));
        var d = r.Resolve("bash", null, dirs.Cwd);
        Assert.Equal(ToolPermission.Deny, d.Permission);
        Assert.Equal("global", d.Source);
    }

    [Fact]
    public void DenyIsSticky_ProjectDenyBeatsSessionAllow()
    {
        using var dirs = new TempDirs();
        var r = Resolver(project: [new PermissionRule("bash", null, ToolPermission.Deny)]);
        r.AddSessionRule(new PermissionRule("bash", null, ToolPermission.Allow));
        var d = r.Resolve("bash", null, dirs.Cwd);
        Assert.Equal(ToolPermission.Deny, d.Permission);
        Assert.Equal("project", d.Source);
    }

    [Fact]
    public void ProjectBeatsGlobal_ForNonDenyRules()
    {
        using var dirs = new TempDirs();
        var r = Resolver(
            global: [new PermissionRule("bash", null, ToolPermission.Ask)],
            project: [new PermissionRule("bash", null, ToolPermission.Allow)]);
        var d = r.Resolve("bash", null, dirs.Cwd);
        Assert.Equal(ToolPermission.Allow, d.Permission);
        Assert.Equal("project", d.Source);
    }

    [Fact]
    public void DenyIsSticky_GlobalDenyBeatsProjectAllow()
    {
        using var dirs = new TempDirs();
        var r = Resolver(
            global: [new PermissionRule("bash", null, ToolPermission.Deny)],
            project: [new PermissionRule("bash", null, ToolPermission.Allow)]);
        var d = r.Resolve("bash", null, dirs.Cwd);
        Assert.Equal(ToolPermission.Deny, d.Permission);
        Assert.Equal("global", d.Source);
    }

    [Fact]
    public void SessionBeatsProject()
    {
        using var dirs = new TempDirs();
        var r = Resolver(project: [new PermissionRule("bash", null, ToolPermission.Deny)]);
        // session has an allow, but project denies → deny wins (sticky).
        r.AddSessionRule(new PermissionRule("bash", null, ToolPermission.Allow));
        Assert.Equal(ToolPermission.Deny, r.Resolve("bash", null, dirs.Cwd).Permission);

        // without the deny: session allow beats project ask.
        var r2 = Resolver(project: [new PermissionRule("bash", null, ToolPermission.Ask)]);
        r2.AddSessionRule(new PermissionRule("bash", null, ToolPermission.Allow));
        var d = r2.Resolve("bash", null, dirs.Cwd);
        Assert.Equal(ToolPermission.Allow, d.Permission);
        Assert.Equal("session", d.Source);
    }

    [Fact]
    public void WithinALayer_AnyMatchingDenyWins()
    {
        using var dirs = new TempDirs();
        var r = Resolver(project:
        [
            new PermissionRule("bash", null, ToolPermission.Allow),
            new PermissionRule("bash", null, ToolPermission.Deny)
        ]);
        var d = r.Resolve("bash", null, dirs.Cwd);
        Assert.Equal(ToolPermission.Deny, d.Permission);
        Assert.Equal("project", d.Source);
    }

    [Fact]
    public void PathScopedRule_DoesNotApplyToBash()
    {
        using var dirs = new TempDirs();
        var r = Resolver(project: [new PermissionRule("bash", "x", ToolPermission.Deny)]);
        var d = r.Resolve("bash", null, dirs.Cwd);
        Assert.Equal(ToolPermission.Ask, d.Permission); // falls through to default
        Assert.Equal("default", d.Source);
    }

    [Fact]
    public void PathScopedDeny_AppliesToMatchingPathOnly()
    {
        using var dirs = new TempDirs();
        var r = Resolver(project: [new PermissionRule("write_file", "secrets/*", ToolPermission.Deny)]);

        var denied = r.Resolve("write_file", Path.Combine(dirs.Cwd, "secrets", "key.txt"), dirs.Cwd);
        Assert.Equal(ToolPermission.Deny, denied.Permission);
        Assert.Equal("project", denied.Source);

        var allowed = r.Resolve("write_file", Path.Combine(dirs.Cwd, "src", "a.txt"), dirs.Cwd);
        Assert.Equal(ToolPermission.Allow, allowed.Permission);
    }

    [Fact]
    public void SetProjectRules_ReplacesLayer()
    {
        using var dirs = new TempDirs();
        var r = Resolver(project: [new PermissionRule("bash", null, ToolPermission.Allow)]);
        Assert.Equal(ToolPermission.Allow, r.Resolve("bash", null, dirs.Cwd).Permission);

        r.SetProjectRules([]);
        var d = r.Resolve("bash", null, dirs.Cwd);
        Assert.Equal(ToolPermission.Ask, d.Permission);
        Assert.Equal("default", d.Source);
    }

    [Fact]
    public void NoApproveStyle_SessionAskRule_RestoresPrompting()
    {
        using var dirs = new TempDirs();
        var r = Resolver(project: [new PermissionRule("bash", null, ToolPermission.Allow)]);
        r.AddSessionRule(new PermissionRule("bash", null, ToolPermission.Ask));
        var d = r.Resolve("bash", null, dirs.Cwd);
        Assert.Equal(ToolPermission.Ask, d.Permission);
        Assert.Equal("session", d.Source);
    }
}
