namespace PiNet.Permissions;

/// <summary>
/// Outcome of resolving a tool call against the permission layers.
/// </summary>
/// <param name="Permission">Allow, Ask, or Deny.</param>
/// <param name="Source">"safety" | "session" | "project" | "global" | "default".</param>
/// <param name="Reason">Human-readable reason (for prompts and deny messages).</param>
public sealed record PermissionDecision(ToolPermission Permission, string Source, string? Reason);

/// <summary>
/// Resolves tool calls to permission decisions across the rule layers
/// (session → project → global) and the built-in default policy.
/// </summary>
public interface IPermissionResolver
{
    /// <summary>
    /// Resolves a tool call. Synchronous: pure in-memory evaluation, no IO.
    /// </summary>
    /// <param name="toolName">Tool name as invoked by the model.</param>
    /// <param name="absolutePath">Absolute path affected by the call, if extractable; else null.</param>
    /// <param name="cwd">Working directory the rules are relative to.</param>
    PermissionDecision Resolve(string toolName, string? absolutePath, string cwd);

    /// <summary>Adds an in-memory session rule (e.g. "allow for session").</summary>
    void AddSessionRule(PermissionRule rule);

    /// <summary>Replaces the project-layer rules (e.g. after /trust).</summary>
    void SetProjectRules(IReadOnlyList<PermissionRule> rules);
}
