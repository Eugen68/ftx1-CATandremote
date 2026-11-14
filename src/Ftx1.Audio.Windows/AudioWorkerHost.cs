using System.Text;
using System.Threading.Channels;
namespace Ftx1.Audio;
/// <summary>Run before Avalonia, on the executable's STA main thread. No CAT or network access in this process.</summary>
public static class AudioWorkerHost
{
    public static bool TryRun(string[] args)
    {
        if (!args.Contains("--audio-worker")) return false;
        string? Value(string key) { int index = Array.IndexOf(args, key); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null; }
        string? logFile = Value("--audio-log");
        void Log(Exception error) { try { if (logFile != null) { Directory.CreateDirectory(Path.GetDirectoryName(logFile)!); File.AppendAllText(logFile, DateTimeOffset.Now.ToString("O") + " " + error + Environment.NewLine); } } catch { } }
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { if (e.ExceptionObject is Exception error) Log(error); };
        try { Run(() => args.Contains("--audio-worker-simulate") ? new SimVoiceDevice() : AudioDevices.Create(args.Contains("--audio-station"), Value("--audio-input"), Value("--audio-output"))); }
        catch (Exception error) { Log(error); Environment.ExitCode = 2; }
        return true;
    }
    public static void Run(Func<IVoiceDevice> factory, bool hangOnStop = false)
    {
        var input = Console.OpenStandardInput(); var output = Console.OpenStandardOutput(); var gate = new object();
        void Send(byte type, byte[] bytes) { lock (gate) AudioPipe.Write(output, new(type, bytes), CancellationToken.None).GetAwaiter().GetResult(); }
        IVoiceDevice? device = null; bool ready = false; using var cancel = new CancellationTokenSource();
        var captured = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(3) { FullMode = BoundedChannelFullMode.DropOldest });
        try
        {
            device = factory(); device.Captured += samples => { if (!ready) return; var bytes = new byte[640]; Buffer.BlockCopy(samples, 0, bytes, 0, 640); captured.Writer.TryWrite(bytes); };
            device.Failed += error => { Send(AudioPipe.Error, Encoding.UTF8.GetBytes(AudioDiagnostic.Describe(error))[..Math.Min(4000, Encoding.UTF8.GetByteCount(AudioDiagnostic.Describe(error)))]); };
            device.Start(); string description = OperatingSystem.IsWindows() && device is WasapiVoiceDevice wasapi ? wasapi.Description : device.GetType().Name;
            Send(AudioPipe.Ready, Encoding.UTF8.GetBytes(description)); ready = true;
            _ = Task.Run(async () => { try { await foreach (var bytes in captured.Reader.ReadAllAsync(cancel.Token)) Send(AudioPipe.Capture, bytes); } catch (Exception) { } });
            while (true)
            {
                var frame = AudioPipe.Read(input, CancellationToken.None).GetAwaiter().GetResult(); if (frame == null || frame.Type == AudioPipe.Stop) break;
                if (frame.Type == AudioPipe.Clear) device.Clear();
                else if (frame.Type == AudioPipe.Play && frame.Bytes.Length == 640) { var samples = new short[320]; Buffer.BlockCopy(frame.Bytes, 0, samples, 0, 640); device.Play(samples); }
                else throw new InvalidDataException("InvalidAudioWorkerCommand");
            }
        }
        catch (Exception error) { try { var bytes = Encoding.UTF8.GetBytes(AudioDiagnostic.Describe(error)); Send(AudioPipe.Error, bytes[..Math.Min(4000, bytes.Length)]); } catch { } throw; }
        finally { ready = false; cancel.Cancel(); if (hangOnStop) Thread.Sleep(Timeout.Infinite); device?.Dispose(); }
    }
}
