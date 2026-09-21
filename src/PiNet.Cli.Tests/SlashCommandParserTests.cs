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

    [Theory]
    [InlineData("/skill:ensure-pr", "ensure-pr", null)]
    [InlineData("/skill:ensure-pr two things", "ensure-pr", "two things")]
    public void Parse_SkillPrefix(string line, string expectedName, string? expectedArg)
    {
        var command = SlashCommandParser.Parse(line);
        Assert.Equal(SlashCommandKind.Skill, command.Kind);
        Assert.Equal(expectedName, command.Name);
        Assert.Equal(expectedArg, command.Arg);
    }

    [Fact]
    public void Parse_SkillPrefix_TrimsArgument()
    {
        var command = SlashCommandParser.Parse("/skill:ensure-pr   spaced  ");
        Assert.Equal(SlashCommandKind.Skill, command.Kind);
        Assert.Equal("ensure-pr", command.Name);
        Assert.Equal("spaced", command.Arg);
    }

    [Fact]
    public void Parse_SkillName_IsLowerCased()
    {
        Assert.Equal("ensure-pr", SlashCommandParser.Parse("/skill:Ensure-PR").Name);
    }

    [Fact]
    public void Parse_TrimmedArgument()
    {
        Assert.Equal("spaced", SlashCommandParser.Parse("/resume   spaced  ").Arg);
    }
}
