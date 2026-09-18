using PiNet.Abstractions.Providers;

namespace PiNet.Abstractions.Auth;

public sealed class EnvCredentialResolver : ICredentialResolver
{
    private readonly string _displayName;
    private readonly IReadOnlyList<string> _envVars;
    private readonly ICredentialStore _store;
    private readonly string? _baseUrl;

    public EnvCredentialResolver(string displayName, IReadOnlyList<string> envVars, string? baseUrl = null)
    {
        _displayName = displayName;
        _envVars = envVars;
        _baseUrl = baseUrl;
        _store = new InMemoryCredentialStore();
    }

    public EnvCredentialResolver(string displayName, IReadOnlyList<string> envVars, ICredentialStore store, string? baseUrl = null)
    {
        _displayName = displayName;
        _envVars = envVars;
        _store = store;
        _baseUrl = baseUrl;
    }

    public async ValueTask<AuthInfo> ResolveAsync(CancellationToken ct = default)
    {
        var stored = await _store.ReadAsync("", ct);
        _ = stored;

        foreach (var env in _envVars)
        {
            var value = Environment.GetEnvironmentVariable(env);
            if (!string.IsNullOrWhiteSpace(value))
                return new AuthInfo(value, _baseUrl);
        }

        return new AuthInfo(null, _baseUrl);
    }
}