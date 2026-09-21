using Xunit;

namespace PiNet.Providers.Tests;

public sealed class RegistryMergeTests
{
    [Fact]
    public void NoConfigFile_RegistryHasBuiltins()
    {
        using var dirs = new TestDirs();

        var registry = DefaultModelRegistry.Create();

        Assert.NotNull(registry.GetProvider("openai"));
        Assert.NotNull(registry.GetProvider("anthropic"));
        Assert.NotNull(registry.GetProvider("faux"));
    }

    [Fact]
    public void ConfiguredOpenaiKey_MergesCatalog_ByModelId()
    {
        using var dirs = new TestDirs();
        dirs.WriteConfig("""
        {
          "providers": {
            "openai": {
              "baseUrl": "https://api.openai.com/v1",
              "api": "openai-completions",
              "apiKey": "sk-override",
              "models": [
                { "id": "gpt-4o", "name": "GPT-4o (custom)" },
                { "id": "local-gpt", "name": "Local GPT" }
              ]
            }
          }
        }
        """);

        var registry = DefaultModelRegistry.Create();

        Assert.NotNull(registry.GetProvider("openai"));
        Assert.Equal("GPT-4o (custom)", registry.GetModel("openai", "gpt-4o")!.Name);
        Assert.NotNull(registry.GetModel("openai", "local-gpt"));
        Assert.NotNull(registry.GetModel("openai", "gpt-4o-mini"));
        Assert.NotNull(registry.GetProvider("anthropic"));
    }

    [Fact]
    public void EndToEnd_CustomProviderAlongsideBuiltins()
    {
        using var dirs = new TestDirs();
        dirs.WriteConfig("""
        {
          "providers": {
            "ollama": {
              "baseUrl": "http://localhost:11434/v1",
              "api": "openai-completions",
              "apiKey": "ollama",
              "models": [ { "id": "llama3.1:8b" } ]
            }
          }
        }
        """);

        var registry = DefaultModelRegistry.Create();

        Assert.NotNull(registry.GetModel("ollama", "llama3.1:8b"));
        Assert.Equal("llama3.1:8b", registry.GetModel("ollama", "llama3.1:8b")!.Name);
        Assert.NotNull(registry.GetProvider("faux"));
        Assert.NotNull(registry.GetModel("openai", "gpt-4o-mini"));
    }

    [Fact]
    public void MalformedConfig_StillStartsWithBuiltins()
    {
        using var dirs = new TestDirs();
        dirs.WriteConfig("{ definitely not json ]");

        var registry = DefaultModelRegistry.Create();

        Assert.NotNull(registry.GetProvider("openai"));
        Assert.NotNull(registry.GetProvider("anthropic"));
        Assert.NotNull(registry.GetProvider("faux"));
    }
}