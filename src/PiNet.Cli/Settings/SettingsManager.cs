using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiNet.Abstractions.Chat;

namespace PiNet.Cli.Settings;

/// <summary>Raw per-file settings schema. Nullable members mean "not set" in that file.</summary>
public sealed record SettingsFile
{
    // Mutable so SetGlobal can update a known key (read-modify-write).
    public string? Provider { get; set; }
    public string? Model { get; set; }
    public string? ThinkingLevel { get; set; }
    public double? CompactThreshold { get; set; }
    public int? CompactPreserveRecent { get; set; }
    public bool? AutoCompact { get; set; }
    public bool? Verbose { get; set; }
}

/// <summary>
/// Effective (merged) settings: global (<c>~/.pinet/settings.json</c>, via <c>PI_NET_HOME</c>)
/// plus project (<c>&lt;cwd&gt;/.pi/settings.json</c>), with the project winning on every field.
/// File I/O is local and cheap, so this loads synchronously.
/// </summary>
public sealed class EffectiveSettings
{
    public string? Provider { get; }
    public string? Model { get; }
    public ThinkingLevel? ThinkingLevel { get; }
    public double CompactThreshold { get; }
    public int CompactPreserveRecent { get; }
    public bool AutoCompact { get; }
    public bool Verbose { get; }
    public string GlobalPath { get; }
    public string ProjectPath { get; }
    public SettingsFile? Global { get; }
    public SettingsFile? Project { get; }
    public IReadOnlyList<string> Warnings { get; }

    public EffectiveSettings(
        string? provider, string? model, ThinkingLevel? thinkingLevel,
        double compactThreshold, int compactPreserveRecent,
        bool autoCompact, bool verbose,
        string globalPath, string projectPath,
        SettingsFile? global, SettingsFile? project, IReadOnlyList<string> warnings)
    {
        Provider = provider;
        Model = model;
        ThinkingLevel = thinkingLevel;
        CompactThreshold = compactThreshold;
        CompactPreserveRecent = compactPreserveRecent;
        AutoCompact = autoCompact;
        Verbose = verbose;
        GlobalPath = globalPath;
        ProjectPath = projectPath;
        Global = global;
        Project = project;
        Warnings = warnings;
    }
}

public static class SettingsManager
{
    /// <summary>Global home (<c>PI_NET_HOME</c> override, else <c>~/.pinet</c>).</summary>
    public static string HomeDir =>
        Environment.GetEnvironmentVariable("PI_NET_HOME")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pinet");

    public static string GlobalPath => Path.Combine(HomeDir, "settings.json");

    public static string ProjectPath(string cwd) => Path.Combine(cwd, ".pi", "settings.json");

    /// <summary>
    /// Loads and merges global + project settings (project wins). Missing files contribute
    /// nothing. A malformed file produces a warning and falls back to defaults (non-fatal).
    /// </summary>
    public static EffectiveSettings Load(string cwd)
    {
        var globalPath = GlobalPath;
        var projectPath = ProjectPath(cwd);
        var warnings = new List<string>();

        SettingsFile? global = LoadFile(globalPath, "global", warnings);
        SettingsFile? project = LoadFile(projectPath, "project", warnings);

        if (project?.ThinkingLevel is not null && !TryParseThinking(project.ThinkingLevel))
            warnings.Add($"Invalid thinkingLevel '{project.ThinkingLevel}' in project settings; ignored.");
        if (global?.ThinkingLevel is not null && !TryParseThinking(global.ThinkingLevel))
            warnings.Add($"Invalid thinkingLevel '{global.ThinkingLevel}' in global settings; ignored.");

        ThinkingLevel? thinkingLevel =
            project?.ThinkingLevel is { } pTL && TryParseThinking(pTL)
                ? Enum.Parse<ThinkingLevel>(pTL, ignoreCase: true)
                : global?.ThinkingLevel is { } gTL && TryParseThinking(gTL)
                    ? Enum.Parse<ThinkingLevel>(gTL, ignoreCase: true)
                    : null;

        double? threshold = project?.CompactThreshold ?? global?.CompactThreshold;
        if (threshold is not null)
        {
            if (threshold > 1.0)
            {
                warnings.Add($"Invalid compactThreshold {threshold} in settings; clamped to 1.0.");
                threshold = 1.0;
            }
            else if (threshold <= 0)
            {
                warnings.Add($"Invalid compactThreshold {threshold} in settings; using default 0.75.");
                threshold = null;
            }
        }

        int? preserve = project?.CompactPreserveRecent ?? global?.CompactPreserveRecent;
        if (preserve is not null && preserve < 0)
        {
            warnings.Add($"Invalid compactPreserveRecent {preserve} in settings; using default 4.");
            preserve = null;
        }

        return new EffectiveSettings(
            project?.Provider ?? global?.Provider,
            project?.Model ?? global?.Model,
            thinkingLevel,
            threshold ?? 0.75,
            preserve is null ? 4 : preserve.Value,
            project?.AutoCompact ?? global?.AutoCompact ?? false,
            project?.Verbose ?? global?.Verbose ?? false,
            globalPath,
            projectPath,
            global,
            project,
            warnings.ToArray());
    }

