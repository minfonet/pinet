using System.Text.Json;
using PiNet.Abstractions.Chat;
using PiNet.Abstractions.Models;
using PiNet.Abstractions.Providers;
using PiNet.Abstractions.Streaming;

namespace PiNet.Cli.Tests;

/// <summary>
/// Deterministic IChatProvider: each StreamAsync call returns the next queued
/// plain-text answer as TextDelta events followed by a StreamEnd.
/// </summary>
public sealed class ScriptedProvider : IChatProvider
{
    private readonly Queue<IReadOnlyList<StreamEvent>> _script = new();

    public string Id { get; }
    public string Name => "Scripted test provider";
    public IReadOnlyList<ModelDefinition> Models { get; }
    public ICredentialResolver Credentials { get; } = NoCredentials.Instance;
    public int StreamCalls { get; private set; }

    public List<ChatRequest> Requests { get; } = [];

    public ScriptedProvider(string id, params string[] answers) : this(id, supportsVision: false, answers) { }

    public ScriptedProvider(string id, bool supportsVision, params string[] answers)
    {
        Id = id;
        Models =
        [
            new ModelDefinition
            {
                Provider = id,
                Id = $"{id}-model",
                Name = $"{id} model",
                ContextWindow = 1000,
                SupportsVision = supportsVision
            }
        ];
        foreach (var answer in answers)
            _script.Enqueue(Answer(answer));
    }

    public ScriptedProvider Enqueue(string answer)
    {
        _script.Enqueue(Answer(answer));
        return this;
    }

    /// <summary>Enqueues a raw event sequence (e.g. a tool-call turn).</summary>
    public ScriptedProvider EnqueueEvents(IReadOnlyList<StreamEvent> events)
    {
        _script.Enqueue(events);
        return this;
    }

    public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, AuthInfo auth)
    {
        StreamCalls++;
        Requests.Add(request);
        await Task.Yield();
        var events = _script.Count > 0 ? _script.Dequeue() : Answer("(no script)");
        foreach (var evt in events)
            yield return evt;
    }

    private static IReadOnlyList<StreamEvent> Answer(string text)
    {
        var events = new List<StreamEvent>(text.Length + 1);
        foreach (var c in text)
            events.Add(new TextDelta(c.ToString()));
        events.Add(new StreamEnd("end_turn", Usage, ChatMessage.Assistant(text)));
        return events;
    }

    private static Usage Usage { get; } = new(1, 2, 0, 0, new Cost(0, 0, 0, 0));

    public sealed class NoCredentials : ICredentialResolver
    {
        public static readonly NoCredentials Instance = new();
        public ValueTask<AuthInfo> ResolveAsync(CancellationToken ct = default) =>
            new(new AuthInfo("none", null));
    }
}

/// <summary>
/// Builders for scripted tool-call turns: the event sequence an assistant
/// message containing one tool call produces.
/// </summary>
public static class ToolCallScript
{
    private static Usage Usage { get; } = new(1, 2, 0, 0, new Cost(0, 0, 0, 0));

    /// <summary>A turn that calls the bash tool with the given command.</summary>
    public static IReadOnlyList<StreamEvent> Bash(string command, string callId = "call-1")
        => ToolCall(callId, "bash", $"{{\"command\": {JsonSerializer.Serialize(command)}}}");

    /// <summary>A turn that calls the write_file tool.</summary>
    public static IReadOnlyList<StreamEvent> WriteFile(string path, string content, string callId = "call-1")
        => ToolCall(callId, "write_file", $"{{\"path\": {JsonSerializer.Serialize(path)}, \"content\": {JsonSerializer.Serialize(content)}}}");

    private static IReadOnlyList<StreamEvent> ToolCall(string id, string name, string argumentsJson)
    {
        var message = new ChatMessage(ChatRole.Assistant,
            [new ToolCallContent(id, name, JsonDocument.Parse(argumentsJson).RootElement)]);
        return
        [
            new StreamStart("faux", "faux-model", $"{id}-req"),
            new ToolCallStart(0, id, name),
            new ToolCallDelta(0, argumentsJson),
            new ToolCallEnd(0, new ToolCallDetail(id, name, argumentsJson)),
            new StreamEnd("tool_calls", Usage, message)
        ];
    }
}

/// <summary>
/// IChatProvider that signals when streaming starts and then hangs until the
/// request cancellation token fires (used for Ctrl+C cancel tests).
/// </summary>
public sealed class HangingSignalProvider : IChatProvider
{
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Started => _started.Task;
    public string Id { get; }
    public string Name => "Hanging test provider";
    public IReadOnlyList<ModelDefinition> Models { get; }
    public ICredentialResolver Credentials { get; } = ScriptedProvider.NoCredentials.Instance;

    public HangingSignalProvider(string id = "hang")
    {
        Id = id;
        Models =
        [
            new ModelDefinition
            {
                Provider = id,
                Id = $"{id}-model",
                Name = $"{id} model",
                ContextWindow = 1000
            }
        ];
    }

    public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, AuthInfo auth)
    {
        _started.TrySetResult();
        yield return new StreamStart(Id, request.ModelId, "hang-req-1");
        await Task.Delay(Timeout.Infinite, request.CancellationToken);
    }
}
