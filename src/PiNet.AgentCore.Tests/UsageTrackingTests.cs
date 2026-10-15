using PiNet.Abstractions.Chat;

namespace PiNet.AgentCore.Tests;

/// <summary>
/// Unit tests for usage bookkeeping on <see cref="Agent"/> (AC7b.2: "usage is
/// tracked from each StreamEnd's Usage and feeds the compactor decision").
/// </summary>
public sealed class UsageTrackingTests
{
    [Fact]
    public async Task LastUsage_MatchesLastStreamEnd()
    {
        var runner = new ScriptedRunner().Enqueue(r => ScriptedRunner.Answer("First answer"));
        var agent = Helpers.NewAgent(runner);

        await agent.PromptAsync("hi");

        Assert.NotNull(agent.State.LastUsage);
        Assert.Equal(10, agent.State.LastUsage!.InputTokens);   // ScriptedRunner.SampleUsage
        Assert.Equal(20, agent.State.LastUsage.OutputTokens);
    }

    [Fact]
    public async Task TotalUsage_AccumulatesAcrossTurns()
    {
        var runner = new ScriptedRunner()
            .Enqueue(r => ScriptedRunner.Answer("First answer"))
            .Enqueue(r => ScriptedRunner.Answer("Second answer"));
        var agent = Helpers.NewAgent(runner);

        await agent.PromptAsync("first");
        await agent.PromptAsync("second");

        // SampleUsage is (input=10, output=20); two turns accumulate.
        Assert.Equal(20, agent.State.TotalUsage.InputTokens);
        Assert.Equal(40, agent.State.TotalUsage.OutputTokens);
    }

    [Fact]
    public async Task Usage_Feedback_IntoCompactorViaLastPromptTokens()
    {
        var runner = new ScriptedRunner().Enqueue(r => ScriptedRunner.Answer("A"));
        var agent = Helpers.NewAgent(runner);

        await agent.PromptAsync("hi");
        Assert.NotNull(agent.State.LastUsage);
        // The compactor reads this via the Agent state when deciding to compact.
        Assert.Equal(10, agent.State.LastUsage!.InputTokens);
    }
}
