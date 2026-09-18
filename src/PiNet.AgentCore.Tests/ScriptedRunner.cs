using System.Text.Json;
using PiNet.Abstractions.Chat;
using PiNet.Abstractions.Models;
using PiNet.Abstractions.Providers;
using PiNet.Abstractions.Streaming;

namespace PiNet.AgentCore.Tests;

/// <summary>
/// Scripted IModelRunner for tests. Each GenerateAsync call consumes the next
/// queued responder, letting tests drive deterministic multi-turn scenarios.
/// </summary>
public sealed class ScriptedRunner : IModelRunner
{
    private readonly Queue<Func<ModelRequest, IReadOnlyList<StreamEvent>>> _script = new();

    public int GenerateCalls { get; private set; }
    public List<ModelRequest> Requests { get; } = [];
    public bool HangOnGenerate { get; set; }

    public ScriptedRunner Enqueue(params StreamEvent[] events) => Enqueue(_ => events);

    public ScriptedRunner Enqueue(Func<ModelRequest, IReadOnlyList<StreamEvent>> responder)
    {
        _script.Enqueue(responder);
        return this;
    }

    public async IAsyncEnumerable<StreamEvent> GenerateAsync(ModelRequest request)
    {
        GenerateCalls++;
        Requests.Add(request);

        if (HangOnGenerate)
        {
            yield return new StreamStart("scripted", request.ModelId, "req-1");
            await Task.Delay(-1, request.CancellationToken);
        }

        if (_script.TryDequeue(out var responder))
        {
            foreach (var evt in responder(request))
                yield return evt;
        }
    }

    public static StreamEvent[] Answer(string text, string stopReason = "end_turn")
    {
        var events = new List<StreamEvent>(text.Length + 1);
        foreach (var ch in text)
            events.Add(new TextDelta(ch.ToString()));
        events.Add(new StreamEnd(stopReason, SampleUsage, ChatMessage.Assistant(text)));
        return events.ToArray();
    }

    public static StreamEvent[] ToolTurn(string id, string name, string argsJson) =>
        MultiToolTurn((id, name, argsJson));

    public static StreamEvent[] MultiToolTurn(params (string Id, string Name, string ArgsJson)[] calls)
    {
        var events = new List<StreamEvent>();
        for (var i = 0; i < calls.Length; i++)
        {
            events.Add(new ToolCallStart(i, calls[i].Id, calls[i].Name));
            events.Add(new ToolCallDelta(i, calls[i].ArgsJson));
            events.Add(new ToolCallEnd(i, new ToolCallDetail(calls[i].Id, calls[i].Name, calls[i].ArgsJson)));
        }
        events.Add(new StreamEnd(
            "toolUse",
            SampleUsage,
            new ChatMessage(
                ChatRole.Assistant,
                calls.Select(c => new ToolCallContent(c.Id, c.Name, JsonDocument.Parse(c.ArgsJson).RootElement)).ToArray(),
                Timestamp: 0)));
        return events.ToArray();
    }

    public static Usage SampleUsage { get; } = new(10, 20, 0, 0, new Cost(0.0001m, 0.0002m, 0, 0));
}