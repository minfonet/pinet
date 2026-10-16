using PiNet.Abstractions.Chat;
using PiNet.Cli.Interact;
using Xunit;

namespace PiNet.Cli.Tests;

/// <summary>
/// Spec 08 §3.3 (7a.2) — keystroke capture while a turn is in flight:
/// Enter → steer, Alt+Enter → follow-up, Ctrl+C cancels the turn, and
/// unsubmitted text carries over to the next prompt.
/// </summary>
public class SteeringCaptureTests
{
    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Timed out waiting for {what}");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task EnterDuringTurn_EnqueuesSteering_DeliveredAtNextTurnBoundary()
    {
        using var fx = new InteractiveLoopTests.Fixture();
        var provider = new GatedProvider("gate");
        fx.Registry.RegisterProvider(provider);

        var input = new ScriptedInput([.. Keys.Type("first"), Keys.Enter], interactive: true);
        var output = new CapturingOutput();
        var loop = InteractiveLoopTests.NewLoop(fx, input, output, providerId: "gate", modelId: "gate-model");

        var loopTask = loop.RunAsync();
        await provider.Started;

        input.Push([.. Keys.Type("steer this"), Keys.Enter]);
        await WaitForAsync(() => output.All.Contains("(steering: steer this)"), "the steering acknowledgment");

        provider.ReleaseFirstCall();
        var code = await loopTask;

        Assert.Equal(0, code);
        Assert.Equal(2, provider.StreamCalls);
        // Injected at the next turn boundary: absent from the in-flight request,
        // present in the request that followed it.
        Assert.DoesNotContain(provider.Requests[0].Messages, m => m.TryGetText() == "steer this");
        Assert.Contains(provider.Requests[1].Messages,
            m => m.Role == ChatRole.User && m.TryGetText() == "steer this");
    }

    [Fact]
    public async Task AltEnterDuringTurn_EnqueuesFollowUp_StartingAnotherTurn()
    {
        using var fx = new InteractiveLoopTests.Fixture();
        var provider = new GatedProvider("gate");
        fx.Registry.RegisterProvider(provider);

        var input = new ScriptedInput([.. Keys.Type("first"), Keys.Enter], interactive: true);
        var output = new CapturingOutput();
        var loop = InteractiveLoopTests.NewLoop(fx, input, output, providerId: "gate", modelId: "gate-model");

        var loopTask = loop.RunAsync();
        await provider.Started;

        input.Push([.. Keys.Type("then summarize"), Keys.Alt(ConsoleKey.Enter)]);
        await WaitForAsync(() => output.All.Contains("(follow-up: then summarize)"), "the follow-up acknowledgment");

        provider.ReleaseFirstCall();
        var code = await loopTask;

        Assert.Equal(0, code);
        Assert.Equal(2, provider.StreamCalls);
        Assert.Contains(provider.Requests[1].Messages,
            m => m.Role == ChatRole.User && m.TryGetText() == "then summarize");
    }

    [Fact]
    public async Task CtrlCDuringTurn_CancelsTheTurn_BackToPrompt()
    {
        using var fx = new InteractiveLoopTests.Fixture();
        var provider = new HangingSignalProvider("hang");
        fx.Registry.RegisterProvider(provider);

        var input = new ScriptedInput([.. Keys.Type("hang please"), Keys.Enter], interactive: true);
        var output = new CapturingOutput();
        var loop = InteractiveLoopTests.NewLoop(fx, input, output, providerId: "hang", modelId: "hang-model");

        var loopTask = loop.RunAsync();
        await provider.Started;

        input.Push(Keys.CtrlC);
        var code = await loopTask;

        Assert.Equal(0, code);
        Assert.Contains("(cancelled)", output.All);
    }

    [Fact]
    public async Task UnsubmittedTextDuringTurn_CarriesOverToTheNextPrompt()
    {
        using var fx = new InteractiveLoopTests.Fixture();
        var provider = new GatedProvider("gate");
        fx.Registry.RegisterProvider(provider);

        var input = new ScriptedInput([.. Keys.Type("first"), Keys.Enter], interactive: true);
        var output = new CapturingOutput();
        var loop = InteractiveLoopTests.NewLoop(fx, input, output, providerId: "gate", modelId: "gate-model");

        var loopTask = loop.RunAsync();
        await provider.Started;

        input.Push([.. Keys.Type("draft in pro")]); // typed but not submitted
        await WaitForAsync(() => input.Pending == 0, "the pump to consume the typed text");

        provider.ReleaseFirstCall();
        await WaitForAsync(
            () => output.EditorLinesSnapshot.Any(l => l.Buffer == "draft in pro"),
            "the carried-over text to appear at the next prompt");

        var code = await loopTask; // scripted input is exhausted: the loop exits cleanly

        Assert.Equal(0, code);
        // The partial text was never sent as a prompt or a steering message.
        Assert.Equal(1, provider.StreamCalls);
        Assert.DoesNotContain(
            provider.Requests.SelectMany(r => r.Messages),
            m => m.TryGetText() == "draft in pro");
    }

    [Fact]
    public async Task ScriptedMode_DoesNotStealKeys_BetweenTurns()
    {
        // Regression: non-interactive (scripted/redirected) input keeps the
        // sequential semantics — keys after a prompt are not captured mid-turn.
        using var fx = new InteractiveLoopTests.Fixture();
        var provider = new ScriptedProvider("faux", "one", "two");
        fx.Registry.RegisterProvider(provider);

        var input = new ScriptedInput(
        [
            ..Keys.Type("hello"), Keys.Enter,
            ..Keys.Type("world"), Keys.Enter,
            ..Keys.Type("/quit"), Keys.Enter
        ]);
        var output = new CapturingOutput();
        var loop = InteractiveLoopTests.NewLoop(fx, input, output);

        var code = await loop.RunAsync();

        Assert.Equal(0, code);
        Assert.Equal(2, provider.StreamCalls);
        Assert.DoesNotContain("(steering:", output.All);
    }
}
