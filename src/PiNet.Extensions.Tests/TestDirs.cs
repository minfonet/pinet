using Xunit;

// The PI_NET_HOME override test mutates a process-wide environment variable,
// so this project runs single-threaded.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace PiNet.Extensions.Tests;

/// <summary>
/// Temp home dir (PI_NET_HOME) + a temp project cwd. The env var is restored
/// on dispose.
/// </summary>
public sealed class TestDirs : IDisposable
{
    private readonly string _root;
    private readonly string? _previousHome;

    public TestDirs()
    {
        _root = Path.Combine(Path.GetTempPath(), "pinet-ext-" + Guid.NewGuid().ToString("N")[..8]);
        Home = Path.Combine(_root, "home");
        Cwd = Path.Combine(_root, "project");
        Directory.CreateDirectory(Home);
        Directory.CreateDirectory(Cwd);
        _previousHome = Environment.GetEnvironmentVariable("PI_NET_HOME");
        Environment.SetEnvironmentVariable("PI_NET_HOME", Home);
    }

    public string Home { get; }
    public string Cwd { get; }

    public static void WriteFile(string root, string file, string content)
    {
        var full = Path.Combine(root, file);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(full, content);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PI_NET_HOME", _previousHome);
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}