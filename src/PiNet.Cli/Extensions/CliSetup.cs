using PiNet.Abstractions.Models;
using PiNet.AgentCore;
using PiNet.Cli.Interact;
using PiNet.Extensions;

namespace PiNet.Cli.Extensions;

/// <summary>
/// Converts a neutral ExtensionToolSpec from PiNet.Extensions into an
/// AgentTool the agent loop can execute. Extension tool names that collide
/// with built-in tools are skipped (built-ins win); the warning is reported.
/// </summary>
public static class ExtensionToolAdapter
{
    public static AgentTool ToAgentTool(ExtensionToolSpec spec)
    {
        var schema = ToolSchemaParser.Parse(spec.ParametersJson);
        return new AgentTool
        {
            Name = spec.Name,
            Label = spec.Name,
            Description = spec.Description,
            Parameters = schema is null ? null : MapSchema(schema),
            Execute = async (callId, arguments, ct, progress) =>
            {
                var result = await spec.Execute(callId, arguments, ct);
                return new ToolResult(result.Content, result.IsError, result.Terminate);
            }
        };
    }

    /// <summary>Builds the full agent tool list: built-ins first, then non-colliding extension tools.</summary>
    public static IReadOnlyList<AgentTool> BuildAll(
        IReadOnlyList<string> builtInNames,
        IReadOnlyList<ExtensionToolSpec> extensionTools,
        List<string> warnings)
    {
        var result = new List<AgentTool>();
        var names = new HashSet<string>(builtInNames, StringComparer.OrdinalIgnoreCase);
        foreach (var spec in extensionTools)
        {
            if (names.Contains(spec.Name))
            {
                warnings.Add($"extension tool '{spec.Name}' skipped: name conflicts with a built-in tool");
                continue;
            }
            result.Add(ToAgentTool(spec));
            names.Add(spec.Name);
        }
        return result;
    }

    private static ToolParameterSchema MapSchema(ExtensionParameterSchema schema) =>
        new()
        {
            Type = schema.Type,
            Description = schema.Description,
            Properties = schema.Properties?.ToDictionary(
                pair => pair.Key,
                pair => MapSchema(pair.Value),
                StringComparer.OrdinalIgnoreCase),
            Required = schema.Required
        };
}

public sealed record CliSetup(
    ExtensionHost? Extensions,
    SkillRegistry? Skills,
    IReadOnlyList<PromptTemplate> Templates,
    string SystemPrompt);

public static class CliEnvironment
{
    public static async Task<CliSetup> LoadAsync(
        string cwd,
        bool noExtensions = false,
        bool noSkills = false,
        bool noTemplates = false,
        CancellationToken ct = default)
    {
        var extensions = noExtensions ? null : await ExtensionHost.LoadAsync(cwd, ct);
        var skills = noSkills ? null : await SkillRegistry.LoadAsync(cwd, ct);
        var templates = noTemplates ? [] : await TemplateRegistry.LoadAsync(cwd, ct);
        var context = ContextFiles.Load(cwd);
        var systemPrompt = SystemPromptBuilder.Build(CliConfig.DefaultSystemPrompt, context, skills?.Skills ?? []);
        return new CliSetup(extensions, skills, templates, systemPrompt);
    }

    public static IEnumerable<string> Warnings(CliSetup setup)
    {
        if (setup.Extensions is not null)
            foreach (var warning in setup.Extensions.Warnings)
                yield return warning;
        if (setup.Skills is not null)
            foreach (var warning in setup.Skills.Warnings)
                yield return warning;
    }
}