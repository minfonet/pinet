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
    }
}