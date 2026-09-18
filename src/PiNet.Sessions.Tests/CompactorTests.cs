using PiNet.Abstractions.Chat;
using PiNet.AgentCore;
using PiNet.AgentCore.Tests;

namespace PiNet.Sessions.Tests;

public class CompactorTests
{
    private static ChatMessage Msg(ChatRole role, string text) =>
        new(role, [new TextContent(text)], Timestamp: 0);

    [Fact]
    public async Task Compactor_SummarizesPrefix_AndDoesNotTouchDisk()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pinet-compact-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var manager = new SessionManager(dir);
            var session = manager.CreateSession(dir, [Msg(ChatRole.User, "q1"), Msg(ChatRole.Assistant, "a1")]);
            var path = manager.SessionFilePath(dir, session.Id);
            var bytesBefore = File.ReadAllBytes(path);

            var runner = new ScriptedRunner()
                .Enqueue(r => ScriptedRunner.Answer("SUM: user asked q1."));
            var compactor = new SessionCompactor(runner, "scripted", "scripted-model");

            var summary = await compactor.CompactAsync(
                session.ToMessages(),
                systemPrompt: "Be brief.",
                summaryInstruction: "Summarize the conversation.");

            Assert.Contains("SUM:", summary.ReadText());
            Assert.Equal(1, runner.GenerateCalls);
            Assert.True(File.ReadAllBytes(path).AsSpan().SequenceEqual(bytesBefore));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Compactor_EmptyPrefix_ReturnsSentinelWithoutLlmCall()
    {
        var runner = new ScriptedRunner();
        var compactor = new SessionCompactor(runner, "scripted", "m");

        var summary = await compactor.CompactAsync([], "s", "Summarize.");

        Assert.Equal(0, runner.GenerateCalls);
        Assert.Contains("(conversation is empty)", summary.ReadText());
    }
}

public class SessionIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pinet-sess-int", Guid.NewGuid().ToString("N"));

    public SessionIntegrationTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => Directory.Delete(_root, true);

    private static ChatMessage Msg(ChatRole role, string text) =>
        new(role, [new TextContent(text)], Timestamp: 0);

    /// <summary>
    /// AC1: three sequential invocations, each loading the previous context from disk
    /// and persisting the new turns, exactly like the CLI --session flow.
    /// </summary>
    [Fact]
    public async Task Ac1_ThreeInvocations_PreserveAllContext()
    {
        var runner = new ScriptedRunner()
            .Enqueue(r => ScriptedRunner.Answer("A1\nloaded turn: " + string.Join(",", r.Messages.Select(m => m.ReadText()))))
            .Enqueue(r => ScriptedRunner.Answer("A2\nloaded turn: " + string.Join(",", r.Messages.Select(m => m.ReadText()))))
            .Enqueue(r => ScriptedRunner.Answer("A3\nloaded turn: " + string.Join(",", r.Messages.Select(m => m.ReadText()))));
        var manager = new SessionManager(_root);
        var cwd = _root;

        var session = manager.CreateSession(cwd, seed: [Msg(ChatRole.User, "anchor")]);

        foreach (var q in new[] { "question 1", "question 2", "question 3" })
        {
            var loadedMessages = session.ToMessages().ToList();
            var agent = new Agent(new AgentOptions
            {
                Runner = runner,
                InitialState = new AgentState
                {
                    ProviderId = "scripted",
                    ModelId = "m",
                    SystemPrompt = "test",
                    Messages = loadedMessages
                }
            });
            await agent.PromptAsync(q);

            var newMessages = agent.State.Messages.Skip(loadedMessages.Count).ToArray();
            foreach (var message in newMessages)
                session.AppendMessage(message);
            await manager.AppendAsync(session, cwd);
        }

        var reloaded = await manager.LoadSessionAsync(cwd, session.Id);
        var branch = reloaded.GetActiveBranch();
        var texts = branch.Select(m => TextOf(m)).ToArray();

        Assert.Equal(7, texts.Length);
        Assert.Equal("anchor", texts[0]);
        Assert.Equal("question 1", texts[1]);
        Assert.StartsWith("A1", texts[2]);
        Assert.Equal("question 2", texts[3]);
        Assert.StartsWith("A2", texts[4]);
        Assert.Equal("question 3", texts[5]);
        Assert.StartsWith("A3", texts[6]);

        // each invocation received rising context on the wire (SR-03.15)
        var lastRequest = runner.Requests[^1];
        Assert.Contains(lastRequest.Messages, m => m.ReadText() == "question 1");
        Assert.Contains(lastRequest.Messages, m => m.ReadText() == "question 2");
    }

    private static string TextOf(SessionEntry entry) =>
        entry.Content.OfType<TextContent>().FirstOrDefault()?.Text ?? string.Empty;
}