using PiNet.Cli.Interact;
using Xunit;

namespace PiNet.Cli.Tests;

/// <summary>SR-04.04 — slash-command parsing.</summary>
public class SlashCommandParserTests
{
    [Theory]
    [InlineData("/quit", SlashCommandKind.Quit, null)]
    [InlineData("/exit", SlashCommandKind.Quit, null)]
    [InlineData("/help", SlashCommandKind.Help, null)]
    [InlineData("/help quit", SlashCommandKind.Help, "quit")]
    [InlineData("/new", SlashCommandKind.NewSession, null)]
    [InlineData("/resume", SlashCommandKind.Resume, null)]
    [InlineData("/resume ywbri5s32t5gd", SlashCommandKind.Resume, "ywbri5s32t5gd")]
    [InlineData("/fork abc123", SlashCommandKind.Fork, "abc123")]
    [InlineData("/model faux/faux-model", SlashCommandKind.Model, "faux/faux-model")]
    public void Parse_MapsKnownCommands(string line, SlashCommandKind expectedKind, string? expectedArg)
    {
        var command = SlashCommandParser.Parse(line);
        Assert.Equal(expectedKind, command.Kind);
        Assert.Equal(expectedArg, command.Arg);
        Assert.Equal(line, command.Raw);
    }

    [Theory]
    [InlineData("/wat", "wat")]
    [InlineData("/wat extra", "wat")]
    public void Parse_UnknownCommand(string line, string expectedName)
    {
        var command = SlashCommandParser.Parse(line);
        Assert.Equal(SlashCommandKind.Unknown, command.Kind);
        Assert.Equal(expectedName, command.Name);
    }

    [Fact]
    public void Parse_AllowsLeadingWhitespace()
    {
        var command = SlashCommandParser.Parse("   /quit   ");
        Assert.Equal(SlashCommandKind.Quit, command.Kind);
    }

    [Fact]
    public void Parse_IsCaseInsensitive()
    {
        Assert.Equal(SlashCommandKind.Quit, SlashCommandParser.Parse("/QUIT").Kind);
        Assert.Equal(SlashCommandKind.Resume, SlashCommandParser.Parse("/Resume x").Kind);
    }

    [Fact]
    public void Parse_TrimsArgument()
    {
        Assert.Equal("  spaced  ".Trim(), SlashCommandParser.Parse("/resume   spaced  ").Arg);
    }
}
