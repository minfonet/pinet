using PiNet.AgentCore;
using PiNet.Sessions;

namespace PiNet.Cli.Interact;

public delegate Task AgentEventSink(AgentEvent evt, CancellationToken ct);

public sealed record TurnResult(bool Ok, string? Error, bool Aborted, int NewMessageCount, string? SessionId);

public sealed class TurnRunner : IDisposable
{
    private readonly SessionManager _manager;
    private readonly string _cwd;
    private readonly IDisposable _subscription;
    private bool _disposed;

    public TurnRunner(Agent agent, string cwd, SessionManager manager, bool noSession)
    {
        Agent = agent;
        _cwd = cwd;
        _manager = manager;
        NoSession = noSession;
        _subscription = agent.Subscribe(ForwardAsync);
    }

    public Agent Agent { get; }
    public bool NoSession { get; }
    public Session? Session { get; private set; }
    public int PriorMessageCount { get; private set; }

    public event AgentEventSink? AgentEventSink;

    public void AttachSession(Session session)
    {
        Session = session;
        PriorMessageCount = session.Entries.Count;
    }

    public void DetachSession()
    {
        Session = null;
    }

    public async Task<TurnResult> RunAsync(string prompt, CancellationToken ct)
    {
        try
        {
            await Agent.PromptAsync(prompt, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new TurnResult(false, null, true, 0, null);
        }

        if (ct.IsCancellationRequested)
            return new TurnResult(false, null, true, 0, null);

        if (Agent.State.ErrorMessage is not null)
            return new TurnResult(false, Agent.State.ErrorMessage, false, 0, null);

        var newMessages = Agent.State.Messages.Skip(PriorMessageCount).ToArray();

        if (Session is not null)
        {
            foreach (var message in newMessages)
                Session.AppendMessage(message);
            await _manager.AppendAsync(Session, _cwd, ct);
            PriorMessageCount = Agent.State.Messages.Count;
        }

        return new TurnResult(true, null, false, newMessages.Length, Session?.Id);
    }

    private async Task ForwardAsync(AgentEvent evt, CancellationToken ct)
    {
        if (AgentEventSink is not null)
            await AgentEventSink(evt, ct);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _subscription.Dispose();
    }
}