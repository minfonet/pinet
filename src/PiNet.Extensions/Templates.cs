using System.Text;

namespace PiNet.Extensions;

public sealed record PromptTemplate(
    string Name,
    string Description,
    string? ArgumentHint,
    string Body);

public static class TemplateRegistry
{
    public static async Task<IReadOnlyList<PromptTemplate>> LoadAsync(string cwd, CancellationToken ct = default)
    {
        var templates = new List<PromptTemplate>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();

        var locations = new List<string>();
        Add(locations, ExtensionsPaths.GlobalPromptsDir);
        Add(locations, ExtensionsPaths.ProjectPromptsDir(cwd));
        var packages = PackageManager.ListPackagesFromDisk(ExtensionsPaths.PackagesRoot);
        foreach (var package in packages)
            Add(locations, Path.Combine(package.Path, "prompts"));

        foreach (var dir in locations)
        {
            if (!Directory.Exists(dir))
                continue;
            var files = Directory.GetFiles(dir, "*.md", SearchOption.TopDirectoryOnly)
                .OrderBy(f => f, StringComparer.Ordinal);
            foreach (var file in files)
            {
                var parsed = await ParseFileAsync(file, ct);
                if (parsed is null)
                    continue;
                if (names.Contains(parsed.Name))
                {
                    warnings.Add($"template '{parsed.Name}' skipped: duplicate name (first one wins)");
                    continue;
                }
                names.Add(parsed.Name);
                templates.Add(parsed);
            }
        }

        return templates;
    }

    private static void Add(List<string> locations, string dir)
    {
        if (Directory.Exists(dir))
            locations.Add(dir);
    }

    private static async Task<PromptTemplate?> ParseFileAsync(string path, CancellationToken ct)
    {
        string content;
        try
        {
            content = await File.ReadAllTextAsync(path, ct);
        }
        catch (Exception)
        {
            return null;
        }

        var (fields, body) = SkillFrontmatter.ParseFrontmatter(content);
        var name = System.IO.Path.GetFileNameWithoutExtension(path).ToLowerInvariant();

        var fullBody = body ?? content;
        fullBody = fullBody.TrimEnd('\r', '\n');

        string description;
        if (fields is not null && fields.TryGetValue("description", out var d) && !string.IsNullOrWhiteSpace(d))
        {
            description = d;
        }
        else
        {
            var firstLine = FirstTextLine(fullBody);
            description = firstLine.Length > 80 ? firstLine[..80] : firstLine;
        }

        string? argumentHint = null;
        if (fields is not null && fields.TryGetValue("argument-hint", out var hint) && !string.IsNullOrWhiteSpace(hint))
            argumentHint = hint;

        return new PromptTemplate(name, description, argumentHint, fullBody);
    }

    private static string FirstTextLine(string body)
    {
        foreach (var line in body.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
                return trimmed;
        }
        return string.Empty;
    }
}

public static class TemplateExpander
{
    public static string Expand(PromptTemplate template, IReadOnlyList<string> args)
    {
        var body = template.Body ?? string.Empty;
        var sb = new StringBuilder();
        var i = 0;
        while (i < body.Length)
        {
            var c = body[i];
            if (c != '$' || i + 1 >= body.Length)
            {
                sb.Append(c);
                i++;
                continue;
            }

            var next = body[i + 1];
            if (next == '{')
            {
                var close = body.IndexOf('}', i + 2);
                if (close < 0)
                {
                    sb.Append(c);
                    i++;
                    continue;
                }
                var spec = body[(i + 2)..close];
                var expanded = ExpandBrace(spec, args);
                if (expanded is null)
                {
                    sb.Append(c);
                    i++;
                    continue;
                }
                sb.Append(expanded);
                i = close + 1;
                continue;
            }

            if (next == '@' || IsArgumentsStart(body, i))
            {
                var allToken = next == '@' ? "$@" : "$ARGUMENTS";
                sb.Append(string.Join(' ', args));
                i += allToken.Length;
                continue;
            }

            if (next == '0')
            {
                sb.Append(template.Name);
                i += 2;
                continue;
            }

            if (char.IsDigit(next))
            {
                var j = i + 1;
                while (j < body.Length && char.IsDigit(body[j]))
                    j++;
                var token = body[i..j];
                var index = int.Parse(token[1..]);
                if (index >= 1 && index <= args.Count)
                {
                    sb.Append(args[index - 1]);
                    i = j;
                }
                else if (token.Length == 2)
                {
                    sb.Append(string.Empty);
                    i = j;
                }
                else
                {
                    sb.Append(c);
                    i++;
                }
                continue;
            }

            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }

    private static bool IsArgumentsStart(string body, int i) =>
        i + 10 <= body.Length && body.Substring(i, 10).Equals("$ARGUMENTS", StringComparison.Ordinal);

    private static string? ExpandBrace(string spec, IReadOnlyList<string> args)
    {
        if (spec.Contains(":-"))
        {
            var parts = spec.Split(":-", 2);
            var value = ExpandBraceToken(parts[0], args);
            var fallback = parts[1];
            return string.IsNullOrEmpty(value)
                ? fallback
                : (value ?? fallback);
        }

        var colon = spec.IndexOf(':');
        if (colon < 0)
            return ExpandBraceToken(spec, args);

        var target = spec[..colon];
        if (target != "@" && !target.Equals("ARGUMENTS", StringComparison.Ordinal))
            return ExpandBraceToken(spec, args);

        var ranges = spec[(colon + 1)..].Split(':');
        if (ranges.Length == 0)
            return string.Join(' ', args);

        var start = int.TryParse(ranges[0], out var s) ? s : 0;
        var items = args.Skip(start).ToArray();
        if (ranges.Length >= 2 && int.TryParse(ranges[1], out var length))
            items = items.Take(length).ToArray();
        return string.Join(' ', items);
    }

    private static string? ExpandBraceToken(string token, IReadOnlyList<string> args)
    {
        if (token == "@" || token.Equals("ARGUMENTS", StringComparison.Ordinal))
            return string.Join(' ', args);
        if (token == "0")
            return string.Empty;
        if (int.TryParse(token, out var index) && index >= 1 && index <= args.Count)
            return args[index - 1];
        return null;
    }
}