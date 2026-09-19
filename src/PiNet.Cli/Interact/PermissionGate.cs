using System.Text.Json;
using PiNet.Abstractions.Chat;
using PiNet.AgentCore;
using PiNet.Permissions;

namespace PiNet.Cli.Interact;

/// <summary>
/// Adapts the permission resolver + prompt to the agent's BeforeToolCall hook.
/// See spec 05 §3.6.
/// </summary>
public static class PermissionGate
{
    public static BeforeToolCallDelegate Create(IPermissionResolver resolver, IPermissionPrompt prompt, string cwd)
    {
        return async (ToolCallContent toolCall, JsonElement arguments, IReadOnlyList<ChatMessage> context, CancellationToken ct) =>
        {
            var path = ToolPaths.ExtractPath(toolCall.Name, arguments);
            var decision = resolver.Resolve(toolCall.Name, path, cwd);

            switch (decision.Permission)
            {
                case ToolPermission.Allow:
                    return ToolCallDecision.Allow();

                case ToolPermission.Deny:
                    return ToolCallDecision.Block($"Permission denied: {decision.Reason ?? decision.Source}");

                default:
                    var request = new PermissionRequest(
                        toolCall.Name,
                        path,
                        ToolPaths.ExtractCommand(toolCall.Name, arguments),
                        decision.Source,
                        decision.Reason);
                    var choice = await prompt.PromptAsync(request, ct);
                    switch (choice)
                    {
                        case PermissionChoice.Allow:
                            return ToolCallDecision.Allow();
                        case PermissionChoice.AllowSession:
                            resolver.AddSessionRule(new PermissionRule(toolCall.Name, null, ToolPermission.Allow));
                            return ToolCallDecision.Allow();
                        default:
                            return ToolCallDecision.Block("Denied by user");
                    }
            }
        };
    }
}
