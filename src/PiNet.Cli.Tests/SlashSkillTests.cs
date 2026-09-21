using PiNet.Abstractions.Chat;
using PiNet.Cli.Interact;
using PiNet.Extensions;
using Xunit;

namespace PiNet.Cli.Tests;

/// <summary>
/// Spec 06: `/skill:<name>` dispatch through the interactive loop, project
/// skills discovered under .pi/skills, and the system prompt skills section.
/// </summary>
[Collection("Extensions")]
public class SlashSkillTests
{
    private static string SkillMd(string name, string description, string body) =>
        $"---\nname: {name}\ndescription: {description}\n---\n{body}";

    [Fact]
    public async Task SkillSlash_DispatchesBodyAsTurn()
    {
        using var env = new ExtTestEnv();
        env.Write(".pi/skills/ensure-pr/SKILL.md", SkillMd("ensure-pr", "Ensure a PR", "Step one.\nStep two."));
        using var fx = new LoopFixture(env);
        var provider = new ScriptedProvider("faux", "ok");
        fx.Registry.RegisterProvider(provider);

        var skills = await SkillRegistry.LoadAsync(fx.Cwd);
        var input = new ScriptedInput(
        [
            ..Keys.Type("/skill:ensure-pr two things"), Keys.Enter,
            Keys.CtrlC
        ]);
        var output = new CapturingOutput();
        var loop = LoopFixture.NewLoop(fx, input, output, skills: skills);

        await loop.RunAsync();

        var request = Assert.Single(provider.Requests);
        Assert.Contains(request.Messages, m => m.Role == ChatRole.User && m.TryGetText()!.Contains("[Skill: ensure-pr"));
        Assert.Contains(request.Messages, m => m.Role == ChatRole.User && m.TryGetText()!.Contains("Step one."));
        Assert.Contains(request.Messages, m => m.Role == ChatRole.User && m.TryGetText()!.Contains("User: two things"));
    }

    [Fact]
    public async Task SkillSlash_UnknownSkill_PrintsError_NoTurn()
    {
        using var env = new ExtTestEnv();
        using var fx = new LoopFixture(env);
        var provider = new ScriptedProvider("faux", "ok");
        fx.Registry.RegisterProvider(provider);

        var skills = await SkillRegistry.LoadAsync(fx.Cwd);
        var input = new ScriptedInput(
        [
            ..Keys.Type("/skill:nope"), Keys.Enter,
            Keys.CtrlC
        ]);
        var output = new CapturingOutput();
        var loop = LoopFixture.NewLoop(fx, input, output, skills: skills);

        await loop.RunAsync();

        Assert.Contains(output.RedLines, l => l.Contains("Unknown skill: nope"));
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task SkillDisabled_WhenNullSkills()
    {
        using var env = new ExtTestEnv();
        using var fx = new LoopFixture(env);
        var provider = new ScriptedProvider("faux", "ok");
        fx.Registry.RegisterProvider(provider);

        var input = new ScriptedInput(
        [
            ..Keys.Type("/skill:ensure-pr"), Keys.Enter,
            Keys.CtrlC
        ]);
        var output = new CapturingOutput();
        var loop = LoopFixture.NewLoop(fx, input, output);

        await loop.RunAsync();

        Assert.Contains(output.RedLines, l => l.Contains("Skills are disabled."));
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task SystemPrompt_IncludesDiscoverableSkill()
    {
        using var env = new ExtTestEnv();
        env.Write(".pi/skills/ensure-pr/SKILL.md", SkillMd("ensure-pr", "Ensure a PR", "Do it."));
        using var fx = new LoopFixture(env);
        var provider = new ScriptedProvider("faux", "ok");
        fx.Registry.RegisterProvider(provider);

        var skills = await SkillRegistry.LoadAsync(fx.Cwd);
        var context = ContextFiles.Load(fx.Cwd);
        var systemPrompt = SystemPromptBuilder.Build("You are a helper.", context, skills.Skills);
        var input = new ScriptedInput(
        [
            ..Keys.Type("hi"), Keys.Enter,
            Keys.CtrlC
        ]);
        var loop = LoopFixture.NewLoop(fx, input, new CapturingOutput(), skills: skills, systemPrompt: systemPrompt);

        await loop.RunAsync();

        var request = Assert.Single(provider.Requests);
        Assert.Contains("ensure-pr", request.SystemPrompt);
        Assert.Contains("# Skills", request.SystemPrompt);
        Assert.Contains("<available_skills>", request.SystemPrompt);
    }
}