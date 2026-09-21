using Xunit;

namespace PiNet.Extensions.Tests;

public class TarExtractorTests
{
    public static string DestDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pinet-tar-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void ExtractsFilesAndDirectories()
    {
        var build = new TarBuilder()
            .AddDir("pkg")
            .AddFile("pkg/a.txt", "hello")
            .AddDir("pkg/sub")
            .AddFile("pkg/sub/b.txt", "world")
            .Build();
        var dest = DestDir();
        try
        {
            TarExtractor.ExtractFromBytes(TarBuilder.Gzip(build), dest);

            Assert.Equal("hello", File.ReadAllText(Path.Combine(dest, "pkg", "a.txt")));
            Assert.Equal("world", File.ReadAllText(Path.Combine(dest, "pkg", "sub", "b.txt")));
        }
        finally
        {
            Directory.Delete(dest, recursive: true);
        }
    }

    [Fact]
    public void StripPrefix_RemovesLeadingPackageDir()
    {
        var build = new TarBuilder()
            .AddFile("package/pinet.json", "{}")
            .AddFile("package/extensions/x.cs", "code")
            .Build();
        var dest = DestDir();
        try
        {
            TarExtractor.ExtractFromBytes(TarBuilder.Gzip(build), dest, stripPrefix: "package");

            Assert.Equal("{}", File.ReadAllText(Path.Combine(dest, "pinet.json")));
            Assert.Equal("code", File.ReadAllText(Path.Combine(dest, "extensions", "x.cs")));
        }
        finally
        {
            Directory.Delete(dest, recursive: true);
        }
    }

    [Fact]
    public void PaxEntries_AreSkipped()
    {
        var build = new TarBuilder()
            .AddPax("pkg/pax-header", "30 path=pkg/a.txt\n")
            .AddFile("pkg/a.txt", "ok")
            .Build();
        var dest = DestDir();
        try
        {
            TarExtractor.ExtractFromBytes(TarBuilder.Gzip(build), dest);

            Assert.Equal("ok", File.ReadAllText(Path.Combine(dest, "pkg", "a.txt")));
        }
        finally
        {
            Directory.Delete(dest, recursive: true);
        }
    }

    [Fact]
    public void DotDotTraversal_IsRejected()
    {
        var build = new TarBuilder()
            .AddFile("../evil.txt", "boom")
            .Build();
        var dest = DestDir();
        try
        {
            TarExtractor.ExtractFromBytes(TarBuilder.Gzip(build), dest);

            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(dest)!, "evil.txt")));
            Assert.False(File.Exists(Path.Combine(dest, "evil.txt")));
        }
        finally
        {
            Directory.Delete(dest, recursive: true);
        }
    }

    [Fact]
    public void AbsolutePath_IsRejected()
    {
        var build = new TarBuilder()
            .AddFile("/abs/evil.txt", "boom")
            .Build();
        var dest = DestDir();
        try
        {
            TarExtractor.ExtractFromBytes(TarBuilder.Gzip(build), dest);

            Assert.False(File.Exists(Path.Combine(dest, "abs", "evil.txt")));
        }
        finally
        {
            Directory.Delete(dest, recursive: true);
        }
    }

    [Fact]
    public void WindowsDrivePath_IsRejected()
    {
        var build = new TarBuilder()
            .AddFile("C:/evil.txt", "boom")
            .Build();
        var dest = DestDir();
        try
        {
            TarExtractor.ExtractFromBytes(TarBuilder.Gzip(build), dest);

            Assert.False(File.Exists(Path.Combine(dest, "evil.txt")));
        }
        finally
        {
            Directory.Delete(dest, recursive: true);
        }
    }
}