using System.Diagnostics;
using System.Text.Json;

namespace PiNet.Extensions;

public sealed record PackageInfo(string Name, string Version, string Source, string Path);

public static class PackageManager
{
    public static string PackagesRoot() => ExtensionsPaths.PackagesRoot;

    public static Task<PackageInfo> InstallAsync(string source, CancellationToken ct = default)
    {
        if (source.StartsWith("npm:", StringComparison.OrdinalIgnoreCase))
            return InstallNpmAsync(source[4..], ct);
        if (source.StartsWith("git:", StringComparison.OrdinalIgnoreCase))
            return InstallGitAsync(source[4..], ct);
        if (source.StartsWith("path:", StringComparison.OrdinalIgnoreCase))
            return InstallFromPathAsync(source[5..], ct);
        return InstallFromPathAsync(source, ct);
    }

    public static async Task<PackageInfo> InstallFromPathAsync(string sourceDir, CancellationToken ct = default)
    {
        var resolved = Path.GetFullPath(sourceDir);
        if (!Directory.Exists(resolved))
            throw new IOException($"package source directory not found: {resolved}");

        var (name, version) = ReadManifest(resolved);
        var dest = Path.Combine(ExtensionsPaths.PackagesRoot, name);
        if (Directory.Exists(dest))
            Directory.Delete(dest, recursive: true);
        Directory.CreateDirectory(dest);

        CopyDirectory(resolved, dest);

        var package = new PackageInfo(name, version, sourceDir, dest);
        await RecordAsync(package, ct);
        return package;
    }

    public static async Task<PackageInfo> InstallNpmAsync(string package, CancellationToken ct = default)
    {
        var temp = Directory.CreateTempSubdirectory("pinet-npm-").FullName;
        try
        {
            var (exit, output, error) = await RunAsync("npm", cmd =>
            {
                cmd.ArgumentList.Add("pack");
                cmd.ArgumentList.Add(package);
            }, temp, ct);
            if (exit != 0)
                throw new IOException($"npm pack failed: {Trim(error)}");

            var tgz = Directory.GetFiles(temp, "*.tgz", SearchOption.TopDirectoryOnly).OrderBy(f => f).FirstOrDefault();
            if (tgz is null)
                throw new IOException("npm pack produced no .tgz file");

            var extracted = Path.Combine(temp, "pkg");
            TarExtractor.Extract(tgz, extracted, stripPrefix: "package");

            return await InstallFromPathAsync(extracted, ct);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { /* best effort */ }
        }
    }

