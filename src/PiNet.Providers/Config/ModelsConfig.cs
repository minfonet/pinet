using System.Text.Json;
using PiNet.Abstractions.Models;
using PiNet.Abstractions.Providers;
using PiNet.Providers.Anthropic;
using PiNet.Providers.Auth;
using PiNet.Providers.OpenAI;

namespace PiNet.Providers;

/// <summary>A model entry from models.json (mirrors Pi's custom model config).</summary>
public sealed record CustomModelConfig
{
    public required string Id { get; init; }
    public string? Name { get; init; }
    public bool? Reasoning { get; init; }
    public string[]? Input { get; init; }
    public long? ContextWindow { get; init; }
}

/// <summary>A provider entry from models.json (mirrors Pi's models.json provider config).</summary>
public sealed record CustomProviderConfig
{
    public string? BaseUrl { get; init; }
    public string? Api { get; init; }
    public string? ApiKey { get; init; }
    public List<CustomModelConfig>? Models { get; init; }
}

/// <summary>
/// Pi-compatible custom provider configuration file
/// (<c>~/.pinet/models.json</c>, or <c>PI_NET_HOME/models.json</c>).
/// Loading is total: a missing or malformed file yields an empty config.
/// </summary>
public sealed record ModelsConfig
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public Dictionary<string, CustomProviderConfig>? Providers { get; init; }

    /// <summary>Pi home directory: PI_NET_HOME override, else ~/.pinet.</summary>
    public static string HomeDir() =>
        Environment.GetEnvironmentVariable("PI_NET_HOME")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pinet");

    public static ModelsConfig TryLoad(out string? warning)
    {
        warning = null;
        var path = Path.Combine(HomeDir(), "models.json");
        if (!File.Exists(path))
            return new ModelsConfig();

        try
        {
            return JsonSerializer.Deserialize<ModelsConfig>(File.ReadAllText(path), JsonOpts) ?? new ModelsConfig();
        }
        catch (Exception ex)
        {
            warning = $"Could not load {path}: {ex.Message}";
            return new ModelsConfig();
        }
    }

    /// <summary>
    /// Registers every configured provider into the registry. Built-ins are kept;
    /// a configured key aliasing a built-in merges its model catalog by id. Bad
    /// entries produce a warning and are skipped (startup never fails).
    /// </summary>
    public void ApplyTo(ModelRegistry registry)
    {
        if (Providers is null)
            return;

        foreach (var (key, cfg) in Providers)
        {
            if (string.IsNullOrWhiteSpace(cfg.BaseUrl))
            {
                Console.Error.WriteLine($"Warning: models.json provider '{key}': missing baseUrl, skipped.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(cfg.Api))
            {
                Console.Error.WriteLine($"Warning: models.json provider '{key}': missing api, skipped.");
                continue;
            }

            if (!TryCreateProvider(key, cfg, out var provider))
            {
                Console.Error.WriteLine($"Warning: models.json provider '{key}': unsupported api '{cfg.Api}' (supported: openai-completions, anthropic-messages); skipped.");
                continue;
            }

            registry.RegisterProvider(provider, BuildModels(registry, key, cfg.Models));
        }
    }

    private static bool TryCreateProvider(string key, CustomProviderConfig cfg, out IChatProvider provider)
    {
        switch (cfg.Api!)
        {
            case "openai-completions":
                provider = new OpenAIChatProvider(null, cfg.BaseUrl!, cfg.ApiKey, providerId: key, name: key);
                return true;
            case "anthropic-messages":
                provider = new AnthropicChatProvider(null, cfg.BaseUrl!, cfg.ApiKey, providerId: key, name: key);
                return true;
            default:
                provider = null!;
                return false;
        }
    }

    private static IReadOnlyList<ModelDefinition> BuildModels(
        ModelRegistry registry,
        string key,
        List<CustomModelConfig>? custom)
    {
        var models = registry.GetProvider(key)?.Models.ToList() ?? [];

        if (custom is null)
            return models;

        foreach (var m in custom)
        {
            var definition = new ModelDefinition
            {
                Provider = key,
                Id = m.Id,
                Name = string.IsNullOrWhiteSpace(m.Name) ? m.Id : m.Name,
                SupportsReasoning = m.Reasoning ?? false,
                SupportsVision = m.Input?.Contains("image", StringComparer.OrdinalIgnoreCase) ?? false,
                ContextWindow = m.ContextWindow ?? 0
            };

            var index = models.FindIndex(b => string.Equals(b.Id, m.Id, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
                models[index] = definition;
            else
                models.Add(definition);
        }

        return models;
    }
}