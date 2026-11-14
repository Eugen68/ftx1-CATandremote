using global::Android.Media;
using global::Android.Media.Audiofx;
using Ftx1.Audio;
namespace Ftx1.Android;
public sealed class AndroidVoiceDevice : IVoiceDevice
{
    readonly AudioRecord recorder; readonly AudioTrack player; readonly AcousticEchoCanceler? echo;
    readonly object gate = new(); readonly Queue<short[]> output = []; readonly CancellationTokenSource cancel = new(); Task? capture, playback; volatile bool disposed; int generation;
    public long CapturedFrames, PlayedFrames;
    public event Action<short[]>? Captured; public event Action<Exception>? Failed;
    public AndroidVoiceDevice()
    {
        using var inputFormat = new AudioFormat.Builder().SetEncoding(Encoding.Pcm16bit)!.SetSampleRate(16000)!.SetChannelMask((ChannelOut)(int)ChannelIn.Mono)!.Build()!;
        int inBuffer = Math.Max(320 * 2 * 4, AudioRecord.GetMinBufferSize(16000, ChannelIn.Mono, Encoding.Pcm16bit));
        recorder = new AudioRecord.Builder().SetAudioSource(AudioSource.VoiceCommunication)!.SetAudioFormat(inputFormat)!.SetBufferSizeInBytes(inBuffer)!.Build()!;
        using var attrs = new AudioAttributes.Builder().SetUsage(AudioUsageKind.VoiceCommunication)!.SetContentType(AudioContentType.Speech)!.Build()!;
        using var outFormat = new AudioFormat.Builder().SetEncoding(Encoding.Pcm16bit)!.SetSampleRate(16000)!.SetChannelMask(ChannelOut.Mono)!.Build()!;
        int outBuffer = Math.Max(320 * 2 * 4, AudioTrack.GetMinBufferSize(16000, ChannelOut.Mono, Encoding.Pcm16bit));
        player = new AudioTrack.Builder().SetAudioAttributes(attrs)!.SetAudioFormat(outFormat)!.SetBufferSizeInBytes(outBuffer)!.SetTransferMode(AudioTrackMode.Stream)!.Build()!;
        if (recorder.State != State.Initialized || player.State != AudioTrackState.Initialized) { recorder.Dispose(); player.Dispose(); throw new IOException("AudioUnavailable"); }
        if (AcousticEchoCanceler.IsAvailable) { echo = AcousticEchoCanceler.Create(recorder.AudioSessionId); echo?.SetEnabled(true); }
    }
    public void Start() { recorder.StartRecording(); player.Play(); capture = Task.Run(CaptureLoop); playback = Task.Run(PlayLoop); }
    void CaptureLoop()
    {
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                int epoch = Volatile.Read(ref generation); var pcm = new short[320]; int offset = 0;
                while (offset < pcm.Length && !cancel.IsCancellationRequested) { int read = recorder.Read(pcm, offset, pcm.Length - offset, (int)AudioRecordReadOptions.Blocking); if (read <= 0) { if (cancel.IsCancellationRequested) return; throw new IOException("MicrophoneFailed"); } offset += read; }
                if (!cancel.IsCancellationRequested && epoch == Volatile.Read(ref generation)) { Interlocked.Increment(ref CapturedFrames); Captured?.Invoke(pcm); }
            }
        }
        catch (Exception e) { if (!disposed) Failed?.Invoke(e); }
    }
    void PlayLoop()
    {
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                short[]? pcm; lock (gate) { while (output.Count == 0 && !cancel.IsCancellationRequested) Monitor.Wait(gate, 100); pcm = output.Count > 0 ? output.Dequeue() : null; }
                if (pcm == null) continue;
                int epoch = Volatile.Read(ref generation), offset = 0;
                while (offset < pcm.Length && !cancel.IsCancellationRequested)
                {
                    int sent;
                    lock (gate) { if (disposed || epoch != Volatile.Read(ref generation)) break; sent = player.Write(pcm, offset, pcm.Length - offset, WriteMode.NonBlocking); }
                    if (sent < 0) throw new IOException("AudioUnavailable"); offset += sent; if (sent == 0) Thread.Sleep(2);
                }
                if (offset == pcm.Length) Interlocked.Increment(ref PlayedFrames);
            }
        }
        catch (Exception e) { if (!disposed) Failed?.Invoke(e); }
    }
    public void Play(short[] pcm) { lock (gate) { if (disposed) return; while (output.Count >= 3) output.Dequeue(); output.Enqueue(pcm); Monitor.PulseAll(gate); } }
    public void Clear() { Interlocked.Increment(ref generation); lock (gate) { output.Clear(); if (!disposed) { player.Pause(); player.Flush(); player.Play(); } } }
    public void Dispose()
    {
        if (disposed) return; disposed = true; cancel.Cancel(); lock (gate) { output.Clear(); Monitor.PulseAll(gate); }
        try { recorder.Stop(); player.Pause(); player.Flush(); } catch (Java.Lang.IllegalStateException) { }
        // Stop unblocks AudioRecord.Read. Avoid disposing JNI objects while a worker still uses them.
        _ = Task.Run(async () => { if (capture != null) await capture; if (playback != null) await playback; echo?.Dispose(); recorder.Release(); recorder.Dispose(); player.Release(); player.Dispose(); });
    }
}
