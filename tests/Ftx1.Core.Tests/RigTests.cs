using System.Text;
using Ftx1.Core.Cat;
using Ftx1.Core.Safety;
using Ftx1.Sim;
using Xunit;
namespace Ftx1.Core.Tests;

public class RigTests
{
    static async Task Until(Func<bool> condition, int ms = 4000)
    { using var timeout = new CancellationTokenSource(ms); while (!condition()) await Task.Delay(10, timeout.Token); }
    [Fact]
    public async Task ConnectControlCoalesceAndDispose()
    {
        using var sim = new SimRadio(); var transport = new SimTransport(sim); var rig = new Rig(transport);
        await rig.Start(); await Until(() => rig.State.GetValueOrDefault("radio_id") as string == "0840");
        rig.SetParam("af", 77); await Until(() => rig.State.GetValueOrDefault("af") is 77);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Raw += line => { if (line == ">AG0;") entered.TrySetResult(); };
        var seq = rig.ExecSeq(["AG0;", "~250;"]); await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        int before = sim.Received.Count(c => c.StartsWith("FA", StringComparison.Ordinal) && c.Length > 2);
        for (int i = 0; i < 100; i++) rig.SetParam("freq_a", 14074000 + i);
        await seq; await Until(() => sim.Received.Contains("FA014074099"));
        Assert.Equal(before + 1, sim.Received.Count(c => c.StartsWith("FA", StringComparison.Ordinal) && c.Length > 2));
        rig.Dispose(); Assert.Contains("TX0", sim.Received); Assert.Equal("0", sim.State["TX"]);
    }
    [Fact]
    public async Task UnsupportedPollingStopsAfterThreeRejections()
    {
        using var sim = new SimRadio(); var transport = new SimTransport(sim) { RejectQuery = cmd => cmd == "AG0" };
        await using var rig = new Rig(transport); await rig.Start();
        for (int i = 0; i < 3; i++) { rig.Refresh(); await Until(() => rig.UnsupportedCount("AG0;") >= i + 1, 15000); }
        Assert.Equal(3, rig.UnsupportedCount("AG0;")); rig.Refresh(); await Task.Delay(300); Assert.Equal(3, rig.UnsupportedCount("AG0;"));
    }
    [Fact]
    public async Task PttOwnerCannotBeStolenAndDisposalStopsTx()
    {
        using var sim = new SimRadio(); var rig = new Rig(new SimTransport(sim)); await rig.Start();
        using var lease = rig.Ptt("manual"); Assert.NotNull(lease); Assert.Null(rig.Ptt("ft8"));
        await Until(() => sim.State["TX"] == "1"); await rig.DisposeAsync(); Assert.Equal("0", sim.State["TX"]); Assert.Contains("KY00", sim.Received);
    }
    [Fact]
    public async Task DisconnectForcesOffAndConsoleCannotBypassOwnership()
    {
        using var sim = new SimRadio(); var transport = new SimTransport(sim); await using var rig = new Rig(transport); await rig.Start();
        using var lease = rig.Ptt("manual"); Assert.NotNull(lease);
        Assert.Throws<InvalidOperationException>(() => rig.Send("FA;TX1;"));
        Assert.Throws<InvalidOperationException>(() => rig.ExecSeq(["KY05;"]).GetAwaiter().GetResult());
        transport.Fail(); Assert.Null(rig.Transmit.Owner); Assert.False((bool)rig.State["tx"]);
    }
    [Fact]
    public async Task MenuWidthAndSignedValueArePreserved()
    {
        using var sim = new SimRadio(); sim.Handle("EX010101+0001"); await using var rig = new Rig(new SimTransport(sim)); await rig.Start();
        Assert.Equal("-0012", await rig.MenuSet("010101", -12));
    }
    [Fact]
    public async Task ActualRigWatchdogStopsSimulatorTransmission()
    {
        using var sim = new SimRadio();
        await using var rig = new Rig(new SimTransport(sim), maxTx: TimeSpan.FromMilliseconds(150));
        await rig.Start();
        using var lease = rig.Ptt("manual");
        Assert.NotNull(lease);
        await Until(() => sim.State["TX"] == "1");
        await Until(() => rig.Transmit.Owner == null);
        Assert.Equal("0", sim.State["TX"]);
        Assert.Contains("TX0", sim.Received);
    }

    [Fact]
    public async Task UnsignedMenuRetainsWidthWhenWritingNegativeValue()
    {
        using var sim = new SimRadio();
        sim.Handle("EX010101000");
        await using var rig = new Rig(new SimTransport(sim));
        await rig.Start();
        Assert.Equal("-02", await rig.MenuSet("010101", -2));
    }

    [Fact]
    public void TransmitDeadlineAndRemoteHeartbeatAreMonotonic()
    {
        double clock = 0; int stops = 0; using var guard = new TransmitGuard(() => stops++, TimeSpan.FromSeconds(180), () => clock);
        using var a = guard.TryAcquire("manual", () => { }); Assert.NotNull(a); Assert.Null(guard.TryAcquire("cw", () => { }));
        clock = 181; guard.Tick(); Assert.Null(guard.Owner); Assert.Equal(1, stops);
        using var b = guard.TryAcquire("phone", () => { }, true); Assert.NotNull(b); clock += 1.6; guard.Tick(); Assert.Null(guard.Owner); Assert.Equal(2, stops);
    }
    [Fact]
    public void FailedStartAndLeaseFinallyStopTransmission()
    {
        int stops = 0; using var guard = new TransmitGuard(() => stops++);
        Assert.Throws<IOException>(() => guard.TryAcquire("manual", () => throw new IOException())); Assert.Null(guard.Owner); Assert.Equal(1, stops);
        try { using var lease = guard.TryAcquire("cw", () => { }); throw new IOException(); } catch (IOException) { }
        Assert.Equal(2, stops);
    }
    sealed class Line : IPttLine { public bool On; public void Set(bool value) => On = value; public void Dispose() => On = false; }
    sealed class FailingTransport : ITransport
    {
        public bool Failed => false; public bool FailWrites; public void Open(Action<byte[]?> cb) { }
        public void Reopen() { }
        public void Close() { }
        public void Write(byte[] b) { if (FailWrites) throw new IOException(); }
    }
    [Fact]
    public async Task CatWriteFailureStillDropsIndependentLine()
    {
        var transport = new FailingTransport(); var line = new Line(); var rig = new Rig(transport, "RTS", line); await rig.Start();
        using var lease = rig.Ptt("manual"); Assert.True(line.On); transport.FailWrites = true;
        await rig.DisposeAsync(); Assert.False(line.On);
    }
}
