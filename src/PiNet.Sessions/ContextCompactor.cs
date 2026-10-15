using PiNet.Abstractions.Chat;

namespace PiNet.Sessions;

/// <summary>Configuration for <see cref="ContextCompactor"/>.</summary>
public sealed record CompactionSettings
{
    /// <summary>Model context window in tokens; 0 disables auto-compaction.</summary>
    public long ContextWindow { get; init; }

    /// <summary>Fraction of the window at which compaction triggers (0..1).</summary>
    public double Threshold { get; init; } = 0.75;

    /// <summary>Number of trailing messages always kept verbatim.</summary>
    public int PreserveRecentMessages { get; init; } = 4;

    public string DefaultInstruction { get; init; } =
        "Summarize the conversation so far. Preserve decisions, file paths, commands run, and open tasks.";
}

/// <summary>Result of a compaction attempt.</summary>
public sealed record CompactionOutcome(IReadOnlyList<ChatMessage> Messages, bool Compacted, int Summarized);

/// <summary>
/// Replaces an oversized prefix of a conversation with a single cached summary
/// message, keeping the most recent messages verbatim. Lossless by design: the
/// original messages are never removed or mutated; callers decide whether to
/// feed the returned view to the agent. Used both for automatic compaction (via
/// <c>Agent.TransformContext</c>) and for the manual <c>/compact</c> command.
/// </summary>
public sealed class ContextCompactor
{
    private readonly SessionCompactor _summarizer;
    private readonly CompactionSettings _settings;
    private ChatMessage? _summary;
    private int _summarizedCount;

    public ContextCompactor(SessionCompactor summarizer, CompactionSettings settings)
    {
        _summarizer = summarizer;
        _settings = settings;
    }

    /// <summary>
    /// Carries over compaction state when the agent is rebuilt against the same in-memory
    /// context (e.g. a mid-session model switch). <paramref name="existingSummary"/> and
    /// <paramref name="existingSummarizedCount"/> are the summary and prefix-index the
    /// previous compactor already computed.
    /// </summary>
    public ContextCompactor(
        SessionCompactor summarizer,
        CompactionSettings settings,
        ChatMessage? existingSummary,
        int existingSummarizedCount)
    {
        _summarizer = summarizer;
        _settings = settings;
        _summary = existingSummary;
        _summarizedCount = existingSummarizedCount;
    }

    /// <summary>True when auto-compaction is enabled (window known, threshold &gt; 0).</summary>
    public bool AutoEnabled => _settings.ContextWindow > 0 && _settings.Threshold > 0;

    public bool HasSummary => _summary is not null;

    /// <summary>The summary message produced by the most recent compaction, when any.</summary>
    public ChatMessage? Summary => _summary;

    /// <summary>
    /// Resets the compactor to a fresh state. Call after the compacted view (which
    /// now carries the summary as a literal leading message) is substituted into
    /// <c>AgentState.Messages</c>; the embedded summary must then be treated as
    /// ordinary context for any future compaction.
    /// </summary>
    public void Reset()
    {
        _summary = null;
        _summarizedCount = 0;
        LastPromptTokens = null;
    }


    /// <summary>Index into the original list up to which messages have been summarized.</summary>
    public int SummarizedCount => _summarizedCount;

    /// <summary>
    /// Real prompt-token count from the last request, when available. Combined
    /// with the character estimator to decide when to compact.
    /// </summary>
    public int? LastPromptTokens { get; set; }

    /// <summary>
    /// Auto path: returns the context to send to the model, compacting when the
    /// current view exceeds the threshold. Returns <paramref name="messages"/>
    /// unchanged when auto-compaction is disabled.
    /// </summary>
    public async ValueTask<IReadOnlyList<ChatMessage>> TransformAsync(
        IReadOnlyList<ChatMessage> messages,
        string systemPrompt,
        CancellationToken ct = default)
    {
        if (!AutoEnabled)
            return messages;

        var outcome = await CompactAsync(messages, systemPrompt, instructions: null, force: false, ct);
        return outcome.Messages;
    }

    /// <summary>
    /// Compacts the conversation. With <paramref name="force"/> (manual
    /// <c>/compact</c>) the threshold is ignored; otherwise compaction happens
    /// only when the current view exceeds it.
    /// </summary>
    public async Task<CompactionOutcome> CompactAsync(
        IReadOnlyList<ChatMessage> messages,
        string systemPrompt,
        string? instructions,
        bool force = false,
        CancellationToken ct = default)
    {
        var current = Current(messages);
        var cut = messages.Count - Math.Max(0, _settings.PreserveRecentMessages);
        if (cut <= _summarizedCount)
            return new CompactionOutcome(current, false, 0);

        if (!force && (!AutoEnabled || !ExceedsThreshold(current)))
            return new CompactionOutcome(current, false, 0);

        var prefix = new List<ChatMessage>();
        if (_summary is not null)
            prefix.Add(_summary);
        for (var i = _summarizedCount; i < cut; i++)
            prefix.Add(messages[i]);

        var instruction = string.IsNullOrWhiteSpace(instructions) ? _settings.DefaultInstruction : instructions!;
        _summary = await _summarizer.CompactAsync(prefix, systemPrompt, instruction, ct);

        var summarized = cut - _summarizedCount;
        _summarizedCount = cut;
        return new CompactionOutcome(Current(messages), true, summarized);
    }

    private IReadOnlyList<ChatMessage> Current(IReadOnlyList<ChatMessage> messages)
    {
        if (_summary is null)
            return messages;

        var view = new List<ChatMessage>(messages.Count - _summarizedCount + 1) { _summary };
        for (var i = _summarizedCount; i < messages.Count; i++)
            view.Add(messages[i]);
        return view;
    }

    private bool ExceedsThreshold(IReadOnlyList<ChatMessage> messages)
    {
        var estimate = TokenEstimator.Estimate(messages);
        if (LastPromptTokens is > 0)
            estimate = Math.Max(estimate, LastPromptTokens.Value);
        return estimate > _settings.Threshold * _settings.ContextWindow;
    }
}
