using PiNet.Abstractions.Chat;
using PiNet.Abstractions.Streaming;
using Xunit;

namespace PiNet.AgentCore.Tests;

/// <summary>7c — thinking deltas are captured and the level reaches the runner.</summary>
public class ThinkingTests
{
    [Fact]
    public async Task ThinkingDeltas_AreCapturedAsThinkingContent()
    {
        var runner = new ScriptedRunner().Enqueue(
            new StreamStart("scripted", "scripted-model", "req-1"),
            new ThinkingDelta("let me think "),
            new ThinkingDelta("about it"),
            new TextDelta("answer"),
            new StreamEnd("end_turn", ScriptedRunner.SampleUsage, ChatMessage.Assistant("answer")));

        var agent = Helpers.NewAgent(runner);
        await agent.PromptAsync("go");

        var assistant = agent.State.Messages.Last(m => m.Role == ChatRole.Assistant);
        var thinking = Assert.Single(assistant.Content.OfType<ThinkingContent>());
        Assert.Equal("let me think about it", thinking.Thinking);
    }

    [Fact]
    public async Task NoThinkingDeltas_ProducesNoThinkingContent()
    {
        var runner = new ScriptedRunner().Enqueue(ScriptedRunner.Answer("plain"));
        var agent = Helpers.NewAgent(runner);
        await agent.PromptAsync("go");

        var assistant = agent.State.Messages.Last(m => m.Role == ChatRole.Assistant);
        Assert.Empty(assistant.Content.OfType<ThinkingContent>());
    }

    [Fact]
    public async Task State_ThinkingLevel_IsPassedToTheRunner()
    {
        var runner = new ScriptedRunner().Enqueue(ScriptedRunner.Answer("ok"));
        var state = new AgentState
        {
            ProviderId = "scripted",
            ModelId = "scripted-model",
            ThinkingLevel = ThinkingLevel.High
        };
        var agent = Helpers.NewAgent(runner, state: state);

        await agent.PromptAsync("go");

        Assert.Equal(ThinkingLevel.High, runner.Requests[0].ThinkingLevel);
    }
}
