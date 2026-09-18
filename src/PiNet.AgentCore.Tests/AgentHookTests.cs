using PiNet.Abstractions.Chat;

namespace PiNet.AgentCore.Tests;

public class AgentHookTests
{
    [Fact]
    public async Task BeforeToolCall_BlocksTool_ReasonReachesContext()
    {
        var executed = false;
        var runner = new ScriptedRunner()
            .Enqueue(ScriptedRunner.ToolTurn("call_1", "bash", """{"command":"ls"}"""))
            .Enqueue(ScriptedRunner.Answer("acknowledged"));
        var agent = Helpers.NewAgent(
            runner,
            tools: [Helpers.Tool("bash", _ => { executed = true; return ToolResult.Ok("ran"); })],
            options: new AgentOptions
            {
                BeforeToolCall = (tc, args, ctx, ct) => Task.FromResult(ToolCallDecision.Block("bash is disabled"))
            });
        var events = Helpers.Record(agent);

        await agent.PromptAsync("run ls");

        Assert.False(executed);
        var end = events.OfType<ToolExecutionEndEvent>().Single();
        Assert.True(end.Result.IsError);
        Assert.Contains("bash is disabled", end.Result.Content);

        var toolMessage = agent.State.Messages.First(m => m.Role == ChatRole.Tool);
        Assert.True(toolMessage.IsError);
        Assert.Contains("bash is disabled", toolMessage.ReadText());
        Assert.Equal(2, runner.GenerateCalls);
    }

    [Fact]
    public async Task AfterToolCall_ReplacesResult_InEventAndContext()
    {
        var runner = new ScriptedRunner()
            .Enqueue(ScriptedRunner.ToolTurn("call_1", "read_file", """{"path":"a.txt"}"""))
            .Enqueue(ScriptedRunner.Answer("replaced"));
        var agent = Helpers.NewAgent(
            runner,
            tools: [Helpers.Tool("read_file", _ => ToolResult.Ok("original"))],
            options: new AgentOptions
            {
                AfterToolCall = (tc, result, ctx, ct) => Task.FromResult<ToolResult?>(ToolResult.Ok("REPLACED"))
            });
        var events = Helpers.Record(agent);

        await agent.PromptAsync("read");

        var end = events.OfType<ToolExecutionEndEvent>().Single();
        Assert.Equal("REPLACED", end.Result.Content);
        Assert.Equal("REPLACED", agent.State.Messages.First(m => m.Role == ChatRole.Tool).ReadText());
    }

    [Fact]
    public async Task TransformContext_RunsBeforeEachLlmCall_AndShapesRequestMessages()
    {
        var runner = new ScriptedRunner().Enqueue(r => ScriptedRunner.Answer("hello!"));
        var injection = new ChatMessage(ChatRole.System, [new TextContent("injected")], Timestamp: 0);
        var agent = Helpers.NewAgent(
            runner,
            options: new AgentOptions
            {
                TransformContext = (msgs, ct) =>
                    ValueTask.FromResult<IReadOnlyList<ChatMessage>>(msgs.Append(injection).ToArray())
            });

        await agent.PromptAsync("hi");

        var request = Assert.Single(runner.Requests);
        Assert.Contains(request.Messages, m => m.Role == ChatRole.System && m.ReadText() == "injected");
    }

    [Fact]
    public async Task ConvertToLlm_FiltersMessagesBeforeWireCall()
    {
        var runner = new ScriptedRunner().Enqueue(r => ScriptedRunner.Answer("ok"));
        var agent = Helpers.NewAgent(
            runner,
            state: new AgentState
            {
                ProviderId = "scripted",
                ModelId = "m",
                Tools = [],
                Messages =
                [
                    ChatMessage.User("hello"),
                    new ChatMessage(ChatRole.Tool, [new TextContent("x")], ToolCallId: "call_1", Timestamp: 0)
                ]
            },
            options: new AgentOptions
            {
                ConvertToLlm = msgs => msgs.Where(m => m.Role == ChatRole.User).ToArray()
            });

        await agent.PromptAsync("extra");

        var request = Assert.Single(runner.Requests);
        Assert.All(request.Messages, m => Assert.Equal(ChatRole.User, m.Role));
    }

    [Fact]
    public async Task TerminateHint_OnOnlyResult_StopsLoopWithoutFollowUpTurn()
    {
        var runner = new ScriptedRunner()
            .Enqueue(ScriptedRunner.ToolTurn("call_1", "finish_tool", "{}"));
        var agent = Helpers.NewAgent(runner, tools: [Helpers.Tool("finish_tool", _ => ToolResult.Done("finished"))]);
        var events = Helpers.Record(agent);

        await agent.PromptAsync("end it");

        Assert.Equal(1, runner.GenerateCalls);
        var end = events.OfType<ToolExecutionEndEvent>().Single();
        Assert.True(end.Result.Terminate);
        Assert.IsType<AgentEndEvent>(events[^1]);
    }

    [Fact]
    public async Task TerminateHint_PartialInBatch_KeepsLooping()
    {
        var runner = new ScriptedRunner()
            .Enqueue(ScriptedRunner.MultiToolTurn(("call_1", "stop_tool", "{}"), ("call_2", "plain_tool", "{}")))
            .Enqueue(ScriptedRunner.Answer("continued"));
        var agent = Helpers.NewAgent(runner,
            tools:
            [
                Helpers.Tool("stop_tool", _ => ToolResult.Done("stop")),
                Helpers.Tool("plain_tool", _ => ToolResult.Ok("plain"))
            ]);

        await agent.PromptAsync("go");

        Assert.Equal(2, runner.GenerateCalls);
        Assert.Contains(agent.State.Messages, m => m.Role == ChatRole.Assistant && m.ReadText() == "continued");
    }
}