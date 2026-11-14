using Ftx1.Audio;
namespace Ftx1.Server;

/// <summary>Only the station process opens the USB endpoint. Device failure cannot leave TX asserted.</summary>
public sealed class StationAudio : IDisposable
{
    readonly object gate = new(); readonly Func<IVoiceDevice> factory; IVoiceDevice? device;
    readonly Dictionary<int, VoiceCodec> encoders = new() { [12000] = new(12000), [24000] = new(24000), [32000] = new(32000) }; uint sequence;
    public bool Ready { get; private set; }
    public event Action<int, uint, long, byte[]>? Encoded;
    public event Action<Exception>? Failed;
    public StationAudio(Func<IVoiceDevice> factory) => this.factory = factory;
    public void Start()
    {
        lock (gate)
        {
            if (Ready) return;
            device?.Dispose(); device = factory(); device.Captured += Capture; device.Failed += e => { Ready = false; Failed?.Invoke(e); };
            try { device.Start(); Ready = true; } catch { device.Dispose(); device = null; throw; }
        }
    }
    void Capture(short[] pcm)
    {
        try
        {
            var frames = new List<(int Bitrate, byte[] Payload)>(); uint seq; long stamp = Environment.TickCount64;
            lock (gate) { if (!Ready) return; seq = sequence++; foreach (var (rate, codec) in encoders) frames.Add((rate, codec.Encode(pcm))); }
            foreach (var frame in frames) Encoded?.Invoke(frame.Bitrate, seq, stamp, frame.Payload);
        }
        catch (Exception e) { Ready = false; Failed?.Invoke(e); }
    }
    public void Play(short[] pcm) { if (!Ready) throw new IOException("AudioUnavailable"); device!.Play(pcm); }
    public void Clear() => device?.Clear();
    public void Dispose() { Ready = false; device?.Dispose(); }
}
