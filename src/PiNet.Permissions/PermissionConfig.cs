using System.Text.Json;

namespace PiNet.Permissions;

/// <summary>Thrown when a permissions config file exists but cannot be parsed.</summary>
public sealed class PermissionConfigException : Exception
{
    public PermissionConfigException(string path, string message)
        : base($"{System.IO.Path.GetFileName(path)}: {message}")
    {
        this.Path = path;
    }

    public string Path { get; }
}

/// <summary>
/// Parsed permissions config: an ordered list of rules.
/// JSON shape: { "rules": [ { "tool": "bash", "path": "secrets/*", "permission": "deny" } ] }
/// </summary>
public sealed class PermissionConfig
{
    public List<PermissionRule> Rules { get; } = [];

    public PermissionConfig(IReadOnlyList<PermissionRule>? rules = null)
    {
        if (rules is not null)
            Rules.AddRange(rules);
    }

    /// <summary>
    /// Loads a config file. A missing file yields an empty config; malformed
    /// content throws <see cref="PermissionConfigException"/>.
    /// </summary>
    public static async Task<PermissionConfig> LoadAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path))
            return new PermissionConfig();

        string json;
        try
        {
            json = await File.ReadAllTextAsync(path, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new PermissionConfigException(path, $"cannot read file: {ex.Message}");
        }

        return Parse(path, json);
    }

    /// <summary>Parses config JSON; throws <see cref="PermissionConfigException"/> on bad content.</summary>
    public static PermissionConfig Parse(string path, string json)
    {
        JsonElement root;
        try
        {
            root = JsonDocument.Parse(json).RootElement;
        }
        catch (JsonException ex)
        {
            throw new PermissionConfigException(path, $"invalid JSON: {ex.Message}");
        }

        if (root.ValueKind != JsonValueKind.Object)
            throw new PermissionConfigException(path, "root must be a JSON object");

        var config = new PermissionConfig();
        if (!root.TryGetProperty("rules", out var rules) || rules.ValueKind != JsonValueKind.Array)
            return config;

        for (var i = 0; i < rules.GetArrayLength(); i++)
        {
            var r = rules[i];
            if (r.ValueKind != JsonValueKind.Object)
                throw new PermissionConfigException(path, $"rule {i}: must be a JSON object");

            var tool = r.TryGetProperty("tool", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()!
                : throw new PermissionConfigException(path, $"rule {i}: missing 'tool'");

            var permission = r.TryGetProperty("permission", out var p) && p.ValueKind == JsonValueKind.String
                ? ParsePermission(p.GetString()!)
                : throw new PermissionConfigException(path, $"rule {i}: missing 'permission'");

            string? pathPattern = null;
            if (r.TryGetProperty("path", out var pp) && pp.ValueKind == JsonValueKind.String)
                pathPattern = pp.GetString();

            config.Rules.Add(new PermissionRule(tool, pathPattern, permission));
        }
        return config;
    }

    /// <summary>Serializes to the canonical JSON shape.</summary>
    public string ToJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("rules");
            writer.WriteStartArray();
            foreach (var rule in Rules)
            {
                writer.WriteStartObject();
                writer.WriteString("tool", rule.ToolPattern);
                if (rule.PathPattern is not null)
                    writer.WriteString("path", rule.PathPattern);
                writer.WriteString("permission", rule.Permission.ToString().ToLowerInvariant());
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Saves to disk, creating parent directories as needed.</summary>
    public static async Task SaveAsync(PermissionConfig config, string path, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(path);
        if (dir is not null && dir.Length > 0)
            Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(path, config.ToJson() + Environment.NewLine, ct);
    }

    private static ToolPermission ParsePermission(string value) =>
        value.ToLowerInvariant() switch
        {
            "allow" => ToolPermission.Allow,
            "ask" => ToolPermission.Ask,
            "deny" => ToolPermission.Deny,
            _ => throw new PermissionConfigException("<config>", $"unknown permission '{value}' (expected allow/ask/deny)")
        };
}

/// <summary>
/// Well-known config locations. The global home directory honors the
/// PI_NET_HOME environment variable (default: ~/.pinet).
/// </summary>
public static class PermissionPaths
{
    /// <summary>Global config home (PI_NET_HOME override, else ~/.pinet).</summary>
    public static string HomeDir =>
        Environment.GetEnvironmentVariable("PI_NET_HOME")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pinet");

    /// <summary>Global config file path.</summary>
    public static string GlobalConfigPath => Path.Combine(HomeDir, "permissions.json");

    /// <summary>Project config file path for a given working directory.</summary>
    public static string ProjectConfigPath(string cwd) => Path.Combine(cwd, ".pi", "permissions.json");
}
