using System.Text;
using System.Text.Json;
using PiNet.Abstractions.Chat;
using PiNet.Abstractions.Models;
using PiNet.Abstractions.Providers;
using PiNet.Abstractions.Streaming;

namespace PiNet.AgentCore;

public sealed record ToolCallDecision(bool IsBlocked = false, string? BlockReason = null, bool Terminate = false)
{
    public static ToolCallDecision Allow() => new();
    public static ToolCallDecision Block(string reason, bool terminate = false) => new(true, reason, terminate);
}

public delegate ValueTask<IReadOnlyList<ChatMessage>> TransformContextDelegate(IReadOnlyList<ChatMessage> messages, CancellationToken ct);
public delegate IReadOnlyList<ChatMessage> ConvertToLlmDelegate(IReadOnlyList<ChatMessage> messages);
public delegate Task<ToolCallDecision> BeforeToolCallDelegate(ToolCallContent toolCall, JsonElement arguments, IReadOnlyList<ChatMessage> context, CancellationToken ct);
public delegate Task<ToolResult?> AfterToolCallDelegate(ToolCallContent toolCall, ToolResult result, IReadOnlyList<ChatMessage> context, CancellationToken ct);

public sealed record AgentOptions
{
    public IModelRunner? Runner { get; init; }
    public AgentState? InitialState { get; init; }
    public TransformContextDelegate? TransformContext { get; init; }
    public ConvertToLlmDelegate? ConvertToLlm { get; init; }
    public BeforeToolCallDelegate? BeforeToolCall { get; init; }
    public AfterToolCallDelegate? AfterToolCall { get; init; }
    public bool ParallelToolExecution { get; init; } = true;
}

public sealed class Agent
{
    private readonly IModelRunner _runner;
    private readonly SemaphoreSlim _emitGate = new(1, 1);
    private readonly object _subscribersLock = new();
    private readonly List<Func<AgentEvent, CancellationToken, Task>> _subscribers = [];
    private CancellationTokenSource? _cts;

    public AgentState State { get; }
    public TransformContextDelegate? TransformContext { get; }
    public ConvertToLlmDelegate? ConvertToLlm { get; }
    public BeforeToolCallDelegate? BeforeToolCall { get; }
    public AfterToolCallDelegate? AfterToolCall { get; }
    public bool ParallelToolExecution { get; set; }

    public Agent(AgentOptions options)
    {
        _runner = options.Runner ?? throw new ArgumentException("Runner is required.", nameof(options));
        State = options.InitialState ?? new AgentState();
        TransformContext = options.TransformContext;
        ConvertToLlm = options.ConvertToLlm;
        BeforeToolCall = options.BeforeToolCall;
        AfterToolCall = options.AfterToolCall;
        ParallelToolExecution = options.ParallelToolExecution;
    }

    public IDisposable Subscribe(Action<AgentEvent> handler) =>
        Subscribe((evt, _) => { handler(evt); return Task.CompletedTask; });

    public IDisposable Subscribe(Func<AgentEvent, CancellationToken, Task> handler)
    {
        lock (_subscribersLock)
            _subscribers.Add(handler);
        return new Subscription(() =>
        {
            lock (_subscribersLock)
                _subscribers.Remove(handler);
        });
    }

    public void Abort() => _cts?.Cancel();

    public Task PromptAsync(string prompt, CancellationToken ct = default)
    {
        State.AppendMessage(ChatMessage.User(prompt));
        return RunAsync(ct);
    }

    public Task PromptAsync(ChatMessage message, CancellationToken ct = default)
    {
        State.AppendMessage(message);
        return RunAsync(ct);
    }

    public Task ContinueAsync(CancellationToken ct = default) => RunAsync(ct);

