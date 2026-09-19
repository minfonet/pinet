namespace PiNet.Permissions;

/// <summary>
/// Outcome of a permission rule or default policy for a tool call.
/// </summary>
public enum ToolPermission
{
    /// <summary>Run the tool without prompting.</summary>
    Allow,
    /// <summary>Ask the user before running the tool.</summary>
    Ask,
    /// <summary>Block the tool call and report the reason to the LLM.</summary>
    Deny,
}

/// <summary>
/// A single permission rule: a tool-name pattern, an optional cwd-relative
/// path pattern, and the permission to apply when both match.
/// </summary>
/// <param name="ToolPattern">Exact tool name or glob ('*' matches any sequence).</param>
/// <param name="PathPattern">Glob matched against the cwd-relative path (forward
/// slashes); null = applies regardless of path.</param>
/// <param name="Permission">Permission granted by this rule.</param>
public sealed record PermissionRule(string ToolPattern, string? PathPattern, ToolPermission Permission)
{
    /// <summary>
    /// True iff the tool name matches the tool pattern and (the path pattern is
    /// null or the relative path matches it). A rule with a path pattern never
    /// matches a call with no extractable path.
    /// </summary>
    public bool Matches(string toolName, string? relativePath)
    {
        if (!Glob.Match(ToolPattern, toolName, StringComparison.Ordinal))
            return false;

        if (PathPattern is null)
            return true;

        if (relativePath is null)
            return false;

        return Glob.Match(PathPattern, relativePath, PathComparison);
    }

    /// <summary>Path matching is case-insensitive on Windows, ordinal elsewhere.</summary>
    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}

/// <summary>
/// Minimal glob matcher: '*' matches any character sequence (including '/').
/// No other metacharacters.
/// </summary>
public static class Glob
{
    public static bool Match(string pattern, string value, StringComparison comparison)
    {
        var pi = 0;
        var vi = 0;
        var starPi = -1;
        var starVi = 0;

        while (vi < value.Length)
        {
            if (pi < pattern.Length && pattern[pi] == '*')
            {
                starPi = pi++;
                starVi = vi;
            }
            else if (pi < pattern.Length && CharEquals(pattern[pi], value[vi], comparison))
            {
                pi++;
                vi++;
            }
            else if (starPi >= 0)
            {
                starVi++;
                vi = starVi;
            }
            else
            {
                return false;
            }
        }

        while (pi < pattern.Length && pattern[pi] == '*')
            pi++;

        return pi == pattern.Length;
    }

    private static bool CharEquals(char a, char b, StringComparison comparison) =>
        comparison == StringComparison.Ordinal
            ? a == b
            : a.ToString().Equals(b.ToString(), comparison);
}
