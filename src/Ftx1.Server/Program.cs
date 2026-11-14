using Avalonia;
using Ftx1.Core;
using Ftx1.Audio;
namespace Ftx1.Server;
public static class Program
{
    public static string[] Arguments { get; private set; } = [];
    [STAThread]
    public static void Main(string[] args)
    {
        if (AudioWorkerHost.TryRun(args)) return;
        Arguments = args;
        if (args.Contains("--headless")) { RunHeadless(args).GetAwaiter().GetResult(); return; }
        AppBuilder.Configure<ServerApp>().UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime(args);
    }
    static async Task RunHeadless(string[] args)
    {
        var root = PrivateFiles.Root; int rootIndex = Array.IndexOf(args, "--data-dir"); if (rootIndex >= 0 && rootIndex + 1 < args.Length) root = Path.GetFullPath(args[rootIndex + 1]); var cfg = new ConfigStore(Path.Combine(root, "station"), Path.Combine(root, "station")).Load(); if (args.Contains("--sim")) cfg = cfg with { Simulator = true };
        int port = 8443; int index = Array.IndexOf(args, "--port"); if (index >= 0 && index + 1 < args.Length) port = int.Parse(args[index + 1], System.Globalization.CultureInfo.InvariantCulture);
        var audio = AudioPreferencesStore.Load(Path.Combine(root, "server-audio.json"));
        var network = NetworkSettingsStore.Load(root);
        await using var server = new StationServer(new(root, cfg, port, Addresses: NetworkSettingsStore.Addresses(network), AllowOtherInterfaces: network.AllowLan, AudioInput: audio.Input, AudioOutput: audio.Output, PairingAddress: network.AllowLan ? System.Net.IPAddress.Parse(network.LanAddress) : null)); await server.Start();
        using var stop = new CancellationTokenSource(); Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        try { await Task.Delay(Timeout.Infinite, stop.Token); } catch (OperationCanceledException) { }
    }
}
