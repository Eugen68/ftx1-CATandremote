using Concentus;
using Concentus.Enums;
namespace Ftx1.Audio;

/// <summary>One independent codec per stream. 16 kHz mono, 20 ms, no DTX (silence is still a heartbeat).</summary>
public sealed class VoiceCodec
{
    public const int Rate = 16000, Samples = 320, MaxPacket = 1275;
    readonly IOpusEncoder encoder = OpusCodecFactory.CreateEncoder(Rate, 1, OpusApplication.OPUS_APPLICATION_VOIP);
    readonly IOpusDecoder decoder = OpusCodecFactory.CreateDecoder(Rate, 1);
    public VoiceCodec(int bitrate = 24000) { SetBitrate(bitrate); encoder.UseDTX = false; encoder.Complexity = 5; }
    public void SetBitrate(int bitrate) { if (bitrate is not (12000 or 24000 or 32000)) throw new ArgumentOutOfRangeException(nameof(bitrate)); encoder.Bitrate = bitrate; }
    public byte[] Encode(short[] samples)
    {
        if (samples.Length != Samples) throw new ArgumentException("Expected20ms");
        var packet = new byte[MaxPacket]; int size = encoder.Encode(samples, Samples, packet, packet.Length); return packet[..size];
    }
    public short[] Decode(byte[] packet)
    {
        if (packet.Length > MaxPacket) throw new InvalidDataException("AudioPacketTooLarge");
        var pcm = new short[Samples];
        try { if (decoder.Decode(packet, pcm, Samples, false) != Samples) throw new InvalidDataException("Expected20ms"); }
        catch (Exception e) when (e is ArgumentException or Concentus.OpusException) { throw new InvalidDataException("InvalidOpus", e); }
        return pcm;
    }
    public void Reset() { encoder.ResetState(); decoder.ResetState(); }
}
