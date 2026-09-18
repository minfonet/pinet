using PiNet.Abstractions.Chat;
using PiNet.Abstractions.Models;

namespace PiNet.Abstractions.Providers;

public sealed record ChatRequest
{
    public required string ProviderId { get; init; }
    public required string ModelId { get; init; }
    public required string SystemPrompt { get; init; }
    public required IReadOnlyList<ChatMessage> Messages { get; init; }
    public IReadOnlyList<ToolDefinition>? Tools { get; init; }
    public double? Temperature { get; init; }
    public int? MaxTokens { get; init; }
    public CancellationToken CancellationToken { get; init; }
}

public sealed record AuthInfo(string? ApiKey, string? BaseUrl);

public interface ICredentialResolver
{
    ValueTask<AuthInfo> ResolveAsync(CancellationToken ct = default);
}

public interface IChatProvider
{
    string Id { get; }
    string Name { get; }
    IReadOnlyList<ModelDefinition> Models { get; }
    ICredentialResolver Credentials { get; }
    IAsyncEnumerable<Streaming.StreamEvent> StreamAsync(ChatRequest request, AuthInfo auth);
}