using System.Text.Json;
using PiNet.Abstractions.Chat;
using PiNet.Abstractions.Streaming;

namespace PiNet.AgentCore.Tests;

public static class Helpers
{
    public static JsonElement Args(string json) =>
        JsonDocument.Parse(json).RootElement;

    public static Agent NewAgent(
        ScriptedRunner runner,
        IReadOnlyList<AgentTool>? tools = null,
        AgentOptions? options = null,
        AgentState? state = null)
    {
        state ??= new AgentState
        {
            ProviderId = "scripted",
            ModelId = "scripted-model",
            SystemPrompt = "You are a test assistant.",
            Tools = tools ?? []
        };

        var opts = options ?? new AgentOptions { Runner = runner };
        return new Agent(opts with { Runner = runner, InitialState = state });
    }

    public static List<AgentEvent> Record(Agent agent)
    {
        var events = new List<AgentEvent>();
        agent.Subscribe(e => events.Add(e));
        return events;
    }

    public static AgentTool Tool(
        string name,
        Func<string, ToolResult> execute) =>
        new()
        {
            Name = name,
            Description = $"Test tool {name}",
            Execute = (id, args, ct, progress) =>
            {
                var result = execute(name);
                return Task.FromResult(result);
            }
        };

    public static AgentTool Tool(
        string name,
        Func<string, JsonElement, CancellationToken, IProgress<StreamEvent>?, Task<ToolResult>> execute) =>
        new()
        {
            Name = name,
            Description = $"Test tool {name}",
            Execute = execute
        };

    public static string ReadText(this ChatMessage message) =>
        message.TryGetText() ?? string.Empty;
}