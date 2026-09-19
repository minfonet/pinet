using System.Text.Json;

namespace PiNet.Permissions;

/// <summary>
/// Rule-based permission resolver with three layers (session → project → global)
/// plus the built-in default policy and the out-of-cwd write safety floor.
///
/// Resolution algorithm (see spec 05, §3.2):
/// 1. Safety floor: write_file/edit_file outside the cwd is always Deny.
/// 2. Any matching Deny rule in any layer wins (deny is sticky).
/// 3. Otherwise the first matching rule in layer order session → project →
///    global decides (file order within a layer).
/// 4. Otherwise the default policy: bash → Ask, everything else → Allow.
/// </summary>
public sealed class RulePermissionResolver : IPermissionResolver
{
    private readonly object _gate = new();
    private List<PermissionRule> _session;
    private List<PermissionRule> _project;

    public RulePermissionResolver(IReadOnlyList<PermissionRule> global, IReadOnlyList<PermissionRule> project)
    {
        GlobalRules = global;
        _project = [.. project];
        _session = [];
    }

    /// <summary>Session-layer rules (in-memory, per run).</summary>
    public IReadOnlyList<PermissionRule> SessionRules { get { lock (_gate) return _session; } }

    /// <summary>Project-layer rules (from .pi/permissions.json).</summary>
    public IReadOnlyList<PermissionRule> ProjectRules { get { lock (_gate) return _project; } }

    /// <summary>Global-layer rules (from &lt;PI_NET_HOME&gt;/permissions.json).</summary>
    public IReadOnlyList<PermissionRule> GlobalRules { get; }

    /// <summary>
    /// Loads global and project config files (missing files contribute zero rules).
    /// </summary>
    public static async Task<RulePermissionResolver> LoadAsync(string cwd, CancellationToken ct = default)
    {
        var global = await PermissionConfig.LoadAsync(PermissionPaths.GlobalConfigPath, ct);
        var project = await PermissionConfig.LoadAsync(PermissionPaths.ProjectConfigPath(cwd), ct);
        return new RulePermissionResolver(global.Rules, project.Rules);
    }

    public void AddSessionRule(PermissionRule rule)
    {
        lock (_gate) _session.Add(rule);
    }

    public void SetProjectRules(IReadOnlyList<PermissionRule> rules)
    {
        lock (_gate) _project = [.. rules];
    }

    public PermissionDecision Resolve(string toolName, string? absolutePath, string cwd)
    {
        List<PermissionRule> sessionRules, projectRules;
        lock (_gate)
        {
            sessionRules = _session;
            projectRules = _project;
        }

        var relativePath = PathScope.Relative(absolutePath, cwd);

        // 1. Safety floor: destructive writes outside the cwd are never allowed.
        if (toolName is "write_file" or "edit_file"
            && relativePath is not null
            && !PathScope.IsInsideCwd(absolutePath, cwd))
        {
            return new PermissionDecision(
                ToolPermission.Deny, "safety",
                $"writes outside the working directory are not allowed ({absolutePath})");
        }

        // 2. Deny is sticky: any matching deny in any layer wins.
        if (LayerVerdict(sessionRules, toolName, relativePath) == ToolPermission.Deny)
            return new PermissionDecision(ToolPermission.Deny, "session", "denied by session rule");
        if (LayerVerdict(projectRules, toolName, relativePath) == ToolPermission.Deny)
            return new PermissionDecision(ToolPermission.Deny, "project", "denied by project rule");
        if (LayerVerdict(GlobalRules, toolName, relativePath) == ToolPermission.Deny)
            return new PermissionDecision(ToolPermission.Deny, "global", "denied by global rule");

        // 3. First matching non-deny rule in layer order decides.
        var session = LayerVerdict(sessionRules, toolName, relativePath);
        if (session is not null)
            return new PermissionDecision(session.Value, "session", ReasonFor(session.Value, "session"));
        var project = LayerVerdict(projectRules, toolName, relativePath);
        if (project is not null)
            return new PermissionDecision(project.Value, "project", ReasonFor(project.Value, "project"));
        var global = LayerVerdict(GlobalRules, toolName, relativePath);
        if (global is not null)
            return new PermissionDecision(global.Value, "global", ReasonFor(global.Value, "global"));

        // 4. Default policy: bash asks, reads (and everything else) are allowed.
        return toolName == "bash"
            ? new PermissionDecision(ToolPermission.Ask, "default", "bash always asks by default")
            : new PermissionDecision(ToolPermission.Allow, "default", null);
    }

    /// <summary>
    /// The verdict of one layer: Deny if any matching rule denies, otherwise the
    /// first matching Allow/Ask rule, otherwise null (no match).
    /// </summary>
    private static ToolPermission? LayerVerdict(IReadOnlyList<PermissionRule> rules, string toolName, string? relativePath)
    {
        ToolPermission? first = null;
        foreach (var rule in rules)
        {
            if (!rule.Matches(toolName, relativePath))
                continue;
            if (rule.Permission == ToolPermission.Deny)
                return ToolPermission.Deny;
            first ??= rule.Permission;
        }
        return first;
    }

    private static string? ReasonFor(ToolPermission permission, string source) =>
        permission == ToolPermission.Ask ? $"asking per {source} rule" : null;
}

/// <summary>
/// Path scoping helpers: cwd-relative paths (forward slashes) and the
/// inside-cwd check used by the safety floor.
/// </summary>
public static class PathScope
{
    private static StringComparison CwdComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// cwd-relative path with forward slashes; '..' segments are preserved when
    /// the path escapes the cwd. Null when the input is null/empty.
    /// </summary>
    public static string? Relative(string? absolutePath, string cwd)
    {
        if (string.IsNullOrWhiteSpace(absolutePath))
            return null;
        return Path.GetRelativePath(cwd, ResolveAgainst(absolutePath, cwd)).Replace('\\', '/');
    }

    /// <summary>
    /// True iff the (absolute or cwd-relative) path resolves inside the cwd.
    /// </summary>
    public static bool IsInsideCwd(string? path, string cwd)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        var full = ResolveAgainst(path, cwd);
        var cwdFull = Path.GetFullPath(cwd);
        return full.StartsWith(cwdFull + Path.DirectorySeparatorChar, CwdComparison)
            || full.Equals(cwdFull, CwdComparison);
    }

    /// <summary>Resolves a path against the given cwd (not the process CWD).</summary>
    private static string ResolveAgainst(string path, string cwd)
    {
        var cwdFull = Path.GetFullPath(cwd);
        return Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(cwdFull, path));
    }
}
