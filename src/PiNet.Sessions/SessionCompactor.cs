using PiNet.Abstractions.Chat;
using PiNet.Abstractions.Providers;
using PiNet.Abstractions.Streaming;

namespace PiNet.Sessions;

/// <summary>
/// Summarizes an oversized prefix of a conversation into a single history message
/// using an LLM call. Lossless by design: the original messages are never removed
/// or mutated on disk; callers decide which representation to feed the agent.
/// </summary>
public sealed class SessionCompactor
{
    private readonly IModelRunner _runner;
    private readonly string _providerId;
    private readonly string _modelId;

    public SessionCompactor(IModelRunner runner, string providerId, string modelId)
    {
        _runner = runner;
        _providerId = providerId;
        _modelId = modelId;
    }

    public async Task<ChatMessage> CompactAsync(
        IReadOnlyList<ChatMessage> prefix,
        string systemPrompt,
        string summaryInstruction,
        CancellationToken ct = default)
    {
        if (prefix.Count == 0)
            return ChatMessage.User("(conversation is empty)");

        var serialized = string.Join(
            "\n",
            prefix.Select(m =>
                $"{RoleName(m.Role)}: {m.TryGetText() ?? string.Format(
                    "{0} content block(s)", m.Content.Count)}"));

        var request = new ModelRequest(
            _providerId,
            _modelId,
            systemPrompt,
            [ChatMessage.User($"{summaryInstruction}\n\n{serialized}")],
            CancellationToken: ct);

        var summary = new System.Text.StringBuilder();
        await foreach (var evt in _runner.GenerateAsync(request).WithCancellation(ct))
        {
            switch (evt)
            {
                case TextDelta t:
                    summary.Append(t.Delta);
                    break;
                case StreamError e when !string.IsNullOrEmpty(e.Message):
                    throw new InvalidOperationException(e.Message);
            }
        }

        return ChatMessage.User($"<summary of earlier conversation>\n{summary}");
    }

    private static string RoleName(ChatRole role) => role switch
    {
        ChatRole.System => "system",
        ChatRole.User => "user",
        ChatRole.Assistant => "assistant",
        _ => "tool"
    };
}