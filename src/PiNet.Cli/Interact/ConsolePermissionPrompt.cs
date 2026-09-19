using PiNet.Permissions;

namespace PiNet.Cli.Interact;

/// <summary>
/// Console permission prompt (spec 05 §3.5). Renders the request, then reads
/// one keystroke: a/A → allow once, s/S → allow for session,
/// d/D/Enter/EOF/anything else → deny.
/// </summary>
public sealed class ConsolePermissionPrompt : IPermissionPrompt
{
    private readonly IConsoleInput _input;
    private readonly IConsoleOutput _output;

    public ConsolePermissionPrompt(IConsoleInput input, IConsoleOutput output)
    {
        _input = input;
        _output = output;
    }

    public Task<PermissionChoice> PromptAsync(PermissionRequest request, CancellationToken ct)
    {
        _output.WriteLine($"⚠ Permission required — {request.ToolName} [{request.Source}]", ConsoleColor.Yellow);
        if (request.Command is not null)
            _output.WriteLine($"  $ {request.Command}");
        else if (request.Path is not null)
            _output.WriteLine($"  path: {request.Path}");
        _output.WriteLine("  [a] allow once   [s] allow for session   [d] deny (default)");
        _output.Write("> ");

        var key = _input.ReadKey();
        _output.WriteLine();

        // Switch on the character pressed (KeyChar), not the scan code (Key):
        // scan codes are layout-dependent and unreliable for letter keys.
        var choice = key is null
            ? PermissionChoice.Deny
            : char.ToLowerInvariant(key.Value.KeyChar) switch
            {
                'a' => PermissionChoice.Allow,
                's' => PermissionChoice.AllowSession,
                _ => PermissionChoice.Deny
            };
        return Task.FromResult(choice);
    }
}
