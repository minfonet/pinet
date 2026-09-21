using PiNet.Abstractions.Auth;
using PiNet.Abstractions.Providers;

namespace PiNet.Providers.Auth;

/// <summary>
/// Resolves an apiKey exactly like Pi's models.json: a literal value unless it
/// starts with '$' (then it names an environment variable whose value is used).
/// When no apiKey is configured, falls back to the provider's default env var
/// names. The baseUrl always flows through as <see cref="AuthInfo.BaseUrl"/>.
/// </summary>
public sealed class ConfigCredentialResolver : ICredentialResolver
{
    private readonly string? _baseUrl;
    private readonly string? _apiKey;
    private readonly IReadOnlyList<string> _fallbackEnv;

    public ConfigCredentialResolver(string? baseUrl, string? apiKey, IReadOnlyList<string> fallbackEnv)
    {
        _baseUrl = baseUrl;
        _apiKey = apiKey;
        _fallbackEnv = fallbackEnv;
    }

    public ValueTask<AuthInfo> ResolveAsync(CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(_apiKey))
        {
            var key = _apiKey!.StartsWith('$')
                ? Environment.GetEnvironmentVariable(_apiKey[1..])
                : _apiKey;
            return ValueTask.FromResult(new AuthInfo(string.IsNullOrWhiteSpace(key) ? null : key, _baseUrl));
        }

        foreach (var env in _fallbackEnv)
        {
            var value = Environment.GetEnvironmentVariable(env);
            if (!string.IsNullOrWhiteSpace(value))
                return ValueTask.FromResult(new AuthInfo(value, _baseUrl));
        }

        return ValueTask.FromResult(new AuthInfo(null, _baseUrl));
    }
}