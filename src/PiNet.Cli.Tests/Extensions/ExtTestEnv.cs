using PiNet.Abstractions.Providers;
using PiNet.Cli.Interact;
using PiNet.Extensions;
using PiNet.Providers;
using PiNet.Sessions;
using Xunit;

namespace PiNet.Cli.Tests;

[CollectionDefinition("Extensions", DisableParallelization = true)]
public sealed class ExtensionsSerialCollection;

/// <summary>
/// Overrides the Pi home to a temp folder for the duration of a test and
/// restores it afterwards. Deterministic isolation from the developer machine.
/// </summary>
public sealed class ExtTestEnv : IDisposable
{
    public string Home { get; }
    public string Cwd { get; }
    private readonly string? _previous;

    public ExtTestEnv()
    {
        var baseDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "pinet-ext-cli-" + Guid.NewGuid().ToString("N")[..8])).FullName;
        Home = Directory.CreateDirectory(Path.Combine(baseDir, "home")).FullName;
        Cwd = Directory.CreateDirectory(Path.Combine(baseDir, "cwd")).FullName;
        _previous = Environment.GetEnvironmentVariable("PI_NET_HOME");
        Environment.SetEnvironmentVariable("PI_NET_HOME", Home);
    }

    public void Write(string file, string content)
    {
        var path = Path.Combine(Cwd, file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public void WriteToHome(string file, string content)
    {
        var path = Path.Combine(Home, file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PI_NET_HOME", _previous);
        try { Directory.Delete(Path.GetDirectoryName(Cwd)!, recursive: true); } catch { /* best effort */ }
    }
}

/// <summary>Shared wiring used by the extension/skill/template loop tests.</summary>
internal sealed class LoopFixture : IDisposable
{
    public ModelRegistry Registry { get; }
    public ChatService Chat { get; }
    public SessionManager Manager { get; }
    public string Cwd { get; }
    public ExtTestEnv Env { get; }

    public LoopFixture(ExtTestEnv env)
    {
        Env = env;
        Cwd = env.Cwd;
        Registry = new ModelRegistry();
        Chat = new ChatService(Registry);
        Manager = new SessionManager(Path.Combine(env.Home, "sessions"));
    }

    public void Dispose() => Env.Dispose();

    public static InteractiveLoop NewLoop(
        LoopFixture fx,
        ScriptedInput input,
        CapturingOutput output,
        ExtensionHost? extensions = null,
        SkillRegistry? skills = null,
        IReadOnlyList<PromptTemplate>? templates = null,
        string? systemPrompt = null) =>
        new(chat: fx.Chat,
            manager: fx.Manager,
            cwd: fx.Cwd,
            providerId: "faux",
            modelId: "faux-model",
            input: input,
            output: output,
            extensions: extensions,
            skills: skills,
            templates: templates,
            systemPrompt: systemPrompt);
}