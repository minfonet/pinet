using System.Text;
using System.Text.RegularExpressions;

namespace PiNet.Extensions;

public sealed record Skill(
    string Name,
    string Description,
    string Path,
    string Directory,
    bool DisableModelInvocation);

public sealed record SkillParseResult(Skill? Skill, string? Warning);

public static partial class SkillFrontmatter
{
    [GeneratedRegex(@"^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex NamePattern();

    public static SkillParseResult Parse(string? path, string content)
    {
        var (fields, body) = ParseFrontmatter(content);
        if (fields is null)
            return new SkillParseResult(null, null);

        var name = ValueOf(fields, "name");
        var warning = ValidateName(name);
        if (warning is not null)
            return new SkillParseResult(null, warning);

        var description = ValueOf(fields, "description");
        if (string.IsNullOrWhiteSpace(description))
            return new SkillParseResult(null, $"skill '{name}': missing 'description'");
        if (description.Length > 1024)
            return new SkillParseResult(null, $"skill '{name}': description longer than 1024 chars");

        var disable = BooleanValue(fields, "disable-model-invocation") ?? false;
        _ = body;

        var filePath = path ?? string.Empty;
        var dir = System.IO.Path.GetDirectoryName(filePath) ?? string.Empty;

        var skill = new Skill(name!, description!, filePath, dir, disable);
        return new SkillParseResult(skill, null);
    }

    public static string ReadBody(string path)
    {
        try
        {
            var content = File.ReadAllText(path);
            var (_, body) = ParseFrontmatter(content);
            return body ?? content;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static string? ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "skill skipped: missing 'name'";
        if (name!.Length > 64)
            return $"skill '{name}': name longer than 64 chars";
        if (!NamePattern().IsMatch(name))
            return $"skill '{name}': name must be lowercase [a-z0-9] with '-' separators";
        return null;
    }

    internal static (Dictionary<string, string>? Fields, string? Body) ParseFrontmatter(string content)
    {
        if (content is null || !content.StartsWith("---", StringComparison.Ordinal))
            return (null, null);

        var firstNewline = content.IndexOf('\n');
        if (firstNewline < 0)
            return (null, null);

        var endMarker = EndOfFrontmatter(content, firstNewline + 1);
        if (endMarker < 0)
            return (null, null);

        var raw = content[(firstNewline + 1)..endMarker];
        var body = content[(endMarker + 3)..].TrimStart('\r', '\n');

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in raw.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(trimmed))
                continue;
            var colon = trimmed.IndexOf(':');
            if (colon <= 0)
                continue;
            var key = trimmed[..colon].Trim();
            var value = trimmed[(colon + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0])
                value = value[1..^1];
            fields[key] = value;
        }

        return (fields, body);
    }

    private static int EndOfFrontmatter(string content, int start)
    {
        var index = content.IndexOf("---", start, StringComparison.Ordinal);
        while (index >= 0)
        {
            var after = index + 3;
            var isLine = (index == 0 || content[index - 1] == '\n') &&
                         (after == content.Length || content[after] == '\n' || content[after] == '\r');
            if (isLine)
                return index;
            index = content.IndexOf("---", after, StringComparison.Ordinal);
        }
        return -1;
    }

    private static string? ValueOf(Dictionary<string, string> fields, string key) =>
        fields.TryGetValue(key, out var v) ? v : null;

    private static bool? BooleanValue(Dictionary<string, string> fields, string key)
    {
        if (!fields.TryGetValue(key, out var v))
            return null;
        return v.Trim().ToLowerInvariant() is "true" or "1" or "yes";
    }
}

public sealed class SkillRegistry
{
    private readonly List<Skill> _skills = [];
    private readonly List<string> _warnings = [];
    private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<Skill> Skills => _skills;
    public IReadOnlyList<string> Warnings => _warnings;

