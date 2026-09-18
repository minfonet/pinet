using PiNet.Abstractions.Chat;

namespace PiNet.AgentCore.Tests;

public class ToolExecutionTests
{
    [Fact]
    public async Task ParallelMode_RunsToolsConcurrently_ResultsAppendInAssistantOrder()
    {
        var started = 0;
        var gate = new TaskCompletionSource();
        var tool = new AgentTool
        {
            Name = "concurrent_tool",
            Description = "waits until both invocations have started",
            Execute = async (id, args, ct, progress) =>
            {
                if (Interlocked.Increment(ref started) == 2)
                    gate.TrySetResult();
                await gate.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
                return ToolResult.Ok(id);
            }
        };

        var runner = new ScriptedRunner()
            .Enqueue(ScriptedRunner.MultiToolTurn(
                ("call_1", "concurrent_tool", "{}"),
                ("call_2", "concurrent_tool", "{}")))
            .Enqueue(ScriptedRunner.Answer("done"));
        var agent = Helpers.NewAgent(runner, tools: [tool]);
        var events = Helpers.Record(agent);

        await agent.PromptAsync("go");

        Assert.Equal(2, started);
        Assert.Equal(2, runner.GenerateCalls);

        var results = events.OfType<ToolExecutionEndEvent>().Select(e => e.Result.Content).ToArray();
        Assert.Equal(["call_1", "call_2"], results);

        var context = agent.State.Messages.Where(m => m.Role == ChatRole.Tool)
            .Select(m => m.ReadText()).ToArray();
        Assert.Equal(["call_1", "call_2"], context);
    }

    [Fact]
    public async Task SequentialMode_ExecutesStrictlyInAssistantOrder()
    {
        var order = new List<string>();
        var runner = new ScriptedRunner()
            .Enqueue(ScriptedRunner.MultiToolTurn(
                ("call_1", "first", "{}"),
                ("call_2", "second", "{}")))
            .Enqueue(ScriptedRunner.Answer("done"));
        var agent = Helpers.NewAgent(
            runner,
            tools:
            [
                Helpers.Tool("first", _ => { order.Add("first"); return ToolResult.Ok("a"); }),
                Helpers.Tool("second", _ => { order.Add("second"); return ToolResult.Ok("b"); })
            ],
            options: new AgentOptions { ParallelToolExecution = false });

        await agent.PromptAsync("go");

        Assert.Equal(["first", "second"], order);
        Assert.Equal(2, runner.GenerateCalls);
    }

    [Fact]
    public async Task ToolThrowingN_IsCapturedIntoErrorResult_LoopSurvives()
    {
        var runner = new ScriptedRunner()
            .Enqueue(ScriptedRunner.ToolTurn("call_1", "explode", "{}"))
            .Enqueue(ScriptedRunner.Answer("still running"));
        var agent = Helpers.NewAgent(
            runner,
            tools: [Helpers.Tool("explode", _ => throw new InvalidOperationException("boom"))]);

        await agent.PromptAsync("go");

        Assert.Equal(2, runner.GenerateCalls);
        var toolMessage = agent.State.Messages.First(m => m.Role == ChatRole.Tool);
        Assert.True(toolMessage.IsError);
        Assert.Contains("boom", toolMessage.ReadText());
    }
}