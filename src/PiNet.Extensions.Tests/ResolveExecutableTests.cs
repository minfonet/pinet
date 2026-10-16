using Xunit;

namespace PiNet.Extensions.Tests;

/// <summary>
/// Windows CLI shims: tools like npm exist only as .cmd/.bat wrappers and
/// Process.Start with UseShellExecute = false does not resolve them through
/// PATHEXT, so `pinet install npm:...` used to fail with "cannot find the file".
/// </summary>
public class ResolveExecutableTests
{
    [Fact]
    public void Windows_FindsCmdShim_ThroughPathExt()
    {
        var dir = Directory.CreateTempSubdirectory("pinet-resolve-").FullName;
        try
        {
            var shim = Path.Combine(dir, "faketool.cmd");
            File.WriteAllText(shim, "@echo off");

            var resolved = PackageManager.ResolveExecutable("faketool", dir, ".cmd;.exe", isWindows: true);

            Assert.Equal(shim, resolved, ignoreCase: true);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void NonWindows_ReturnsBareName()
    {
        Assert.Equal("faketool", PackageManager.ResolveExecutable("faketool", "/usr/bin", ".cmd", isWindows: false));
    }

    [Fact]
    public void Windows_MissingTool_ReturnsBareName()
    {
        var dir = Directory.CreateTempSubdirectory("pinet-resolve-").FullName;
        try
        {
            Assert.Equal("faketool", PackageManager.ResolveExecutable("faketool", dir, ".cmd;.exe", isWindows: true));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }
}
