using System.Text;

namespace PiNet.Extensions;

public sealed record ContextFileSet(string? SystemMd, string? AppendSystemMd, string? ProjectInstructions);

public static class ContextFiles
{
    public static ContextFileSet Load(string cwd)
    {
        var systemMd = ReadFileIfExists(Path.Combine(cwd, "SYSTEM.md"));
        var appendSystemMd = ReadFileIfExists(Path.Combine(cwd, "APPEND_SYSTEM.md"));
        var projectInstructions = FindProjectInstructions(cwd);
        return new ContextFileSet(systemMd, appendSystemMd, projectInstructions);
    }

    private static string? FindProjectInstructions(string cwd)
    {
        var dir = new DirectoryInfo(Path.GetFullPath(cwd));
        while (dir is not null)
        {
            var agentsMd = Path.Combine(dir.FullName, "AGENTS.md");
            var claudeMd = Path.Combine(dir.FullName, "CLAUDE.md");
            if (File.Exists(agentsMd))
                return ReadFileIfExists(agentsMd);
            if (File.Exists(claudeMd))
                return ReadFileIfExists(claudeMd);
            dir = dir.Parent;
        }
        return null;
    }

    private static string? ReadFileIfExists(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

public static class SystemPromptBuilder
{
    public static string Build(string basePrompt, ContextFileSet ctx, IReadOnlyList<Skill> skills)
    {
        var sections = new List<string>();

        if (!string.IsNullOrWhiteSpace(ctx.SystemMd))
            sections.Add(ctx.SystemMd.TrimEnd());
        else if (!string.IsNullOrWhiteSpace(basePrompt))
            sections.Add(basePrompt.TrimEnd());

        if (!string.IsNullOrWhiteSpace(ctx.AppendSystemMd))
            sections.Add(ctx.AppendSystemMd.TrimEnd());

        if (!string.IsNullOrWhiteSpace(ctx.ProjectInstructions))
            sections.Add($"# Project instructions\n{ctx.ProjectInstructions.TrimEnd()}");

        var skillSection = SkillCatalog.ToSystemPromptSection(skills);
        if (skillSection is not null)
            sections.Add(skillSection);

        return string.Join("\n\n", sections);
    }
}