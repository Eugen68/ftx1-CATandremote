using Ftx1.Audio;
using Ftx1.Protocol;
namespace Ftx1.Client;

/// <summary>Shared desktop/Android codec pipeline; only the endpoint implementation is platform-specific.</summary>
public sealed class ClientAudio : IAsyncDisposable
{
    readonly object gate = new(); readonly StationConnection connection; readonly IVoiceDevice device;
    readonly VoiceCodec encoder; readonly AudioPlayout playout = new(new JitterBuffer(), new VoiceCodec()); readonly CancellationTokenSource cancel = new();
    Task? playback; uint sequence; long receiveEpoch, transmitEpoch; bool disposed; readonly int bitrate;
    public AudioQuality Quality => playout.Quality;
    public bool Ready { get; private set; }
    public event Action<string>? Error;
    public event Action<Exception>? Failed;
    public ClientAudio(StationConnection connection, IVoiceDevice device, int bitrate = 24000)
    {
        this.connection = connection; this.device = device; this.bitrate = bitrate; encoder = new(bitrate);
        connection.Received += Message; connection.AudioReceived += Receive; connection.ConnectionChanged += Connected;
        device.Captured += Capture; device.Failed += DeviceFailed;
    }
    public void Start() { device.Start(); playback = Task.Run(Play); if (connection.Connected) Connected(true); }
    void Connected(bool connected) { lock (gate) { Ready = false; receiveEpoch = transmitEpoch = 0; playout.Clear(); encoder.Reset(); device.Clear(); } if (connected) _ = Configure(); }
    async Task Configure() { try { await connection.Send(new() { T = "audioConfig", On = true, Bitrate = bitrate }); } catch (Exception) { Error?.Invoke("AudioUnavailable"); } }
    void Message(Message message)
    {
        lock (gate)
        {
            if (disposed) return;
            if (message.T == "audioReady") { receiveEpoch = message.AudioEpoch ?? 0; Ready = message.AudioAvailable == true; playout.Clear(); }
            if (message.T == "pttState") { long epoch = message.You == true ? message.AudioEpoch ?? 0 : 0; if (epoch != transmitEpoch) { transmitEpoch = epoch; encoder.Reset(); sequence = 0; playout.Clear(); device.Clear(); } }
        }
    }
    void Capture(short[] pcm)
    {
        lock (gate)
        {
            if (disposed || !Ready || transmitEpoch == 0 || !connection.OwnsTransmit) return;
            try { connection.QueueMicrophone(new(ProtocolVersion.Microphone, transmitEpoch, sequence++, Environment.TickCount64, encoder.Encode(pcm))); }
            catch (Exception) { DeviceFailed(new IOException("MicrophoneFailed")); }
        }
    }
    void Receive(AudioPacket packet)
    {
        lock (gate) { if (!disposed && Ready && packet.Type == ProtocolVersion.Audio && packet.Epoch == receiveEpoch) playout.Push(new(packet.Sequence, packet.Timestamp, packet.Payload)); }
    }
    async Task Play()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
        try { while (await timer.WaitForNextTickAsync(cancel.Token)) { lock (gate) { if (disposed || !Ready) continue; if (playout.Next() is { } pcm) device.Play(pcm); } } }
        catch (OperationCanceledException) { }
        catch (Exception e) { DeviceFailed(e); }
    }
    void DeviceFailed(Exception error) { Ready = false; connection.Abort(); Error?.Invoke("ClientAudioUnavailable"); Failed?.Invoke(error); }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return; disposed = true; Ready = false;
        connection.Received -= Message; connection.AudioReceived -= Receive; connection.ConnectionChanged -= Connected; device.Captured -= Capture; device.Failed -= DeviceFailed;
        if (connection.Connected) try { await connection.Ptt(false); await connection.Send(new() { T = "audioConfig", On = false }); } catch (Exception) { connection.Abort(); }
        cancel.Cancel(); if (playback != null) await playback; await Task.Run(device.Dispose);
    }
}
