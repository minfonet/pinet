using PiNet.Cli;
using Xunit;

namespace PiNet.Cli.Tests;

/// <summary>
/// Stage 1 / Stage 8 — print mode end-to-end through <see cref="Program.RunAsync"/>
/// with the deterministic faux provider: the streamed reply plus the token/cost
/// summary printed at StreamEnd.
/// </summary>
[Collection("Extensions")]
public class PrintModeCliTests
{
    [Fact]
    public void PrintMode_StreamsReply_AndPrintsTokensAndCost()
    {
        using var env = new ExtTestEnv();
        var (stdout, _, code) = RunProgram(
        [
            "--provider", "faux", "--model", "faux-model",
            "-p", "hi",
            "--no-extensions", "--no-skills", "--no-prompt-templates"
        ]);

        Assert.Equal(0, code);
        Assert.Contains("You said: hi", stdout);
        Assert.Contains("Tokens: ↑10 ↓20", stdout);
        Assert.Contains("Cost: $", stdout);
    }

    [Fact]
    public void AgentMode_WinsOverPrintMode_WhenBothFlagsAreGiven()
    {
        using var env = new ExtTestEnv();
        var (stdout, _, code) = RunProgram(
        [
            "--agent", "--approve", "--provider", "faux", "--model", "faux-model",
            "-p", "read_file docs/README.md",
            "--no-extensions", "--no-skills", "--no-prompt-templates"
        ]);

        Assert.Equal(0, code);
        Assert.Contains("> Tool: read_file", stdout);
    }

    private static (string Stdout, string Stderr, int Code) RunProgram(string[] args)
    {
        var oldOut = Console.Out;
        var oldError = Console.Error;
        var oldIn = Console.In;
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            // Deterministic EOF on the redirected-stdin branch.
            Console.SetIn(new StringReader(string.Empty));
            var code = Program.RunAsync(args).GetAwaiter().GetResult();
            return (stdout.ToString(), stderr.ToString(), code);
        }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldError);
            Console.SetIn(oldIn);
        }
    }
}
