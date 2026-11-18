namespace Ftx1.Audio;

public sealed record BufferedAudio(uint Sequence, long Timestamp, byte[] Payload);
/// <summary>Next frame to decode. Empty payload = lost (conceal); <see cref="Fec"/> = payload is the FOLLOWING packet, decode its FEC.</summary>
public readonly record struct PlayoutFrame(byte[] Payload, bool Fec) { public bool Missing => Payload.Length == 0; }
public sealed record AudioQuality(int TargetMs, int BufferedMs, long Lost, long Late, double JitterMs, long Recovered = 0, long Bursts = 0, long Dropped = 0, long Skipped = 0);

/// <summary>Adaptive 20 ms playout. The target follows jitter AND recent stalls (TCP head-of-line on mobile links:
/// nothing for 200–500 ms, then a burst). A burst is kept up to <c>maxMs</c> instead of being thrown away;
/// the caller catches up gently using <see cref="ExcessMs"/>. Stalls are remembered for a minute: on 4G they recur
/// every ~30 s, and a shorter memory let the target fall back between them, so each new stall became an audible gap.</summary>
public sealed class JitterBuffer
{
    public const int FrameMs = 20, MinMs = 60, DefaultMaxMs = 400, StallMs = 60, MemoryMs = 60_000;
    readonly object gate = new(); readonly SortedDictionary<uint, BufferedAudio> frames = []; readonly Func<long> now; readonly int maxMs;
    readonly Queue<(long At, int Ms)> stalls = new();
    uint next; bool playing, initialized; long firstArrival, previousArrival, previousTimestamp, lost, late, recovered, bursts, dropped; double jitter;
    public JitterBuffer(Func<long>? clock = null, int maxMs = DefaultMaxMs)
    {
        if (maxMs < MinMs) throw new ArgumentOutOfRangeException(nameof(maxMs));
        now = clock ?? (() => Environment.TickCount64); this.maxMs = maxMs;
    }
    public int MaxMs => maxMs;
    int Target
    {
        get
        {
            long t = now(); while (stalls.Count > 0 && t - stalls.Peek().At > MemoryMs) stalls.Dequeue();
            int stall = 0; foreach (var s in stalls) stall = Math.Max(stall, s.Ms);
            double wanted = Math.Max(MinMs + 4 * jitter, stall + FrameMs);
            return Math.Clamp((int)Math.Ceiling(wanted / FrameMs) * FrameMs, MinMs, maxMs);
        }
    }
    public AudioQuality Quality { get { lock (gate) return new(Target, frames.Count * FrameMs, lost, late, jitter, recovered, bursts, dropped); } }
    /// <summary>How far the buffer runs ahead of its target, in ms (≤ 0 when not playing).</summary>
    public int ExcessMs { get { lock (gate) return playing ? frames.Count * FrameMs - Target : 0; } }
    public void Push(BufferedAudio frame)
    {
        lock (gate)
        {
            long arrival = now();
            if (initialized && frame.Sequence < next) { late++; return; }
            if (frames.ContainsKey(frame.Sequence)) return;
            if (!initialized) { initialized = true; next = frame.Sequence; firstArrival = arrival; }
            if (previousArrival != 0)
            {
                long expected = frame.Timestamp - previousTimestamp, actual = arrival - previousArrival;
                if (expected > 0 && actual - expected > StallMs) { stalls.Enqueue((arrival, (int)Math.Min(actual - expected, maxMs))); bursts++; }
                jitter += (Math.Abs(actual - expected) - jitter) / 16;
            }
            previousArrival = arrival; previousTimestamp = frame.Timestamp;
            frames[frame.Sequence] = frame;
            // Bounded latency: never hold more than maxMs; the oldest frames go first.
            uint last = frames.Keys.Last(), window = (uint)(maxMs / FrameMs);
            if (last - next >= window)
            {
                uint first = last - window + 1;
                foreach (var key in frames.Keys.Where(k => k < first).ToList()) { frames.Remove(key); dropped++; }
                next = first;
            }
        }
    }
    public PlayoutFrame? Pull()
    {
        lock (gate)
        {
            if (!initialized) return null;
            if (!playing) { if (now() - firstArrival < Target) return null; playing = true; }
            if (frames.Remove(next, out var frame)) { next++; return new(frame.Payload, false); }
            lost++;
            if (frames.Count == 0)
            {
                // Underrun with nothing newer known: a stall, not a loss (TCP delivers it later, in a burst).
                // Conceal WITHOUT consuming the sequence, so the burst is played instead of arriving "late";
                // the extra delay is removed afterwards by AudioPlayout's catch-up.
                if (now() - previousArrival > Math.Max(240, maxMs)) initialized = playing = false;
                return new([], false);
            }
            next++; // a newer frame exists: this one is really lost
            if (frames.TryGetValue(next, out var following)) { recovered++; return new(following.Payload, true); }
            return new([], false);
        }
    }
    public void Clear() { lock (gate) { frames.Clear(); stalls.Clear(); initialized = playing = false; previousArrival = previousTimestamp = 0; jitter = 0; } }
}
