using Xunit;

namespace PiNet.Extensions.Tests;

public class PackageManagerTests
{
    [Fact]
    public async Task InstallFromPath_CopiesBundle_AndLists()
    {
        using var dirs = new TestDirs();
        var bundle = Path.Combine(Path.GetTempPath(), "pinet-bundle-" + Guid.NewGuid().ToString("N")[..8]);
        TestDirs.WriteFile(bundle, "pinet.json", """{ "name": "my-tools", "version": "1.2.3" }""");
        TestDirs.WriteFile(bundle, "extensions/hello.cs", "// hello");
        TestDirs.WriteFile(bundle, "skills/README.md", "not a skill");
        TestDirs.WriteFile(bundle, "prompts/review.md", "Review $1");
        try
        {
            var installed = await PackageManager.InstallFromPathAsync(bundle);

            Assert.Equal("my-tools", installed.Name);
            Assert.Equal("1.2.3", installed.Version);
            Assert.True(File.Exists(Path.Combine(installed.Path, "extensions", "hello.cs")));
            Assert.True(File.Exists(Path.Combine(dirs.Home, "packages.json")));

            var listed = await PackageManager.ListAsync();
            var match = Assert.Single(listed, p => p.Name == "my-tools");
            Assert.Equal("1.2.3", match.Version);
        }
        finally
        {
            try { Directory.Delete(bundle, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task InstallPathPrefix_Works()
    {
        using var dirs = new TestDirs();
        var bundle = Path.Combine(Path.GetTempPath(), "pinet-bundle-" + Guid.NewGuid().ToString("N")[..8]);
        TestDirs.WriteFile(bundle, "hello.txt", "hi");
        try
        {
            var installed = await PackageManager.InstallAsync($"path:{bundle}");

            Assert.Equal(new DirectoryInfo(bundle).Name.ToLowerInvariant(), installed.Name);
            Assert.Equal("0.0.0", installed.Version);
        }
        finally
        {
            try { Directory.Delete(bundle, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task Remove_DeletesDirectoryAndIndex()
    {
        using var dirs = new TestDirs();
        var bundle = Path.Combine(Path.GetTempPath(), "pinet-bundle-" + Guid.NewGuid().ToString("N")[..8]);
        TestDirs.WriteFile(bundle, "pinet.json", """{ "name": "gone", "version": "0.0.1" }""");
        try
        {
            await PackageManager.InstallFromPathAsync(bundle);
            await PackageManager.RemoveAsync("gone");

            Assert.Empty(await PackageManager.ListAsync());
            Assert.False(Directory.Exists(Path.Combine(dirs.Home, "packages", "gone")));
        }
        finally
        {
            try { Directory.Delete(bundle, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task Update_ReinstallsFromRecordedSource()
    {
        using var dirs = new TestDirs();
        var bundle = Path.Combine(Path.GetTempPath(), "pinet-bundle-" + Guid.NewGuid().ToString("N")[..8]);
        TestDirs.WriteFile(bundle, "pinet.json", """{ "name": "up", "version": "0.0.1" }""");
        TestDirs.WriteFile(bundle, "data.txt", "v1");
        try
        {
            var installed = await PackageManager.InstallFromPathAsync(bundle);
            TestDirs.WriteFile(bundle, "data.txt", "v2");

            await PackageManager.UpdateAsync("up");

            var dest = Path.Combine(dirs.Home, "packages", "up");
            Assert.Equal("v2", File.ReadAllText(Path.Combine(dest, "data.txt")));
            _ = installed;
        }
        finally
        {
            try { Directory.Delete(bundle, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task Remove_UnknownPackage_Throws()
    {
        using var dirs = new TestDirs();
        await Assert.ThrowsAsync<IOException>(() => PackageManager.RemoveAsync("nope"));
    }

    [Fact]
    public async Task IndexRoundTrip_AfterReinstall_KeepsOneEntry()
    {
        using var dirs = new TestDirs();
        var bundle = Path.Combine(Path.GetTempPath(), "pinet-bundle-" + Guid.NewGuid().ToString("N")[..8]);
        TestDirs.WriteFile(bundle, "pinet.json", """{ "name": "again", "version": "0.0.1" }""");
        try
        {
            await PackageManager.InstallFromPathAsync(bundle);
            await PackageManager.InstallFromPathAsync(bundle);

            var listed = await PackageManager.ListAsync();
            Assert.Single(listed, p => p.Name == "again");
        }
        finally
        {
            try { Directory.Delete(bundle, recursive: true); } catch { /* best effort */ }
        }
    }
}