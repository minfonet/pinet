using PiNet.Abstractions.Chat;
using PiNet.Extensions;
using Xunit;

namespace PiNet.Cli.Tests;

/// <summary>
/// Spec 06: `/template` expansion through the loop and template discovery
/// from .pi/prompts (TemplateRegistry).
/// </summary>
[Collection("Extensions")]
public class TemplateLoopTests
{
    private static readonly PromptTemplate Review =
        new("review", "Review files", "files", "Review: $1 ($@)");

    [Fact]
    public async Task TemplateSlash_Expands_AndSendsOnEnter()
    {
        using var env = new ExtTestEnv();
        using var fx = new LoopFixture(env);
        var provider = new ScriptedProvider("faux", "ok");
        fx.Registry.RegisterProvider(provider);

        var input = new ScriptedInput(
        [
            ..Keys.Type("/review shipping checkout"), Keys.Enter,
            Keys.Enter,
            Keys.CtrlC
        ]);
        var output = new CapturingOutput();
        var loop = LoopFixture.NewLoop(fx, input, output, templates: [Review]);

        await loop.RunAsync();

        Assert.Contains("press Enter to send", output.All);
        var request = Assert.Single(provider.Requests);
        Assert.Contains(request.Messages, m => m.Role == ChatRole.User && m.TryGetText() == "Review: shipping (shipping checkout)");
    }

    [Fact]
    public async Task TemplateRegistry_LoadsProjectAndGlobal()
    {
        using var env = new ExtTestEnv();
        env.Write(".pi/prompts/triage.md", "---\nname: triage\ndescription: Triage issues\n---\nTriage $@");
        env.WriteToHome("prompts/review.md", "---\nname: review\ndescription: Review code\n---\nReview $1");

        var templates = await TemplateRegistry.LoadAsync(env.Cwd);

        Assert.Contains(templates, t => t.Name == "triage");
        Assert.Contains(templates, t => t.Name == "review");
    }

    [Fact]
    public async Task TemplateRegistry_GlobalWinsOverProject()
    {
        using var env = new ExtTestEnv();
        env.Write(".pi/prompts/triage.md", "---\nname: triage\ndescription: project\n---\nproject $@");
        env.WriteToHome("prompts/triage.md", "---\nname: triage\ndescription: global\n---\nglobal $1");

        var templates = await TemplateRegistry.LoadAsync(env.Cwd);

        var triage = Assert.Single(templates, t => t.Name == "triage");
        Assert.Equal("global $1", triage.Body);
    }

    [Fact]
    public async Task UnknownSlash_PrintsError()
    {
        using var env = new ExtTestEnv();
        using var fx = new LoopFixture(env);
        var provider = new ScriptedProvider("faux", "ok");
        fx.Registry.RegisterProvider(provider);

        var input = new ScriptedInput(
        [
            ..Keys.Type("/missing"), Keys.Enter,
            Keys.CtrlC
        ]);
        var output = new CapturingOutput();
        var loop = LoopFixture.NewLoop(fx, input, output);

        await loop.RunAsync();

        Assert.Contains(output.RedLines, l => l.Contains("Unknown command: /missing"));
    }
}