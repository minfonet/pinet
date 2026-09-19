namespace PiNet.Permissions;

/// <summary>
/// Persists the project trust decision in .pi/permissions.json.
/// "Trusted" = the project contains the rule {tool:"bash", permission:"allow"}.
/// </summary>
public static class TrustStore
{
    private static readonly PermissionRule TrustRule = new("bash", null, ToolPermission.Allow);

    /// <summary>
    /// Merges the trust rule into the project config (idempotent).
    /// Returns true if the file changed.
    /// </summary>
    public static async Task<bool> TrustAsync(string cwd, CancellationToken ct = default)
    {
        var config = await LoadProjectAsync(cwd, ct);
        if (config.Rules.Contains(TrustRule))
            return false;
        config.Rules.Add(TrustRule);
        await PermissionConfig.SaveAsync(config, PermissionPaths.ProjectConfigPath(cwd), ct);
        return true;
    }

    /// <summary>
    /// Removes the trust rule from the project config (no-op if absent).
    /// Returns true if the file changed.
    /// </summary>
    public static async Task<bool> UntrustAsync(string cwd, CancellationToken ct = default)
    {
        var config = await LoadProjectAsync(cwd, ct);
        var removed = config.Rules.Remove(TrustRule);
        if (!removed)
            return false;
        await PermissionConfig.SaveAsync(config, PermissionPaths.ProjectConfigPath(cwd), ct);
        return true;
    }

    /// <summary>True iff the project config contains the trust rule.</summary>
    public static bool IsTrusted(string cwd)
    {
        var path = PermissionPaths.ProjectConfigPath(cwd);
        if (!File.Exists(path))
            return false;
        return PermissionConfig.Parse(path, File.ReadAllText(path)).Rules.Contains(TrustRule);
    }

    private static async Task<PermissionConfig> LoadProjectAsync(string cwd, CancellationToken ct)
    {
        var path = PermissionPaths.ProjectConfigPath(cwd);
        if (!File.Exists(path))
            return new PermissionConfig();
        return await PermissionConfig.LoadAsync(path, ct);
    }
}