    private async Task RunAsync(CancellationToken ct)
    {
        if (State.IsStreaming)
            throw new InvalidOperationException("Agent is already streaming.");

        State.ErrorMessage = null;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _cts = cts;
        State.IsStreaming = true;

        try
        {
            await EmitAsync(new AgentStartEvent(), cts.Token);
            var done = false;

            while (!done)
            {
                await EmitAsync(new TurnStartEvent(), cts.Token);
                var llmMessages = await BuildLlmMessagesAsync(cts.Token);

                var (assistant, toolCalls) = await StreamAssistantAsync(llmMessages, cts.Token);
                State.AppendMessage(assistant);
                await EmitAsync(new MessageEndEvent(assistant), cts.Token);

                if (toolCalls.Count == 0)
                {
                    await EmitAsync(new TurnEndEvent(assistant, []), cts.Token);
                    done = true;
                    break;
                }

                var results = await ExecuteToolCallsAsync(llmMessages, toolCalls, cts.Token);
                await EmitAsync(new TurnEndEvent(assistant, results), cts.Token);

                if (results.Count > 0 && results.All(r => r.ShouldTerminate))
                    done = true;
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            State.ErrorMessage = "Aborted by user.";
        }
        catch (Exception ex)
        {
            State.ErrorMessage = ex.Message;
        }
        finally
        {
            State.IsStreaming = false;
            _cts = null;
            await EmitAsync(new AgentEndEvent(), CancellationToken.None);
        }
    }

    private async ValueTask<IReadOnlyList<ChatMessage>> BuildLlmMessagesAsync(CancellationToken ct)
    {
        var messages = State.Messages;
        if (TransformContext is not null)
            messages = await TransformContext(messages, ct);
        return ConvertToLlm is null ? messages : ConvertToLlm(messages);
    }

    private IReadOnlyList<ToolDefinition>? BuildToolDefinitions()
    {
        if (State.Tools.Count == 0)
            return null;

        return State.Tools
            .Select(t => new ToolDefinition
            {
                Name = t.Name,
                Description = t.Description,
                Parameters = t.Parameters
            })
            .ToArray();
    }

    private async Task<(ChatMessage Message, IReadOnlyList<ToolCallContent> ToolCalls)> StreamAssistantAsync(
        IReadOnlyList<ChatMessage> llmMessages,
        CancellationToken ct)
    {
        var text = new StringBuilder();
        var toolCalls = new List<ToolCallContent>();
        ToolCallAccumulator? current = null;

        await EmitAsync(new MessageStartEvent(ChatMessage.Assistant("")), ct);

        var request = new ModelRequest(
            State.ProviderId,
            State.ModelId,
            State.SystemPrompt,
            llmMessages,
            BuildToolDefinitions(),
            CancellationToken: ct);

        await foreach (var evt in _runner.GenerateAsync(request).WithCancellation(ct))
        {
            switch (evt)
            {
                case TextDelta t:
                    text.Append(t.Delta);
                    await EmitAsync(new MessageUpdateEvent(t.Delta), ct);
                    break;
                case ThinkingDelta th:
                    await EmitAsync(new MessageUpdateEvent(th.Delta, IsThinking: true), ct);
                    break;
                case ToolCallStart tcs:
                    current = new ToolCallAccumulator { Index = tcs.Index, Id = tcs.Id, Name = tcs.Name };
                    break;
                case ToolCallDelta tcd:
                    current ??= new ToolCallAccumulator { Index = tcd.Index };
                    current.Arguments.Append(tcd.ArgumentsJsonFragment);
                    break;
                case ToolCallEnd tce:
                    var detail = tce.ToolCall;
                    toolCalls.Add(new ToolCallContent(detail.Id, detail.Name, ParseArguments(detail.ArgumentsJson)));
                    current = null;
                    break;
                case StreamError err when err.IsAborted:
                    throw new OperationCanceledException("Aborted by user.");
                case StreamError err:
                    throw new InvalidOperationException(err.Message);
            }
        }

        var content = new List<ContentBlock>();
        if (text.Length > 0)
            content.Add(new TextContent(text.ToString()));
        content.AddRange(toolCalls);

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var message = content.Count == 0
            ? ChatMessage.Assistant("")
            : new ChatMessage(ChatRole.Assistant, content, Timestamp: timestamp);

        return (message, toolCalls);
    }

    private async Task<IReadOnlyList<ToolExecutionResult>> ExecuteToolCallsAsync(
        IReadOnlyList<ChatMessage> llmMessages,
        IReadOnlyList<ToolCallContent> toolCalls,
        CancellationToken ct)
    {
        var results = new List<ToolExecutionResult>(toolCalls.Count);
        var allowed = new List<int>();

        for (var i = 0; i < toolCalls.Count; i++)
        {
            var tc = toolCalls[i];
            await EmitAsync(new ToolExecutionStartEvent(tc), ct);

            var decision = ToolCallDecision.Allow();
            if (BeforeToolCall is not null)
                decision = await BeforeToolCall(tc, tc.Arguments, llmMessages, ct);

            if (decision.IsBlocked)
            {
                var blocked = new ToolResult(
                    $"Blocked: {decision.BlockReason ?? "blocked by policy"}",
                    IsError: true,
                    Terminate: decision.Terminate);
                await FinalizeToolResultAsync(tc, blocked, results);
            }
            else
            {
                allowed.Add(i);
            }
        }

        var parallel = ParallelToolExecution &&
                       !toolCalls.Any(tc => GetTool(tc.Name)?.ExecutionMode == ToolExecutionMode.Sequential);

        if (parallel && allowed.Count > 1)
        {
            var tasks = new List<(int Index, Task<ToolResult> Task)>();
            foreach (var i in allowed)
            {
                var tc = toolCalls[i];
                var task = ExecuteOneAsync(tc, llmMessages, ct);
                tasks.Add((i, task));
            }

            var done = await Task.WhenAll(tasks.Select(t => t.Task));
            for (var n = 0; n < tasks.Count; n++)
            {
                var index = tasks[n].Index;
                await FinalizeToolResultAsync(toolCalls[index], done[n], results);
            }
        }
        else
        {
            foreach (var i in allowed)
            {
                var tc = toolCalls[i];
                var result = await ExecuteOneAsync(tc, llmMessages, ct);
                await FinalizeToolResultAsync(tc, result, results);
            }
        }

        return results;
    }

    private async Task<ToolResult> ExecuteOneAsync(ToolCallContent tc, IReadOnlyList<ChatMessage> llmMessages, CancellationToken ct)
    {
        var tool = GetTool(tc.Name);
        if (tool is null)
            return ToolResult.Error($"Unknown tool: {tc.Name}");

        var progress = new AgentEventProgress(evt =>
            _ = EmitAsync(new ToolExecutionUpdateEvent(tc, PrettyEvent(evt)), CancellationToken.None));

        ToolResult result;
        try
        {
            result = await tool.Execute(tc.Id, tc.Arguments, ct, progress);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            result = ToolResult.Error(ex.Message);
        }

        if (AfterToolCall is not null)
        {
            var replaced = await AfterToolCall(tc, result, llmMessages, ct);
            if (replaced is not null)
                result = replaced;
        }

        return result;
    }

    private async Task FinalizeToolResultAsync(ToolCallContent tc, ToolResult result, List<ToolExecutionResult> results)
    {
        await EmitAsync(new ToolExecutionEndEvent(tc, result), CancellationToken.None);
        State.AppendMessage(ChatMessage.Tool(tc.Id, tc.Name, result.Content, result.IsError));
        results.Add(new ToolExecutionResult(tc, result));
    }

    private AgentTool? GetTool(string name) =>
        State.Tools.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));

    private async Task EmitAsync(AgentEvent evt, CancellationToken ct)
    {
        await _emitGate.WaitAsync(ct);
        try
        {
            Func<AgentEvent, CancellationToken, Task>[] handlers;
            lock (_subscribersLock)
                handlers = [.. _subscribers];

            foreach (var handler in handlers)
            {
                ct.ThrowIfCancellationRequested();
                await handler(evt, ct);
            }
        }
        finally
        {
            _emitGate.Release();
        }
    }

    private static JsonElement ParseArguments(string json)
    {
        try { return JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json).RootElement; }
        catch (JsonException) { return JsonDocument.Parse("{}").RootElement; }
    }

    private static string PrettyEvent(StreamEvent evt) => evt switch
    {
        TextDelta t => t.Delta,
        ToolCallDelta td => td.ArgumentsJsonFragment,
        _ => string.Empty
    };

    private sealed class ToolCallAccumulator
    {
        public int Index { get; init; }
        public string? Id { get; init; }
        public string? Name { get; init; }
        public StringBuilder Arguments { get; } = new();
    }

    private sealed class AgentEventProgress(Action<StreamEvent> report) : IProgress<StreamEvent>
    {
        public void Report(StreamEvent value) => report(value);
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}