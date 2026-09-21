using PiNet.Cli.Extensions;
using Xunit;

namespace PiNet.Cli.Tests;

/// <summary>
/// Spec 06: CliEnvironment.LoadAsync composition (extensions, skills,
/// templates, system prompt) and flag disabling.
/// </summary>
[Collection("Extensions")]
public class CliEnvironmentTests
{
    [Fact]
    public async Task LoadAsync_NoFlags_BuildsEverything()
    {
        using var env = new ExtTestEnv();
        env.Write(".pi/skills/alpha/SKILL.md", "---\nname: alpha\ndescription: Alpha skill\n---\nBody.");
        env.Write("AGENTS.md", "Follow the rules.");
        env.WriteToHome("extensions/noop.cs", "// nothing compilable here, ignored");

        var setup = await CliEnvironment.LoadAsync(env.Cwd);

        Assert.NotNull(setup.Extensions);
        Assert.NotNull(setup.Skills);
        Assert.Contains(setup.Skills.Skills, s => s.Name == "alpha");
        Assert.Contains("# Skills", setup.SystemPrompt);
        Assert.Contains("Follow the rules.", setup.SystemPrompt);
    }

    [Fact]
    public async Task LoadAsync_SYSTEMMd_ReplacesBasePrompt()
    {
        using var env = new ExtTestEnv();
        env.Write("SYSTEM.md", "You are the test system.");

        var setup = await CliEnvironment.LoadAsync(env.Cwd);

        Assert.Equal("You are the test system.", setup.SystemPrompt);
    }

    [Fact]
    public async Task LoadAsync_NoExtensions_DisablesButSkillsWork()
    {
        using var env = new ExtTestEnv();
        env.Write(".pi/skills/alpha/SKILL.md", "---\nname: alpha\ndescription: A\n---\nB.");

        var setup = await CliEnvironment.LoadAsync(env.Cwd, noExtensions: true);

        Assert.Null(setup.Extensions);
        Assert.Contains(setup.Skills!.Skills, s => s.Name == "alpha");
        Assert.Contains("# Skills", setup.SystemPrompt);
    }

    [Fact]
    public async Task LoadAsync_NoSkills_OmitsSkillSection()
    {
        using var env = new ExtTestEnv();
        env.Write(".pi/skills/alpha/SKILL.md", "---\nname: alpha\ndescription: A\n---\nB.");

        var setup = await CliEnvironment.LoadAsync(env.Cwd, noSkills: true);

        Assert.Null(setup.Skills);
        Assert.DoesNotContain("# Skills", setup.SystemPrompt);
    }

    [Fact]
    public async Task LoadAsync_TemplatesFlag_ControlsDiscovery()
    {
        using var env = new ExtTestEnv();
        env.Write(".pi/prompts/triage.md", "---\ndescription: Triage\n---\nTriage $@");

        var setup = await CliEnvironment.LoadAsync(env.Cwd, noTemplates: true);

        Assert.Empty(setup.Templates);

        var with = await CliEnvironment.LoadAsync(env.Cwd);
        Assert.Contains(with.Templates, t => t.Name == "triage");
    }

    [Fact]
    public async Task Warnings_AggregateAcrossComponents()
    {
        using var env = new ExtTestEnv();
        env.Write(".pi/skills/dup.md", "---\nname: dup\ndescription: D1\n---\nx");
        env.Write(".pi/skills/dup2.md", "---\nname: dup\ndescription: D2\n---\nx");
        env.WriteToHome("extensions/broken.cs", "this is not valid csharp");

        var setup = await CliEnvironment.LoadAsync(env.Cwd);

        Assert.NotEmpty(CliEnvironment.Warnings(setup));
        Assert.Contains(CliEnvironment.Warnings(setup), w => w.Contains("duplicate name"));
    }
}