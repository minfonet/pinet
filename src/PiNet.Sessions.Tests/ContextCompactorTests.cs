using PiNet.Abstractions.Chat;
using PiNet.AgentCore;
using PiNet.AgentCore.Tests;

namespace PiNet.Sessions.Tests;

/// <summary>
/// Unit tests for <see cref="ContextCompactor"/> (AC7b.3–7b.8, SR-07b.04/05/06/07/09).
/// </summary>
public sealed class ContextCompactorTests
{
    private static ChatMessage Msg(ChatRole role, string text) =>
        new(role, [new TextContent(text)], Timestamp: 0);

    private static List<ChatMessage> ListOf(int n) =>
        Enumerable.Range(1, n).Select(i => Msg(ChatRole.User, $"message {i} of {n}")).ToList();

    [Fact]
    public async Task BelowThreshold_NoSummarizerCall_NoCompaction()
    {
        var runner = new ScriptedRunner();
        var compactor = new ContextCompactor(
            new SessionCompactor(runner, "p", "m"),
            new CompactionSettings { ContextWindow = 10000, Threshold = 0.9, PreserveRecentMessages = 4 });

        var messages = ListOf(4);
        var result = await compactor.TransformAsync(messages, "system", default);

        Assert.False(compactor.HasSummary);
        Assert.Equal(0, runner.GenerateCalls);
        Assert.Equal(messages, result);
    }

    [Fact]
    public async Task ForceCompaction_RewritesPrefix_KeepsTail_OriginalUntouched()
    {
        var runner = new ScriptedRunner().Enqueue(r => ScriptedRunner.Answer("SUMMARY: early stuff"));
        var compactor = new ContextCompactor(
            new SessionCompactor(runner, "p", "m"),
            new CompactionSettings { ContextWindow = 100, Threshold = 0.5, PreserveRecentMessages = 3 });

        var messages = ListOf(10);
        var before = messages.ToList();

        var outcome = await compactor.CompactAsync(messages, "system", null, force: true, default);

        Assert.True(outcome.Compacted);
        Assert.Equal(10, before.Count);
        Assert.Equal(10, messages.Count);
        Assert.True(compactor.HasSummary);
        // View is [summary, last 3 messages].
        Assert.Equal(1 + 3, outcome.Messages.Count);
        var summaryText = outcome.Messages[0].ReadText();
        Assert.StartsWith("<summary of earlier conversation>", summaryText);
        Assert.EndsWith("SUMMARY: early stuff", summaryText);
        Assert.Contains("message 10 of 10", outcome.Messages.Select(m => m.ReadText()));
        Assert.Equal(1, runner.GenerateCalls);
        Assert.Equal(10 - 3, outcome.Summarized);
    }

    [Fact]
    public async Task AfterCompaction_SubsequentUnderThreshold_NoFurtherSummary()
    {
        var runner = new ScriptedRunner().Enqueue(r => ScriptedRunner.Answer("SUMMARY1: first"));
        var compactor = new ContextCompactor(
            new SessionCompactor(runner, "p", "m"),
            new CompactionSettings { ContextWindow = 100, Threshold = 0.5, PreserveRecentMessages = 3 });

        var messages = ListOf(10);
        var first = await compactor.CompactAsync(messages, "system", null, force: true, default);
        Assert.True(first.Compacted);
        var callsAfterFirst = runner.GenerateCalls;

        // A later auto-compaction call on the same (already-summarized) list: the
        // prefix is already summarized (cut <= _summarizedCount), so no LLM call.
        var view = await compactor.TransformAsync(messages, "system", default);

        Assert.Equal(callsAfterFirst, runner.GenerateCalls);
        Assert.True(compactor.HasSummary);
    }

