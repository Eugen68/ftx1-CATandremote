using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ftx1.Client;
using Ftx1.Core;
using Ftx1.Core.Cat;
using Ftx1.Protocol;
using Ftx1.Server;
using Ftx1.Sim;
using Xunit;
namespace Ftx1.Network.Tests;

public sealed class StationFixture : IAsyncDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "ftx1-net-" + Guid.NewGuid().ToString("N"));
    public SimRadio Radio { get; } = new(); public StationServer Server { get; }
    public StationFixture(Func<SimRadio, ITransport>? factory = null) { Server = new(new(Root, new StationConfig(), 0, [IPAddress.Loopback]), factory?.Invoke(Radio) ?? new SimTransport(Radio)); }
    public async Task Start() => await Server.Start();
    public async Task<StationConnection> Client(string name = "test")
    {
        var token = Server.Tokens.Create(name); var client = new StationConnection(new(Server.Address!.ToString(), Server.Certificate.Fingerprint, token.DeviceToken)); await client.Start(); await Wait(() => client.Connected && client.State.GetValueOrDefault("connected") is true && client.State.GetValueOrDefault("radio_id") as string=="0840",10000); return client;
    }
    public static async Task Wait(Func<bool> condition, int timeout = 5000) { using var cts = new CancellationTokenSource(timeout); while (!condition()) await Task.Delay(10, cts.Token); }
    public async ValueTask DisposeAsync() { await Server.DisposeAsync(); Radio.Dispose(); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
}
public class NetworkTests
{
    [Fact]
    public async Task FullStateAndBidirectionalControlOverRealTls()
    {
        await using var f = new StationFixture(); await f.Start(); await using var c = await f.Client();
        await StationFixture.Wait(() => c.State.GetValueOrDefault("radio_id") as string == "0840");
        await c.Send(new() { T = "set", Key = "af", Value = Wire.Element(77) }); await StationFixture.Wait(() => f.Radio.State["AG0"] == "077" && c.State.GetValueOrDefault("af") is 77);
        f.Radio.Handle("AG0042"); await StationFixture.Wait(() => c.State.GetValueOrDefault("af") is 42);
        var pong = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); c.Received += m => { if (m.T == "pong" && m.Ts == 123) pong.TrySetResult(); }; await c.Send(new() { T = "ping", Ts = 123 }); await pong.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }
    [Theory]
    [InlineData(null)]
    [InlineData("invalid")]
    public async Task LocalhostDoesNotBypassAuthentication(string? token)
    {
        await using var f = new StationFixture(); await f.Start(); using var ws = new ClientWebSocket(); ws.Options.RemoteCertificateValidationCallback = (_, cert, _, _) => cert != null && Convert.ToHexString(SHA256.HashData(cert.GetRawCertData())) == f.Server.Certificate.Fingerprint;
        using var timeout = new CancellationTokenSource(4000); await ws.ConnectAsync(f.Server.Address!, timeout.Token); await ws.SendAsync(Wire.Encode(new() { T = "hello", ProtocolVersion = 1, DeviceToken = token }), WebSocketMessageType.Text, true, timeout.Token);
        var response = Wire.Decode((await SocketFrames.Receive(ws, timeout.Token))!); Assert.Equal("authFail", response.T); Assert.Equal("AuthenticationFailed", response.Error); Assert.Equal("0", f.Radio.State["TX"]);
    }
    [Fact]
    public async Task MissingHeartbeatStopsTxBeforeOnePointSevenFiveSeconds()
    {
        await using var f = new StationFixture(); await f.Start(); await using var c = await f.Client(); c.HeartbeatsEnabled = false; await c.Ptt(true); await StationFixture.Wait(() => f.Radio.State["TX"] == "1"); var watch = Stopwatch.StartNew(); await StationFixture.Wait(() => f.Radio.State["TX"] == "0", 2200); Assert.True(watch.Elapsed.TotalSeconds <= 1.75, watch.Elapsed.ToString()); Assert.Null(f.Server.Station.Transmit.Owner);
    }
    [Fact]
    public async Task SecondClientCannotStealPtt()
    {
        await using var f = new StationFixture(); await f.Start(); await using var a = await f.Client("a"); await using var b = await f.Client("b"); await a.Ptt(true); await StationFixture.Wait(() => a.OwnsTransmit);
        var refused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); b.Error += e => { if (e == "TransmitBusy") refused.TrySetResult(); }; await b.Ptt(true); await refused.Task.WaitAsync(TimeSpan.FromSeconds(2)); Assert.True(a.OwnsTransmit); Assert.False(b.OwnsTransmit); await a.Ptt(false); await StationFixture.Wait(() => f.Radio.State["TX"] == "0");
    }
    [Fact]
    public async Task AbruptDisconnectStopsTxAndReconnectNeverRekeys()
    {
        await using var f = new StationFixture(); await f.Start(); await using var c = await f.Client(); await c.Send(new() { T = "set", Key = "af", Value = Wire.Element(79) }); await StationFixture.Wait(() => c.State.GetValueOrDefault("af") is 79);
        int hellos = 0; c.Received += m => { if (m.T == "helloOk") Interlocked.Increment(ref hellos); }; await c.Ptt(true); await StationFixture.Wait(() => c.OwnsTransmit); c.Abort(); await StationFixture.Wait(() => f.Radio.State["TX"] == "0", 1000); await StationFixture.Wait(() => Volatile.Read(ref hellos) > 0); Assert.False(c.OwnsTransmit); Assert.Equal(79, c.State["af"]); await Task.Delay(350); Assert.Equal("0", f.Radio.State["TX"]);
    }
    [Fact]
    public async Task PairingIsPinnedOneTimeAndRevocable()
    {
        await using var f = new StationFixture(); await f.Start(); var qr = JsonSerializer.Deserialize(f.Server.CreateQr(), ProtocolJson.Default.PairingQr)!;
        await Assert.ThrowsAsync<HttpRequestException>(() => StationConnection.Pair(qr with { Fingerprint = new string('A', 64) }, "bad"));
        var pair = await StationConnection.Pair(qr, "phone"); Assert.NotNull(f.Server.Tokens.Authenticate(pair.DeviceToken)); await Assert.ThrowsAsync<HttpRequestException>(() => StationConnection.Pair(qr, "again"));
        await using var c = new StationConnection(new(qr.Address, qr.Fingerprint, pair.DeviceToken));
        var errors=new System.Collections.Concurrent.ConcurrentQueue<string>();c.Error+=errors.Enqueue;
        await c.Start();
        try{await StationFixture.Wait(() => c.Connected && c.State.GetValueOrDefault("connected") is true && c.State.GetValueOrDefault("radio_id") as string=="0840",10000);}catch(OperationCanceledException){throw new TimeoutException("Paired client could not connect: "+string.Join(",",errors));}
        await c.Ptt(true);
        try{await StationFixture.Wait(() => f.Radio.State["TX"] == "1");}catch(OperationCanceledException){throw new TimeoutException("Paired client could not acquire PTT: "+string.Join(",",errors)+"; connected="+f.Server.Station.State.GetValueOrDefault("connected"));}
        f.Server.Tokens.Revoke(pair.DeviceId);
        await StationFixture.Wait(() => f.Radio.State["TX"] == "0", 1000);
        Assert.Null(f.Server.Tokens.Authenticate(pair.DeviceToken));
    }
    [Fact]
    public async Task ServerShutdownStopsTransmitterAndWritesAudit()
    {
        await using var f = new StationFixture(); await f.Start(); await using var c = await f.Client("operator"); await c.Ptt(true); await StationFixture.Wait(() => f.Radio.State["TX"] == "1"); await f.Server.DisposeAsync(); Assert.Equal("0", f.Radio.State["TX"]); var log = File.ReadAllText(Path.Combine(f.Root, "transmit.jsonl")); Assert.Contains("operator", log); Assert.Contains("Shutdown", log);
    }
    [Fact]
    public async Task TotWarningCanBeResolvedThroughProtocol()
    {
        await using var f = new StationFixture(); f.Radio.Handle("EX03011200"); await f.Start(); await using var c = await f.Client(); Assert.Equal(0, c.TotMinutes); await c.Send(new() { T = "setTot" }); await StationFixture.Wait(() => c.TotMinutes == 3); Assert.Contains("EX03011203", f.Radio.Received);
    }
    [Fact]
    public async Task UnsupportedVersionIsExplicitlyRejected()
    {
        await using var f = new StationFixture(); await f.Start(); using var ws = new ClientWebSocket(); ws.Options.RemoteCertificateValidationCallback = (_, cert, _, _) => cert != null && Convert.ToHexString(SHA256.HashData(cert.GetRawCertData())) == f.Server.Certificate.Fingerprint;
        using var token = new CancellationTokenSource(4000); await ws.ConnectAsync(f.Server.Address!, token.Token); await ws.SendAsync(Wire.Encode(new() { T = "hello", ProtocolVersion = 99 }), WebSocketMessageType.Text, true, token.Token); var reply = Wire.Decode((await SocketFrames.Receive(ws, token.Token))!); Assert.Equal("ProtocolMismatch", reply.Error);
    }
    sealed class SwrTransport(SimRadio radio) : ITransport
    {
        readonly SimTransport inner = new(radio); Action<byte[]?>? receive; public bool HighSwr; public bool Failed => inner.Failed;
        public void Open(Action<byte[]?> cb) { receive = cb; inner.Open(cb); }
        public void Reopen() => inner.Reopen(); public void Close() => inner.Close();
        public void Write(byte[] bytes) { foreach (var c in Encoding.ASCII.GetString(bytes).Split(';', StringSplitOptions.RemoveEmptyEntries)) { if (c == "RM6" && HighSwr) receive?.Invoke(Encoding.ASCII.GetBytes("RM6250000;")); else inner.Write(Encoding.ASCII.GetBytes(c + ";")); } }
    }
    [Fact]
    public async Task SustainedHighSwrStopsAndIsAudited()
    {
        SwrTransport? transport = null; await using var f = new StationFixture(r => transport = new SwrTransport(r)); await f.Start(); await using var c = await f.Client("swr-test");
        var stopped=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);c.Received+=m=>{if(m.T=="pttState"&&m.Reason=="HighSwr")stopped.TrySetResult();};
        transport!.HighSwr = true; await c.Ptt(true); await StationFixture.Wait(() => f.Radio.State["TX"] == "1"); await StationFixture.Wait(() => f.Radio.State["TX"] == "0", 1800);
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(2));Assert.Contains("HighSwr",File.ReadAllText(Path.Combine(f.Root,"transmit.jsonl")));
    }
    [Fact]
    public async Task WrongWebSocketCertificatePinNeverAuthenticates()
    {
        await using var f = new StationFixture(); await f.Start(); var token = f.Server.Tokens.Create("pin-test"); await using var c = new StationConnection(new(f.Server.Address!.ToString(), new string('A', 64), token.DeviceToken));
        int hellos = 0; c.Received += m => { if (m.T == "helloOk") Interlocked.Increment(ref hellos); }; await c.Start(); await Task.Delay(600); Assert.False(c.Connected); Assert.Equal(0, hellos); Assert.Equal("0", f.Radio.State["TX"]);
    }
    [Fact]
    public void FailedAuthenticationAttemptsAreRateLimited()
    {
        var limiter = new AttemptLimiter(); for (int i = 0; i < 10; i++) { Assert.True(limiter.Allowed("ip")); limiter.Fail("ip"); }
        Assert.False(limiter.Allowed("ip")); Assert.True(limiter.Allowed("other"));
    }
    [Fact]
    public void FailedDeviceStoreWriteDoesNotPretendRevocationSucceeded()
    {
        var root=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try{var tokens=new DeviceTokens(root);var pair=tokens.Create("device");var file=Path.Combine(root,"devices.json");File.Move(file,file+".backup");Directory.CreateDirectory(file);bool revoked=false;tokens.Revoked+=_=>revoked=true;
            var error = Record.Exception(() => tokens.Revoke(pair.DeviceId));
            Assert.True(error is IOException or UnauthorizedAccessException);
            Assert.NotNull(tokens.Authenticate(pair.DeviceToken));
            Assert.False(revoked);
        }finally{Directory.Delete(root,true);}
    }
    [Fact]
    public void CodesExpireAndDefaultInterfacesAreRestricted()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); try { DateTimeOffset now = DateTimeOffset.UtcNow; var tokens = new DeviceTokens(root); var codes = new PairingCodes(tokens, () => now); var code = codes.Issue(); now = now.AddMinutes(5); Assert.Null(codes.Consume(code, "late")); Assert.True(ListenPolicy.IsTailscale(IPAddress.Parse("100.64.0.1"))); Assert.False(ListenPolicy.IsTailscale(IPAddress.Parse("100.128.0.1"))); Assert.Throws<ArgumentException>(() => ListenPolicy.Validate([IPAddress.Any], false)); ListenPolicy.Validate([IPAddress.Any], true); } finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
