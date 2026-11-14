using System.Globalization;
using System.Text;
using System.Threading.Channels;
using Ftx1.Core.Safety;
namespace Ftx1.Core.Cat;

public sealed class Rig : IDisposable, IAsyncDisposable
{
    readonly ITransport transport;
    readonly IPttLine? pttLine;
    readonly string pttMethod;
    readonly object gate = new(), writeGate = new();
    readonly Dictionary<string, object> state = new() { ["connected"] = false };
    readonly Dictionary<string, string> freqPend = new();
    readonly Dictionary<string, int> unsupported = new();
    readonly Dictionary<string, List<TaskCompletionSource<string?>>> menuWaiters = new();
    readonly Channel<object> queue = Channel.CreateUnbounded<object>(new UnboundedChannelOptions { SingleReader = true });
    readonly CancellationTokenSource cancel = new();
    Task? worker;
    TaskCompletionSource<string?>? pending;
    string pendingPrefix = "", buffer = "";
    double lastRx, freqTime;
    int slowIndex;
    bool started, closed;
    static double Now => (double)System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency;
    public event Action<IReadOnlyDictionary<string, object>>? Changed;
    public event Action<string>? Raw;
    public event Action<Exception>? Error;
    public TransmitGuard Transmit { get; }
    public IReadOnlyDictionary<string, object> State { get { lock (gate) return new Dictionary<string, object>(state); } }
    public Rig(ITransport transport, string pttMethod = "TX1", IPttLine? pttLine = null, TimeSpan? maxTx = null)
    {
        this.transport = transport; this.pttMethod = pttMethod; this.pttLine = pttLine;
        Transmit = new TransmitGuard(EmergencyStop, maxTx);
    }
    public Task Start()
    {
        lock (gate) { if (closed) throw new ObjectDisposedException(nameof(Rig)); if (started) return Task.CompletedTask; started = true; }
        try
        {
            transport.Open(OnBytes); lastRx = Now; Set(new Dictionary<string, object> { { "connected", true } });
            foreach (var c in new[] { "ID;", "VE0;", "VE1;", "VE2;", "VE3;", "VE4;", "VE5;", "AI1;" }) Enqueue(c);
            worker = Task.Run(Worker);
        }
        catch { Dispose(); throw; }
        return Task.CompletedTask;
    }
    void Set(IReadOnlyDictionary<string, object> changes)
    {
        Dictionary<string, object> diff = [];
        lock (gate) foreach (var (key, v) in changes) if (!state.TryGetValue(key, out var old) || !Equals(old, v)) { state[key] = v; diff[key] = v; }
        if (diff.Count > 0) Changed?.Invoke(diff);
    }
    void OnBytes(byte[]? bytes)
    {
        if (closed) return;
        if (bytes is null) { Transmit.Stop(StopReason.Disconnected); Set(new Dictionary<string, object> { { "connected", false } }); return; }
        List<string> frames = [];
        lock (gate)
        {
            lastRx = Now; buffer += Encoding.ASCII.GetString(bytes);
            int i; while ((i = buffer.IndexOf(';')) >= 0) { var frame = buffer[..i].Trim(); buffer = buffer[(i + 1)..]; if (frame.Length > 0) frames.Add(frame); }
            if (buffer.Length > 512) buffer = "";
        }
        foreach (var f in frames) OnFrame(f);
    }
    void OnFrame(string frame)
    {
        Raw?.Invoke("<" + frame + ";");
        TaskCompletionSource<string?>? reply = null;
        List<TaskCompletionSource<string?>>? menus = null;
        lock (gate)
        {
            if (frame.StartsWith("EX", StringComparison.Ordinal) && frame.Length >= 8) menuWaiters.Remove(frame.Substring(2, 6), out menus);
            if (pending != null && (frame == "?" || frame.StartsWith(pendingPrefix, StringComparison.Ordinal))) reply = pending;
        }
        if (menus != null) foreach (var waiter in menus) waiter.TrySetResult(frame[8..]);
        Set(CatProtocol.ParseFrame(frame)); reply?.TrySetResult(frame);
    }
    static string Normalize(string c) => c.EndsWith(';') ? c : c + ";";
    void Enqueue(string c) { lock (gate) { if (!closed) queue.Writer.TryWrite(Normalize(c)); } }
    public static bool CanStartTx(string c)
    {
        c = c.Trim().TrimEnd(';');
        return (c.StartsWith("TX", StringComparison.Ordinal) && c.Length > 2 && c != "TX0") || (c.StartsWith("VX", StringComparison.Ordinal) && c.Length > 2 && c != "VX0") || (c.StartsWith("KY0", StringComparison.Ordinal) && c != "KY00" && c.Length > 3)
            || (c.StartsWith("PB0", StringComparison.Ordinal) && c.Length > 3) || (c.StartsWith("AC", StringComparison.Ordinal) && c.Length == 5 && c[^1] is '1' or '2' or '3');
    }
    public void Send(string commands)
    {
        var parts = commands.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // A compound console command cannot smuggle a TX command past the guard.
        if (parts.Any(CanStartTx)) throw new InvalidOperationException("UnsafeRawTransmit");
        if (parts.Any(c => c is "TX0" or "KY00")) Transmit.Stop();
        foreach (var c in parts) Enqueue(c);
    }
    void Write(string cmd) { lock (writeGate) { if (closed) return; Raw?.Invoke(">" + cmd); transport.Write(Encoding.ASCII.GetBytes(cmd)); } }
    public void EmergencyStop()
    {
        lock (writeGate)
        {
            try { transport.Write(Encoding.ASCII.GetBytes("KY00;TX0;")); }
            catch (Exception e) { Error?.Invoke(e); }
            finally { try { pttLine?.Set(false); } catch (Exception e) { Error?.Invoke(e); } }
        }
        Set(new Dictionary<string, object> { { "tx", false } });
    }
    public IDisposable? Ptt(string source, bool remote = false)
    {
        if (closed || !State.TryGetValue("connected", out var c) || !Equals(c, true)) return null;
        return Transmit.TryAcquire(source, () =>
        {
            if (pttMethod is "RTS" or "DTR")
            {
                if (pttLine is null) throw new InvalidOperationException("MissingPttPort");
                pttLine.Set(true);
            }
            else Write(pttMethod == "TX2" ? "TX2;" : "TX1;");
            Enqueue("TX;");
        }, remote);
    }
    sealed record Sequence(List<string> Commands, TaskCompletionSource<List<string?>> Completion);
    public async Task<List<string?>> ExecSeq(IEnumerable<string> commands, TimeSpan? timeout = null)
    {
        var seq = new Sequence(commands.Select(Normalize).ToList(), new(TaskCreationOptions.RunContinuationsAsynchronously));
        if (seq.Commands.Any(CanStartTx)) throw new InvalidOperationException("UnsafeRawTransmit");
        lock (gate) { ObjectDisposedException.ThrowIf(closed, this); queue.Writer.TryWrite(seq); }
        return await seq.Completion.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(20), cancel.Token);
    }
    async Task<string?> Xfer(string cmd)
    {
        TaskCompletionSource<string?>? tcs = null;
        if (CatProtocol.IsQuery(cmd)) { tcs = new(TaskCreationOptions.RunContinuationsAsynchronously); lock (gate) { pending = tcs; pendingPrefix = cmd[..2]; } }
        try
        {
            try { Write(cmd); }
            catch (Exception e) { Transmit.Stop(StopReason.Failure); Set(new Dictionary<string, object> { { "connected", false } }); Error?.Invoke(e); await Task.Delay(1000, cancel.Token); return null; }
            if (tcs == null) { await Task.Delay(12, cancel.Token); return null; }
            try { return await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(250), cancel.Token); } catch (TimeoutException) { return null; }
        }
        finally { if (tcs != null) lock (gate) { if (ReferenceEquals(pending, tcs)) pending = null; } }
    }
    async Task Poll(string cmd)
    {
        var result = await Xfer(cmd);
        lock (gate) { if (result == "?") unsupported[cmd] = unsupported.GetValueOrDefault(cmd) + 1; else if (result != null) unsupported.Remove(cmd); }
    }
    public int UnsupportedCount(string cmd) { lock (gate) return unsupported.GetValueOrDefault(cmd); }
    async Task Worker()
    {
        double medium = 0;
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                try
                {
                    if (transport.Failed)
                    {
                        Transmit.Stop(StopReason.Disconnected);
                        await Task.Delay(3000, cancel.Token);
                        try { transport.Reopen(); EmergencyStop(); Enqueue("AI1;"); } catch (Exception e) { Error?.Invoke(e); }
                        continue;
                    }
                    while (queue.Reader.TryRead(out var item))
                    {
                        if (item is Sequence seq)
                        {
                            List<string?> results = [];
                            try { foreach (var c in seq.Commands) { if (c.StartsWith('~')) { await Task.Delay(int.Parse(c[1..^1], CultureInfo.InvariantCulture), cancel.Token); results.Add(null); } else results.Add(await Xfer(c)); } seq.Completion.TrySetResult(results); }
                            catch (Exception e) { seq.Completion.TrySetException(e); throw; }
                        }
                        else
                        {
                            var c = (string)item;
                            if (c.StartsWith('@'))
                            {
                                var key = c[1..^1]; string? val;
                                lock (gate) freqPend.Remove(key, out val);
                                if (val != null) { await Xfer(val); await Xfer(key == "freq_a" ? "FA;" : "FB;"); freqTime = Now; }
                            }
                            else await Xfer(c);
                        }
                    }
                    foreach (var c in (State.GetValueOrDefault("tx") is true ? CatProtocol.FastTx : CatProtocol.FastRx)) await Poll(c);
                    if (queue.Reader.TryPeek(out _)) continue;
                    if (Now - medium > 0.3)
                    {
                        medium = Now;
                        foreach (var c in CatProtocol.Medium)
                        {
                            bool skip; lock (gate) skip = (c is "FA;" or "FB;") && (freqPend.Count > 0 || Now - freqTime < 0.5);
                            if (!skip) await Poll(c);
                        }
                    }
                    for (int n = 0; n < 2; n++)
                    {
                        var cmd = CatProtocol.Slow[slowIndex++ % CatProtocol.Slow.Length].Replace("{band}", CatProtocol.BandType(Convert.ToInt64(State.GetValueOrDefault("freq_a", 0), CultureInfo.InvariantCulture)).ToString(CultureInfo.InvariantCulture));
                        if (UnsupportedCount(cmd) < 3) await Poll(cmd);
                    }
                    await Task.Delay(30, cancel.Token);
                    var age = Now - lastRx;
                    if (age > 3) { Transmit.Stop(StopReason.Disconnected); Set(new Dictionary<string, object> { { "connected", false } }); }
                    else if (age < 1) Set(new Dictionary<string, object> { { "connected", true } });
                }
                catch (OperationCanceledException) when (cancel.IsCancellationRequested) { break; }
                catch (Exception e) { Transmit.Stop(StopReason.Failure); Error?.Invoke(e); await Task.Delay(500, cancel.Token); }
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        finally
        {
            EmergencyStop();
            while (queue.Reader.TryRead(out var item)) if (item is Sequence seq) seq.Completion.TrySetCanceled();
        }
    }
    public void Refresh(double delay = 0) => _ = RefreshCore(delay);
    async Task RefreshCore(double delay)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(delay), cancel.Token); var band = CatProtocol.BandType(Convert.ToInt64(State.GetValueOrDefault("freq_a", 0), CultureInfo.InvariantCulture)).ToString(CultureInfo.InvariantCulture);
            foreach (var c in new[] { "FA;", "FB;", "MD0;" }.Concat(CatProtocol.Slow)) { var cmd = c.Replace("{band}", band); if (UnsupportedCount(cmd) < 3) Enqueue(cmd); }
        }
        catch (OperationCanceledException) { }
    }
    public async Task<string?> MenuGet(string ex)
    {
        if (ex.Length != 6 || !ex.All(char.IsAsciiDigit)) throw new ArgumentException("EX number", nameof(ex));
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate) { if (!menuWaiters.TryGetValue(ex, out var list)) menuWaiters[ex] = list = []; list.Add(tcs); }
        Enqueue("EX" + ex + ";");
        try { return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1.5), cancel.Token); }
        catch (TimeoutException) { return null; }
        finally { lock (gate) { if (menuWaiters.TryGetValue(ex, out var list)) { list.Remove(tcs); if (list.Count == 0) menuWaiters.Remove(ex); } } }
    }
    public async Task<string?> MenuSet(string ex, object value, bool signed = false, int? digits = null)
    {
        var cur = await MenuGet(ex); var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
        {
            int width = cur != null ? Math.Max(1, cur.TrimStart('+', '-').Length) : digits ?? 1;
            signed |= cur?.StartsWith('+') == true || cur?.StartsWith('-') == true;
            text = (signed ? (n >= 0 ? "+" : "-") : n < 0 ? "-" : "") + Math.Abs(n).ToString(CultureInfo.InvariantCulture).PadLeft(!signed && n < 0 ? Math.Max(0, width - 1) : width, '0');
        }
        if (text.Contains(';')) throw new ArgumentException("Invalid menu value", nameof(value));
        Enqueue("EX" + ex + text + ";"); await Task.Delay(100, cancel.Token); return await MenuGet(ex);
    }
    public List<string> SetParam(string key, object value)
    {
        var snapshot = State; var cmds = CatCommands.SetParam(key, value, snapshot);
        if (key is "freq_a" or "freq_b")
        {
            if (cmds.Count > 0) lock (gate)
                {
                    if (closed) return [];
                    if (!freqPend.ContainsKey(key)) queue.Writer.TryWrite("@" + key + ";");
                    freqPend[key] = cmds[0];
                }
            Set(new Dictionary<string, object> { { key, CatCommands.Clamp(value, 30000, 470000000) } });
            if (key == "freq_a" && CatProtocol.BandType(CatCommands.Clamp(value, 30000, 470000000)) != CatProtocol.BandType(Convert.ToInt64(snapshot.GetValueOrDefault("freq_a", 0), CultureInfo.InvariantCulture))) Refresh(0.4);
            return cmds;
        }
        foreach (var c in cmds) Send(c);
        if (cmds.Count > 0) { Enqueue(CatCommands.Readback(key, cmds[0])); if (key == "mode") Refresh(0.3); }
        return cmds;
    }
    public async Task<List<string>> Action(string name, object? arg = null, string? source = null, bool remote = false)
    {
        var commands = CatCommands.Action(name, arg, State);
        if (commands.Any(CanStartTx))
        {
            var owner = source ?? name;
            var lease = Transmit.TryAcquire(owner, () => { foreach (var c in commands) Write(c); }, remote);
            if (lease == null) throw new InvalidOperationException("TransmitBusy");
            try
            {
                Enqueue("TX;"); Enqueue("AC;");
                await Task.Delay(300, cancel.Token);
                while (Transmit.Owner == owner && (State.GetValueOrDefault("tx") is true || State.GetValueOrDefault("tuning") is true)) await Task.Delay(100, cancel.Token);
            }
            finally { lease.Dispose(); }
        }
        else foreach (var c in commands) Send(c);
        if (commands.Count > 0) { foreach (var c in new[] { "FA;", "FB;", "MD0;", "AC;" }) Enqueue(c); if (new[] { "band", "band_up", "band_down", "mem_ch", "vfo_mode", "mem_mode", "qmb_recall", "swap", "b_to_a" }.Contains(name)) Refresh(0.4); }
        return commands;
    }
    public void Dispose() { DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    public async ValueTask DisposeAsync()
    {
        lock (gate) { if (closed) return; closed = true; }
        try { cancel.Cancel(); queue.Writer.TryComplete(); Transmit.Dispose(); if (worker != null) await worker.ConfigureAwait(false); }
        finally { try { EmergencyStop(); } finally { try { transport.Close(); } finally { pttLine?.Dispose(); Set(new Dictionary<string, object> { { "connected", false } }); } } }
    }
}