    [Fact]
    public async Task RepeatedCompaction_FoldsPreviousSummaryIntoNewPrefix()
    {
        var runner = new ScriptedRunner()
            .Enqueue(r => ScriptedRunner.Answer("S1: first-half summary"))
            .Enqueue(r => ScriptedRunner.Answer("S2: folded summary"));
        var compactor = new ContextCompactor(
            new SessionCompactor(runner, "p", "m"),
            new CompactionSettings { ContextWindow = 100, Threshold = 0.1, PreserveRecentMessages = 2 });

        var messages = ListOf(20);
        var o1 = await compactor.CompactAsync(messages, "system", null, force: true, default);
        Assert.True(o1.Compacted);
        var summary1 = compactor.Summary!.ReadText();

        messages.Add(Msg(ChatRole.User, "message 21 of 20"));
        messages.Add(Msg(ChatRole.User, "message 22 of 20"));

        var o2 = await compactor.CompactAsync(messages, "system", null, force: true, default);
        Assert.True(o2.Compacted);
        Assert.True(compactor.HasSummary);

        // The second summarization must have folded the first summary in.
        var secondRequest = runner.Requests[1];
        var secondRequestText = secondRequest.Messages[0].ReadText();
        Assert.Contains(summary1, secondRequestText);
    }

    [Fact]
    public async Task ContextWindowZero_AutoNoOp_ForceStillCompacts()
    {
        var runner = new ScriptedRunner().Enqueue(r => ScriptedRunner.Answer("SUMMARY: forced"));
        var compactor = new ContextCompactor(
            new SessionCompactor(runner, "p", "m"),
            new CompactionSettings { ContextWindow = 0, Threshold = 0.5, PreserveRecentMessages = 2 });

        var messages = ListOf(10);

        // Auto path: a 0 context window disables auto-compaction (no summarizer call).
        Assert.False(compactor.AutoEnabled);
        var auto = await compactor.TransformAsync(messages, "system", default);
        Assert.Equal(0, runner.GenerateCalls);
        Assert.Equal(10, auto.Count);

        // Manual /compact still compacts regardless of window.
        var outcome = await compactor.CompactAsync(messages, "system", null, force: true, default);
        Assert.True(outcome.Compacted);
        Assert.Equal(1, runner.GenerateCalls);
    }

    [Fact]
    public async Task Compact_CachesSummaryAndDoesNotReSummarize()
    {
        var runner = new ScriptedRunner().Enqueue(r => ScriptedRunner.Answer("SUMMARY1: cached"));
        var compactor = new ContextCompactor(
            new SessionCompactor(runner, "p", "m"),
            new CompactionSettings { ContextWindow = 100, Threshold = 0.1, PreserveRecentMessages = 2 });

        var messages = ListOf(10);
        var o1 = await compactor.CompactAsync(messages, "system", null, force: true, default);
        Assert.True(o1.Compacted);
        Assert.Equal(1, runner.GenerateCalls);

        // Compacting the same underlying list again: the prefix is already summarized
        // (cut <= _summarizedCount), so no further LLM call.
        var o2 = await compactor.CompactAsync(messages, "system", null, force: true, default);
        Assert.False(o2.Compacted);
        Assert.Equal(1, runner.GenerateCalls);
    }

    [Fact]
    public async Task Reset_ForgetsSummaryAndCount()
    {
        var runner = new ScriptedRunner().Enqueue(r => ScriptedRunner.Answer("S1: before reset"));
        var compactor = new ContextCompactor(
            new SessionCompactor(runner, "p", "m"),
            new CompactionSettings { ContextWindow = 100, Threshold = 0.1, PreserveRecentMessages = 2 });

        var messages = ListOf(10);
        var o1 = await compactor.CompactAsync(messages, "system", null, force: true, default);
        Assert.True(o1.Compacted);
        Assert.True(compactor.HasSummary);
        Assert.True(compactor.SummarizedCount > 0);

        compactor.Reset();
        Assert.False(compactor.HasSummary);
        Assert.Null(compactor.Summary);
        Assert.Equal(0, compactor.SummarizedCount);
        Assert.Null(compactor.LastPromptTokens);
    }
}
