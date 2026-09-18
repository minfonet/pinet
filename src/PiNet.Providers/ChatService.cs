using PiNet.Abstractions.Chat;
using PiNet.Abstractions.Models;
using PiNet.Abstractions.Providers;
using PiNet.Abstractions.Streaming;

namespace PiNet.Providers;

public sealed class ChatService : IModelRunner
{
    private readonly IModelRegistry _registry;

    public ChatService(IModelRegistry registry)
    {
        _registry = registry;
    }

    public IAsyncEnumerable<StreamEvent> GenerateAsync(ModelRequest request) =>
        GenerateAsync(
            request.ProviderId,
            request.ModelId,
            request.SystemPrompt,
            request.Messages,
            request.Tools,
            request.Temperature,
            request.MaxTokens,
            request.CancellationToken);

    public IReadOnlyList<ModelDefinition> GetModels(string? providerId = null) =>
        _registry.GetModels(providerId);

    public ModelDefinition? GetModel(string providerId, string modelId) =>
        _registry.GetModel(providerId, modelId);

    public async IAsyncEnumerable<StreamEvent> GenerateAsync(
        string providerId,
        string modelId,
        string systemPrompt,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools = null,
        double? temperature = null,
        int? maxTokens = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var provider = _registry.GetProvider(providerId);
        if (provider is null)
        {
            yield return new StreamError($"Unknown provider: {providerId}");
            yield break;
        }

        var auth = await provider.Credentials.ResolveAsync(ct);
        var request = new ChatRequest
        {
            ProviderId = providerId,
            ModelId = modelId,
            SystemPrompt = systemPrompt,
            Messages = messages,
            Tools = tools,
            Temperature = temperature,
            MaxTokens = maxTokens,
            CancellationToken = ct
        };

        await foreach (var evt in provider.StreamAsync(request, auth).WithCancellation(ct))
            yield return evt;
    }

    public async Task<string> CompleteAsync(
        string providerId,
        string modelId,
        string systemPrompt,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools = null,
        CancellationToken ct = default)
    {
        var sb = new System.Text.StringBuilder();
        await foreach (var evt in GenerateAsync(providerId, modelId, systemPrompt, messages, tools, ct: ct).WithCancellation(ct))
        {
            if (evt is TextDelta { } t)
                sb.Append(t.Delta);
            else if (evt is StreamError { } e && !string.IsNullOrEmpty(e.Message))
                throw new InvalidOperationException(e.Message);
        }

        return sb.ToString();
    }
}