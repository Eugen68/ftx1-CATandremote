using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Ftx1.Audio;
using Ftx1.Client;
using Ftx1.Core;
using Ftx1.Protocol;
using Ftx1.Server;
using Ftx1.Sim;
using Xunit;
namespace Ftx1.Network.Tests;

public class UdpTests
{
    static AudioPacket Mic(long epoch, uint seq) => new(ProtocolVersion.Microphone, epoch, seq, Environment.TickCount64, [1, 2, 3]);

    [Fact]
    public void CipherRoundTripsAndRejectsTamperingReplayForeignIdsAndOwnDirection()
    {
        var key = UdpCipher.NewKey(); long id = UdpCipher.NewId();
        using var client = new UdpCipher(key, id, false); using var server = new UdpCipher(key, id, true);
        var datagram = client.Seal(Mic(5, 1).Encode());
        Assert.Equal(Mic(5, 1).Encode()[..9], server.Open(datagram)![..9]);
        Assert.Null(server.Open(datagram)); // replay
        Assert.Null(client.Open(client.Seal([UdpCipher.Hello, 0]))); // a client cannot accept its own direction (reflection)
        var tampered = client.Seal([UdpCipher.Keepalive, 1]); tampered[^20] ^= 1; Assert.Null(server.Open(tampered));
        var header = client.Seal([UdpCipher.Keepalive, 1]); header[9] ^= 1; Assert.Null(server.Open(header)); // counter is authenticated
        using var other = new UdpCipher(UdpCipher.NewKey(), id, true); Assert.Null(other.Open(client.Seal([UdpCipher.Hello, 0]))); // wrong key
        using var foreign = new UdpCipher(key, id + 1, true); Assert.Null(foreign.Open(client.Seal([UdpCipher.Hello, 0]))); // wrong peer id
        Assert.Null(server.Open(new byte[10])); Assert.Null(server.Open(new byte[UdpCipher.MaxDatagram + 1]));
    }
    [Fact]
    public void ReplayWindowAcceptsReorderingButNotDuplicatesOrVeryOldPackets()
    {
        var w = new ReplayWindow(); foreach (ulong n in new ulong[] { 1, 3, 2, 10, 5 }) { Assert.True(w.Fresh(n)); w.Mark(n); }
        Assert.False(w.Fresh(3)); Assert.False(w.Fresh(10)); Assert.False(w.Fresh(0)); Assert.True(w.Fresh(4));
        w.Mark(200); Assert.False(w.Fresh(100)); Assert.True(w.Fresh(199)); Assert.True(w.Fresh(201));
    }
    [Fact]
    public async Task AudioFlowsOverUdpBothWaysAndTransmitStaysUpOnUdpMicrophone()
    {
        await using var f = new StationFixture(); await f.Start(); Assert.True(f.Server.UdpPort > 0);
        await using var c = await f.Client();
        await StationFixture.Wait(() => c.AudioTransport == "UDP", 5000);
        long before = c.Udp!.Received; await StationFixture.Wait(() => c.Udp!.Received > before + 20, 5000); // RX audio arriving as datagrams
        long accepted = f.Server.UdpAccepted; await c.Ptt(true); await StationFixture.Wait(() => c.OwnsTransmit);
        var watch = Stopwatch.StartNew(); while (watch.Elapsed.TotalSeconds < 2) { await Task.Delay(100); Assert.Equal(c.Udp!.Alive ? "UDP" : "TCP", c.AudioTransport); }
        Assert.True(c.OwnsTransmit, "the microphone heartbeat must reach the server over UDP"); Assert.Equal("1", f.Radio.State["TX"]);
        Assert.True(f.Server.UdpAccepted - accepted > 50, "microphone frames should travel as datagrams");
        await c.Ptt(false); await StationFixture.Wait(() => f.Radio.State["TX"] == "0");
    }
    [Fact]
    public async Task ForgedDatagramsAreIgnoredWithoutAnyReply()
    {
        await using var f = new StationFixture(); await f.Start(); await using var c = await f.Client(); await StationFixture.Wait(() => c.AudioTransport == "UDP", 5000);
        using var attacker = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp); attacker.Connect(IPAddress.Loopback, f.Server.UdpPort);
        var forged = new byte[UdpCipher.HeaderBytes + 40]; forged[0] = UdpCipher.Version; BitConverter.TryWriteBytes(forged.AsSpan(1), 1L); // unknown peer id Random.Shared.NextBytes(forged.AsSpan(9));
        long rejected = f.Server.UdpRejected; for (int i = 0; i < 20; i++) attacker.Send(forged); attacker.Send([1, 2, 3]);
        await StationFixture.Wait(() => f.Server.UdpRejected >= rejected + 21, 3000);
        attacker.ReceiveTimeout = 300; Assert.Throws<SocketException>(() => attacker.Receive(new byte[2048]));
        Assert.Equal("UDP", c.AudioTransport); Assert.True(c.Connected);
    }
    [Fact]
    public async Task WithoutUdpTheWebSocketCarriesAudioAndTransmitAsBefore()
    {
        await using var f = new StationFixture(); await f.Start();
        var token = f.Server.Tokens.Create("tcp-only"); await using var c = new StationConnection(new(f.Server.Address!.ToString(), f.Server.Certificate.Fingerprint, token.DeviceToken)) { UdpEnabled = false };
        await c.Start(); await StationFixture.Wait(() => c.Connected, 10000); await f.EnableAudio(c);
        await c.Ptt(true); await StationFixture.Wait(() => c.OwnsTransmit); await Task.Delay(2000);
        Assert.Equal("TCP", c.AudioTransport); Assert.Null(c.Udp); Assert.True(c.OwnsTransmit); Assert.Equal("1", f.Radio.State["TX"]);
        await c.Ptt(false); await StationFixture.Wait(() => f.Radio.State["TX"] == "0");
    }
    [Fact]
    public async Task ServerWithUdpDisabledOrPortTakenStillServesAudio()
    {
        using var blocker = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp); blocker.Bind(new IPEndPoint(IPAddress.Loopback, 0)); int taken = ((IPEndPoint)blocker.LocalEndPoint!).Port;
        var root = Path.Combine(Path.GetTempPath(), "ftx1-udp-" + Guid.NewGuid().ToString("N")); using var radio = new SimRadio();
        await using var server = new StationServer(new(root, new StationConfig(), 0, [IPAddress.Loopback], UdpPort: taken), new SimTransport(radio));
        try
        {
            await server.Start(); Assert.Equal(0, server.UdpPort);
            var token = server.Tokens.Create("fallback"); await using var c = new StationConnection(new(server.Address!.ToString(), server.Certificate.Fingerprint, token.DeviceToken));
            await c.Start(); await StationFixture.Wait(() => c.Connected, 10000);
            var stream = new ClientAudio(c, new SimVoiceDevice()); stream.Start(); await StationFixture.Wait(() => stream.Ready, 10000);
            await c.Ptt(true); await StationFixture.Wait(() => c.OwnsTransmit); await Task.Delay(1500); Assert.True(c.OwnsTransmit); Assert.Equal("TCP", c.AudioTransport);
            await stream.DisposeAsync();
        }
        finally { await server.DisposeAsync(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact]
    public async Task LosingUdpMidTransmissionFallsBackToTheWebSocketWithoutDroppingPtt()
    {
        await using var f = new StationFixture(); await f.Start(); await using var c = await f.Client(); await StationFixture.Wait(() => c.AudioTransport == "UDP", 5000);
        await c.Ptt(true); await StationFixture.Wait(() => c.OwnsTransmit); await Task.Delay(500);
        c.Udp!.Blocked = true; // the path dies silently: datagrams vanish in both directions
        await StationFixture.Wait(() => c.AudioTransport == "TCP", 1000);
        // LostAudio fires after 1 s without microphone frames; the WebSocket must take over before that.
        await Task.Delay(1500); Assert.True(c.OwnsTransmit); Assert.Equal("1", f.Radio.State["TX"]);
        await c.Ptt(false); await StationFixture.Wait(() => f.Radio.State["TX"] == "0");
    }
}
