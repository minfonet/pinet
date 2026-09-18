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

    public ScriptedProvider(string id, params string[] answers)
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
        foreach (var answer in answers)
            _script.Enqueue(Answer(answer));
    }

    public ScriptedProvider Enqueue(string answer)
    {
        _script.Enqueue(Answer(answer));
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
