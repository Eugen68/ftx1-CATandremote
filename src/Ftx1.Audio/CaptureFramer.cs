using NAudio.Wave;
using NAudio.Wave.SampleProviders;
namespace Ftx1.Audio;

/// <summary>Streaming endpoint PCM to filtered mono 16 kHz / 20 ms; retains resampling state between callbacks.</summary>
public sealed class CaptureFramer
{
    readonly BufferedWaveProvider recording; readonly ISampleProvider source; ISampleProvider resampled;
    readonly float[] frame = new float[VoiceCodec.Samples];
    public CaptureFramer(WaveFormat format)
    {
        recording = new(format) { BufferDuration = TimeSpan.FromMilliseconds(200), DiscardOnBufferOverflow = true, ReadFully = false };
        ISampleProvider samples = recording.ToSampleProvider();
        if (samples.WaveFormat.Channels == 2) samples = new StereoToMonoSampleProvider(samples) { LeftVolume = .5f, RightVolume = .5f };
        else if (samples.WaveFormat.Channels != 1) throw new NotSupportedException("AudioChannelCount");
        source = samples; resampled = new WdlResamplingSampleProvider(source, VoiceCodec.Rate);
    }
    public IReadOnlyList<short[]> Push(byte[] bytes, int count)
    {
        recording.AddSamples(bytes, 0, count); var ready = new List<short[]>();
        // Keep 20 ms of lookahead for filtering; do not manufacture silence when capture stops.
        while (recording.BufferedBytes >= recording.WaveFormat.AverageBytesPerSecond * .04)
        {
            int read = resampled.Read(frame, 0, frame.Length); if (read != frame.Length) break;
            var pcm = new short[read]; for (int i = 0; i < read; i++) pcm[i] = (short)Math.Clamp(frame[i] * 32767, short.MinValue, short.MaxValue); ready.Add(pcm);
        }
        return ready;
    }
    public void Clear() { recording.ClearBuffer(); resampled = new WdlResamplingSampleProvider(source, VoiceCodec.Rate); }
}
