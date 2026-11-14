using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.Wave;
namespace Ftx1.Audio;

public static class AudioDevices
{
    public static IReadOnlyList<AudioDeviceInfo> List(bool input)
    {
        if (!OperatingSystem.IsWindows()) return [];
        return ListWindows(input);
    }
    [SupportedOSPlatform("windows")]
    static AudioDeviceInfo[] ListWindows(bool input) { using var e = new MMDeviceEnumerator(); var devices = e.EnumerateAudioEndPoints(input ? DataFlow.Capture : DataFlow.Render, DeviceState.Active); return devices.Select(d => { using (d) return new AudioDeviceInfo(d.ID, d.FriendlyName); }).ToArray(); }
    public static string? GuessCodec(IEnumerable<AudioDeviceInfo> devices) => devices.OrderBy(d => d.Name.Contains("usb audio codec", StringComparison.OrdinalIgnoreCase) ? 0 : 1).FirstOrDefault(d => d.Name.Contains("usb audio codec", StringComparison.OrdinalIgnoreCase) || d.Name.Contains("usb audio device", StringComparison.OrdinalIgnoreCase))?.Id;
    public static IVoiceDevice Create(bool station, string? inputId = null, string? outputId = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("AudioWindowsOnly");
        if (station) { inputId = string.IsNullOrEmpty(inputId) ? GuessCodec(List(true)) : inputId; outputId = string.IsNullOrEmpty(outputId) ? GuessCodec(List(false)) : outputId; if (inputId == null || outputId == null) throw new InvalidOperationException("UsbCodecNotFound"); }
        return new WasapiVoiceDevice(inputId, outputId);
    }
}

/// <summary>WASAPI shared mode; hardware mix rate (normally 48 kHz) downsampled with WDL to 16 kHz mono.</summary>
[SupportedOSPlatform("windows")]
public sealed class WasapiVoiceDevice : IVoiceDevice
{
    readonly object gate = new(); readonly MMDevice input, output; readonly WasapiCapture capture; readonly WasapiOut playback;
    readonly BufferedWaveProvider speaker; readonly CaptureFramer framer;
    volatile bool disposed;
    public event Action<short[]>? Captured;
    public event Action<Exception>? Failed;
    public WasapiVoiceDevice(string? inputId, string? outputId)
    {
        using var e = new MMDeviceEnumerator(); input = string.IsNullOrEmpty(inputId) ? e.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications) : e.GetDevice(inputId); output = string.IsNullOrEmpty(outputId) ? e.GetDefaultAudioEndpoint(DataFlow.Render, Role.Communications) : e.GetDevice(outputId);
        try
        {
            capture = new WasapiCapture(input, true, 20);
            framer = new(capture.WaveFormat);
            speaker = new BufferedWaveProvider(new WaveFormat(VoiceCodec.Rate, 16, 1)) { BufferDuration = TimeSpan.FromMilliseconds(120), DiscardOnBufferOverflow = true, ReadFully = true };
            // NAudio's shared-mode WasapiOut resamples to the endpoint's 48 kHz mix format.
            playback = new WasapiOut(output, AudioClientShareMode.Shared, true, 40); playback.Init(speaker);
            capture.DataAvailable += OnData;
            capture.RecordingStopped += (_, args) => { if (!disposed && args.Exception != null) Failed?.Invoke(args.Exception); };
            playback.PlaybackStopped += (_, args) => { if (!disposed && args.Exception != null) Failed?.Invoke(args.Exception); };
        }
        catch { capture?.Dispose(); playback?.Dispose(); input.Dispose(); output.Dispose(); throw; }
    }
    public void Start() { playback.Play(); capture.StartRecording(); }
    void OnData(object? sender, WaveInEventArgs args)
    {
        try
        {
            IReadOnlyList<short[]> ready;
            lock (gate) { if (disposed) return; ready = framer.Push(args.Buffer, args.BytesRecorded); }
            foreach (var shorts in ready) Captured?.Invoke(shorts);
        }
        catch (Exception e) { Failed?.Invoke(e); }
    }
    public void Play(short[] samples) { var bytes = new byte[samples.Length * 2]; Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length); lock (gate) { if (disposed) return; if (speaker.BufferedDuration.TotalMilliseconds > 80) speaker.ClearBuffer(); speaker.AddSamples(bytes, 0, bytes.Length); } }
    public void Clear() { lock (gate) { if (!disposed) { speaker.ClearBuffer(); framer.Clear(); } } }
    public void Dispose() { if (disposed) return; disposed = true; capture.StopRecording(); playback.Stop(); capture.Dispose(); playback.Dispose(); input.Dispose(); output.Dispose(); }
}
