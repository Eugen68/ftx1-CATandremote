namespace Ftx1.Audio;

/// <summary>Jitter buffer + decoder + gentle catch-up, producing exact 20 ms PCM frames.
/// Lost frames are rebuilt from FEC or concealed. When the buffer runs ahead of its target
/// (after a burst), quiet frames are skipped first; otherwise playback runs ~5% faster until caught up.</summary>
public sealed class AudioPlayout
{
    public const int CatchUpMs = 60, QuietRms = 300, StretchInput = 336;
    readonly object gate = new(); readonly JitterBuffer jitter; readonly VoiceCodec codec;
    readonly short[] pending = new short[VoiceCodec.Samples * 4]; int count; long skipped, stretched;
    public AudioPlayout(JitterBuffer jitter, VoiceCodec codec) { this.jitter = jitter; this.codec = codec; }
    public JitterBuffer Buffer => jitter;
    public AudioQuality Quality { get { var q = jitter.Quality; lock (gate) return q with { Skipped = skipped + stretched }; } }
    public void Push(BufferedAudio frame) => jitter.Push(frame);
    public short[]? Next()
    {
        lock (gate)
        {
            bool hurry = jitter.ExcessMs >= CatchUpMs;
            while (count < (hurry ? StretchInput : VoiceCodec.Samples))
            {
                var frame = jitter.Pull();
                if (frame is not { } f) { if (count >= VoiceCodec.Samples) { hurry = false; break; } return null; }
                var pcm = f.Fec ? codec.DecodeFec(f.Payload) : f.Missing ? codec.Conceal() : codec.Decode(f.Payload);
                if (hurry && !f.Missing && !f.Fec && Rms(pcm) < QuietRms) { skipped++; hurry = jitter.ExcessMs >= CatchUpMs; continue; }
                pcm.CopyTo(pending, count); count += pcm.Length;
            }
            var output = new short[VoiceCodec.Samples];
            int used = hurry && count >= StretchInput ? StretchInput : VoiceCodec.Samples;
            if (used == StretchInput) { stretched++; Resample(pending.AsSpan(0, used), output); } else pending.AsSpan(0, used).CopyTo(output);
            pending.AsSpan(used, count - used).CopyTo(pending); count -= used;
            return output;
        }
    }
    public void Clear() { lock (gate) { jitter.Clear(); codec.Reset(); count = 0; } }
    static double Rms(short[] pcm) { double sum = 0; foreach (var s in pcm) sum += (double)s * s; return Math.Sqrt(sum / pcm.Length); }
    /// <summary>Linear interpolation, input.Length → output.Length (both ends kept, so frames join without clicks).</summary>
    public static void Resample(ReadOnlySpan<short> input, Span<short> output)
    {
        double step = (input.Length - 1) / (double)(output.Length - 1);
        for (int i = 0; i < output.Length; i++)
        {
            double position = i * step; int index = (int)position; double fraction = position - index;
            output[i] = index + 1 < input.Length ? (short)Math.Round(input[index] + (input[index + 1] - input[index]) * fraction) : input[index];
        }
    }
}
