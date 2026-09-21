using PiNet.Providers.Auth;
using Xunit;

namespace PiNet.Providers.Tests;

public sealed class ModelsConfigTests
{
    [Fact]
    public void MissingFile_ReturnsEmpty()
    {
        using var dirs = new TestDirs();
        var config = ModelsConfig.TryLoad(out var warning);

        Assert.Null(config.Providers);
        Assert.Null(warning);
    }

    [Fact]
    public void ValidJson_ParsesProvidersAndModels()
    {
        using var dirs = new TestDirs();
        dirs.WriteConfig("""
        {
          "providers": {
            "ollama": {
              "baseUrl": "http://localhost:11434/v1",
              "api": "openai-completions",
              "apiKey": "ollama",
              "models": [
                { "id": "llama3.1:8b" },
                { "id": "qwen2.5-coder:7b", "reasoning": true }
              ]
            }
          }
        }
        """);

        var config = ModelsConfig.TryLoad(out var warning);

        Assert.Null(warning);
        var provider = Assert.Single(config.Providers!);
        Assert.Equal("ollama", provider.Key);
        Assert.Equal("http://localhost:11434/v1", provider.Value.BaseUrl);
        Assert.Equal("openai-completions", provider.Value.Api);
        Assert.Equal("ollama", provider.Value.ApiKey);
        Assert.Collection(provider.Value.Models!,
            m => Assert.Equal("llama3.1:8b", m.Id),
            m =>
            {
                Assert.Equal("qwen2.5-coder:7b", m.Id);
                Assert.True(m.Reasoning);
            });
    }

    [Fact]
    public void MalformedJson_ReturnsEmpty_WithWarning()
    {
        using var dirs = new TestDirs();
        dirs.WriteConfig("{ this is not json ]");

        var config = ModelsConfig.TryLoad(out var warning);

        Assert.Null(config.Providers);
        Assert.NotNull(warning);
        Assert.Contains("models.json", warning);
    }

    [Fact]
    public void MissingRequiredModelId_YieldsWarning()
    {
        using var dirs = new TestDirs();
        dirs.WriteConfig("""{ "providers": { "ollama": { "baseUrl": "http://x/v1", "api": "openai-completions", "models": [ { } ] } } }""");

        var config = ModelsConfig.TryLoad(out var warning);

        Assert.Null(config.Providers);
        Assert.NotNull(warning);
    }

    [Fact]
    public async Task ConfigCredentialResolver_Literal_Used()
    {
        var resolver = new ConfigCredentialResolver("http://localhost:11434/v1", "ollama", ["OPENAI_API_KEY", "OPENAI_KEY"]);
        var auth = await resolver.ResolveAsync();

        Assert.Equal("ollama", auth.ApiKey);
        Assert.Equal("http://localhost:11434/v1", auth.BaseUrl);
    }

    [Fact]
    public async Task ConfigCredentialResolver_DollarPrefixed_ReadsEnvVar()
    {
        using var dirs = new TestDirs();
        const string envVar = "PINET_TEST_KEY";
        var original = Environment.GetEnvironmentVariable(envVar);
        try
        {
            Environment.SetEnvironmentVariable(envVar, "from-env");
            var resolver = new ConfigCredentialResolver("http://x", $"${envVar}", []);

            var auth = await resolver.ResolveAsync();

            Assert.Equal("from-env", auth.ApiKey);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, original);
        }
    }

    [Fact]
    public async Task ConfigCredentialResolver_MissingEnv_ReturnsNullKey()
    {
        using var dirs = new TestDirs();
        var resolver = new ConfigCredentialResolver("http://x", "$PINET_DEFINITELY_UNSET_12345", []);

        var auth = await resolver.ResolveAsync();

        Assert.Null(auth.ApiKey);
    }

    [Fact]
    public async Task ConfigCredentialResolver_FallsBackToDefaultEnv()
    {
        using var dirs = new TestDirs();
        const string envVar = "OPENAI_API_KEY";
        var original = Environment.GetEnvironmentVariable(envVar);
        try
        {
            Environment.SetEnvironmentVariable(envVar, "fallback-key");
            var resolver = new ConfigCredentialResolver("http://x", null, ["OPENAI_API_KEY", "OPENAI_KEY"]);

            var auth = await resolver.ResolveAsync();

            Assert.Equal("fallback-key", auth.ApiKey);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, original);
        }
    }
}