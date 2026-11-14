using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Ftx1.Audio;
using Ftx1.Core;
using Ftx1.Core.Cat;
using Ftx1.Core.Safety;
using Ftx1.Protocol;
using Ftx1.Sim;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
namespace Ftx1.Server;

public sealed record ServerOptions(string Root, StationConfig Station, int Port = 8443, IPAddress[]? Addresses = null, bool AllowOtherInterfaces = false, double SwrLimit = 3, string? AudioInput = null, string? AudioOutput = null);
public sealed class StationServer : IAsyncDisposable
{
    public const string Version = "0.3.0";
    readonly StationAudio audio; long audioEpoch;
    readonly ServerOptions options; readonly Rig rig; readonly IDisposable? simulator;
    readonly CancellationTokenSource lifetime = new(); readonly ConcurrentDictionary<string, Session> sessions = new(); readonly AttemptLimiter limiter = new();
    readonly object auditGate = new(); TxAudit? activeTx; long txStarted, highSwrSince;
    WebApplication? app; Task? monitor; Task? audioPlayback; bool disposed;
    public DeviceTokens Tokens { get; }
    public PairingCodes Pairing { get; }
    public ServerCertificate Certificate { get; }
    public IPAddress[]? ListeningAddresses { get; private set; }
    public Uri? Address { get; private set; }
    public int TotMinutes { get; private set; } = -1;
    public Rig Station => rig;
    public event Action? DevicesChanged;
    public event Action<bool, string>? AudioStatusChanged;
    public string LastAudioStatus { get; private set; } = "";
    public void CheckAudio() { try { audio.Start(); AudioStatus(true, audio.Description); } catch (Exception error) { AudioStatus(false, AudioDiagnostic.Describe(error)); throw; } }
    void AudioStatus(bool ready, string detail) { LastAudioStatus = detail; AudioStatusChanged?.Invoke(ready, detail); }
    public StationServer(ServerOptions options, ITransport? transport = null, Func<IVoiceDevice>? audioFactory = null)
    {
        this.options = options; options.Station.Validate(); Directory.CreateDirectory(options.Root);
        Tokens = new(options.Root); Pairing = new(Tokens); Certificate = new(options.Root);
        IPttLine? line = null;
        if (transport == null) { if (options.Station.Simulator) { var radio = new SimRadio(); simulator = radio; transport = new SimTransport(radio); } else { transport = new SerialTransport(options.Station.CatPort, options.Station.CatBaud); if (options.Station.PttMethod is "RTS" or "DTR") line = new SerialPtt(options.Station.PttPort, options.Station.PttMethod); } }
        audio = new(audioFactory ?? (() => options.Station.Simulator ? new SimVoiceDevice() : AudioDevices.Create(true, options.AudioInput, options.AudioOutput)));
        audio.Encoded += (bitrate, seq, stamp, payload) => { foreach (var session in sessions.Values) if (session.AudioEnabled && session.Bitrate == bitrate) session.SendAudio(new(ProtocolVersion.Audio, session.ReceiveEpoch, seq, stamp, payload)); };
        rig = new(transport, options.Station.Simulator ? "TX1" : options.Station.PttMethod, line, TimeSpan.FromSeconds(options.Station.MaxTxSeconds));
        audio.Failed += error => { rig.Transmit.Stop(StopReason.Failure); var detail = AudioDiagnostic.Describe(error); AudioStatus(false, detail); Broadcast(new() { T = "error", Error = "ServerAudioUnavailable", Detail = detail }); };
        rig.Changed += diff => Broadcast(new() { T = "state", Diff = Wire.State(diff) });
        rig.Raw += line => Broadcast(new() { T = "catLine", Command = line });
        rig.Transmit.Started += OnTxStarted;
        rig.Transmit.Stopped += reason => { try { FinishAudit(reason); } catch (IOException) { Broadcast(new() { T = "error", Error = "AuditWriteFailed" }); } finally { foreach (var session in sessions.Values) session.ResetTransmit(); audio.Clear(); BroadcastPtt(reason.ToString()); } };
        Tokens.Revoked += id => { foreach (var s in sessions.Values.Where(s => s.Device.Id == id)) { StopOwner(s, StopReason.Revoked); s.Abort(); } DevicesChanged?.Invoke(); };
    }
    public async Task Start(CancellationToken token = default)
    {
        var addresses = options.Addresses ?? ListenPolicy.DefaultAddresses(); ListenPolicy.Validate(addresses, options.AllowOtherInterfaces); ListeningAddresses = addresses;
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => { k.Limits.MaxRequestBodySize = 4096; foreach (var ip in addresses) k.Listen(ip, options.Port, l => l.UseHttps(Certificate.Certificate)); });
        app = builder.Build(); app.UseWebSockets(new() { KeepAliveInterval = TimeSpan.FromSeconds(15) });
        app.MapPost("/pair", async context =>
        {
            var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (!limiter.Allowed(ip)) { context.Response.StatusCode = 429; return; }
            try
            {
                var request = await JsonSerializer.DeserializeAsync(context.Request.Body, ProtocolJson.Default.PairRequest, context.RequestAborted);
                if (request == null || request.ProtocolVersion != ProtocolVersion.Current || string.IsNullOrWhiteSpace(request.Code) || string.IsNullOrWhiteSpace(request.DeviceName)) { context.Response.StatusCode = 400; return; }
                var response = Pairing.Consume(request.Code, request.DeviceName);
                if (response == null) { limiter.Fail(ip); context.Response.StatusCode = 401; return; }
                context.Response.ContentType = "application/json"; await JsonSerializer.SerializeAsync(context.Response.Body, response, ProtocolJson.Default.PairResponse, context.RequestAborted); DevicesChanged?.Invoke();
            }
            catch (JsonException) { context.Response.StatusCode = 400; }
        });
        app.Map("/ws", HandleSocket);
        try
        {
            await rig.Start(); await app.StartAsync(token);
            var urls = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
            var first = urls.First(u => u.Contains("127.0.0.1", StringComparison.Ordinal)); Address = new Uri(first.Replace("https:", "wss:", StringComparison.Ordinal) + "/ws");
            await ReadTot(); EnsureLocalDesktopToken(); monitor = Task.Run(Monitor); audioPlayback = Task.Run(PlayMicrophone);
        }
        catch { await DisposeAsync(); throw; }
    }
    void EnsureLocalDesktopToken()
    {
        var file = Path.Combine(options.Root, "desktop-connection.json");
        if (File.Exists(file)) { try { var old = JsonSerializer.Deserialize(File.ReadAllBytes(file), ProtocolJson.Default.ClientSettings); if (old != null && Tokens.Authenticate(old.DeviceToken) != null) { PrivateFiles.Write(file, JsonSerializer.SerializeToUtf8Bytes(old with { Address = Address!.ToString(), Fingerprint = Certificate.Fingerprint }, ProtocolJson.Default.ClientSettings)); return; } } catch (JsonException) { } }
        var pair = Tokens.Create("LocalDesktop"); var cfg = new ClientSettings(Address!.ToString(), Certificate.Fingerprint, pair.DeviceToken); PrivateFiles.Write(file, JsonSerializer.SerializeToUtf8Bytes(cfg, ProtocolJson.Default.ClientSettings));
    }
    public string CreateQr()
    {
        if (Address == null) throw new InvalidOperationException("ServerNotStarted");
        var ip = (options.Addresses ?? ListenPolicy.DefaultAddresses()).FirstOrDefault(ListenPolicy.IsTailscale) ?? IPAddress.Loopback;
        var uri = new UriBuilder(Address) { Host = ip.ToString() }.Uri;
        return JsonSerializer.Serialize(new PairingQr(uri.ToString(), Certificate.Fingerprint, Pairing.Issue()), ProtocolJson.Default.PairingQr);
    }
    async Task ReadTot()
    {
        var value = await rig.MenuGet("030112"); TotMinutes = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : -1;
        Broadcast(new() { T = "tot", TotMinutes = TotMinutes });
    }
    async Task HandleSocket(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (!limiter.Allowed(ip)) { context.Response.StatusCode = 429; return; }
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lifetime.Token); deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var first = await SocketFrames.Receive(socket, deadline.Token); if (first == null) return; var hello = Wire.Decode(first);
            if (hello.T != "hello" || hello.ProtocolVersion != ProtocolVersion.Current) { await Reject(socket, "ProtocolMismatch"); return; }
            var device = Tokens.Authenticate(hello.DeviceToken);
            if (device == null) { limiter.Fail(ip); await Reject(socket, "AuthenticationFailed"); return; }
            // A reconnect starts with a new session and can never inherit PTT.
            foreach (var old in sessions.Values.Where(s => s.Device.Id == device.Id)) { StopOwner(old, StopReason.Disconnected); old.Abort(); }
            var session = new Session(socket, device, lifetime.Token);
            sessions[session.Id] = session; DevicesChanged?.Invoke();
            session.Send(new() { T = "helloOk", ProtocolVersion = ProtocolVersion.Current, ServerVersion = Version, State = Wire.State(rig.State), TotMinutes = TotMinutes }); BroadcastPtt();
            try { await session.Run(message => Dispatch(session, message), packet => Microphone(session, packet), () => StopOwner(session, StopReason.Disconnected)); }
            finally { StopOwner(session, StopReason.Disconnected); sessions.TryRemove(session.Id, out _); session.Abort(); DevicesChanged?.Invoke(); }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or JsonException or InvalidDataException or ObjectDisposedException) { socket.Abort(); }
    }
    static async Task Reject(WebSocket socket, string reason)
    {
        using var timeout = new CancellationTokenSource(1000);
        try { await socket.SendAsync(Wire.Encode(new() { T = "authFail", Error = reason }), WebSocketMessageType.Text, true, timeout.Token); await socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, reason, timeout.Token); } catch (WebSocketException) { } catch (OperationCanceledException) { }
    }
    void StopOwner(Session s, StopReason reason) { if (rig.Transmit.Owner == s.Id) rig.Transmit.Stop(reason); }
    Task Dispatch(Session s, Message m)
    {
        try
        {
            switch (m.T)
            {
                case "ping": s.Send(new() { T = "pong", Ts = m.Ts }); break;
                case "pttHeartbeat": rig.Transmit.Heartbeat(s.Id); break;
                case "audioConfig":
                    if (rig.Transmit.Owner == s.Id) rig.Transmit.Stop(StopReason.Released);
                    s.AudioEnabled = false; s.ResetTransmit();
                    if (m.On == true) { int bitrate = m.Bitrate ?? 24000; if (bitrate is not (12000 or 24000 or 32000)) throw new ArgumentException("InvalidBitrate"); try { audio.Start(); AudioStatus(true, audio.Description); } catch (Exception error) { var detail = AudioDiagnostic.Describe(error); AudioStatus(false, detail); s.Send(new() { T = "error", Error = "ServerAudioUnavailable", Detail = detail }); break; } s.Bitrate = bitrate; s.ReceiveEpoch = Interlocked.Increment(ref audioEpoch); s.Send(new() { T = "audioReady", AudioAvailable = true, AudioEpoch = s.ReceiveEpoch, Bitrate = bitrate }); s.AudioEnabled = true; }
                    else s.Send(new() { T = "audioReady", AudioAvailable = false });
                    break;
                case "pttRequest":
                    if (m.On == true) { if (!s.AudioEnabled || !audio.Ready) throw new InvalidOperationException("AudioRequired"); if (s.Lease != null && rig.Transmit.Owner == s.Id) { rig.Transmit.Heartbeat(s.Id); break; } s.Lease?.Dispose(); s.Lease = rig.Ptt(s.Id, true); if (s.Lease == null) s.Send(new() { T = "error", Error = "TransmitBusy" }); }
                    else { StopOwner(s, StopReason.Released); s.Lease?.Dispose(); s.Lease = null; }
                    BroadcastPtt(); break;
                case "set":
                    if (m.Key == null || m.Value == null) throw new JsonException();
                    if (rig.Transmit.Owner != null && rig.Transmit.Owner != s.Id) throw new InvalidOperationException("TransmitBusy");
                    rig.SetParam(m.Key, Wire.Object(m.Value.Value)); break;
                case "act": s.Track(ExecuteAction(s, m)); break;
                case "cat": rig.Send(m.Command ?? ""); break;
                case "menuGet": s.Track(Menu(s, m, false)); break;
                case "menuSet": s.Track(Menu(s, m, true)); break;
                case "setTot": s.Track(SetTot(s)); break;
                default: s.Send(new() { T = "error", Error = "UnsupportedMessage" }); break;
            }
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or JsonException or FormatException) { StopOwner(s, StopReason.Failure); s.Send(new() { T = "error", Error = e is JsonException ? "InvalidMessage" : e.Message, RequestId = m.RequestId }); }
        return Task.CompletedTask;
    }
    async Task ExecuteAction(Session s, Message m)
    {
        try { if (rig.Transmit.Owner != null && rig.Transmit.Owner != s.Id) throw new InvalidOperationException("TransmitBusy"); await rig.Action(m.Name ?? "", m.Arg is { } a ? Wire.Object(a) : null, s.Id, true); }
        catch (Exception e) { StopOwner(s, StopReason.Failure); s.Send(new() { T = "error", Error = e is InvalidOperationException ? e.Message : "OperationFailed" }); }
    }
    async Task Menu(Session s, Message m, bool write)
    {
        try
        {
            if (write && rig.Transmit.Owner != null) throw new InvalidOperationException("TransmitBusy");
            var result = write ? await rig.MenuSet(m.Ex ?? "", m.Value is { } v ? Wire.Object(v) : "", m.Signed ?? false, m.Digits) : await rig.MenuGet(m.Ex ?? ""); s.Send(new() { T = "menuResult", RequestId = m.RequestId, Result = result });
            if (write && m.Ex == "030112") await ReadTot();
        }
        catch (Exception) { StopOwner(s, StopReason.Failure); s.Send(new() { T = "error", Error = "MenuFailed", RequestId = m.RequestId }); }
    }
    async Task SetTot(Session s) { if (rig.Transmit.Owner != null) throw new InvalidOperationException("TransmitBusy"); await rig.MenuSet("030112", 3, false, 2); await ReadTot(); s.Send(new() { T = "tot", TotMinutes = TotMinutes }); }
    void Microphone(Session s, AudioPacket packet)
    {
        if (packet.Type != ProtocolVersion.Microphone || !s.AudioEnabled) return;
        rig.Transmit.WithOwner(s.Id, () =>
        {
            if (packet.Epoch != s.TransmitEpoch || (s.LastMicSequence is { } last && packet.Sequence <= last)) return;
            // Reject TCP catch-up bursts whose sender timestamp trails the latest accepted frame by >120 ms.
            if (s.LastMicTimestamp != 0 && (packet.Timestamp < s.LastMicTimestamp || packet.Timestamp - s.LastMicTimestamp > 1000)) { rig.Transmit.Stop(StopReason.LostAudio); return; }
            try { var pcm = s.Decoder.Decode(packet.Payload); s.LastMicSequence = packet.Sequence; s.LastMicTimestamp = packet.Timestamp; s.Microphone.Push(new(packet.Sequence, packet.Timestamp, packet.Payload)); rig.Transmit.AudioHeartbeat(s.Id); }
            catch (InvalidDataException) { rig.Transmit.Stop(StopReason.Failure); }
        });
    }
    void Broadcast(Message m) { foreach (var s in sessions.Values) s.Send(m); }
    void BroadcastPtt(string? reason = null) { var owner = rig.Transmit.Owner; foreach (var s in sessions.Values) s.Send(new() { T = "pttState", Owner = owner ?? "", You = owner == s.Id, Reason = reason, AudioEpoch = owner == s.Id ? s.TransmitEpoch : null }); }
    void OnTxStarted(string owner)
    {
        if (sessions.TryGetValue(owner, out var session)) { session.ResetTransmit(); session.TransmitEpoch = Interlocked.Increment(ref audioEpoch); audio.Clear(); rig.Transmit.RequireAudio(owner); }
        lock (auditGate) { var device=sessions.GetValueOrDefault(owner)?.Device; activeTx = new(device?.Id??owner, device?.Name??owner, DateTimeOffset.UtcNow, Convert.ToInt64(rig.State.GetValueOrDefault("freq_a", 0), CultureInfo.InvariantCulture), 0, ""); txStarted = Stopwatch.GetTimestamp(); }
        BroadcastPtt();
    }
    void FinishAudit(StopReason reason)
    {
        lock (auditGate) { if (activeTx == null) return; var record = activeTx with { Duration = Stopwatch.GetElapsedTime(txStarted).TotalSeconds, Reason = reason.ToString() }; activeTx = null; File.AppendAllText(Path.Combine(options.Root, "transmit.jsonl"), JsonSerializer.Serialize(record, ProtocolJson.Default.TxAudit) + "\n"); }
    }
    async Task PlayMicrophone()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
        try { while (await timer.WaitForNextTickAsync(lifetime.Token)) { var owner = rig.Transmit.Owner; if (owner != null && sessions.TryGetValue(owner, out var s)) rig.Transmit.WithOwner(owner, () => { if (s.Microphone.Pull() is { } payload) { try { audio.Play(s.Playout.Decode(payload)); } catch { rig.Transmit.Stop(StopReason.Failure); } } }); } }
        catch (OperationCanceledException) { }
    }
    async Task Monitor()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                await Task.Delay(100, lifetime.Token); var state = rig.State; bool tx = rig.Transmit.Owner != null;
                var swr = RadioControls.Meter(Convert.ToDouble(state.GetValueOrDefault("swr", 0), CultureInfo.InvariantCulture), RadioControls.SwrCal);
                if (tx && swr > options.SwrLimit) { if (highSwrSince == 0) highSwrSince = Stopwatch.GetTimestamp(); if (Stopwatch.GetElapsedTime((long)highSwrSince).TotalSeconds >= 0.5) rig.Transmit.Stop(StopReason.HighSwr); } else highSwrSince = 0;
            }
        }
        catch (OperationCanceledException) { }
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return; disposed = true;
        try { rig.Transmit.Stop(StopReason.Shutdown); lifetime.Cancel(); foreach (var s in sessions.Values) s.Abort(); if (monitor != null) await monitor; if (audioPlayback != null) await audioPlayback; audio.Dispose(); }
        finally { try { if (app != null) { using var timeout = new CancellationTokenSource(3000); try { await app.StopAsync(timeout.Token); } catch (OperationCanceledException) { } await app.DisposeAsync(); } } finally { try { await rig.DisposeAsync(); } finally { try { simulator?.Dispose(); } finally { Certificate.Certificate.Dispose(); } } } }
    }
    sealed class Session
    {
        readonly WebSocket socket; readonly CancellationTokenSource cancel; readonly Channel<Message> queue = Channel.CreateBounded<Message>(256); readonly ConcurrentDictionary<Task, byte> tasks = []; int aborted;
        public string Id { get; } = Guid.NewGuid().ToString("N"); public DeviceRecord Device { get; }
        public IDisposable? Lease;
        public volatile bool AudioEnabled; public int Bitrate = 24000; public long ReceiveEpoch, TransmitEpoch;
        public readonly VoiceCodec Decoder = new(), Playout = new(); public readonly JitterBuffer Microphone = new();
        public uint? LastMicSequence; public long LastMicTimestamp;
        readonly Channel<byte[]> audioQueue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(3) { FullMode = BoundedChannelFullMode.DropOldest });
        public void ResetTransmit() { TransmitEpoch = 0; LastMicSequence = null; LastMicTimestamp = 0; Microphone.Clear(); Decoder.Reset(); Playout.Reset(); }
        public void SendAudio(AudioPacket packet) { audioQueue.Writer.TryWrite(packet.Encode()); }

        public Session(WebSocket socket, DeviceRecord device, CancellationToken token) { this.socket = socket; Device = device; cancel = CancellationTokenSource.CreateLinkedTokenSource(token); }
        public void Send(Message m) { if (!queue.Writer.TryWrite(m)) Abort(); }
        public void Track(Task task) { tasks[task] = 0; _ = Observe(task); }
        async Task Observe(Task task) { try { await task; } catch (Exception) { Send(new() { T = "error", Error = "OperationFailed" }); } finally { tasks.TryRemove(task, out _); } }
        public void Abort() { if (Interlocked.Exchange(ref aborted, 1) != 0) return; cancel.Cancel(); socket.Abort(); Lease?.Dispose(); Lease = null; }
        public async Task Run(Func<Message, Task> receive, Action<AudioPacket> binary, Action disconnected)
        {
            var send = SendLoop();
            try { while (!cancel.IsCancellationRequested) { var frame = await SocketFrames.ReceiveFrame(socket, cancel.Token); if (frame == null) break; if (frame.Binary) binary(AudioPacket.Decode(frame.Bytes)); else await receive(Wire.Decode(frame.Bytes)); } }
            finally { disconnected(); Abort(); try { await send; } catch (Exception e) when (e is OperationCanceledException or WebSocketException or ObjectDisposedException) { } foreach (var task in tasks.Keys) try { await task.WaitAsync(TimeSpan.FromSeconds(2)); } catch (Exception) { } cancel.Dispose(); }
        }
        async Task SendLoop()
        {
            try
            {
                while (!cancel.IsCancellationRequested)
                {
                    // One writer for both frame types; control always precedes the bounded audio queue.
                    if (queue.Reader.TryRead(out var m)) { await SendFrame(Wire.Encode(m), false); continue; }
                    if (audioQueue.Reader.TryRead(out var bytes)) { await SendFrame(bytes, true); continue; }
                    await Task.Delay(3, cancel.Token);
                }
            }
            catch (Exception e) when (e is OperationCanceledException or WebSocketException or ObjectDisposedException) { Abort(); }
        }
        async Task SendFrame(byte[] bytes, bool binary) { using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token); timeout.CancelAfter(500); await socket.SendAsync(bytes, binary ? WebSocketMessageType.Binary : WebSocketMessageType.Text, true, timeout.Token); }
    }
}
