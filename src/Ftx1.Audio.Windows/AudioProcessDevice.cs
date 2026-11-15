using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
namespace Ftx1.Audio;

public sealed class AudioProcessDevice : IVoiceDevice
{
    readonly ProcessStartInfo start; readonly int shutdownMs; readonly CancellationTokenSource cancel = new();
    readonly Channel<AudioPipe.Frame> controls = Channel.CreateUnbounded<AudioPipe.Frame>();
    readonly Channel<AudioPipe.Frame> playback = Channel.CreateBounded<AudioPipe.Frame>(new BoundedChannelOptions(3) { FullMode = BoundedChannelFullMode.DropOldest });
    readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    Process? process; Task? read, write, watchdog; volatile bool disposed; long lastFrame; int reported; string? logFile;
    public string Description { get; private set; } = "";
    public int WorkerId => process?.Id ?? 0;
    public event Action<short[]>? Captured;
    public event Action<Exception>? Failed;
    public AudioProcessDevice(ProcessStartInfo start, int shutdownMs = 2000)
    {
        this.start = start; this.shutdownMs = shutdownMs;
        start.UseShellExecute = false; start.CreateNoWindow = true; start.RedirectStandardInput = start.RedirectStandardOutput = start.RedirectStandardError = true;
    }
    public static AudioProcessDevice CreateSelf(bool station, string? input, string? output, string logFile)
    {
        string host = Environment.ProcessPath ?? throw new IOException("AudioWorkerUnavailable"); var info = new ProcessStartInfo(host);
        if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, System.Reflection.Assembly.GetEntryAssembly()!.GetName().Name + ".dll"));
        info.ArgumentList.Add("--audio-worker"); if (station) info.ArgumentList.Add("--audio-station");
        if (!string.IsNullOrEmpty(input)) { info.ArgumentList.Add("--audio-input"); info.ArgumentList.Add(input); }
        if (!string.IsNullOrEmpty(output)) { info.ArgumentList.Add("--audio-output"); info.ArgumentList.Add(output); }
        info.ArgumentList.Add("--audio-log"); info.ArgumentList.Add(logFile);
        return new(info) { logFile = logFile };
    }
    public void Start()
    {
        if (process != null) throw new InvalidOperationException("AudioAlreadyStarted");
        process = Process.Start(start) ?? throw new IOException("AudioWorkerUnavailable");
        process.ErrorDataReceived += (_, _) => { }; process.BeginErrorReadLine(); lastFrame = Environment.TickCount64;
        read = Task.Run(Read); write = Task.Run(Write); watchdog = Task.Run(Watchdog);
        try { ready.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult(); }
        catch { Dispose(); throw; }
    }
    async Task Read()
    {
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                var frame = await AudioPipe.Read(process!.StandardOutput.BaseStream, cancel.Token); if (frame == null) throw new IOException("AudioWorkerExited");
                lastFrame = Environment.TickCount64;
                if (frame.Type == AudioPipe.Ready) { Description = Encoding.UTF8.GetString(frame.Bytes); ready.TrySetResult(); }
                else if (frame.Type == AudioPipe.Capture) { if (frame.Bytes.Length != 640) throw new InvalidDataException("InvalidAudioWorkerFrame"); var samples = new short[320]; Buffer.BlockCopy(frame.Bytes, 0, samples, 0, 640); Captured?.Invoke(samples); }
                else if (frame.Type == AudioPipe.Error) throw new IOException(Encoding.UTF8.GetString(frame.Bytes));
            }
        }
        catch (Exception error) { if (!disposed) Fault(error); }
    }
    async Task Write()
    {
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                if (controls.Reader.TryRead(out var control)) { await AudioPipe.Write(process!.StandardInput.BaseStream, control, cancel.Token); continue; }
                if (playback.Reader.TryRead(out var audio)) { await AudioPipe.Write(process!.StandardInput.BaseStream, audio, cancel.Token); continue; }
                await Task.Delay(3, cancel.Token);
            }
        }
        catch (Exception error) { if (!disposed) Fault(error); }
    }
    async Task Watchdog()
    {
        try { while (!cancel.IsCancellationRequested) { await Task.Delay(500, cancel.Token); if (ready.Task.IsCompletedSuccessfully && Environment.TickCount64 - lastFrame > 3500) { Fault(new IOException("AudioWorkerNotResponding")); return; } } }
        catch (OperationCanceledException) { }
    }
    void Fault(Exception error)
    {
        ready.TrySetException(error); if (Interlocked.Exchange(ref reported, 1) != 0) return;
        try { if (logFile != null) File.AppendAllText(logFile, DateTimeOffset.Now.ToString("O") + " " + error + Environment.NewLine); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        Failed?.Invoke(error);
    }
    public void Play(short[] samples) { if (disposed) return; if (samples.Length != 320) throw new ArgumentException("Expected20ms"); var bytes = new byte[640]; Buffer.BlockCopy(samples, 0, bytes, 0, 640); playback.Writer.TryWrite(new(AudioPipe.Play, bytes)); }
    public void Clear() { while (playback.Reader.TryRead(out _)) { } if (!disposed) controls.Writer.TryWrite(new(AudioPipe.Clear, [])); }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        try
        {
            controls.Writer.TryWrite(new(AudioPipe.Stop, []));
            if (process != null && !process.HasExited && !process.WaitForExit(shutdownMs)) { process.Kill(true); process.WaitForExit(1000); }
        }
        finally { cancel.Cancel(); try { process?.StandardInput.Close(); } catch (IOException) { } }
        // Reader/writer loops observe cancellation/closed pipes; the parent never waits on a WASAPI thread.
    }
}

public static class AudioPipe
{
    public const byte Ready = 1, Capture = 2, Play = 3, Clear = 4, Stop = 5, Error = 6;
    public sealed record Frame(byte Type, byte[] Bytes);
    public static async Task Write(Stream stream, Frame frame, CancellationToken token)
    {
        var header = new byte[5]; header[0] = frame.Type; System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(1), frame.Bytes.Length);
        await stream.WriteAsync(header, token); await stream.WriteAsync(frame.Bytes, token); await stream.FlushAsync(token);
    }
    public static async Task<Frame?> Read(Stream stream, CancellationToken token)
    {
        var header = new byte[5]; int first = await stream.ReadAsync(header.AsMemory(0, 1), token); if (first == 0) return null;
        await stream.ReadExactlyAsync(header.AsMemory(1), token); int length = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1));
        if (length is < 0 or > 4096) throw new InvalidDataException("InvalidAudioWorkerFrame"); var bytes = new byte[length]; await stream.ReadExactlyAsync(bytes, token); return new(header[0], bytes);
    }
}
