using PiNet.Abstractions.Providers;
using Xunit;

namespace PiNet.Providers.Tests;

public sealed class ProviderRegistrationTests
{
    private static ModelsConfig Config(params (string Key, CustomProviderConfig Value)[] providers) =>
        new() { Providers = providers.ToDictionary(p => p.Key, p => p.Value) };

    [Fact]
    public async Task OpenAICompletions_RegistersCustomProvider_WithModelsAndAuth()
    {
        var registry = new ModelRegistry();
        var config = Config(
            ("ollama", new CustomProviderConfig
            {
                BaseUrl = "http://localhost:11434/v1",
                Api = "openai-completions",
                ApiKey = "ollama",
                Models = [new CustomModelConfig { Id = "llama3.1:8b" }]
            }));

        config.ApplyTo(registry);

        var provider = registry.GetProvider("ollama");
        Assert.NotNull(provider);
        Assert.Equal("ollama", provider!.Id);

        var model = registry.GetModel("ollama", "llama3.1:8b");
        Assert.NotNull(model);
        Assert.Equal("llama3.1:8b", model!.Name);

        var auth = await provider.Credentials.ResolveAsync();
        Assert.Equal("ollama", auth.ApiKey);
        Assert.Equal("http://localhost:11434/v1", auth.BaseUrl);
    }

    [Fact]
    public void ModelCapabilities_AreDerivedFromConfig()
    {
        var registry = new ModelRegistry();
        var config = Config(
            ("vllm", new CustomProviderConfig
            {
                BaseUrl = "http://localhost:8000/v1",
                Api = "openai-completions",
                ApiKey = "local",
                Models = [new CustomModelConfig { Id = "qwen2.5", Name = "Qwen Coder", Reasoning = true, Input = ["text", "image"], ContextWindow = 32768 }]
            }));

        config.ApplyTo(registry);

        var model = registry.GetModel("vllm", "qwen2.5");
        Assert.Equal("Qwen Coder", model!.Name);
        Assert.True(model.SupportsReasoning);
        Assert.True(model.SupportsVision);
        Assert.Equal(32768, model.ContextWindow);
    }

    [Fact]
    public void AnthropicMessages_RegistersProvider()
    {
        var registry = new ModelRegistry();
        var config = Config(
            ("claude-local", new CustomProviderConfig
            {
                BaseUrl = "http://localhost:8080",
                Api = "anthropic-messages",
                ApiKey = "key",
                Models = [new CustomModelConfig { Id = "claude-sonnet" }]
            }));

        config.ApplyTo(registry);

        var provider = registry.GetProvider("claude-local");
        Assert.NotNull(provider);
        Assert.Equal("claude-local", provider!.Id);
        Assert.NotNull(registry.GetModel("claude-local", "claude-sonnet"));
    }

    [Fact]
    public void UnknownApi_IsSkipped()
    {
        var registry = new ModelRegistry();
        var config = Config(
            ("weird", new CustomProviderConfig { BaseUrl = "http://x", Api = "google-generative-ai" }),
            ("good", new CustomProviderConfig { BaseUrl = "http://y/v1", Api = "openai-completions", Models = [new CustomModelConfig { Id = "m" }] }));

        config.ApplyTo(registry);

        Assert.Null(registry.GetProvider("weird"));
        Assert.NotNull(registry.GetProvider("good"));
    }

    [Fact]
    public void MissingBaseUrl_IsSkipped()
    {
        var registry = new ModelRegistry();
        var config = Config(("broken", new CustomProviderConfig { Api = "openai-completions" }));

        config.ApplyTo(registry);

        Assert.Null(registry.GetProvider("broken"));
    }

    [Fact]
    public void MissingModels_RegistersProviderWithNoModels()
    {
        var registry = new ModelRegistry();
        var config = Config(("empty", new CustomProviderConfig { BaseUrl = "http://x/v1", Api = "openai-completions" }));

        config.ApplyTo(registry);

        Assert.NotNull(registry.GetProvider("empty"));
        Assert.Empty(registry.GetModels("empty"));
    }
}