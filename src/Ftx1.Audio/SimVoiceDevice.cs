namespace Ftx1.Audio;

/// <summary>Deterministic receiver noise + tones, and a 200 ms delayed microphone echo for round-trip tests.</summary>
public sealed class SimVoiceDevice : IVoiceDevice
{
    readonly object gate = new(); readonly Queue<(long Due, short[] Pcm)> echo = []; readonly CancellationTokenSource cancel = new(); Task? task; long sample;
    public event Action<short[]>? Captured;
    public event Action<Exception>? Failed;
    public void Start() => task ??= Task.Run(Run);
    async Task Run()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20)); var random = new Random(84);
        try
        {
            while (await timer.WaitForNextTickAsync(cancel.Token))
            {
                var pcm = new short[320]; for (int i = 0; i < pcm.Length; i++, sample++) pcm[i] = (short)(900 * Math.Sin(2 * Math.PI * 700 * sample / 16000) + 300 * Math.Sin(2 * Math.PI * 1200 * sample / 16000) + random.Next(-70, 71));
                lock (gate) { if (echo.TryPeek(out var item) && item.Due <= Environment.TickCount64) { echo.Dequeue(); for (int i = 0; i < pcm.Length; i++) pcm[i] = (short)Math.Clamp(pcm[i] + item.Pcm[i] * .65, short.MinValue, short.MaxValue); } }
                Captured?.Invoke(pcm);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Failed?.Invoke(e); }
    }
    public void Play(short[] samples) { lock (gate) { if (echo.Count >= 16) echo.Dequeue(); echo.Enqueue((Environment.TickCount64 + 200, (short[])samples.Clone())); } }
    public void Clear() { lock (gate) echo.Clear(); }
    public void Dispose() { cancel.Cancel(); }
}
