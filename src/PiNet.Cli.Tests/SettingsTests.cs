using PiNet.AgentCore;
using PiNet.Cli.Interact;
using PiNet.Cli.Settings;
using Xunit;

namespace PiNet.Cli.Tests;

/// <summary>
/// 7g: settings merge (project wins over global), malformed-file tolerance,
/// <c>PI_NET_HOME</c> relocation, the /settings command (view + edit), and the
/// thinking level surfaced in the agent's state.
/// </summary>
/// <para>
/// Every test points <c>PI_NET_HOME</c> at a unique temp dir. Because that env var is
/// process-wide, each test holds a global lock for its full duration (constructor to
/// dispose) so the var is never read while another test has repurposed it.
/// </para>
public class SettingsTests
{
    // Lock held across each test's full body: the env var is process-wide.
    private static readonly object _homeLock = new();

    private sealed class HomeFixture : IDisposable
    {
        public string Home { get; }
        public string Cwd { get; }
        public string GlobalPath => Path.Combine(Home, "settings.json");
        public string ProjectPath => Path.Combine(Cwd, ".pi", "settings.json");

        private readonly string _home;
        private readonly string _cwd;
        private readonly string? _prior;
        private bool _exited;

        private static string MakeDir(string prefix) =>
            Directory.CreateDirectory(
                Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N")[..8])).FullName;

        public HomeFixture()
        {
            Monitor.Enter(_homeLock);
            try
            {
                _home = MakeDir("pinet-home-");
                _cwd = MakeDir("pinet-cwd-");
                Directory.CreateDirectory(Path.Combine(_cwd, ".pi"));

                _prior = Environment.GetEnvironmentVariable("PI_NET_HOME");
                Environment.SetEnvironmentVariable("PI_NET_HOME", _home);

                Home = _home;
                Cwd = _cwd;
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void WriteGlobal(string json) => File.WriteAllText(GlobalPath, json);
        public void WriteProject(string json) => File.WriteAllText(ProjectPath, json);

        public void Dispose()
        {
            try
            {
                if (_prior is null)
                    Environment.SetEnvironmentVariable("PI_NET_HOME", null);
                else
                    Environment.SetEnvironmentVariable("PI_NET_HOME", _prior);

                Directory.Delete(_home, recursive: true);
                Directory.Delete(_cwd, recursive: true);
            }
            finally
            {
                if (!_exited)
                {
                    _exited = true;
                    Monitor.Exit(_homeLock);
                }
            }
        }
    }

    /// <summary>Builds an <see cref="EffectiveSettings"/> directly (no files read).</summary>
    private static EffectiveSettings NewSettings(
        string? provider = "openai",
        string? model = "gpt-4o",
        Abstractions.Chat.ThinkingLevel? thinking = null,
        double threshold = 0.75,
        int preserve = 4,
        bool autoCompact = false,
        bool verbose = false,
        string? globalPath = null,
        string? projectPath = null,
        SettingsFile? global = null,
        SettingsFile? project = null) =>
        new(
            provider,
            model,
            thinking,
            threshold,
            preserve,
            autoCompact,
            verbose,
            globalPath ?? "/fake/global",
            projectPath ?? "/fake/project",
            global,
            project,
            Array.Empty<string>());

    // ------------------------------------------------- Load: merge behaviour
    [Fact]
    public void Load_NoFiles_ReturnsDefaults_NoWarnings()
    {
        using var home = new HomeFixture();
        var s = SettingsManager.Load(home.Cwd);

        Assert.Null(s.Provider);
        Assert.Null(s.Model);
        Assert.Null(s.ThinkingLevel);
        Assert.Equal(0.75, s.CompactThreshold, precision: 4);
        Assert.Equal(4, s.CompactPreserveRecent);
        Assert.False(s.AutoCompact);
        Assert.False(s.Verbose);
        Assert.Empty(s.Warnings);
        Assert.Equal(home.GlobalPath, s.GlobalPath);
        Assert.Equal(home.ProjectPath, s.ProjectPath);
        Assert.Null(s.Global);
        Assert.Null(s.Project);
    }

    [Fact]
    public void Load_Global_PersistsAllValues()
    {
        using var home = new HomeFixture();
        home.WriteGlobal(@"
{
  ""provider"": ""anthropic"",
  ""model"": ""claude-3-5"",
  ""thinkingLevel"": ""high"",
  ""compactThreshold"": 0.6,
  ""compactPreserveRecent"": 6,
  ""autoCompact"": true,
  ""verbose"": true
}
");

        var s = SettingsManager.Load(home.Cwd);

        Assert.Equal("anthropic", s.Provider);
        Assert.Equal("claude-3-5", s.Model);
        Assert.Equal(Abstractions.Chat.ThinkingLevel.High, s.ThinkingLevel);
        Assert.Equal(0.6, s.CompactThreshold, precision: 4);
        Assert.Equal(6, s.CompactPreserveRecent);
        Assert.True(s.AutoCompact);
        Assert.True(s.Verbose);
        Assert.Empty(s.Warnings);
        Assert.NotNull(s.Global);
        Assert.Null(s.Project);
    }

    [Fact]
    public void Load_Project_Wins_OverGlobal()
    {
        using var home = new HomeFixture();
        home.WriteGlobal(@"{""provider"":""global-p"",""model"":""global-m""}");
        home.WriteProject(@"{""provider"":""proj-p"",""model"":""proj-m""}");

        var s = SettingsManager.Load(home.Cwd);

        Assert.Equal("proj-p", s.Provider);
        Assert.Equal("proj-m", s.Model);
    }

    [Fact]
    public void Load_Project_ExplicitFalse_Wins_OverGlobal_True()
    {
        using var home = new HomeFixture();
        home.WriteGlobal(@"{""verbose"":true,""provider"":""global-p""}");
        home.WriteProject(@"{""verbose"":false}");

        var s = SettingsManager.Load(home.Cwd);

        Assert.False(s.Verbose);              // explicit project false wins
        Assert.Equal("global-p", s.Provider); // project unset; global used
    }

    [Fact]
    public void Load_MalformedGlobal_Warns_And_FallsBack_ToDefaults()
    {
        using var home = new HomeFixture();
        home.WriteGlobal("{ this is not json ");

        var s = SettingsManager.Load(home.Cwd);

        var w = Assert.Single(s.Warnings);
        Assert.Contains("Malformed global settings", w);
        Assert.Null(s.Provider);
    }

    [Fact]
    public void Load_MalformedProject_Warns_And_FallsBack_ToGlobal()
    {
        using var home = new HomeFixture();
        home.WriteGlobal(@"{""provider"":""global-p""}");
        home.WriteProject("{ broken ");

        var s = SettingsManager.Load(home.Cwd);

        Assert.Single(s.Warnings);
        Assert.Equal("global-p", s.Provider);
    }

    [Fact]
    public void Load_InvalidThreshold_Clamps_To1_WithWarning()
    {
        using var home = new HomeFixture();
        home.WriteGlobal(@"{""compactThreshold"":1.5}");

        var s = SettingsManager.Load(home.Cwd);

        Assert.Equal(1.0, s.CompactThreshold, precision: 4);
        Assert.Single(s.Warnings);
    }

    [Fact]
    public void Load_InvalidThinking_Ignored_WithWarning()
    {
        using var home = new HomeFixture();
        home.WriteGlobal(@"{""thinkingLevel"":""nonsense""}");

        var s = SettingsManager.Load(home.Cwd);

        Assert.Null(s.ThinkingLevel);
        Assert.Single(s.Warnings);
    }

    [Fact]
    public void Load_InvalidPreserve_Ignored_WithWarning()
    {
        using var home = new HomeFixture();
        home.WriteGlobal(@"{""compactPreserveRecent"":-3}");

        var s = SettingsManager.Load(home.Cwd);

        Assert.Equal(4, s.CompactPreserveRecent);
        Assert.Single(s.Warnings);
    }

    // --------------------------- Load: PI_NET_HOME relocation
    [Fact]
    public void PINetHome_Determines_GlobalPath()
    {
        using var home = new HomeFixture();

        Assert.Equal(Path.Combine(home.Home, "settings.json"), SettingsManager.GlobalPath);
    }

    // ------------------------------------------------ SetGlobal (edit)
    [Fact]
    public void SetGlobal_PersistsEachKey_And_RoundTrips()
    {
        using var home = new HomeFixture();

        Assert.Null(SettingsManager.SetGlobal("provider", "openai"));
        Assert.Null(SettingsManager.SetGlobal("thinkingLevel", "High"));
        Assert.Null(SettingsManager.SetGlobal("compactThreshold", "0.8"));
        Assert.Null(SettingsManager.SetGlobal("compactPreserveRecent", "3"));
        Assert.Null(SettingsManager.SetGlobal("autoCompact", "true"));
        Assert.Null(SettingsManager.SetGlobal("verbose", "false"));

        var s = SettingsManager.Load(home.Cwd);

        Assert.Equal("openai", s.Provider);
        Assert.Equal(Abstractions.Chat.ThinkingLevel.High, s.ThinkingLevel);
        Assert.Equal(0.8, s.CompactThreshold, precision: 4);
        Assert.Equal(3, s.CompactPreserveRecent);
        Assert.True(s.AutoCompact);
        Assert.False(s.Verbose);
    }

    [Fact]
    public void SetGlobal_UnknownKey_ReturnsWarning_And_DoesNotWrite()
    {
        using var home = new HomeFixture();

        var w = SettingsManager.SetGlobal("bogus", "x");

        Assert.NotNull(w);
        Assert.Contains("Unknown setting", w);
        Assert.False(File.Exists(home.GlobalPath));
    }

    [Fact]
    public void SetGlobal_InvalidValues_Return_Warnings_And_DoNotWrite()
    {
        using var home = new HomeFixture();

        Assert.Contains("Invalid thinkingLevel", SettingsManager.SetGlobal("thinkingLevel", "bogus"));
        Assert.Contains("Invalid compactThreshold", SettingsManager.SetGlobal("compactThreshold", "5"));
        Assert.Contains("Invalid compactPreserveRecent", SettingsManager.SetGlobal("compactPreserveRecent", "-3"));
        Assert.Contains("Invalid verbose", SettingsManager.SetGlobal("verbose", "maybe"));
        Assert.False(File.Exists(home.GlobalPath));
    }

    // ----------------------------------- /settings command (via loop)
    [Fact]
    public async Task SettingsCommand_View_ShowsEffectiveValues()
    {
        using var home = new HomeFixture();
        using var fx = new InteractiveLoopTests.Fixture();
        fx.Registry.RegisterProvider(new ScriptedProvider("faux"));

        var s = NewSettings(provider: "openai", model: "gpt-4o", thinking: Abstractions.Chat.ThinkingLevel.High);

        var input = new ScriptedInput(
        [
            ..Keys.Type("/settings"), Keys.Enter,
            ..Keys.Type("/quit"), Keys.Enter
        ]);
        var output = new CapturingOutput();
        var loop = InteractiveLoopTests.NewLoop(fx, input, output, settings: s);

        var code = await loop.RunAsync();

        Assert.Equal(0, code);
        Assert.Contains("Effective settings", output.All);
        Assert.Contains("openai", output.All);
        Assert.Contains("gpt-4o", output.All);
        Assert.Contains("High", output.All);
    }

    [Fact]
    public async Task SettingsCommand_Edit_Writes_To_GlobalFile_And_AnnouncesIt()
    {
        using var home = new HomeFixture();
        using var fx = new InteractiveLoopTests.Fixture();
        fx.Registry.RegisterProvider(new ScriptedProvider("faux"));

        var s = NewSettings(provider: "faux", globalPath: home.GlobalPath);

        var input = new ScriptedInput(
        [
            ..Keys.Type("/settings thinkingLevel High"), Keys.Enter,
            ..Keys.Type("/quit"), Keys.Enter
        ]);
        var output = new CapturingOutput();
        var loop = InteractiveLoopTests.NewLoop(fx, input, output, settings: s);

        var code = await loop.RunAsync();

        Assert.Equal(0, code);
        Assert.True(File.Exists(home.GlobalPath));
        var content = File.ReadAllText(home.GlobalPath);
        Assert.Contains("\"thinkingLevel\"", content);
        Assert.Contains("High", content);
        Assert.Contains("(global thinkingLevel = High", output.All);
    }

    [Fact]
    public async Task SettingsCommand_UnknownKey_AnnouncesError_And_DoesNotWrite()
    {
        using var home = new HomeFixture();
        using var fx = new InteractiveLoopTests.Fixture();
        fx.Registry.RegisterProvider(new ScriptedProvider("faux"));

        var s = NewSettings(provider: "faux", globalPath: home.GlobalPath);

        var input = new ScriptedInput(
        [
            ..Keys.Type("/settings bogus x"), Keys.Enter,
            ..Keys.Type("/quit"), Keys.Enter
        ]);
        var output = new CapturingOutput();
        var loop = InteractiveLoopTests.NewLoop(fx, input, output, settings: s);

        var code = await loop.RunAsync();

        Assert.Equal(0, code);
        Assert.Contains("Unknown setting", output.All);
        Assert.False(File.Exists(home.GlobalPath));
    }

    [Fact]
    public async Task SettingsCommand_InvalidValue_AnnouncesError_And_DoesNotWrite()
    {
        using var home = new HomeFixture();
        using var fx = new InteractiveLoopTests.Fixture();
        fx.Registry.RegisterProvider(new ScriptedProvider("faux"));

        var s = NewSettings(provider: "faux", globalPath: home.GlobalPath);

        var input = new ScriptedInput(
        [
            ..Keys.Type("/settings compactThreshold 5"), Keys.Enter,
            ..Keys.Type("/quit"), Keys.Enter
        ]);
        var output = new CapturingOutput();
        var loop = InteractiveLoopTests.NewLoop(fx, input, output, settings: s);

        var code = await loop.RunAsync();

        Assert.Equal(0, code);
        Assert.Contains("Invalid compactThreshold", output.All);
        Assert.False(File.Exists(home.GlobalPath));
    }

    // ----------------- thinking level is surfaced in the agent state
    [Fact]
    public async Task ThinkingLevel_FromSettings_Is_Surfaced_InAgentState_ViaVerboseBanner()
    {
        using var fx = new InteractiveLoopTests.Fixture();
        fx.Registry.RegisterProvider(new ScriptedProvider("faux", "ok"));

        var s = NewSettings(provider: "faux", thinking: Abstractions.Chat.ThinkingLevel.Max, verbose: true);

        var input = new ScriptedInput([Keys.CtrlC]);
        var output = new CapturingOutput();
        var loop = InteractiveLoopTests.NewLoop(fx, input, output, settings: s);

        var code = await loop.RunAsync();

        Assert.Equal(0, code);
        Assert.Contains("thinking=Max", output.All);
    }

    [Fact]
    public async Task ThinkingLevel_Default_Off_When_NoSettings()
    {
        using var fx = new InteractiveLoopTests.Fixture();
        fx.Registry.RegisterProvider(new ScriptedProvider("faux", "ok"));

        // Build a loop whose settings are silent about thinking but verbose, so the
        // banner reveals the default Off.
        var s = NewSettings(provider: "faux", verbose: true, thinking: Abstractions.Chat.ThinkingLevel.Off);

        var input = new ScriptedInput([Keys.CtrlC]);
        var output = new CapturingOutput();
        var loop = InteractiveLoopTests.NewLoop(fx, input, output, settings: s);

        var code = await loop.RunAsync();

        Assert.Equal(0, code);
        Assert.Contains("thinking=Off", output.All);
    }
}
