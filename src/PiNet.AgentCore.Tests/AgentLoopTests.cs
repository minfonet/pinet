using PiNet.Abstractions.Chat;

namespace PiNet.AgentCore.Tests;

public class AgentLoopTests
{
    [Fact]
    public async Task PromptAsync_AppendsUserMessage_AndEmitsStartEvent()
    {
        var runner = new ScriptedRunner().Enqueue(ScriptedRunner.Answer("hi"));
        var agent = Helpers.NewAgent(runner);
        var events = Helpers.Record(agent);

        await agent.PromptAsync("hello");

        Assert.Equal(1, runner.GenerateCalls);
        Assert.IsType<AgentStartEvent>(events[0]);
        Assert.IsType<AgentEndEvent>(events[^1]);
        var user = agent.State.Messages[0];
        Assert.Equal(ChatRole.User, user.Role);
        Assert.Equal("hello", user.ReadText());
    }

    [Fact]
    public async Task PlainTextTurn_EmitsExactEventOrder()
    {
        var runner = new ScriptedRunner().Enqueue(ScriptedRunner.Answer("hi there"));
        var agent = Helpers.NewAgent(runner);
        var events = Helpers.Record(agent);

        await agent.PromptAsync("hello");

        var expected = new List<Type>
        {
            typeof(AgentStartEvent),
            typeof(TurnStartEvent),
            typeof(MessageStartEvent)
        };
        expected.AddRange(Enumerable.Repeat(typeof(MessageUpdateEvent), "hi there".Length));
        expected.AddRange(
        [
            typeof(MessageEndEvent),
            typeof(TurnEndEvent),
            typeof(AgentEndEvent)
        ]);
        Assert.Equal(expected, events.Select(e => e.GetType()));
    }

    [Fact]
    public async Task StreamedDeltas_AccumulateIntoAssistantMessage()
    {
        var runner = new ScriptedRunner().Enqueue(ScriptedRunner.Answer("hello world"));
        var agent = Helpers.NewAgent(runner);
        Helpers.Record(agent);

        await agent.PromptAsync("hi");

        var messageEnd = agent.State.Messages.Last(m => m.Role == ChatRole.Assistant);
        Assert.Equal("hello world", messageEnd.ReadText());
    }

    [Fact]
    public async Task ToolCallDeltas_BecomeToolCallContentBlocks()
    {
        const string args = """{"path":"a.txt"}""";
        var runner = new ScriptedRunner()
            .Enqueue(ScriptedRunner.ToolTurn("call_1", "read_file", args))
            .Enqueue(ScriptedRunner.Answer("ok"));
        var agent = Helpers.NewAgent(runner, tools: [Helpers.Tool("read_file", _ => ToolResult.Ok("done"))]);
        var events = Helpers.Record(agent);

        await agent.PromptAsync("read a.txt");

        var assistant = agent.State.Messages.First(m => m.Role == ChatRole.Assistant);
        var call = Assert.Single(assistant.GetToolCalls());
        Assert.Equal("call_1", call.Id);
        Assert.Equal("read_file", call.Name);
        Assert.Equal("a.txt", call.Arguments.GetProperty("path").GetString());
        Assert.Contains(events, e => e is ToolExecutionEndEvent);
    }

    [Fact]
    public async Task ToolExecutionSequence_EmitsStartEnd_AndAppendsToolMessage_ThenContinues()
    {
        var runner = new ScriptedRunner()
            .Enqueue(ScriptedRunner.ToolTurn("call_1", "read_file", """{"path":"a.txt"}"""))
            .Enqueue(ScriptedRunner.Answer("all good"));
        var agent = Helpers.NewAgent(runner, tools: [Helpers.Tool("read_file", _ => ToolResult.Ok("the file"))]);
        var events = Helpers.Record(agent);

        await agent.PromptAsync("read it");

        Assert.Equal(2, runner.GenerateCalls);
        Assert.Contains(events, e => e is ToolExecutionStartEvent);
        Assert.Contains(events, e => e is ToolExecutionEndEvent);

        var toolMessage = agent.State.Messages.First(m => m.Role == ChatRole.Tool);
        Assert.Equal("call_1", toolMessage.ToolCallId);
        Assert.Equal("the file", toolMessage.ReadText());

        Assert.Equal(ChatRole.Assistant, agent.State.Messages.Last().Role);
    }

