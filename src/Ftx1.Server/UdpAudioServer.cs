using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Ftx1.Protocol;
namespace Ftx1.Server;

/// <summary>One audio peer per WebSocket session. The address is learned only from authenticated datagrams,
/// so a roaming phone (Wi-Fi → 4G) keeps working and a forged packet cannot redirect the audio.</summary>
public sealed class UdpPeer : IDisposable
{
    public const int ActiveMs = 1000; // client keepalives arrive every 250 ms
    readonly object gate = new(); Socket? socket; EndPoint? remote; long lastSeen; bool receiving;
    internal UdpPeer(string sessionId, Func<long> clock) { SessionId = sessionId; Key = UdpCipher.NewKey(); Cipher = new(Key, UdpCipher.NewId(), true); now = clock; }
    readonly Func<long> now;
    public string SessionId { get; }
    public byte[] Key { get; }
    public UdpCipher Cipher { get; }
    public long Id => Cipher.Id;
    /// <summary>Server → client audio goes over UDP only while the client confirms it is receiving our datagrams.</summary>
    public bool Active { get { lock (gate) return remote != null && receiving && now() - lastSeen < ActiveMs; } }
    internal void Seen(Socket from, EndPoint address, bool? clientReceiving) { lock (gate) { socket = from; remote = address; lastSeen = now(); if (clientReceiving is { } r) receiving = r; } }
    public bool Send(AudioPacket packet) => Send(packet.Encode());
    internal bool Send(byte[] plain)
    {
        Socket? s; EndPoint? to; lock (gate) { s = socket; to = remote; }
        if (s == null || to == null) return false;
        try { s.SendTo(Cipher.Seal(plain), to); return true; }
        catch (Exception e) when (e is SocketException or ObjectDisposedException) { return false; }
    }
    public void Dispose() => Cipher.Dispose();
}

public sealed class UdpAudioServer : IDisposable
{
    readonly List<Socket> sockets = []; readonly ConcurrentDictionary<long, UdpPeer> peers = new(); readonly Action<UdpPeer, AudioPacket> microphone;
    readonly CancellationTokenSource lifetime = new(); readonly List<Task> loops = []; readonly Func<long> clock;
    public int Port { get; }
    public long Accepted, Rejected;
    public UdpAudioServer(IEnumerable<IPAddress> addresses, int port, Action<UdpPeer, AudioPacket> microphone, Func<long>? clock = null)
    {
        this.microphone = microphone; this.clock = clock ?? (() => Environment.TickCount64);
        try
        {
            foreach (var ip in addresses)
            {
                var socket = new Socket(ip.AddressFamily, SocketType.Dgram, ProtocolType.Udp); sockets.Add(socket);
                // Windows reports an earlier ICMP "port unreachable" as a reset on the next receive; ignore it.
                if (OperatingSystem.IsWindows()) socket.IOControl(unchecked((int)0x9800000C), [0, 0, 0, 0], null);
                socket.Bind(new IPEndPoint(ip, port)); port = ((IPEndPoint)socket.LocalEndPoint!).Port;
            }
            Port = port;
        }
        catch { foreach (var s in sockets) s.Dispose(); throw; }
        foreach (var socket in sockets) loops.Add(Task.Run(() => Receive(socket)));
    }
    public UdpPeer Register(string sessionId) { var peer = new UdpPeer(sessionId, clock); peers[peer.Id] = peer; return peer; }
    public void Remove(UdpPeer? peer) { if (peer != null && peers.TryRemove(peer.Id, out _)) peer.Dispose(); }
    async Task Receive(Socket socket)
    {
        var buffer = new byte[UdpCipher.MaxDatagram + 1]; EndPoint any = new IPEndPoint(socket.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
        while (!lifetime.IsCancellationRequested)
        {
            SocketReceiveFromResult result;
            try { result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, lifetime.Token); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }
            try { Handle(socket, buffer.AsSpan(0, result.ReceivedBytes).ToArray(), result.RemoteEndPoint); }
            catch (Exception e) when (e is InvalidDataException or ArgumentException) { Interlocked.Increment(ref Rejected); }
        }
    }
    void Handle(Socket socket, byte[] datagram, EndPoint from)
    {
        // Unauthenticated input never gets a reply: no amplification, no probing.
        if (!UdpCipher.TryPeekId(datagram, out var id) || !peers.TryGetValue(id, out var peer)) { Interlocked.Increment(ref Rejected); return; }
        UdpPeer target = peer; var plain = target.Cipher.Open(datagram);
        if (plain == null) { Interlocked.Increment(ref Rejected); return; }
        Interlocked.Increment(ref Accepted);
        switch (plain[0])
        {
            case UdpCipher.Hello or UdpCipher.Keepalive:
                target.Seen(socket, from, plain.Length > 1 && (plain[1] & UdpCipher.ReceivingFlag) != 0);
                target.Send(UdpCipher.Control(UdpCipher.Ack)); break;
            case ProtocolVersion.Microphone:
                target.Seen(socket, from, null); microphone(target, AudioPacket.Decode(plain)); break;
            default: throw new InvalidDataException("UnsupportedUdpFrame");
        }
    }
    public void Dispose()
    {
        lifetime.Cancel(); foreach (var s in sockets) s.Dispose();
        try { Task.WaitAll([.. loops], TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        foreach (var peer in peers.Values) peer.Dispose(); peers.Clear(); lifetime.Dispose();
    }
}
