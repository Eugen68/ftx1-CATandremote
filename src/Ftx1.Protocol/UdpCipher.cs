using System.Buffers.Binary;
using System.Security.Cryptography;
namespace Ftx1.Protocol;

/// <summary>Optional UDP audio datagrams. The 256-bit session key travels only inside the pinned TLS WebSocket.
/// Layout: version u8 · peer id i64 · counter u64 (all authenticated, in clear) · AES-256-GCM ciphertext · tag 16.
/// Nonce = direction u32 · counter u64, so both directions share one key without ever reusing a nonce.
/// Plaintext is an <see cref="AudioPacket"/> (types 1/3) or a control frame [type, flags].</summary>
public sealed class UdpCipher : IDisposable
{
    public const byte Version = 1, Hello = 10, Ack = 11, Keepalive = 12, ReceivingFlag = 1;
    public const int KeyBytes = 32, HeaderBytes = 17, TagBytes = 16, MaxDatagram = HeaderBytes + AudioPacket.HeaderBytes + AudioPacket.MaxPayload + TagBytes;
    const uint ClientToServer = 1, ServerToClient = 2;
    readonly object gate = new(); readonly AesGcm aes; readonly uint sendDirection, receiveDirection; readonly ReplayWindow window = new(); long counter;
    public long Id { get; }
    public static bool Supported => AesGcm.IsSupported;
    public UdpCipher(byte[] key, long id, bool server)
    {
        if (key.Length != KeyBytes) throw new ArgumentException("InvalidUdpKey");
        aes = new AesGcm(key, TagBytes); Id = id; sendDirection = server ? ServerToClient : ClientToServer; receiveDirection = server ? ClientToServer : ServerToClient;
    }
    public static byte[] NewKey() => RandomNumberGenerator.GetBytes(KeyBytes);
    public static long NewId() { long id; do id = BinaryPrimitives.ReadInt64LittleEndian(RandomNumberGenerator.GetBytes(8)); while (id == 0); return id; }
    public static bool TryPeekId(ReadOnlySpan<byte> datagram, out long id)
    {
        id = 0; if (datagram.Length < HeaderBytes + TagBytes + 1 || datagram.Length > MaxDatagram || datagram[0] != Version) return false;
        id = BinaryPrimitives.ReadInt64LittleEndian(datagram[1..]); return id != 0;
    }
    public byte[] Seal(ReadOnlySpan<byte> plain)
    {
        if (plain.Length == 0 || plain.Length > MaxDatagram - HeaderBytes - TagBytes) throw new ArgumentException("InvalidUdpPayload");
        var datagram = new byte[HeaderBytes + plain.Length + TagBytes]; ulong next = (ulong)Interlocked.Increment(ref counter);
        datagram[0] = Version; BinaryPrimitives.WriteInt64LittleEndian(datagram.AsSpan(1), Id); BinaryPrimitives.WriteUInt64LittleEndian(datagram.AsSpan(9), next);
        Span<byte> nonce = stackalloc byte[12]; BinaryPrimitives.WriteUInt32LittleEndian(nonce, sendDirection); BinaryPrimitives.WriteUInt64LittleEndian(nonce[4..], next);
        lock (gate) aes.Encrypt(nonce, plain, datagram.AsSpan(HeaderBytes, plain.Length), datagram.AsSpan(HeaderBytes + plain.Length), datagram.AsSpan(0, HeaderBytes));
        return datagram;
    }
    /// <summary>Authenticated plaintext, or null for anything forged, corrupted, foreign, from our own direction or replayed.</summary>
    public byte[]? Open(ReadOnlySpan<byte> datagram)
    {
        if (!TryPeekId(datagram, out var id) || id != Id) return null;
        ulong number = BinaryPrimitives.ReadUInt64LittleEndian(datagram[9..]);
        Span<byte> nonce = stackalloc byte[12]; BinaryPrimitives.WriteUInt32LittleEndian(nonce, receiveDirection); BinaryPrimitives.WriteUInt64LittleEndian(nonce[4..], number);
        var plain = new byte[datagram.Length - HeaderBytes - TagBytes];
        lock (gate)
        {
            if (!window.Fresh(number)) return null;
            try { aes.Decrypt(nonce, datagram.Slice(HeaderBytes, plain.Length), datagram[^TagBytes..], plain, datagram[..HeaderBytes]); }
            catch (CryptographicException) { return null; }
            window.Mark(number); return plain;
        }
    }
    public static byte[] Control(byte type, bool receiving = false) => [type, receiving ? ReceivingFlag : (byte)0];
    public void Dispose() => aes.Dispose();
}

/// <summary>64-packet anti-replay window (RFC 4303 style): late reordering is accepted, duplicates are not.</summary>
public sealed class ReplayWindow
{
    ulong highest, seen;
    public bool Fresh(ulong number) => number != 0 && (number > highest || (highest - number < 64 && (seen & (1UL << (int)(highest - number))) == 0));
    public void Mark(ulong number)
    {
        if (number > highest) { ulong shift = number - highest; seen = shift >= 64 ? 1 : (seen << (int)shift) | 1; highest = number; }
        else seen |= 1UL << (int)(highest - number);
    }
}
