namespace PiNet.Cli.Interact;

/// <summary>
/// 7a.2 — coordinates the in-flight keystroke pump with blocking prompts that
/// read from the same input (the console permission prompt). While a scope is
/// active the pump pauses so it cannot steal the prompt's answer.
/// </summary>
public sealed class InputArbiter
{
    private int _busy;

    /// <summary>True while a blocking reader (permission prompt) owns the input.</summary>
    public bool IsBusy => Volatile.Read(ref _busy) != 0;

    /// <summary>Blocks the pump until the returned scope is disposed.</summary>
    public IDisposable Begin()
    {
        Interlocked.Increment(ref _busy);
        return new Scope(this);
    }

    private sealed class Scope(InputArbiter owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                Interlocked.Decrement(ref owner._busy);
        }
    }
}
