using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace PiNet.Extensions;

public sealed class ExtensionHost : IDisposable
{
    private readonly List<ExtensionToolSpec> _tools = [];
    private readonly Dictionary<string, ExtensionCommandHandler> _commands = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _warnings = [];
    private readonly List<IDisposable> _disposables = [];
    private bool _disposed;

    public IReadOnlyList<ExtensionToolSpec> Tools => _tools;
    public IReadOnlyDictionary<string, ExtensionCommandHandler> Commands => _commands;
    public IReadOnlyList<string> Warnings => _warnings;

    public static async Task<ExtensionHost> LoadAsync(string cwd, CancellationToken ct = default)
    {
        var host = new ExtensionHost();
        var sdk = DotnetSdkLocator.Find();
        if (sdk is null)
        {
            host._warnings.Add("extensions disabled (dotnet SDK not found)");
            return host;
        }

        var directories = new List<string>();
        AddExtensionsDirectory(directories, ExtensionsPaths.GlobalExtensionsDir);
        AddExtensionsDirectory(directories, ExtensionsPaths.ProjectExtensionsDir(cwd));
        var packages = PackageManager.ListPackagesFromDisk(ExtensionsPaths.PackagesRoot);
        foreach (var package in packages)
            AddExtensionsDirectory(directories, Path.Combine(package.Path, "extensions"));

        foreach (var dir in directories)
        {
            var sources = Directory.GetFiles(dir, "*.cs", SearchOption.TopDirectoryOnly);
            if (sources.Length == 0)
                continue;

            var dllPath = await CompileAsync(cwd, sources, sdk.Value.SdkRoot, sdk.Value.SdkVersion, sdk.Value.RefAssemblies, host._warnings, ct);
            if (dllPath is null)
                continue;

            host.RegisterAssembly(dllPath, cwd);
        }

        return host;
    }

    public static ExtensionHost LoadInMemory(string cwd, params IExtension[] extensions)
    {
        var host = new ExtensionHost();
        var ctx = host.CreateContext(cwd);
        foreach (var extension in extensions)
        {
            try
            {
                extension.Register(ctx);
            }
            catch (Exception ex)
            {
                host._warnings.Add($"{extension.GetType().FullName}: {ex.Message}");
            }
        }
        return host;
    }

    private static void AddExtensionsDirectory(List<string> directories, string dir)
    {
        if (Directory.Exists(dir))
            directories.Add(dir);
    }

    private IExtensionContext CreateContext(string cwd) => new HostContext(this, cwd);

    private void RegisterAssembly(string dllPath, string cwd)
    {
        try
        {
            var assembly = Assembly.LoadFrom(dllPath);
            foreach (var type in assembly.GetTypes())
            {
                if (type.IsAbstract || type.IsInterface || type.IsNotPublic || !typeof(IExtension).IsAssignableFrom(type))
                    continue;
                if (type.GetConstructor(Type.EmptyTypes) is null)
                    continue;

                try
                {
                    var instance = (IExtension)Activator.CreateInstance(type)!;
                    var ctx = CreateContext(cwd);
                    instance.Register(ctx);
                    if (instance is IDisposable disposable)
                        _disposables.Add(disposable);
                }
                catch (Exception ex)
                {
                    _warnings.Add($"{type.FullName}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            _warnings.Add($"cannot load {Path.GetFileName(dllPath)}: {ex.Message}");
        }
    }

    private static async Task<string?> CompileAsync(
        string cwd,
        IReadOnlyList<string> sources,
        string sdkRoot,
        string sdkVersion,
        IReadOnlyList<string> refAssemblies,
        List<string> warnings,
        CancellationToken ct)
    {
        var cscPath = Path.Combine(sdkRoot, sdkVersion, "Roslyn", "bincore", "csc.dll");
        if (!File.Exists(cscPath))
        {
            var legacyCsc = Path.Combine(sdkRoot, "Roslyn", "bincore", "csc.dll");
            if (!File.Exists(legacyCsc))
            {
                warnings.Add("extensions disabled (csc.dll not found)");
                return null;
            }
            cscPath = legacyCsc;
        }

        var keyInput = sources
            .Select(path => (Path.GetFileName(path), ReadAllBestEffort(path)))
            .OrderBy(pair => pair.Item1, StringComparer.Ordinal)
            .ToList();
        var contentHash = HashOf(keyInput);
        var cacheDir = ExtensionsPaths.ExtensionCacheDir;
        Directory.CreateDirectory(cacheDir);
        var dllPath = Path.Combine(cacheDir, $"{contentHash}.dll");

        if (File.Exists(dllPath))
            return dllPath;

        var tmpDll = $"{dllPath}.{Guid.NewGuid():N}.tmp.dll";
        var entryDir = AppContext.BaseDirectory;

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add(cscPath);
        psi.ArgumentList.Add("/nologo");
        psi.ArgumentList.Add("/target:library");
        psi.ArgumentList.Add("/optimize+");
        psi.ArgumentList.Add($"/out:{tmpDll}");
        foreach (var reference in refAssemblies)
            psi.ArgumentList.Add($"/reference:{reference}");
        if (entryDir is not null)
        {
            foreach (var dll in new[] { "PiNet.Extensions.dll", "PiNet.Abstractions.dll" })
            {
                var path = Path.Combine(entryDir, dll);
                if (File.Exists(path))
                    psi.ArgumentList.Add($"/reference:{path}");
            }
        }
        foreach (var source in sources)
            psi.ArgumentList.Add(source);

        try
        {
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
                return null;
            }
            var output = await outputTask;
            var error = await errorTask;

            if (process.ExitCode == 0 && File.Exists(tmpDll))
            {
                File.Move(tmpDll, dllPath, overwrite: true);
                return dllPath;
            }

            foreach (var line in SplitLines(output, error))
                warnings.Add(line);
        }
        catch (Exception ex)
        {
            warnings.Add($"extension compilation failed: {ex.Message}");
        }

        return null;
    }

    private static string HashOf(IReadOnlyList<(string Name, string Content)> keyInput)
    {
        var sb = new StringBuilder();
        foreach (var (name, content) in keyInput)
            sb.Append(name).Append('\0').Append(content).Append('\0');
        var version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);
        sb.Append(version);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(bytes)[..32].ToLowerInvariant();
    }

    private static string ReadAllBestEffort(string path)
    {
        try { return File.ReadAllText(path); }
        catch { return string.Empty; }
    }

    private static IEnumerable<string> SplitLines(string stdout, string stderr)
    {
        var combined = string.IsNullOrWhiteSpace(stderr) ? stdout : $"{stdout}\n{stderr}";
        foreach (var raw in combined.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            yield return raw.TrimEnd('\r');
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var disposable in _disposables)
        {
            try { disposable.Dispose(); } catch { /* best effort */ }
        }
        _disposables.Clear();
    }

    private sealed class HostContext(ExtensionHost host, string cwd) : IExtensionContext
    {
        public string Cwd { get; } = cwd;

        public void RegisterTool(ExtensionToolSpec tool)
        {
            if (string.IsNullOrWhiteSpace(tool.Name))
            {
                host._warnings.Add("extension tool skipped: name must not be empty");
                return;
            }
            if (host._tools.Any(t => string.Equals(t.Name, tool.Name, StringComparison.OrdinalIgnoreCase)))
            {
                host._warnings.Add($"extension tool skipped: duplicate name '{tool.Name}'");
                return;
            }
            host._tools.Add(tool);
        }

        public void RegisterCommand(string name, ExtensionCommandHandler handler)
        {
            var normalized = name?.ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                host._warnings.Add("extension command skipped: name must not be empty");
                return;
            }
            if (handler is null)
            {
                host._warnings.Add($"extension command skipped '{normalized}': handler is null");
                return;
            }
            if (host._commands.ContainsKey(normalized))
            {
                host._warnings.Add($"extension command skipped: duplicate name '{normalized}'");
                return;
            }
            host._commands[normalized] = handler;
        }
    }
}

