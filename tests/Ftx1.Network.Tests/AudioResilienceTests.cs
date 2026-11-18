using Ftx1.Audio;
using Xunit;
namespace Ftx1.Network.Tests;

/// <summary>Mobile-link behaviour measured by the owner on 4G+/IPsec: TCP stalls of 200–500 ms followed by bursts.</summary>
public class AudioResilienceTests
{
    static short[] Tone(int frame, double amplitude = 8000) => Enumerable.Range(0, 320).Select(i => (short)(amplitude * Math.Sin(2 * Math.PI * 700 * (frame * 320 + i) / 16000))).ToArray();
    static double Rms(short[] pcm) => Math.Sqrt(pcm.Average(x => (double)x * x));

    sealed record Result(AudioQuality Quality, int Concealed, double AverageDelayMs, int MaxBufferedMs);
    /// <summary>60 s of 20 ms frames; base delay 40 ms + 0–15 ms jitter; a TCP stall every ~3 s (200–500 ms) delivering everything at once.</summary>
    static Result Simulate(int maxMs, bool pauses = false, int seed = 7)
    {
        var random = new Random(seed); var arrivals = new List<(long At, uint Seq)>(); long stallUntil = 0, nextStall = 2000;
        for (uint seq = 0; seq < 3000; seq++)
        {
            long sent = seq * 20; if (sent >= nextStall) { stallUntil = sent + random.Next(200, 501); nextStall = sent + random.Next(2500, 3500); }
            long at = sent + 40 + random.Next(0, 16); if (at < stallUntil) at = stallUntil; /* TCP releases the backlog at once, in order */ arrivals.Add((at, seq));
        }
        arrivals.Sort((a, b) => a.At != b.At ? a.At.CompareTo(b.At) : a.Seq.CompareTo(b.Seq));
        long now = 0; var codec = new VoiceCodec(); var payloads = Enumerable.Range(0, 3000).Select(i => codec.Encode(Tone(i, pauses && i % 50 >= 30 ? 0 : 8000))).ToArray(); // pauses: 600 ms speech, 400 ms quiet
        var playout = new AudioPlayout(new JitterBuffer(() => now, maxMs), new VoiceCodec());
        int index = 0, concealed = 0, maxBuffered = 0; double delaySum = 0; int delayCount = 0; long played = 0;
        for (now = 0; now < 62_000; now += 20)
        {
            while (index < arrivals.Count && arrivals[index].At <= now) { var (_, seq) = arrivals[index++]; playout.Push(new(seq, seq * 20, payloads[seq])); }
            var pcm = playout.Next(); if (pcm == null) continue; played++;
            
            var q = playout.Buffer.Quality; maxBuffered = Math.Max(maxBuffered, q.BufferedMs);
            if (now > 5000) { delaySum += q.BufferedMs + 40; delayCount++; }
        }
        concealed = (int)(playout.Quality.Lost - playout.Quality.Recovered); // audible holes: concealed, not rebuilt from FEC
        return new(playout.Quality, concealed, delaySum / Math.Max(1, delayCount), maxBuffered);
    }

