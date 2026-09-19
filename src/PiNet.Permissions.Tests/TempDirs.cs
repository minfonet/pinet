using Xunit;

// The PI_NET_HOME override test mutates a process-wide environment variable,
// so this project runs single-threaded.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace PiNet.Permissions.Tests;

/// <summary>Two temp dirs: a cwd and a sibling dir used as "outside".</summary>
public sealed class TempDirs : IDisposable
{
    private readonly string _root;

    public TempDirs()
    {
        _root = Directory.CreateTempSubdirectory("pinet-perm-").FullName;
        Cwd = Path.Combine(_root, "project");
        Outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(Cwd);
        Directory.CreateDirectory(Outside);
    }

    public string Cwd { get; }
    public string Outside { get; }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
