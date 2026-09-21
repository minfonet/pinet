using Xunit;

// Tests mutate PI_NET_HOME and environment variables process-wide, so this
// project runs single-threaded.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace PiNet.Providers.Tests;

/// <summary>Temp Pi home (PI_NET_HOME). The env var is restored on dispose.</summary>
public sealed class TestDirs : IDisposable
{
    private readonly string _root;
    private readonly string? _previousHome;

    public TestDirs()
    {
        _root = Path.Combine(Path.GetTempPath(), "pinet-prov-" + Guid.NewGuid().ToString("N")[..8]);
        Home = Path.Combine(_root, "home");
        Directory.CreateDirectory(Home);
        _previousHome = Environment.GetEnvironmentVariable("PI_NET_HOME");
        Environment.SetEnvironmentVariable("PI_NET_HOME", Home);
    }

    public string Home { get; }

    public void WriteConfig(string json) =>
        File.WriteAllText(Path.Combine(Home, "models.json"), json);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PI_NET_HOME", _previousHome);
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}