    [Theory]
    [InlineData(true)]  // speech with pauses: catch-up happens in the pauses
    [InlineData(false)] // worst case, continuous tone: catch-up only by stretching
    public void AdaptiveBufferAbsorbsTcpStallsThatTheOld120MsCeilingThrewAway(bool pauses)
    {
        var old = Simulate(120, pauses); var adaptive = Simulate(JitterBuffer.DefaultMaxMs, pauses);
        long oldBad = old.Quality.Late + old.Quality.Dropped, newBad = adaptive.Quality.Late + adaptive.Quality.Dropped;
        string detail = $"old {oldBad} discarded / {old.Concealed} holes; adaptive {newBad} (late {adaptive.Quality.Late}, dropped {adaptive.Quality.Dropped}) / {adaptive.Concealed} holes, bursts {adaptive.Quality.Bursts}, delay {adaptive.AverageDelayMs:F0} ms";
        Assert.True(oldBad > 100, "profile must reproduce the field problem: " + detail);
        // Measured on this harsh profile (stalls up to 500 ms > the 400 ms ceiling): ~ -78% audible interruptions.
        Assert.True(newBad + adaptive.Concealed <= (oldBad + old.Concealed) * 0.25, detail);
        Assert.True(newBad <= oldBad * 0.25, detail);
        Assert.InRange(adaptive.MaxBufferedMs, 0, JitterBuffer.DefaultMaxMs);
        Assert.InRange(adaptive.AverageDelayMs, 0, 400);
        Assert.Equal(0, adaptive.Quality.Late);
    }
    [Fact]
    public void TargetRisesAfterAStallAndFallsBackWhenTheLinkCalmsDown()
    {
        long now = 1; var buffer = new JitterBuffer(() => now); uint seq = 0;
        void Steady(int frames) { for (int i = 0; i < frames; i++) { buffer.Push(new(seq, seq * 20L, [1])); seq++; now += 20; buffer.Pull(); } }
        Steady(50); Assert.Equal(JitterBuffer.MinMs, buffer.Quality.TargetMs);
        now += 300; Steady(1); Assert.InRange(buffer.Quality.TargetMs, 300, 400); Assert.Equal(1, buffer.Quality.Bursts);
        Steady(JitterBuffer.MemoryMs / 20 + 50); Assert.InRange(buffer.Quality.TargetMs, JitterBuffer.MinMs, 80);
    }
    [Fact]
    public void BurstIsKeptUpToTheCeilingAndOnlyOlderFramesAreDropped()
    {
        long now = 1; var buffer = new JitterBuffer(() => now, 400);
        for (uint i = 0; i < 30; i++) buffer.Push(new(i, i * 20, [(byte)i]));
        Assert.Equal(400, buffer.Quality.BufferedMs); Assert.Equal(10, buffer.Quality.Dropped);
        now += 1000; Assert.Equal((byte)10, buffer.Pull()!.Value.Payload[0]);
    }
    [Fact]
    public void LostFrameIsRebuiltFromFecOfTheFollowingPacket()
    {
        long now = 1; var buffer = new JitterBuffer(() => now); buffer.Push(new(0, 0, [10])); buffer.Push(new(2, 40, [12])); now = 200;
        Assert.False(buffer.Pull()!.Value.Fec); var rebuilt = buffer.Pull()!.Value; Assert.True(rebuilt.Fec); Assert.Equal(new byte[] { 12 }, rebuilt.Payload);
        Assert.Equal(1, buffer.Quality.Lost); Assert.Equal(1, buffer.Quality.Recovered); Assert.Equal(new byte[] { 12 }, buffer.Pull()!.Value.Payload);
    }
    [Fact]
    public void FecAndConcealmentKeepTheToneInsteadOfSilence()
    {
        var encoder = new VoiceCodec(); var packets = Enumerable.Range(0, 40).Select(i => encoder.Encode(Tone(i))).ToArray();
        var fec = new VoiceCodec(); for (int i = 0; i < 30; i++) fec.Decode(packets[i]);
        Assert.InRange(Rms(fec.DecodeFec(packets[31])), 2500, 9000); Assert.InRange(Rms(fec.Decode(packets[31])), 2500, 9000);
        var plc = new VoiceCodec(); for (int i = 0; i < 30; i++) plc.Decode(packets[i]);
        Assert.True(Rms(plc.Conceal()) > 1000); Assert.Throws<InvalidDataException>(() => plc.Decode([]));
    }
    [Fact]
    public void CatchUpSkipsQuietFramesFirstThenStretchesSpeech()
    {
        long now = 1; var encoder = new VoiceCodec();
        var quiet = new AudioPlayout(new JitterBuffer(() => now), new VoiceCodec());
        for (uint i = 0; i < 20; i++) quiet.Push(new(i, i * 20, encoder.Encode(new short[320])));
        now += 500; Assert.NotNull(quiet.Next()); var frame = quiet.Next(); Assert.NotNull(frame); Assert.True(quiet.Quality.Skipped > 0); Assert.True(quiet.Buffer.ExcessMs < AudioPlayout.CatchUpMs);

        now = 1; encoder = new VoiceCodec(); var speech = new AudioPlayout(new JitterBuffer(() => now), new VoiceCodec());
        for (uint i = 0; i < 20; i++) speech.Push(new(i, i * 20, encoder.Encode(Tone((int)i))));
        now += 500; Assert.NotNull(speech.Next()); int frames = 0; while (speech.Buffer.ExcessMs >= AudioPlayout.CatchUpMs && frames < 400) { Assert.Equal(320, speech.Next()!.Length); frames++; }
        Assert.InRange(frames, 1, 399); Assert.True(speech.Quality.Skipped > 0);
    }
    [Fact]
    public void ResampleKeepsEndpointsAndShape()
    {
        var input = Enumerable.Range(0, AudioPlayout.StretchInput).Select(i => (short)(i * 10)).ToArray(); var output = new short[320];
        AudioPlayout.Resample(input, output); Assert.Equal(input[0], output[0]); Assert.Equal(input[^1], output[^1]);
        for (int i = 1; i < output.Length; i++) Assert.True(output[i] > output[i - 1]);
    }
}
