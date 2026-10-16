using Xunit;

namespace PiNet.Extensions.Tests;

/// <summary>
/// Windows hardening for package installs: git bundles and clones contain read-only
/// object files, which used to make the reinstall delete (and the post-clone temp
/// cleanup) fail with "access denied"; VCS metadata is also skipped on copy.
/// </summary>
public class PackageManagerReplaceTests
{
    private static string NewBundle()
    {
        var bundle = Path.Combine(Path.GetTempPath(), "pinet-bundle-" + Guid.NewGuid().ToString("N")[..8]);
        TestDirs.WriteFile(bundle, "pinet.json", """{ "name": "ro-pkg", "version": "1.0.0" }""");
        TestDirs.WriteFile(bundle, "extensions/hello.cs", "// hello");
        return bundle;
    }

    [Fact]
    public async Task InstallFromPath_Reinstall_ReplacesReadOnlyFiles()
    {
        using var dirs = new TestDirs();
        var bundle = NewBundle();
        var readOnly = Path.Combine(bundle, "data", "blob.bin");
        TestDirs.WriteFile(bundle, "data/blob.bin", "x");
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);
        try
        {
            await PackageManager.InstallFromPathAsync(bundle);
            var again = await PackageManager.InstallFromPathAsync(bundle);

            Assert.Equal("ro-pkg", again.Name);
        }
        finally
        {
            try
            {
                File.SetAttributes(readOnly, FileAttributes.Normal);
                Directory.Delete(bundle, recursive: true);
            }
            catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task InstallFromPath_SkipsGitMetadata()
    {
        using var dirs = new TestDirs();
        var bundle = NewBundle();
        TestDirs.WriteFile(bundle, ".git/objects/ab/cdef0123", "object");
        try
        {
            var installed = await PackageManager.InstallFromPathAsync(bundle);

            Assert.False(Directory.Exists(Path.Combine(installed.Path, ".git")));
            Assert.True(File.Exists(Path.Combine(installed.Path, "extensions", "hello.cs")));
        }
        finally
        {
            try { Directory.Delete(bundle, recursive: true); } catch { /* best effort */ }
        }
    }
}
