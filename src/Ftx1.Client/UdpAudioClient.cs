using System.Net;
using System.Net.Sockets;
using Ftx1.Protocol;
namespace Ftx1.Client;

/// <summary>Client end of the optional UDP audio channel. A lost datagram is just skipped, so one loss on a
/// mobile link no longer holds back everything behind it (TCP head-of-line). Control, state and PTT stay on TLS.</summary>
public sealed class UdpAudioClient : IDisposable
{
    /// <summary>A dead path must be detected well inside the server's 1 s LostAudio deadline, so the microphone
    /// moves to the WebSocket before PTT is cut. RX audio (50 datagrams/s) also proves the path is alive.</summary>
    public const int HelloMs = 250, KeepaliveMs = 250, AliveMs = 700;
    readonly Socket socket; readonly UdpCipher cipher; readonly Action<AudioPacket> received; readonly CancellationTokenSource lifetime = new(); readonly Func<long> now;
    readonly Task receiver, pulses; long lastReceived = long.MinValue / 2; volatile bool blocked, disposed;
    public long Sent, Received;
    /// <summary>True while authenticated datagrams from the server keep arriving: both directions work.</summary>
    public bool Alive => !disposed && now() - Volatile.Read(ref lastReceived) < AliveMs;
    /// <summary>Test/diagnostic: silently drop everything, as a broken mobile path would.</summary>
    internal bool Blocked { get => blocked; set => blocked = value; }
    public UdpAudioClient(IPEndPoint server, byte[] key, long id, Action<AudioPacket> received, Func<long>? clock = null)
    {
        now = clock ?? (() => Environment.TickCount64); this.received = received; cipher = new(key, id, false);
        socket = new Socket(server.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        if (OperatingSystem.IsWindows()) socket.IOControl(unchecked((int)0x9800000C), [0, 0, 0, 0], null);
        socket.Connect(server);
        receiver = Task.Run(Receive); pulses = Task.Run(Pulse);
    }
    public static async Task<IPEndPoint?> Resolve(string host, int port, CancellationToken token)
    {
        if (IPAddress.TryParse(host, out var ip)) return new(ip, port);
        try { var all = await Dns.GetHostAddressesAsync(host, token); var first = all.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? all.FirstOrDefault(); return first == null ? null : new(first, port); }
        catch (SocketException) { return null; }
    }
    public bool Send(AudioPacket packet) => Send(packet.Encode());
    bool Send(byte[] plain)
    {
        if (blocked) return true; // datagrams vanish, like on a dead path: the sender cannot tell
        try { socket.Send(cipher.Seal(plain)); Interlocked.Increment(ref Sent); return true; }
        catch (Exception e) when (e is SocketException or ObjectDisposedException) { return false; }
    }
    async Task Pulse()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                bool alive = Alive; Send(UdpCipher.Control(alive ? UdpCipher.Keepalive : UdpCipher.Hello, alive));
                await Task.Delay(alive ? KeepaliveMs : HelloMs, lifetime.Token);
            }
        }
        catch (OperationCanceledException) { }
    }
    async Task Receive()
    {
        var buffer = new byte[UdpCipher.MaxDatagram + 1];
        while (!lifetime.IsCancellationRequested)
        {
            int length;
            try { length = await socket.ReceiveAsync(buffer, SocketFlags.None, lifetime.Token); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { try { await Task.Delay(100, lifetime.Token); } catch (OperationCanceledException) { break; } continue; }
            if (blocked) continue;
            var plain = cipher.Open(buffer.AsSpan(0, length)); if (plain == null) continue;
            Volatile.Write(ref lastReceived, now()); Interlocked.Increment(ref Received);
            if (plain[0] == ProtocolVersion.Audio) { try { received(AudioPacket.Decode(plain)); } catch (InvalidDataException) { } }
        }
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true; lifetime.Cancel(); socket.Dispose();
        try { Task.WaitAll([receiver, pulses], TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        cipher.Dispose(); lifetime.Dispose();
    }
}
