using PiNet.Cli;
using Xunit;

namespace PiNet.Cli.Tests;

/// <summary>
/// Spec 06: `pinet install|list|remove|update` over PackageManager, isolated
/// to a temp PI_NET_HOME.
/// </summary>
[Collection("Extensions")]
public class PackageManagerCliTests
{
    private static (string Stdout, string Stderr, int Code) Run(string[] args)
    {
        var oldOut = Console.Out;
        var oldError = Console.Error;
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var code = PackageCommands.RunAsync(args).GetAwaiter().GetResult();
            return (stdout.ToString(), stderr.ToString(), code);
        }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldError);
        }
    }

    [Fact]
    public void Install_FromPathPrintsConfirmation()
    {
        using var env = new ExtTestEnv();
        var bundle = Path.Combine(Path.GetTempPath(), "pinet-cli-bundle-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(bundle);
        File.WriteAllText(Path.Combine(bundle, "pinet.json"), """{ "name": "cli-bundle", "version": "1.0.0" }""");
        try
        {
            var (stdout, _, code) = Run(["install", bundle]);

            Assert.Equal(0, code);
            Assert.Contains("installed cli-bundle", stdout);

            var (list, _, listCode) = Run(["list"]);
            Assert.Equal(0, listCode);
            Assert.Contains("cli-bundle@1.0.0", list);
        }
        finally
        {
            Directory.Delete(bundle, recursive: true);
        }
    }

    [Fact]
    public void List_EmptyPrintsPlaceholder()
    {
        using var env = new ExtTestEnv();
        var (stdout, _, code) = Run(["list"]);

        Assert.Equal(0, code);
        Assert.Contains("(no packages installed)", stdout);
    }

    /// <summary>
    /// Regression: the top-level CLI dispatch must hand the full args (including
    /// the subcommand name) to <see cref="PackageCommands"/>; passing them already
    /// sliced made every `pinet install|list|remove|update` print usage.
    /// </summary>
    [Fact]
    public void Program_DispatchesPackageSubcommands_WithFullArgs()
    {
        using var env = new ExtTestEnv();
        var (stdout, _, code) = RunProgram(["list"]);

        Assert.Equal(0, code);
        Assert.Contains("(no packages installed)", stdout);
    }

    private static (string Stdout, string Stderr, int Code) RunProgram(string[] args)
    {
        var oldOut = Console.Out;
        var oldError = Console.Error;
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var code = Program.RunAsync(args).GetAwaiter().GetResult();
            return (stdout.ToString(), stderr.ToString(), code);
        }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldError);
        }
    }

    [Fact]
    public void Remove_UnknownReturnsError()
    {
        using var env = new ExtTestEnv();
        var (_, stderr, code) = Run(["remove", "nope"]);

        Assert.Equal(1, code);
        Assert.Contains("Error:", stderr);
    }

    [Fact]
    public void NoSubcommand_PrintsUsage()
    {
        using var env = new ExtTestEnv();
        var (_, stderr, code) = Run([]);

        Assert.Equal(1, code);
        Assert.Contains("pinet install", stderr);
    }

    [Fact]
    public void Update_RecordedSourceAppliesNewContent()
    {
        using var env = new ExtTestEnv();
        var bundle = Path.Combine(Path.GetTempPath(), "pinet-cli-bundle-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(bundle);
        File.WriteAllText(Path.Combine(bundle, "pinet.json"), """{ "name": "up-bundle", "version": "1.0.0" }""");
        File.WriteAllText(Path.Combine(bundle, "data.txt"), "v1");
        try
        {
            Assert.Equal(0, Run(["install", bundle]).Code);
            File.WriteAllText(Path.Combine(bundle, "data.txt"), "v2");

            var (stdout, _, code) = Run(["update", "up-bundle"]);

            Assert.Equal(0, code);
            Assert.Contains("updated up-bundle", stdout);
            Assert.Equal("v2", File.ReadAllText(Path.Combine(env.Home, "packages", "up-bundle", "data.txt")));
        }
        finally
        {
            Directory.Delete(bundle, recursive: true);
        }
    }
}