internal readonly record struct DotnetSdk(string SdkRoot, string SdkVersion, IReadOnlyList<string> RefAssemblies);

internal static class DotnetSdkLocator
{
    public static DotnetSdk? Find()
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    ArgumentList = { "--list-sdks" },
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
                return null;

            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0)
                return null;

            var last = lines[^1].TrimEnd('\r');
            var bracketStart = last.IndexOf('[');
            var bracketEnd = last.LastIndexOf(']');
            if (bracketStart < 0 || bracketEnd <= bracketStart)
                return null;

            var sdkVersion = last[..bracketStart].Trim();
            var sdkRoot = last[(bracketStart + 1)..bracketEnd].Trim();
            if (sdkVersion.Length == 0 || !Directory.Exists(sdkRoot))
                return null;

            var refs = ResolveRefAssemblies(sdkRoot);
            return refs is null ? null : new DotnetSdk(sdkRoot, sdkVersion, refs);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static IReadOnlyList<string>? ResolveRefAssemblies(string sdkRoot)
    {
        try
        {
            var dotnetRoot = Path.GetFullPath(Path.Combine(sdkRoot, ".."));
            var refPack = Directory.Exists(Path.Combine(dotnetRoot, "packs", "Microsoft.NETCore.App.Ref"))
                ? Path.Combine(dotnetRoot, "packs", "Microsoft.NETCore.App.Ref")
                : Path.Combine(dotnetRoot, "shared", "Microsoft.NETCore.App");

            string? rootDir = null;
            var direct = Path.Combine(refPack, "ref", "net10.0");
            if (Directory.Exists(direct))
            {
                rootDir = direct;
            }
            else
            {
                var versions = Directory.Exists(refPack)
                    ? Directory.GetDirectories(refPack)
                        .Select(d => (Path: d, Name: Path.GetFileName(d)))
                        .Where(x => Version.TryParse(x.Name, out _))
                        .OrderByDescending(x => Version.Parse(x.Name))
                        .Select(x => x.Path)
                        .ToArray()
                    : [];
                foreach (var versionDir in versions)
                {
                    var candidate = Path.Combine(versionDir, "ref", "net10.0");
                    if (Directory.Exists(candidate))
                    {
                        rootDir = candidate;
                        break;
                    }
                }
            }

            if (rootDir is null)
                return null;
            var files = Directory.GetFiles(rootDir, "*.dll").OrderBy(f => f, StringComparer.Ordinal).ToArray();
            return files.Length == 0 ? null : files;
        }
        catch (Exception)
        {
            return null;
        }
    }
}