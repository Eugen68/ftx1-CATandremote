using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Ftx1.Protocol;
namespace Ftx1.Client;

public sealed class StationConnection : IAsyncDisposable
{
    readonly ClientSettings settings; readonly CancellationTokenSource lifetime = new(); readonly SemaphoreSlim sendGate = new(1, 1); readonly object gate = new();
    readonly Dictionary<string, object> state = []; readonly ConcurrentDictionary<long, TaskCompletionSource<Message>> requests = new();
    ClientWebSocket? socket; Task? run; Task? pulses; long sequence; long lastPing; volatile bool pressed, owns, connected; long lastReply;
    public bool Connected => connected;
    public int LatencyMs { get; private set; }
    public int TotMinutes { get; private set; } = -1;
    public bool OwnsTransmit => owns;
    public bool HeartbeatsEnabled { get; set; } = true;
    public event Action<IReadOnlyDictionary<string, object>>? Changed;
    public event Action<Message>? Received;
    public event Action<string>? Error;
    public IReadOnlyDictionary<string, object> State { get { lock (gate) return new Dictionary<string, object>(state); } }
    public StationConnection(ClientSettings settings) { this.settings = settings; ValidateSettings(settings); }
    public static void ValidateSettings(ClientSettings s) { if (!Uri.TryCreate(s.Address, UriKind.Absolute, out var u) || u.Scheme != "wss" || u.AbsolutePath != "/ws") throw new ArgumentException("InvalidServerAddress"); if (!ValidPin(s.Fingerprint)) throw new ArgumentException("InvalidFingerprint"); }
    static bool ValidPin(string pin) => pin.Length == 64 && pin.All(Uri.IsHexDigit);
    static bool Pinned(X509Certificate? cert, string pin) => cert != null && ValidPin(pin) && CryptographicOperations.FixedTimeEquals(SHA256.HashData(cert.GetRawCertData()), Convert.FromHexString(pin));
    public Task Start() { if (run != null) return Task.CompletedTask; run = Task.Run(Run); pulses = Task.Run(Pulse); return Task.CompletedTask; }
    async Task Run()
    {
        int delay = 1;
        while (!lifetime.IsCancellationRequested)
        {
            using var ws = new ClientWebSocket(); ws.Options.RemoteCertificateValidationCallback = (_, cert, _, _) => Pinned(cert, settings.Fingerprint); socket = ws;
            try
            {
                using var connect = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); connect.CancelAfter(5000); await ws.ConnectAsync(new(settings.Address), connect.Token);
                await Send(new() { T = "hello", ProtocolVersion = ProtocolVersion.Current, DeviceToken = settings.DeviceToken }, connect.Token);
                connect.CancelAfter(Timeout.InfiniteTimeSpan);
                using var auth = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); auth.CancelAfter(5000);
                var initial = await SocketFrames.Receive(ws, auth.Token); if (initial == null) throw new IOException("Disconnected"); var hello = Wire.Decode(initial);
                if (hello.T != "helloOk") { Error?.Invoke(hello.Error ?? "AuthenticationFailed"); lifetime.Cancel(); return; }
                if (hello.ProtocolVersion != ProtocolVersion.Current) { Error?.Invoke("ProtocolMismatch"); lifetime.Cancel(); return; }
                auth.CancelAfter(Timeout.InfiniteTimeSpan);
                pressed = owns = false; connected = true; delay = 1; TotMinutes = hello.TotMinutes ?? -1; lastReply = Stopwatch.GetTimestamp(); Apply(hello.State ?? [], true); Received?.Invoke(hello);
                while (!lifetime.IsCancellationRequested)
                {
                    var bytes = await SocketFrames.Receive(ws, lifetime.Token); if (bytes == null) break; lastReply = Stopwatch.GetTimestamp(); var message = Wire.Decode(bytes);
                    switch (message.T)
                    {
                        case "state": Apply(message.Diff ?? [], false); break;
                        case "pttState": owns = message.You == true; if (!owns) pressed = false; break;
                        case "pong": if (message.Ts is { } ts) LatencyMs = (int)Math.Max(0, Environment.TickCount64 - ts); break;
                        case "tot": TotMinutes = message.TotMinutes ?? -1; break;
                        case "error": Error?.Invoke(message.Error ?? "OperationFailed"); break;
                    }
                    if (message.RequestId is { } id && requests.TryRemove(id, out var waiter)) waiter.TrySetResult(message);
                    Received?.Invoke(message);
                }
            }
            catch (Exception e) when (e is WebSocketException or IOException or OperationCanceledException or JsonException or InvalidDataException) { if (!lifetime.IsCancellationRequested) Error?.Invoke("Disconnected"); }
            finally { connected = pressed = owns = false; Apply(new() { { "connected", Wire.Element(false) }, { "tx", Wire.Element(false) } }, false); foreach (var (id, tcs) in requests) if (requests.TryRemove(id, out _)) tcs.TrySetException(new IOException("Disconnected")); if (ReferenceEquals(socket, ws)) socket = null; }
            try { await Task.Delay(TimeSpan.FromSeconds(delay), lifetime.Token); delay = Math.Min(10, delay * 2); } catch (OperationCanceledException) { break; }
        }
    }
    void Apply(Dictionary<string, JsonElement> update, bool full)
    {
        Dictionary<string, object> diff = []; lock (gate) { if (full) state.Clear(); foreach (var (k, v) in update) { var item = Wire.Object(v); if (!state.TryGetValue(k, out var old) || !Equals(old, item)) { state[k] = item; diff[k] = item; } } }
        if (diff.Count > 0) Changed?.Invoke(diff);
    }
    public async Task Send(Message message, CancellationToken token = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token); timeout.CancelAfter(1500);
        await sendGate.WaitAsync(timeout.Token); try { var ws = socket; if (ws?.State != WebSocketState.Open) throw new IOException("Disconnected"); await ws.SendAsync(Wire.Encode(message), WebSocketMessageType.Text, true, timeout.Token); } finally { sendGate.Release(); }
    }
    async Task Pulse()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                await Task.Delay(250, lifetime.Token); if (!connected) continue;
                try { if ((owns || pressed) && HeartbeatsEnabled) await Send(new() { T = "pttHeartbeat" }); if (Environment.TickCount64 - lastPing >= 1000) { lastPing = Environment.TickCount64; await Send(new() { T = "ping", Ts = lastPing }); } if (Stopwatch.GetElapsedTime(lastReply).TotalSeconds > 5) socket?.Abort(); }
                catch (Exception e) when (e is IOException or WebSocketException or OperationCanceledException) { socket?.Abort(); }
            }
        }
        catch (OperationCanceledException) { }
    }
    public async Task Ptt(bool on) { if (on && !connected) throw new IOException("Disconnected"); pressed = on; try { await Send(new() { T = "pttRequest", On = on }); } catch { pressed = false; socket?.Abort(); throw; } }
    public void Abort() => socket?.Abort();
    public async Task<Message> Request(Message m)
    {
        long id = Interlocked.Increment(ref sequence); var waiter = new TaskCompletionSource<Message>(TaskCreationOptions.RunContinuationsAsynchronously); requests[id] = waiter;
        try { await Send(m with { RequestId = id }); var reply = await waiter.Task.WaitAsync(TimeSpan.FromSeconds(4), lifetime.Token); if (reply.T == "error") throw new InvalidOperationException(reply.Error); return reply; } finally { requests.TryRemove(id, out _); }
    }
    public static async Task<PairResponse> Pair(PairingQr qr, string name, CancellationToken token = default)
    {
        if (!ValidPin(qr.Fingerprint) || !Uri.TryCreate(qr.Address, UriKind.Absolute, out var uri) || uri.Scheme != "wss" || qr.ProtocolVersion != ProtocolVersion.Current) throw new ArgumentException("InvalidPairing");
        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, cert, _, _) => Pinned(cert, qr.Fingerprint) }; using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var address = new UriBuilder(uri) { Scheme = "https", Path = "/pair" }.Uri;
        using var response = await client.PostAsJsonAsync(address, new PairRequest(qr.Code, name), ProtocolJson.Default.PairRequest, token); response.EnsureSuccessStatusCode(); return await response.Content.ReadFromJsonAsync(ProtocolJson.Default.PairResponse, token) ?? throw new JsonException();
    }
    public async ValueTask DisposeAsync()
    {
        if (lifetime.IsCancellationRequested) return;
        try { if (connected) try { await Ptt(false); } catch (Exception e) when (e is IOException or OperationCanceledException or WebSocketException) { } }
        finally { lifetime.Cancel(); socket?.Abort(); if (run != null) await run; if (pulses != null) await pulses; }
    }
}