    public static async Task<PackageInfo> InstallGitAsync(string url, CancellationToken ct = default)
    {
        var temp = Path.Combine(Path.GetTempPath(), "pinet-git-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var (exit, output, error) = await RunAsync("git", cmd =>
            {
                cmd.ArgumentList.Add("clone");
                cmd.ArgumentList.Add("--depth");
                cmd.ArgumentList.Add("1");
                cmd.ArgumentList.Add(url);
                cmd.ArgumentList.Add(temp);
            }, Path.GetTempPath(), ct);
            if (exit != 0)
                throw new IOException($"git clone failed: {Trim(error)}");

            return await InstallFromPathAsync(temp, ct);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { /* best effort */ }
        }
    }

    public static async Task<IReadOnlyList<PackageInfo>> ListAsync(CancellationToken ct = default)
    {
        var builder = LoadIndex(ct);
        return await Task.FromResult(builder);
    }

    public static async Task RemoveAsync(string name, CancellationToken ct = default)
    {
        var normalized = name?.ToLowerInvariant();
        var packages = LoadIndex(ct).ToList();
        var match = packages.FirstOrDefault(p => p.Name.Equals(normalized ?? string.Empty, StringComparison.OrdinalIgnoreCase));
        if (match is null)
            throw new IOException($"package not installed: {name}");
        packages.Remove(match);
        if (Directory.Exists(match.Path))
            Directory.Delete(match.Path, recursive: true);
        await SaveIndexAsync(packages, ct);
    }

    public static async Task UpdateAsync(string? name, CancellationToken ct = default)
    {
        var packages = LoadIndex(ct).ToList();
        if (!string.IsNullOrWhiteSpace(name))
        {
            var match = packages.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (match is null)
                throw new IOException($"package not installed: {name}");
            await InstallAsync(match.Source, ct);
            return;
        }

        foreach (var package in packages)
            await InstallAsync(package.Source, ct);
    }

    internal static IReadOnlyList<PackageInfo> ListPackagesFromDisk(string packagesRoot)
    {
        if (!Directory.Exists(packagesRoot))
            return [];
        return Directory.GetDirectories(packagesRoot)
            .Select(dir => new DirectoryInfo(dir))
            .OrderBy(d => d.Name, StringComparer.Ordinal)
            .Select(d => new PackageInfo(d.Name, "0.0.0", d.Name, d.FullName))
            .ToArray();
    }

    private static IReadOnlyList<PackageInfo> LoadIndex(CancellationToken ct)
    {
        var path = ExtensionsPaths.PackagesIndexPath;
        if (!File.Exists(path))
            return [];

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return [];
            var list = new List<PackageInfo>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;
                var package = ReadPackageInfo(item);
                if (package is not null)
                    list.Add(package);
            }
            return list;
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static PackageInfo? ReadPackageInfo(JsonElement item)
    {
        string? s(string key) =>
            item.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var name = s("name");
        var version = s("version");
        var source = s("source");
        var path = s("path");
        return name is null || version is null || source is null || path is null
            ? null
            : new PackageInfo(name, version, source, path);
    }

    private static async Task RecordAsync(PackageInfo package, CancellationToken ct)
    {
        var existing = LoadIndex(ct).ToList();
        existing.RemoveAll(p => p.Name.Equals(package.Name, StringComparison.OrdinalIgnoreCase));
        existing.Add(package);
        await SaveIndexAsync(existing, ct);
    }

    private static async Task SaveIndexAsync(IReadOnlyList<PackageInfo> packages, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(ExtensionsPaths.PackagesIndexPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var package in packages)
            {
                writer.WriteStartObject();
                writer.WriteString("name", package.Name);
                writer.WriteString("version", package.Version);
                writer.WriteString("source", package.Source);
                writer.WriteString("path", package.Path);
                writer.WriteString("installedUtc", DateTime.UtcNow.ToString("O"));
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }

        await File.WriteAllTextAsync(ExtensionsPaths.PackagesIndexPath, System.Text.Encoding.UTF8.GetString(stream.ToArray()), ct);
    }

    private static (string Name, string Version) ReadManifest(string dir)
    {
        var path = Path.Combine(dir, "pinet.json");
        if (!File.Exists(path))
            return (new DirectoryInfo(dir).Name.ToLowerInvariant(), "0.0.0");
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString()!.Trim().ToLowerInvariant()
                : new DirectoryInfo(dir).Name.ToLowerInvariant();
            var version = root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()!
                : "0.0.0";
            return (string.IsNullOrWhiteSpace(name) ? new DirectoryInfo(dir).Name.ToLowerInvariant() : name, version);
        }
        catch (Exception)
        {
            return (new DirectoryInfo(dir).Name.ToLowerInvariant(), "0.0.0");
        }
    }

    private static void CopyDirectory(string source, string dest)
    {
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(dest, relative);
            var targetDir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(targetDir))
                Directory.CreateDirectory(targetDir);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(
        string command,
        Action<ProcessStartInfo> configure,
        string workingDirectory,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = command,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };
        configure(psi);
        using var process = new Process { StartInfo = psi };
        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(ct);
        var errorTask = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { /* ignore */ }
            throw;
        }
        return (process.ExitCode, await outputTask, await errorTask);
    }

    private static string Trim(string text) =>
        string.IsNullOrWhiteSpace(text) ? string.Empty : text.Trim();
}