    [Fact]
    public async Task UnknownTool_IsReportedAsError_AndLoopRecovers()
    {
        var runner = new ScriptedRunner()
            .Enqueue(ScriptedRunner.ToolTurn("call_1", "no_such_tool", "{}"))
            .Enqueue(ScriptedRunner.Answer("recovered"));
        var agent = Helpers.NewAgent(runner, tools: []);

        await agent.PromptAsync("do it");

        var toolMessage = agent.State.Messages.First(m => m.Role == ChatRole.Tool);
        Assert.True(toolMessage.IsError);
        Assert.Contains("Unknown tool", toolMessage.ReadText());
        Assert.Equal(2, runner.GenerateCalls);
    }

    [Fact]
    public async Task Loop_ContinuesWhileToolsCalled_StopsWhenNoToolCalls()
    {
        var runner = new ScriptedRunner()
            .Enqueue(ScriptedRunner.ToolTurn("call_1", "read_file", """{"path":"a.txt"}"""))
            .Enqueue(ScriptedRunner.Answer("final answer"));
        var agent = Helpers.NewAgent(runner, tools: [Helpers.Tool("read_file", _ => ToolResult.Ok("x"))]);
        var events = Helpers.Record(agent);

        await agent.PromptAsync("go");

        Assert.Equal(2, runner.GenerateCalls);
        Assert.IsType<AgentEndEvent>(events[^1]);
        Assert.Equal(4, agent.State.Messages.Count); // user, assistant (tool call), tool, assistant (text)
    }

    [Fact]
    public async Task ThrowingTool_IsCapturedAsErrorResult_AndLoopContinues()
    {
        var runner = new ScriptedRunner()
            .Enqueue(ScriptedRunner.ToolTurn("call_1", "boom", "{}"))
            .Enqueue(ScriptedRunner.Answer("saw the error"));
        var agent = Helpers.NewAgent(runner, tools:
        [
            Helpers.Tool("boom", _ => throw new InvalidOperationException("kaboom"))
        ]);
        var events = Helpers.Record(agent);

        await agent.PromptAsync("go");

        var end = events.OfType<ToolExecutionEndEvent>().Single();
        Assert.True(end.Result.IsError);
        Assert.Contains("kaboom", end.Result.Content);
        Assert.Equal(2, runner.GenerateCalls);
    }

    [Fact]
    public async Task Abort_CancelsProvider_SetsError_EmitsAgentEndOnce()
    {
        var runner = new ScriptedRunner { HangOnGenerate = true };
        var agent = Helpers.NewAgent(runner);
        var events = Helpers.Record(agent);

        var started = new TaskCompletionSource();
        agent.Subscribe(e => { if (e is AgentStartEvent) started.TrySetResult(); });

        var task = agent.PromptAsync("never ends");
        await started.Task;
        agent.Abort();
        await task;

        Assert.Equal("Aborted by user.", agent.State.ErrorMessage);
        Assert.False(agent.State.IsStreaming);
        Assert.Single(events, e => e is AgentEndEvent);
    }

    [Fact]
    public async Task ContinueAsync_ResumesFromCurrentContext_WithoutNewUserMessage()
    {
        var runner = new ScriptedRunner()
            .Enqueue(ScriptedRunner.Answer("first"))
            .Enqueue(ScriptedRunner.Answer("second"));
        var agent = Helpers.NewAgent(runner);

        await agent.PromptAsync("hello");
        Assert.Equal(1, runner.GenerateCalls);

        await agent.ContinueAsync();
        Assert.Equal(2, runner.GenerateCalls);

        var roles = agent.State.Messages.Select(m => m.Role).ToArray();
        Assert.Equal([ChatRole.User, ChatRole.Assistant, ChatRole.Assistant], roles);
        Assert.Equal("second", agent.State.Messages.Last().ReadText());
    }

    [Fact]
    public async Task Subscribers_AreAwaitedInRegistrationOrder()
    {
        var runner = new ScriptedRunner().Enqueue(ScriptedRunner.Answer("ab"));
        var agent = Helpers.NewAgent(runner);
        var log = new List<string>();
        var counter = 0;

        agent.Subscribe(async (e, ct) =>
        {
            var n = Interlocked.Increment(ref counter);
            log.Add($"{n}:1");
            await Task.Delay(20);
        });
        agent.Subscribe((e, ct) =>
        {
            var n = Interlocked.Increment(ref counter);
            log.Add($"{n}:2");
            return Task.CompletedTask;
        });

        await agent.PromptAsync("hi");

        Assert.Equal(16, log.Count);
        for (var i = 0; i < log.Count; i += 2)
        {
            // Same event: handler 1 completed before handler 2 of the same event
            Assert.True(log[i].EndsWith(":1"), $"Expected handler 1 first, got {log[i]}");
            Assert.True(log[i + 1].EndsWith(":2"), $"Expected handler 2 second, got {log[i + 1]}");
        }
    }
}