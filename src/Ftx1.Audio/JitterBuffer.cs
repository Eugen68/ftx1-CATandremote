namespace Ftx1.Audio;

public sealed record BufferedAudio(uint Sequence, long Timestamp, byte[] Payload);
public sealed record AudioQuality(int TargetMs, int BufferedMs, long Lost, long Late, double JitterMs);
/// <summary>Bounded, monotonic 20 ms playout. Late frames never extend the queue.</summary>
public sealed class JitterBuffer
{
    readonly object gate = new(); readonly SortedDictionary<uint, BufferedAudio> frames = []; readonly Func<long> now;
    uint next; bool playing, initialized; long firstArrival, previousArrival, previousTimestamp, lost, late; double jitter;
    public JitterBuffer(Func<long>? clock = null) => now = clock ?? (() => Environment.TickCount64);
    int Target => Math.Clamp((int)Math.Ceiling((60 + 4 * jitter) / 20) * 20, 60, 120);
    public AudioQuality Quality { get { lock (gate) return new(Target, frames.Count * 20, lost, late, jitter); } }
    public void Push(BufferedAudio frame)
    {
        lock (gate)
        {
            long arrival = now();
            if (initialized && frame.Sequence < next) { late++; return; }
            if (frames.ContainsKey(frame.Sequence)) return;
            if (!initialized) { initialized = true; next = frame.Sequence; firstArrival = arrival; }
            if (previousArrival != 0) jitter += (Math.Abs((arrival - previousArrival) - (frame.Timestamp - previousTimestamp)) - jitter) / 16;
            previousArrival = arrival; previousTimestamp = frame.Timestamp;
            frames[frame.Sequence] = frame;
            // TCP head-of-line recovery can deliver an old burst: keep at most 120 ms, skip the backlog.
            while (frames.Count > 6 || (frames.Count > 1 && frames.Keys.Last() - frames.Keys.First() >= 6)) { frames.Remove(frames.Keys.First()); late++; next = frames.Keys.First(); }
        }
    }
    public byte[]? Pull()
    {
        lock (gate)
        {
            if (!initialized) return null;
            if (!playing) { if (now() - firstArrival < Target) return null; playing = true; }
            if (frames.Remove(next++, out var frame)) return frame.Payload;
            lost++;
            if (frames.Count == 0 && now() - previousArrival > 240) { initialized = playing = false; }
            return [];
        }
    }
    public void Clear() { lock (gate) { frames.Clear(); initialized = playing = false; previousArrival = previousTimestamp = 0; jitter = 0; } }
}
