namespace PiNet.Permissions;

/// <summary>User's answer to a permission prompt.</summary>
public enum PermissionChoice
{
    /// <summary>Allow this call once.</summary>
    Allow,
    /// <summary>Allow this tool for the rest of the session.</summary>
    AllowSession,
    /// <summary>Deny this call.</summary>
    Deny,
}

/// <summary>Everything the UI needs to render an Ask prompt.</summary>
public sealed record PermissionRequest(
    string ToolName,
    string? Path,     // absolute path for file tools
    string? Command,  // command string for bash
    string Source,    // why prompting is required (rule source)
    string? Reason);

/// <summary>
/// Asks the user how to handle an Ask decision. Implementations decide how
/// (and whether) to interact with a human.
/// </summary>
public interface IPermissionPrompt
{
    Task<PermissionChoice> PromptAsync(PermissionRequest request, CancellationToken ct);
}

/// <summary>Always allows (used by --approve).</summary>
public sealed class AutoApprovePrompt : IPermissionPrompt
{
    public Task<PermissionChoice> PromptAsync(PermissionRequest request, CancellationToken ct) =>
        Task.FromResult(PermissionChoice.Allow);
}

/// <summary>
/// Always denies and prints a one-line notice (non-interactive fallback for
/// redirected stdin without --approve).
/// </summary>
public sealed class DenyAllPrompt : IPermissionPrompt
{
    private readonly TextWriter _output;

    public DenyAllPrompt(TextWriter output) => _output = output;

    public Task<PermissionChoice> PromptAsync(PermissionRequest request, CancellationToken ct)
    {
        _output.WriteLine(
            $"(non-interactive: cannot prompt for '{request.ToolName}'; denied. Use --approve to allow.)");
        return Task.FromResult(PermissionChoice.Deny);
    }
}
