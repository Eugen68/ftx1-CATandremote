using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading.Channels;
using Ftx1.Protocol;
namespace Ftx1.Client;

public sealed class StationConnection : IAsyncDisposable
{
    readonly ClientSettings settings; readonly CancellationTokenSource lifetime = new(); readonly SemaphoreSlim sendGate = new(1, 1); readonly object gate = new();
    readonly Dictionary<string, object> state = []; readonly ConcurrentDictionary<long, TaskCompletionSource<Message>> requests = new();
    ClientWebSocket? socket; Task? run; Task? pulses; Task? audioSender;
    readonly Channel<AudioPacket> audioQueue = Channel.CreateBounded<AudioPacket>(new BoundedChannelOptions(3) { FullMode = BoundedChannelFullMode.DropOldest });
    public long TransmitEpoch { get; private set; }
    public event Action<AudioPacket>? AudioReceived;
    public event Action<bool>? ConnectionChanged; long sequence; long lastPing; volatile bool pressed, owns, connected; long lastReply;
    public bool Connected => connected;
    public int LatencyMs { get; private set; }
    public int TotMinutes { get; private set; } = -1;
    public bool OwnsTransmit => owns;
    /// <summary>Use the UDP audio channel when the server offers one (falls back to the WebSocket automatically).</summary>
    public bool UdpEnabled { get; set; } = true;
    UdpAudioClient? udp;
    /// <summary>"UDP" while the UDP channel works in both directions, otherwise "TCP" (audio on the WebSocket).</summary>
    public string AudioTransport => udp?.Alive == true ? "UDP" : "TCP";
    public UdpAudioClient? Udp => udp;
    public bool HeartbeatsEnabled { get; set; } = true;
    public event Action<IReadOnlyDictionary<string, object>>? Changed;
    public event Action<Message>? Received;
    public event Action<string>? Error;
    public IReadOnlyDictionary<string, object> State { get { lock (gate) return new Dictionary<string, object>(state); } }
    public StationConnection(ClientSettings settings) { this.settings = settings; ValidateSettings(settings); }
    public static void ValidateSettings(ClientSettings s) { if (!Uri.TryCreate(s.Address, UriKind.Absolute, out var u) || u.Scheme != "wss" || u.AbsolutePath != "/ws") throw new ArgumentException("InvalidServerAddress"); if (!ValidPin(s.Fingerprint)) throw new ArgumentException("InvalidFingerprint"); }
    static bool ValidPin(string pin) => pin.Length == 64 && pin.All(Uri.IsHexDigit);
    static bool Pinned(X509Certificate? cert, string pin) => cert != null && ValidPin(pin) && CryptographicOperations.FixedTimeEquals(SHA256.HashData(cert.GetRawCertData()), Convert.FromHexString(pin));
    public Task Start() { if (run != null) return Task.CompletedTask; run = Task.Run(Run); pulses = Task.Run(Pulse); audioSender = Task.Run(SendAudioLoop); return Task.CompletedTask; }
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
                pressed = owns = false; connected = true; delay = 1; TotMinutes = hello.TotMinutes ?? -1; lastReply = Stopwatch.GetTimestamp(); Apply(hello.State ?? [], true); ConnectionChanged?.Invoke(true); Received?.Invoke(hello);
                while (!lifetime.IsCancellationRequested)
                {
                    var frame = await SocketFrames.ReceiveFrame(ws, lifetime.Token); if (frame == null) break; lastReply = Stopwatch.GetTimestamp(); if (frame.Binary) { AudioReceived?.Invoke(AudioPacket.Decode(frame.Bytes)); continue; } var message = Wire.Decode(frame.Bytes);
                    switch (message.T)
                    {
                        case "state": Apply(message.Diff ?? [], false); break;
                        case "pttState": owns = message.You == true; TransmitEpoch = owns ? message.AudioEpoch ?? 0 : 0; if (!owns) pressed = false; break;
                        case "pong": if (message.Ts is { } ts) LatencyMs = (int)Math.Max(0, Environment.TickCount64 - ts); break;
                        case "tot": TotMinutes = message.TotMinutes ?? -1; break;
                        case "audioReady": await StartUdp(message); break;
                        case "error": Error?.Invoke(message.Error ?? "OperationFailed"); break;
                    }
                    if (message.RequestId is { } id && requests.TryRemove(id, out var waiter)) waiter.TrySetResult(message);
                    Received?.Invoke(message);
                }
            }
            catch (Exception e) when (e is WebSocketException or IOException or OperationCanceledException or JsonException or InvalidDataException or ObjectDisposedException) { if (!lifetime.IsCancellationRequested) Error?.Invoke("Disconnected"); }
            finally { StopUdp(); connected = pressed = owns = false; TransmitEpoch = 0; ConnectionChanged?.Invoke(false); Apply(new() { { "connected", Wire.Element(false) }, { "tx", Wire.Element(false) } }, false); foreach (var (id, tcs) in requests) if (requests.TryRemove(id, out _)) tcs.TrySetException(new IOException("Disconnected")); if (ReferenceEquals(socket, ws)) socket = null; }
            try { await Task.Delay(TimeSpan.FromSeconds(delay), lifetime.Token); delay = Math.Min(10, delay * 2); } catch (OperationCanceledException) { break; }
        }
    }
    async Task StartUdp(Message message)
    {
        StopUdp();
        if (!UdpEnabled || message.AudioAvailable != true || message.UdpPort is not > 0 || message.UdpId is not { } id || id == 0 || message.UdpKey == null || !UdpCipher.Supported) return;
        byte[] key; try { key = Convert.FromBase64String(message.UdpKey); } catch (FormatException) { return; }
        if (key.Length != UdpCipher.KeyBytes) return;
        var server = await UdpAudioClient.Resolve(new Uri(settings.Address).Host, message.UdpPort.Value, lifetime.Token); if (server == null) return;
        try { udp = new(server, key, id, packet => AudioReceived?.Invoke(packet)); }
        catch (Exception e) when (e is System.Net.Sockets.SocketException or ArgumentException) { udp = null; }
    }
    void StopUdp() => Interlocked.Exchange(ref udp, null)?.Dispose();
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
    public void QueueMicrophone(AudioPacket packet)
    {
        if (!owns || packet.Epoch != TransmitEpoch) return;
        if (udp is { Alive: true } channel && channel.Send(packet)) return;
        audioQueue.Writer.TryWrite(packet);
    }
    async Task SendAudioLoop()
    {
        try
        {
            await foreach (var packet in audioQueue.Reader.ReadAllAsync(lifetime.Token))
            {
                if (!owns || packet.Epoch != TransmitEpoch) continue;
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); timeout.CancelAfter(500);
                    await sendGate.WaitAsync(timeout.Token);
                    try { var ws = socket; if (ws?.State == WebSocketState.Open && owns && packet.Epoch == TransmitEpoch) await ws.SendAsync(packet.Encode(), WebSocketMessageType.Binary, true, timeout.Token); }
                    finally { sendGate.Release(); }
                }
                catch (Exception e) when (e is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException) { if (!lifetime.IsCancellationRequested) socket?.Abort(); }
            }
        }
        catch (OperationCanceledException) { }
    }
    async Task Pulse()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                await Task.Delay(250, lifetime.Token); if (!connected) continue;
                try { if ((owns || pressed) && HeartbeatsEnabled) await Send(new() { T = "pttHeartbeat" }); if (Environment.TickCount64 - lastPing >= 1000) { lastPing = Environment.TickCount64; await Send(new() { T = "ping", Ts = lastPing }); } if (Stopwatch.GetElapsedTime(lastReply).TotalSeconds > 5) socket?.Abort(); }
                catch (Exception e) when (e is IOException or WebSocketException or OperationCanceledException or ObjectDisposedException) { socket?.Abort(); }
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
        bool certificateSeen = false, pinMatched = false;
        using var handler = new HttpClientHandler { UseProxy = false, ServerCertificateCustomValidationCallback = (_, cert, _, _) => { certificateSeen = cert != null; pinMatched = Pinned(cert, qr.Fingerprint); return pinMatched; } };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        var address = new UriBuilder(uri) { Scheme = "https", Path = "/pair" }.Uri;
        try
        {
            using var response = await client.PostAsJsonAsync(address, new PairRequest(qr.Code, name), ProtocolJson.Default.PairRequest, token);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized) throw new InvalidOperationException("PairingCodeExpired");
            if ((int)response.StatusCode == 429) throw new InvalidOperationException("PairingRateLimited");
            response.EnsureSuccessStatusCode(); return await response.Content.ReadFromJsonAsync(ProtocolJson.Default.PairResponse, token) ?? throw new JsonException();
        }
        catch (HttpRequestException error) { throw new InvalidOperationException(certificateSeen && !pinMatched ? "PairingCertificateMismatch" : "PairingServerUnreachable", error); }
        catch (TaskCanceledException error) when (!token.IsCancellationRequested) { throw new InvalidOperationException("PairingServerUnreachable", error); }

    }
    public async ValueTask DisposeAsync()
    {
        if (lifetime.IsCancellationRequested) return;
        try { if (connected) try { await Ptt(false); } catch (Exception e) when (e is IOException or OperationCanceledException or WebSocketException or ObjectDisposedException) { } }
        finally { lifetime.Cancel(); socket?.Abort(); if (run != null) await run; if (pulses != null) await pulses; if (audioSender != null) await audioSender; }
    }
}
