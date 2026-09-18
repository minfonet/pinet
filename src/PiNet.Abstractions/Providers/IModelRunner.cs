using PiNet.Abstractions.Chat;
using PiNet.Abstractions.Models;
using PiNet.Abstractions.Streaming;

namespace PiNet.Abstractions.Providers;

public sealed record ModelRequest(
    string ProviderId,
    string ModelId,
    string SystemPrompt,
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyList<ToolDefinition>? Tools = null,
    double? Temperature = null,
    int? MaxTokens = null,
    CancellationToken CancellationToken = default);

public interface IModelRunner
{
    IAsyncEnumerable<StreamEvent> GenerateAsync(ModelRequest request);
}