using System.Buffers.Binary;
namespace Ftx1.Protocol;

/// <summary>Binary audio v1: type u8, stream epoch i64, sequence u32, sender milliseconds i64, Opus payload.</summary>
public sealed record AudioPacket(byte Type, long Epoch, uint Sequence, long Timestamp, byte[] Payload)
{
    public const int HeaderBytes = 21, MaxPayload = 1275;
    public byte[] Encode()
    {
        if (Type is not (ProtocolVersion.Audio or ProtocolVersion.Microphone) || Epoch <= 0 || Payload.Length is < 1 or > MaxPayload) throw new InvalidDataException("InvalidAudioPacket");
        var result = new byte[HeaderBytes + Payload.Length]; result[0] = Type;
        BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(1), Epoch); BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(9), Sequence); BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(13), Timestamp); Payload.CopyTo(result, HeaderBytes); return result;
    }
    public static AudioPacket Decode(byte[] bytes)
    {
        if (bytes.Length is <= HeaderBytes or > HeaderBytes + MaxPayload || bytes[0] is not (ProtocolVersion.Audio or ProtocolVersion.Microphone)) throw new InvalidDataException("InvalidAudioPacket");
        var packet = new AudioPacket(bytes[0], BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(1)), BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(9)), BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(13)), bytes[HeaderBytes..]);
        if (packet.Epoch <= 0) throw new InvalidDataException("InvalidAudioEpoch"); return packet;
    }
}