    /// <summary>
    /// Sets a known setting on the global file (read-modify-write; existing/unknown keys are
    /// preserved). Returns <see langword="null"/> on success, or a warning message on failure.
    /// </summary>
    /// <summary>
    /// Sets a known setting on the global file (read-modify-write; known keys preserved,
    /// unknown keys dropped). Returns <c>null</c> on success, or a warning message on failure.
    /// </summary>
    public static string? SetGlobal(string key, string value)
    {
        var path = GlobalPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        SettingsFile current;
        try
        {
            if (!File.Exists(path) || string.IsNullOrWhiteSpace(File.ReadAllText(path)))
            {
                current = new SettingsFile();
            }
            else
            {
                current = JsonSerializer.Deserialize<SettingsFile>(
                    File.ReadAllText(path),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new SettingsFile();
            }
        }
        catch (Exception ex)
        {
            return $"Malformed global settings ({path}); refusing to edit. Fix the JSON first. ({ex.Message})";
        }

        switch (key.ToLowerInvariant())
        {
            case "provider":
                current.Provider = value;
                break;
            case "model":
                current.Model = value;
                break;
            case "thinkinglevel":
                if (!TryParseThinking(value))
                    return $"Invalid thinkingLevel '{value}' (use Off/Minimal/Low/Medium/High/XHigh/Max).";
                current.ThinkingLevel = value;
                break;
            case "compactthreshold":
                if (!IsThreshold(value))
                    return $"Invalid compactThreshold '{value}' (use a number in (0, 1]).";
                current.CompactThreshold = double.Parse(value, CultureInfo.InvariantCulture);
                break;
            case "compactpreserverecent":
                if (!IsPreserve(value))
                    return $"Invalid compactPreserveRecent '{value}' (use a non-negative integer).";
                current.CompactPreserveRecent = int.Parse(value, CultureInfo.InvariantCulture);
                break;
            case "autocompact":
                if (!bool.TryParse(value.Trim(), out var ab))
                    return $"Invalid autoCompact '{value}' (use true or false).";
                current.AutoCompact = ab;
                break;
            case "verbose":
                if (!bool.TryParse(value.Trim(), out var vb))
                    return $"Invalid verbose '{value}' (use true or false).";
                current.Verbose = vb;
                break;
            default:
                return $"Unknown setting '{key}' (known: provider, model, thinkingLevel, compactThreshold, compactPreserveRecent, autoCompact, verbose).";
        }

        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(current, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                IgnoreNullValues = true
            }));
            return null;
        }
        catch (Exception ex)
        {
            return $"Could not write global settings ({path}): {ex.Message}.";
        }
    }

    private static bool TryParseThinking(string value) =>
        Enum.TryParse<ThinkingLevel>(value, ignoreCase: true, out _);

    private static bool IsThreshold(string v) =>
        double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
        && d > 0 && d <= 1;

    private static bool IsPreserve(string v) =>
        int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
        && n >= 0;

    private static SettingsFile? LoadFile(string path, string which, List<string> warnings)
    {
        if (!File.Exists(path)) return null;
        string? json;
        try { json = File.ReadAllText(path); }
        catch (Exception ex)
        {
            warnings.Add($"Could not read {which} settings ({path}): {ex.Message}");
            return null;
        }
        if (string.IsNullOrWhiteSpace(json)) return null;

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        try
        {
            return JsonSerializer.Deserialize<SettingsFile>(json, options);
        }
        catch (Exception ex)
        {
            warnings.Add($"Malformed {which} settings ({path}): {ex.Message}. Using defaults.");
            return null;
        }
    }
}
