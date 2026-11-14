using Ftx1.Audio;
namespace Ftx1.Server;

/// <summary>Only the station process opens the USB endpoint. Device failure cannot leave TX asserted.</summary>
public sealed class StationAudio : IDisposable
{
    readonly object gate = new(), startGate = new(); readonly Func<IVoiceDevice> factory; IVoiceDevice? device; volatile bool ready;
    readonly Dictionary<int, VoiceCodec> encoders = new() { [12000] = new(12000), [24000] = new(24000), [32000] = new(32000) }; uint sequence;
    public string Description => OperatingSystem.IsWindows() && device is WasapiVoiceDevice wasapi ? wasapi.Description : device?.GetType().Name ?? "";
    public bool Ready => ready;
    public event Action<int, uint, long, byte[]>? Encoded;
    public event Action<Exception>? Failed;
    public StationAudio(Func<IVoiceDevice> factory) => this.factory = factory;
    public void Start()
    {
        lock (startGate)
        {
            if (Ready) return;
            // Disposing WASAPI waits for its callback thread. Never hold the codec/callback gate here.
            var old = device; device = null; old?.Dispose();
            var next = factory(); device = next;
            next.Captured += pcm => { if (ReferenceEquals(device, next)) Capture(pcm); }; next.Failed += error => { if (!ReferenceEquals(device, next)) return; ready = false; Failed?.Invoke(error); };
            ready = true;
            try { next.Start(); } catch { ready = false; device = null; next.Dispose(); throw; }
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
        catch (Exception error) { ready = false; Failed?.Invoke(error); }
    }
    public void Play(short[] pcm) { if (!Ready) throw new IOException("AudioUnavailable"); device!.Play(pcm); }
    public void Clear() => device?.Clear();
    public void Dispose() { lock (startGate) { ready = false; var old = device; device = null; old?.Dispose(); } }
}
