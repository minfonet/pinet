namespace PiNet.Abstractions.Auth;

public sealed record StoredCredential(string ProviderId, string ApiKey, long StoredAt);

public interface ICredentialStore
{
    ValueTask<StoredCredential?> ReadAsync(string providerId, CancellationToken ct = default);
    ValueTask WriteAsync(StoredCredential credential, CancellationToken ct = default);
    ValueTask DeleteAsync(string providerId, CancellationToken ct = default);
    Task<IReadOnlyList<string>> ListProvidersAsync(CancellationToken ct = default);
}

public sealed class InMemoryCredentialStore : ICredentialStore
{
    private readonly Dictionary<string, StoredCredential> _store = new(StringComparer.OrdinalIgnoreCase);

    public ValueTask<StoredCredential?> ReadAsync(string providerId, CancellationToken ct = default) =>
        ValueTask.FromResult(_store.TryGetValue(providerId, out var c) ? c : null);

    public ValueTask WriteAsync(StoredCredential credential, CancellationToken ct = default)
    {
        _store[credential.ProviderId] = credential;
        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteAsync(string providerId, CancellationToken ct = default)
    {
        _store.Remove(providerId);
        return ValueTask.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListProvidersAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>(_store.Keys.ToArray());
}