    public static async Task<SkillRegistry> LoadAsync(string cwd, CancellationToken ct = default)
    {
        var registry = new SkillRegistry();

        var globalPinet = ExtensionsPaths.GlobalSkillsDir;
        var shared = ExtensionsPaths.SharedSkillsDir;
        if (Directory.Exists(globalPinet))
            registry.ScanStandardLocation(globalPinet);
        if (Directory.Exists(shared))
            registry.ScanAgentsLocation(shared);

        var projectLocations = ProjectSkillLocations(cwd);
        foreach (var (dir, agentsRules) in projectLocations)
        {
            if (!Directory.Exists(dir))
                continue;
            if (agentsRules)
                registry.ScanAgentsLocation(dir);
            else
                registry.ScanStandardLocation(dir);
        }

        var packages = PackageManager.ListPackagesFromDisk(ExtensionsPaths.PackagesRoot);
        foreach (var package in packages)
        {
            var skillsDir = Path.Combine(package.Path, "skills");
            if (Directory.Exists(skillsDir))
                registry.ScanStandardLocation(skillsDir);
        }

        return await Task.FromResult(registry);
    }

    private static IEnumerable<(string Dir, bool AgentsRules)> ProjectSkillLocations(string cwd)
    {
        var parent = Directory.GetParent(cwd);
        var dir = parent is null ? null : new DirectoryInfo(cwd);
        while (dir is not null)
        {
            var d = dir.FullName;
            yield return (ExtensionsPaths.ProjectSkillsDir(d), false);
            yield return (ExtensionsPaths.ProjectAgentsSkillsDir(d), true);
            dir = dir.Parent;
        }
    }

    private void ScanStandardLocation(string root)
    {
        if (!Directory.Exists(root))
            return;

        foreach (var file in Directory.GetFiles(root, "*.md", SearchOption.TopDirectoryOnly))
            AddFile(file);

        var dirs = Directory.GetDirectories(root, "*", SearchOption.AllDirectories);
        foreach (var dir in dirs)
        {
            var skillFile = Path.Combine(dir, "SKILL.md");
            if (File.Exists(skillFile))
                AddFile(skillFile);
        }
    }

    private void ScanAgentsLocation(string root)
    {
        if (!Directory.Exists(root))
            return;

        foreach (var file in Directory.GetFiles(root, "*.md", SearchOption.AllDirectories))
        {
            var parent = Path.GetDirectoryName(file);
            if (string.Equals(parent, root, StringComparison.OrdinalIgnoreCase))
                continue; // location files (AGENTS.md etc.) at the root are ignored
            if (string.Equals(Path.GetFileName(file), "SKILL.md", StringComparison.OrdinalIgnoreCase))
            {
                AddFile(file);
                continue;
            }
            using var stream = File.OpenRead(file);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var content = reader.ReadToEnd();
            var result = SkillFrontmatter.Parse(file, content);
            if (result.Skill is not null)
                Add(result.Skill);
        }
    }

    private void AddFile(string path)
    {
        try
        {
            var content = File.ReadAllText(path);
            var result = SkillFrontmatter.Parse(path, content);
            if (result.Warning is not null)
            {
                _warnings.Add(result.Warning);
                return;
            }
            if (result.Skill is not null)
                Add(result.Skill);
        }
        catch (Exception ex)
        {
            _warnings.Add($"{path}: {ex.Message}");
        }
    }

    private void Add(Skill skill)
    {
        if (_names.Contains(skill.Name))
        {
            _warnings.Add($"skill '{skill.Name}' skipped: duplicate name (first one wins)");
            return;
        }
        _names.Add(skill.Name);
        _skills.Add(skill);
    }

    public Skill? Find(string name) =>
        _skills.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
}

public static class SkillCatalog
{
    public static string? ToSystemPromptSection(IReadOnlyList<Skill> skills)
    {
        var loadable = skills
            .Where(s => !s.DisableModelInvocation)
            .OrderBy(s => s.Name, StringComparer.Ordinal)
            .ToArray();
        if (loadable.Length == 0)
            return null;

        var sb = new StringBuilder();
        sb.Append("# Skills\n");
        sb.Append("The following skills are available. When a task matches a skill, read the\n");
        sb.Append("skill file at its location with the read_file tool and follow its instructions.\n\n");
        sb.Append("<available_skills>\n");
        foreach (var skill in loadable)
        {
            sb.Append("  <skill>\n");
            sb.Append($"    <name>{skill.Name}</name>\n");
            sb.Append($"    <description>{skill.Description}</description>\n");
            sb.Append($"    <location>{skill.Path}</location>\n");
            sb.Append("  </skill>\n");
        }
        sb.Append("</available_skills>");
        return sb.ToString();
    }
}