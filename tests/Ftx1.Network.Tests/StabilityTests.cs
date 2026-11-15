using System.Diagnostics;
using System.Net;
using Ftx1.Audio;
using Ftx1.Server;
using Xunit;
namespace Ftx1.Network.Tests;
public class StabilityTests
{
    static AudioProcessDevice Worker(bool hang = false)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory); while (root != null && !File.Exists(Path.Combine(root.FullName, "Ftx1.sln"))) root = root.Parent;
        Assert.NotNull(root);
        string dll = Path.Combine(root!.FullName, "tools", "Ftx1.AudioWorker.Smoke", "bin", "Release", "net8.0", "Ftx1.AudioWorker.Smoke.dll");
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet" ? Environment.ProcessPath : "dotnet")!;
        var start = new ProcessStartInfo(host); start.ArgumentList.Add(dll); if (hang) start.ArgumentList.Add("--hang-on-stop"); return new(start, 1000);
    }
    [Fact]
    public async Task AudioWorkerTransfersFramesAndStopsWithoutEndingParent()
    {
        using var worker = Worker(); int frames = 0; worker.Captured += _ => Interlocked.Increment(ref frames); worker.Start(); await StationFixture.Wait(() => Volatile.Read(ref frames) > 4);
        worker.Play(new short[320]); worker.Clear(); var watch = Stopwatch.StartNew(); int id = worker.WorkerId; worker.Dispose(); Assert.True(watch.Elapsed.TotalSeconds < 2); Assert.False(IsAlive(id));
    }
    [Fact]
    public async Task CrashedAudioWorkerReportsFailureAndParentCanContinue()
    {
        using var worker = Worker(); var failed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously); worker.Failed += e => failed.TrySetResult(e); worker.Start();
        using var child = Process.GetProcessById(worker.WorkerId); child.Kill(true); var error = await failed.Task.WaitAsync(TimeSpan.FromSeconds(3)); Assert.Contains("AudioWorker", error.Message);
    }
    [Fact]
    public void HungDriverDuringAudioStopHasABoundedShutdown()
    {
        using var worker = Worker(true); worker.Start(); int id = worker.WorkerId; var watch = Stopwatch.StartNew(); worker.Dispose(); Assert.InRange(watch.Elapsed.TotalSeconds, .8, 2.5); Assert.False(IsAlive(id));
    }
    static bool IsAlive(int id) { try { using var process = Process.GetProcessById(id); return !process.HasExited; } catch (ArgumentException) { return false; } }
    [Theory]
    [InlineData("192.168.55.167", true)] [InlineData("10.1.2.3", true)] [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.1", true)] [InlineData("172.32.0.1", false)] [InlineData("0.0.0.0", false)] [InlineData("8.8.8.8", false)] [InlineData("127.0.0.1", false)]
    public void LanOptInAcceptsOnlyPrivateIpv4(string text, bool expected) => Assert.Equal(expected, ListenPolicy.IsPrivateLan(IPAddress.Parse(text)));
    [Fact]
    public void QrAdvertisesSelectedBoundAddressWithoutManualJsonEdits()
    {
        var lan = IPAddress.Parse("192.168.55.167"); var tail = IPAddress.Parse("100.64.0.2");
        Assert.Equal(lan, ListenPolicy.Advertise([IPAddress.Loopback, tail, lan], lan));
        Assert.Equal(tail, ListenPolicy.Advertise([IPAddress.Loopback, tail]));
        Assert.Equal(IPAddress.Loopback, ListenPolicy.Advertise([IPAddress.Loopback], lan));
    }
    [Fact]
    public void InvalidPublicLanSettingDoesNotOpenAListener() => Assert.Throws<ArgumentException>(() => NetworkSettingsStore.Addresses(new(true, "8.8.8.8")));
}
