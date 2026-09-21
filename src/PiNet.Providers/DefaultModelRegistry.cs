using PiNet.Abstractions.Providers;
using PiNet.Providers.Anthropic;
using PiNet.Providers.Faux;
using PiNet.Providers.OpenAI;

namespace PiNet.Providers;

public static class DefaultModelRegistry
{
    public static ModelRegistry Create()
    {
        var registry = new ModelRegistry();
        registry.RegisterProvider(new OpenAIChatProvider());
        registry.RegisterProvider(new AnthropicChatProvider());
        registry.RegisterProvider(new FauxChatProvider());

        var config = ModelsConfig.TryLoad(out var warning);
        if (warning is not null)
            Console.Error.WriteLine($"Warning: {warning}");
        config.ApplyTo(registry);

        return registry;
    }
}