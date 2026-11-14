namespace Ftx1.Core.Safety;

public enum StopReason { Released, Timeout, LostHeartbeat, Disconnected, Failure, Shutdown }

/// <summary>One owner, monotonic deadlines, and an unconditional stop for every lease.</summary>
public sealed class TransmitGuard : IDisposable
{
    readonly object gate = new();
    readonly Action stop;
    readonly Func<double> now;
    readonly Timer timer;
    string? owner;
    long generation;
    double started, alive;
    bool remote, disposed;
    public TimeSpan Limit { get; }
    public event Action<StopReason>? Stopped;
    public string? Owner { get { lock (gate) return owner; } }
    public TransmitGuard(Action stop, TimeSpan? limit = null, Func<double>? clock = null)
    {
        this.stop = stop; Limit = limit ?? TimeSpan.FromSeconds(180);
        if (Limit <= TimeSpan.Zero || Limit > TimeSpan.FromSeconds(600)) throw new ArgumentOutOfRangeException(nameof(limit));
        now = clock ?? (() => (double)System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency);
        timer = new Timer(_ => Tick(), null, 50, 50);
    }
    public IDisposable? TryAcquire(string source, Action start, bool isRemote = false)
    {
        lock (gate)
        {
            if (disposed || owner != null) return null;
            owner = source; remote = isRemote; started = alive = now(); var id = ++generation;
            bool success = false;
            try { start(); success = true; return new Lease(this, id); }
            finally { if (!success) End(StopReason.Failure); }
        }
    }
    public void Heartbeat(string source) { lock (gate) { if (owner == source) alive = now(); } }
    public void Tick()
    {
        lock (gate)
        {
            if (owner == null) return;
            if (now() - started >= Limit.TotalSeconds) End(StopReason.Timeout);
            else if (remote && now() - alive > 1.5) End(StopReason.LostHeartbeat);
        }
    }
    public void Stop(StopReason reason = StopReason.Released) { lock (gate) End(reason); }
    void End(StopReason reason)
    {
        owner = null; generation++;
        try { stop(); }
        finally
        {
            // Notification handlers cannot prevent safety cleanup or kill the timer.
            if (Stopped != null) foreach (Action<StopReason> subscriber in Stopped.GetInvocationList()) { try { subscriber(reason); } catch { } }
        }
    }
    sealed class Lease(TransmitGuard guard, long id) : IDisposable
    {
        public void Dispose() { lock (guard.gate) { if (guard.generation == id) guard.End(StopReason.Released); } }
    }
    public void Dispose() { lock (gate) { if (disposed) return; disposed = true; try { End(StopReason.Shutdown); } finally { timer.Dispose(); } } }
}
