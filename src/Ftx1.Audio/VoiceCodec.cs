using Concentus;
using Concentus.Enums;
namespace Ftx1.Audio;

/// <summary>One independent codec per stream. 16 kHz mono, 20 ms, no DTX (silence is still a heartbeat).
/// In-band FEC lets the receiver rebuild a lost frame from the one that follows it.</summary>
public sealed class VoiceCodec
{
    public const int Rate = 16000, Samples = 320, MaxPacket = 1275, ExpectedLossPercent = 10;
    readonly IOpusEncoder encoder = OpusCodecFactory.CreateEncoder(Rate, 1, OpusApplication.OPUS_APPLICATION_VOIP);
    readonly IOpusDecoder decoder = OpusCodecFactory.CreateDecoder(Rate, 1);
    public VoiceCodec(int bitrate = 24000) { SetBitrate(bitrate); encoder.UseDTX = false; encoder.Complexity = 5; encoder.UseInbandFEC = true; encoder.PacketLossPercent = ExpectedLossPercent; }
    public void SetBitrate(int bitrate) { if (bitrate is not (12000 or 24000 or 32000)) throw new ArgumentOutOfRangeException(nameof(bitrate)); encoder.Bitrate = bitrate; }
    public byte[] Encode(short[] samples)
    {
        if (samples.Length != Samples) throw new ArgumentException("Expected20ms");
        var packet = new byte[MaxPacket]; int size = encoder.Encode(samples, Samples, packet, packet.Length); return packet[..size];
    }
    public short[] Decode(byte[] packet) => Run(packet, false);
    /// <summary>Rebuilds the frame before <paramref name="following"/> from its FEC data (falls back to concealment inside Opus).</summary>
    public short[] DecodeFec(byte[] following) => Run(following, true);
    /// <summary>Packet loss concealment: extrapolates from the decoder state instead of playing a hole.</summary>
    public short[] Conceal()
    {
        var pcm = new short[Samples];
        try { if (decoder.Decode(ReadOnlySpan<byte>.Empty, pcm, Samples, false) != Samples) Array.Clear(pcm); }
        catch (Exception e) when (e is ArgumentException or Concentus.OpusException) { Array.Clear(pcm); }
        return pcm;
    }
    short[] Run(byte[] packet, bool fec)
    {
        if (packet.Length is 0 or > MaxPacket) throw new InvalidDataException(packet.Length == 0 ? "EmptyAudioPacket" : "AudioPacketTooLarge");
        var pcm = new short[Samples];
        try { if (decoder.Decode(packet, pcm, Samples, fec) != Samples) throw new InvalidDataException("Expected20ms"); }
        catch (Exception e) when (e is ArgumentException or Concentus.OpusException) { throw new InvalidDataException("InvalidOpus", e); }
        return pcm;
    }
    public void Reset() { encoder.ResetState(); decoder.ResetState(); }
}
