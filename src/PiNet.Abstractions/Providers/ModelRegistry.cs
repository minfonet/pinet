using PiNet.Abstractions.Models;

namespace PiNet.Abstractions.Providers;

public interface IModelRegistry
{
    void RegisterProvider(IChatProvider provider);
    void RegisterProvider(IChatProvider provider, IReadOnlyList<ModelDefinition> models);
    IReadOnlyList<IChatProvider> GetProviders();
    IChatProvider? GetProvider(string id);
    IReadOnlyList<ModelDefinition> GetModels(string? providerId = null);
    ModelDefinition? GetModel(string providerId, string modelId);
}

public sealed class ModelRegistry : IModelRegistry
{
    private readonly Dictionary<string, IChatProvider> _providers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<ModelDefinition>> _models = new(StringComparer.OrdinalIgnoreCase);

    public void RegisterProvider(IChatProvider provider) =>
        RegisterProvider(provider, provider.Models);

    public void RegisterProvider(IChatProvider provider, IReadOnlyList<ModelDefinition> models)
    {
        _providers[provider.Id] = provider;
        _models[provider.Id] = models.ToList();
    }

    public IReadOnlyList<IChatProvider> GetProviders() => _providers.Values.ToArray();

    public IChatProvider? GetProvider(string id) =>
        _providers.TryGetValue(id, out var p) ? p : null;

    public IReadOnlyList<ModelDefinition> GetModels(string? providerId = null) =>
        providerId is null
            ? _models.Values.SelectMany(m => m).ToArray()
            : _models.TryGetValue(providerId, out var m) ? m : [];

    public ModelDefinition? GetModel(string providerId, string modelId)
    {
        if (_models.TryGetValue(providerId, out var models))
            return models.FirstOrDefault(m => string.Equals(m.Id, modelId, StringComparison.OrdinalIgnoreCase));
        return null;
    }
}