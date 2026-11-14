using System.Diagnostics;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Buffers.Binary;
using Ftx1.Audio;
using Ftx1.Client;
using Ftx1.Core.Safety;
using Ftx1.Protocol;
using Xunit;
using NAudio.Wave;
namespace Ftx1.Network.Tests;

public class AudioTests
{
    [Theory]
    [InlineData(12000)] [InlineData(24000)] [InlineData(32000)]
    public void OpusRoundTripPreservesToneFrequencyAndLevel(int bitrate)
    {
        var codec = new VoiceCodec(bitrate); var decoded = new List<double>();
        for (int frame = 0; frame < 50; frame++)
        {
            var pcm = Enumerable.Range(0, 320).Select(i => (short)(8000 * Math.Sin(2 * Math.PI * 700 * (frame * 320 + i) / 16000))).ToArray(); var packet = codec.Encode(pcm); Assert.InRange(packet.Length, 1, 200); var result = codec.Decode(packet); Assert.Equal(320, result.Length); if (frame > 10) decoded.AddRange(result.Select(x => (double)x));
        }
        double rms = Math.Sqrt(decoded.Average(x => x * x)); Assert.InRange(rms, 3000, 8000);
        double Energy(double hz) { double re = 0, im = 0; for (int i = 0; i < decoded.Count; i++) { re += decoded[i] * Math.Cos(2 * Math.PI * hz * i / 16000); im += decoded[i] * Math.Sin(2 * Math.PI * hz * i / 16000); } return re * re + im * im; }
        Assert.True(Energy(700) > 30 * Energy(600)); Assert.True(Energy(700) > 30 * Energy(800));
    }
    [Theory]
    [InlineData(48000)] [InlineData(44100)]
    public void StereoEndpointCaptureResamplesContinuouslyWithoutPaddingSilence(int sampleRate)
    {
        var framer = new CaptureFramer(WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2)); var output = new List<short>(); long sample = 0;
        for (int callback = 0; callback < 100; callback++)
        {
            var pcm = new float[sampleRate / 100 * 2];
            for (int i = 0; i < pcm.Length; i += 2, sample++) pcm[i] = pcm[i + 1] = (float)(.25 * Math.Sin(2 * Math.PI * 700 * sample / sampleRate));
            var bytes = new byte[pcm.Length * 4]; Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length); foreach (var frame in framer.Push(bytes, bytes.Length)) { Assert.Equal(320, frame.Length); output.AddRange(frame); }
        }
        Assert.InRange(output.Count, 15000, 16000); Assert.InRange(Math.Sqrt(output.Skip(320).Average(x => (double)x * x)), 5000, 6500);
        int crossings = 0; for (int i = 321; i < output.Count; i++) if (output[i - 1] < 0 && output[i] >= 0) crossings++;
        Assert.InRange(crossings * 16000d / (output.Count - 321), 695, 705);
        framer.Clear(); Assert.Empty(framer.Push([], 0));
    }
    [Fact]
    public void CodecRejectsUnsupportedFrameAndBitrate() { Assert.Throws<ArgumentOutOfRangeException>(() => new VoiceCodec(64000)); Assert.Throws<ArgumentException>(() => new VoiceCodec().Encode(new short[160])); Assert.Throws<InvalidDataException>(() => new VoiceCodec().Decode(new byte[1276])); }
    [Fact]
    public void BinaryHeaderRoundTripsAndRejectsWrongTypeOrOversize()
    {
        var value = new AudioPacket(ProtocolVersion.Microphone, 17, 34, 567, [1, 2, 3]); var result = AudioPacket.Decode(value.Encode()); Assert.Equal(value.Epoch, result.Epoch); Assert.Equal(value.Sequence, result.Sequence); Assert.Equal(value.Timestamp, result.Timestamp); Assert.Equal(value.Payload, result.Payload);
        Assert.Throws<InvalidDataException>(() => AudioPacket.Decode(new byte[21])); var invalid = value.Encode(); invalid[0] = 2; Assert.Throws<InvalidDataException>(() => AudioPacket.Decode(invalid)); invalid = value.Encode(); BinaryPrimitives.WriteInt64LittleEndian(invalid.AsSpan(1), 0); Assert.Throws<InvalidDataException>(() => AudioPacket.Decode(invalid));
    }
    [Fact]
    public void JitterWaitsThenPlaysInOrderAndDiscardsLateFrames()
    {
        long now = 1; var buffer = new JitterBuffer(() => now); buffer.Push(new(0, 0, [10])); now = 21; buffer.Push(new(1, 20, [11])); now = 41; buffer.Push(new(2, 40, [12])); Assert.Null(buffer.Pull()); now = 61; Assert.Equal(new byte[] { 10 }, buffer.Pull()); Assert.Equal(new byte[] { 11 }, buffer.Pull()); buffer.Push(new(0, 0, [99])); Assert.Equal(1, buffer.Quality.Late); Assert.Equal(new byte[] { 12 }, buffer.Pull()); Assert.Empty(buffer.Pull()!); Assert.Equal(1, buffer.Quality.Lost);
    }
    [Fact]
    public void JitterHandlesLossAndBurstWithoutGrowingLatency()
    {
        long now = 1; var buffer = new JitterBuffer(() => now);
        for (uint i = 0; i < 100; i++) { now += i % 5 == 0 ? 80 : 1; if (i % 7 != 0) buffer.Push(new(i, i * 20, [(byte)i])); Assert.InRange(buffer.Quality.BufferedMs, 0, 120); Assert.InRange(buffer.Quality.TargetMs, 60, 120); }
        Assert.True(buffer.Quality.Late > 0); now += 200; var packet = buffer.Pull(); Assert.NotNull(packet); Assert.True(packet![0] >= 94); buffer.Clear(); Assert.Null(buffer.Pull());
    }
    [Fact]
    public void AudioDeadlineDoesNotDependOnControlHeartbeatOrInvalidSender()
    {
        double now = 0; int stopped = 0; using var guard = new TransmitGuard(() => stopped++, clock: () => now); StopReason? reason = null; guard.Stopped += r => reason = r; using var lease = guard.TryAcquire("a", () => { }, true); guard.RequireAudio("a"); now = .8; guard.Heartbeat("a"); guard.AudioHeartbeat("b"); now = 1.01; guard.Tick(); Assert.Null(guard.Owner); Assert.Equal(StopReason.LostAudio, reason); Assert.True(stopped > 0);
    }
    [Fact]
    public async Task SimulatedRxAndMicrophoneEchoCrossRealTlsAtEveryBitrate()
    {
        await using var f = new StationFixture(); await f.Start();
        foreach (int bitrate in new[] { 12000, 24000, 32000 })
        {
            var pair = f.Server.Tokens.Create("audio" + bitrate); await using var c = new StationConnection(new(f.Server.Address!.ToString(), f.Server.Certificate.Fingerprint, pair.DeviceToken)); var endpoint = new ProbeDevice(); await using var audio = new ClientAudio(c, endpoint, bitrate); audio.Start(); await c.Start(); await StationFixture.Wait(() => audio.Ready && endpoint.Received > 5, 10000);
            await c.Ptt(true); await StationFixture.Wait(() => c.OwnsTransmit); await Task.Delay(500); Assert.Equal("1", f.Radio.State["TX"]); Assert.True(endpoint.Max > 2500, "The 700 Hz simulated RX is <1500; the delayed microphone echo should be louder."); await c.Ptt(false); await StationFixture.Wait(() => f.Radio.State["TX"] == "0");
        }
    }
    [Fact]
    public async Task MicrophoneStopsButControlHeartbeatContinues_TxStopsWithinDeadline()
    {
        await using var f = new StationFixture(); await f.Start(); var pair = f.Server.Tokens.Create("audio-deadman"); await using var c = new StationConnection(new(f.Server.Address!.ToString(), f.Server.Certificate.Fingerprint, pair.DeviceToken)); var endpoint = new ProbeDevice(); await using var audio = new ClientAudio(c, endpoint); audio.Start(); await c.Start(); await StationFixture.Wait(() => audio.Ready, 10000);
        var stopped = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously); c.Received += m => { if (m.T == "pttState" && m.Reason != null) stopped.TrySetResult(m.Reason); }; await c.Ptt(true); await StationFixture.Wait(() => c.OwnsTransmit); await Task.Delay(200); endpoint.CaptureEnabled = false; var watch = Stopwatch.StartNew(); Assert.Equal("LostAudio", await stopped.Task.WaitAsync(TimeSpan.FromSeconds(1.5))); Assert.InRange(watch.Elapsed.TotalSeconds, .7, 1.3); Assert.Equal("0", f.Radio.State["TX"]); Assert.True(c.Connected); Assert.Contains("LostAudio", File.ReadAllText(Path.Combine(f.Root, "transmit.jsonl")));
    }
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task StaleEpochOrNonOwnerCannotKeepTxAliveEvenWithValidControlHeartbeats(bool staleEpoch)
    {
        await using var f = new StationFixture(); await f.Start();
        async Task<ClientWebSocket> ConnectRaw(string name)
        {
            var pair = f.Server.Tokens.Create(name); var ws = new ClientWebSocket(); ws.Options.RemoteCertificateValidationCallback = (_, cert, _, _) => cert != null && Convert.ToHexString(SHA256.HashData(cert.GetRawCertData())) == f.Server.Certificate.Fingerprint;
            await ws.ConnectAsync(f.Server.Address!, CancellationToken.None); await Send(ws, new() { T = "hello", ProtocolVersion = ProtocolVersion.Current, DeviceToken = pair.DeviceToken }); await Read(ws, m => m.T == "helloOk"); await Send(ws, new() { T = "audioConfig", On = true }); await Read(ws, m => m.T == "audioReady"); return ws;
        }
        static Task Send(ClientWebSocket ws, Message m) => ws.SendAsync(Wire.Encode(m), WebSocketMessageType.Text, true, CancellationToken.None);
        static async Task<Message> Read(ClientWebSocket ws, Func<Message, bool> predicate)
        {
            using var deadline = new CancellationTokenSource(5000);
            while (true) { var frame = await SocketFrames.ReceiveFrame(ws, deadline.Token); Assert.NotNull(frame); if (frame.Binary) continue; var m = Wire.Decode(frame.Bytes); if (predicate(m)) return m; }
        }
        using var owner = await ConnectRaw("owner"); using var other = await ConnectRaw("other");
        await Send(owner, new() { T = "pttRequest", On = true }); long old = (await Read(owner, m => m.T == "pttState" && m.You == true)).AudioEpoch!.Value;
        await Send(owner, new() { T = "pttRequest", On = false }); await Read(owner, m => m.T == "pttState" && m.You == false);
        await Send(owner, new() { T = "pttRequest", On = true }); long current = (await Read(owner, m => m.T == "pttState" && m.You == true)).AudioEpoch!.Value; Assert.NotEqual(old, current);
        var codec = new VoiceCodec(); var watch = Stopwatch.StartNew(); uint sequence = 0;
        while (watch.Elapsed.TotalSeconds < 1.25)
        {
            await Send(owner, new() { T = "pttHeartbeat" }); var packet = new AudioPacket(ProtocolVersion.Microphone, staleEpoch ? old : current, sequence++, Environment.TickCount64, codec.Encode(new short[320]));
            await (staleEpoch ? owner : other).SendAsync(packet.Encode(), WebSocketMessageType.Binary, true, CancellationToken.None); await Task.Delay(100);
        }
        Assert.Null(f.Server.Station.Transmit.Owner); Assert.Equal("0", f.Radio.State["TX"]); Assert.Contains("LostAudio", File.ReadAllText(Path.Combine(f.Root, "transmit.jsonl")));
    }
    [Fact]
    public async Task DisablingAudioDuringTxReleasesPtt()
    {
        await using var f = new StationFixture(); await f.Start(); await using var c = await f.Client(); await c.Ptt(true); await StationFixture.Wait(() => c.OwnsTransmit); await c.Send(new() { T = "audioConfig", On = false }); await StationFixture.Wait(() => f.Radio.State["TX"] == "0"); Assert.Null(f.Server.Station.Transmit.Owner);
    }
    [Fact]
    public void UsbDetectionPrefersCodecAndNeverChoosesUnrelatedSpeakers()
    {
        AudioDeviceInfo[] devices = [new("a", "Speakers"), new("b", "USB Audio Device"), new("c", "USB Audio CODEC")]; Assert.Equal("c", AudioDevices.GuessCodec(devices)); Assert.Null(AudioDevices.GuessCodec(devices[..1]));
    }
    sealed class ProbeDevice : IVoiceDevice
    {
        readonly SimVoiceDevice clock = new(); long sample; public volatile bool CaptureEnabled = true; public int Received, Max;
        public event Action<short[]>? Captured; public event Action<Exception>? Failed;
        public ProbeDevice() { clock.Captured += _ => { if (!CaptureEnabled) return; var pcm = new short[320]; for (int i = 0; i < pcm.Length; i++, sample++) pcm[i] = (short)(9000 * Math.Sin(2 * Math.PI * 1100 * sample / 16000)); Captured?.Invoke(pcm); }; clock.Failed += e => Failed?.Invoke(e); }
        public void Start() => clock.Start(); public void Play(short[] pcm) { Interlocked.Increment(ref Received); Max = Math.Max(Max, pcm.Max(x => Math.Abs((int)x))); } public void Clear() { } public void Dispose() => clock.Dispose();
    }
}
