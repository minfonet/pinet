using PiNet.Extensions;

namespace PiNet.Cli;

/// <summary>
/// Backs the `pinet install|list|remove|update` subcommands.
/// </summary>
public static class PackageCommands
{
    public static async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken ct = default)
    {
        if (args.Count == 0)
        {
            PrintUsage();
            return 1;
        }

        try
        {
            switch (args[0].ToLowerInvariant())
            {
                case "install":
                    if (args.Count < 2)
                    {
                        Console.Error.WriteLine("Usage: pinet install <path|npm:<pkg>|git:<url>>");
                        return 1;
                    }
                    var installed = await PackageManager.InstallAsync(args[1], ct);
                    Console.WriteLine($"installed {installed.Name}@{installed.Version} from {installed.Source}");
                    return 0;

                case "list":
                    var packages = await PackageManager.ListAsync(ct);
                    if (packages.Count == 0)
                    {
                        Console.WriteLine("(no packages installed)");
                        return 0;
                    }
                    foreach (var package in packages)
                        Console.WriteLine($"{package.Name}@{package.Version}  ({package.Source})");
                    return 0;

                case "remove":
                    if (args.Count < 2)
                    {
                        Console.Error.WriteLine("Usage: pinet remove <name>");
                        return 1;
                    }
                    await PackageManager.RemoveAsync(args[1], ct);
                    Console.WriteLine($"removed {args[1]}");
                    return 0;

                case "update":
                    if (args.Count > 1 && args[1] is "--self")
                        return await SelfUpdateAsync(ct);
                    await PackageManager.UpdateAsync(args.Count > 1 ? args[1] : null, ct);
                    Console.WriteLine(args.Count > 1 ? $"updated {args[1]}" : "updated all packages");
                    return 0;

                default:
                    PrintUsage();
                    return 1;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("Usage:");
        Console.Error.WriteLine("  pinet install <path|npm:<pkg>|git:<url>>");
        Console.Error.WriteLine("  pinet list");
        Console.Error.WriteLine("  pinet remove <name>");
        Console.Error.WriteLine("  pinet update [name]");
        Console.Error.WriteLine("  pinet update --self          (update the PiNet global tool)");
    }

    /// <summary>
    /// `pinet update --self` — delegates to `dotnet tool update -g PiNet`. Works
    /// when PiNet was installed as a global tool; prints guidance otherwise.
    /// </summary>
    private static async Task<int> SelfUpdateAsync(CancellationToken ct)
    {
        Console.WriteLine("updating the PiNet global tool: dotnet tool update -g PiNet");

        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo("dotnet", "tool update -g PiNet")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Self-update failed to start: {ex.Message}");
            return 1;
        }

        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        var output = (await stdout).Trim();
        if (output.Length > 0)
            Console.WriteLine(output);
        var error = (await stderr).Trim();
        if (error.Length > 0)
            Console.Error.WriteLine(error);

        if (process.ExitCode != 0)
        {
            Console.Error.WriteLine(
                "Self-update failed. If PiNet is not installed as a global tool, update it from " +
                "its source checkout (git pull && dotnet build) or re-run the install script.");
            return process.ExitCode;
        }

        Console.WriteLine("PiNet is up to date.");
        return 0;
    }
}