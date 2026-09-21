using PiNet.Abstractions.Chat;

namespace PiNet.AgentCore.Tests;

public class SteeringTests
{
    [Fact]
    public async Task Steering_IsDeliveredAtNextTurnBoundary()
    {
        var runner = new ScriptedRunner();
        var agent = Helpers.NewAgent(runner, tools: [Helpers.Tool("t", _ => ToolResult.Ok("x"))]);
        runner.Enqueue(req =>
        {
            agent.Steer("steer-1");
            return ScriptedRunner.ToolTurn("call_1", "t", "{}");
        });
        runner.Enqueue(ScriptedRunner.Answer("done"));

        await agent.PromptAsync("go");

        Assert.Equal(2, runner.GenerateCalls);
        Assert.DoesNotContain(runner.Requests[0].Messages, m => m.ReadText() == "steer-1");
        Assert.Contains(runner.Requests[1].Messages, m => m.Role == ChatRole.User && m.ReadText() == "steer-1");
        Assert.Equal(0, agent.PendingSteering);
    }

    [Fact]
    public async Task Steering_All_InjectsEveryMessageAtSameBoundary()
    {
        var runner = new ScriptedRunner();
        var agent = Helpers.NewAgent(runner, tools: [Helpers.Tool("t", _ => ToolResult.Ok("x"))]);
        agent.State.SteeringMode = QueueMode.All;
        runner.Enqueue(req =>
        {
            agent.Steer("s1");
            agent.Steer("s2");
            return ScriptedRunner.ToolTurn("call_1", "t", "{}");
        });
        runner.Enqueue(ScriptedRunner.Answer("done"));

        await agent.PromptAsync("go");

        var users = runner.Requests[1].Messages
            .Where(m => m.Role == ChatRole.User)
            .Select(m => m.ReadText())
            .ToList();
        Assert.Contains("s1", users);
        Assert.Contains("s2", users);
    }

    [Fact]
    public async Task Steering_OneAtATime_InjectsOnePerBoundary()
    {
        var runner = new ScriptedRunner();
        var agent = Helpers.NewAgent(runner, tools: [Helpers.Tool("t", _ => ToolResult.Ok("x"))]);
        runner.Enqueue(req =>
        {
            agent.Steer("s1");
            agent.Steer("s2");
            return ScriptedRunner.ToolTurn("call_1", "t", "{}");
        });
        runner.Enqueue(ScriptedRunner.ToolTurn("call_2", "t", "{}"));
        runner.Enqueue(ScriptedRunner.Answer("done"));

        await agent.PromptAsync("go");

        Assert.Equal(3, runner.GenerateCalls);
        var second = runner.Requests[1].Messages.Where(m => m.Role == ChatRole.User).Select(m => m.ReadText()).ToList();
        var third = runner.Requests[2].Messages.Where(m => m.Role == ChatRole.User).Select(m => m.ReadText()).ToList();
        Assert.Contains("s1", second);
        Assert.DoesNotContain("s2", second);
        Assert.Contains("s2", third);
    }

    [Fact]
    public async Task Steering_QueuedDuringFinalTurn_ContinuesRun()
    {
        var runner = new ScriptedRunner();
        var agent = Helpers.NewAgent(runner);
        runner.Enqueue(req =>
        {
            agent.Steer("steer-1");
            return ScriptedRunner.Answer("first");
        });
        runner.Enqueue(ScriptedRunner.Answer("second"));

        await agent.PromptAsync("go");

        Assert.Equal(2, runner.GenerateCalls);
        Assert.Contains(runner.Requests[1].Messages, m => m.Role == ChatRole.User && m.ReadText() == "steer-1");
    }

    [Fact]
    public async Task FollowUp_StartsAnotherTurn_AfterRunWouldEnd()
    {
        var runner = new ScriptedRunner();
        var agent = Helpers.NewAgent(runner);
        runner.Enqueue(req =>
        {
            agent.FollowUp("follow-1");
            return ScriptedRunner.Answer("first");
        });
        runner.Enqueue(ScriptedRunner.Answer("second"));

        await agent.PromptAsync("go");

        Assert.Equal(2, runner.GenerateCalls);
        Assert.Contains(runner.Requests[1].Messages, m => m.Role == ChatRole.User && m.ReadText() == "follow-1");
        Assert.Equal("second", agent.State.Messages.Last().ReadText());
        Assert.Equal(0, agent.PendingFollowUp);
    }

    [Fact]
    public async Task InjectedMessage_EmitsStartAndEndEvents()
    {
        var runner = new ScriptedRunner();
        var agent = Helpers.NewAgent(runner, tools: [Helpers.Tool("t", _ => ToolResult.Ok("x"))]);
        var events = Helpers.Record(agent);
        runner.Enqueue(req =>
        {
            agent.Steer("steer-1");
            return ScriptedRunner.ToolTurn("call_1", "t", "{}");
        });
        runner.Enqueue(ScriptedRunner.Answer("done"));

        await agent.PromptAsync("go");

        Assert.Contains(events, e => e is MessageStartEvent m && m.Message.Role == ChatRole.User && m.Message.ReadText() == "steer-1");
        Assert.Contains(events, e => e is MessageEndEvent m && m.Message.Role == ChatRole.User && m.Message.ReadText() == "steer-1");
    }

    [Fact]
    public void PendingCounters_ReflectQueues()
    {
        var runner = new ScriptedRunner();
        var agent = Helpers.NewAgent(runner);

        agent.Steer("a");
        agent.Steer("b");
        agent.FollowUp("c");

        Assert.Equal(2, agent.PendingSteering);
        Assert.Equal(1, agent.PendingFollowUp);
    }

    [Fact]
    public void Steer_IsThreadSafe()
    {
        var runner = new ScriptedRunner();
        var agent = Helpers.NewAgent(runner);

        Parallel.For(0, 200, i => agent.Steer($"s{i}"));

        Assert.Equal(200, agent.PendingSteering);
    }
}
