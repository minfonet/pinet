using System.Text.Json;
using PiNet.Abstractions.Auth;
using PiNet.Abstractions.Chat;
using PiNet.Abstractions.Models;
using PiNet.Abstractions.Providers;
using PiNet.Abstractions.Streaming;

namespace PiNet.Providers.Faux;

public sealed class FauxChatProvider : IChatProvider
{
    public string Id => "faux";
    public string Name => "Faux (local test)";
    public IReadOnlyList<ModelDefinition> Models { get; }
    public ICredentialResolver Credentials { get; } = new EnvCredentialResolver("none", []);

    public FauxChatProvider()
    {
        Models =
        [
            new ModelDefinition
            {
                Provider = Id, Id = "faux-model", Name = "Faux model",
                SupportsReasoning = true, SupportsVision = false, ContextWindow = 32000
            }
        ];
    }

    public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, AuthInfo auth)
    {
        yield return new StreamStart(Id, request.ModelId, "faux-req-1");
        yield return new ThinkingDelta("Thinking deterministically");

        var toolMessage = request.Messages.LastOrDefault(m => m.Role == ChatRole.Tool);
        if (toolMessage is not null)
        {
            var reply = $"The tool completed. Result:\n\n{toolMessage.TryGetText()}";
            foreach (var ch in reply)
                yield return new TextDelta(ch.ToString());
            yield return new StreamEnd("end_turn", SampleUsage(), ChatMessage.Assistant(reply));
            yield break;
        }

        var messages = request.Messages
            .Where(m => m.Role == ChatRole.User)
            .Select(m => m.TryGetText() ?? string.Empty);

        var prompt = string.Join(" ", messages);

        if (prompt.Contains("read_file", StringComparison.OrdinalIgnoreCase))
        {
            const string argsJson = """{"path":"docs/README.md"}""";
            const string id = "call_faux_1";
            yield return new ToolCallStart(0, id, "read_file");
            yield return new ToolCallDelta(0, """{"path":"docs/README.md"}""");
            yield return new ToolCallEnd(0, new ToolCallDetail(id, "read_file", argsJson));
            yield return new StreamEnd(
                "toolUse",
                SampleUsage(),
                new ChatMessage(
                    ChatRole.Assistant,
                    [new ToolCallContent(id, "read_file", JsonDocument.Parse(argsJson).RootElement)],
                    Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            yield break;
        }

        foreach (var word in EchoAsync(prompt))
            yield return word;

        yield return new StreamEnd("stop", SampleUsage(), ChatMessage.Assistant($"You said: {prompt}"));
    }

    private static IEnumerable<TextDelta> EchoAsync(string prompt)
    {
        foreach (var ch in $"You said: {prompt}")
            yield return new TextDelta(ch.ToString());
    }

    private static Usage SampleUsage() =>
        new(10, 20, 0, 0, new Cost(0.0001m, 0.0002m, 0, 0));
}