using System.Diagnostics;
using System.Text.Json;
using Ftx1.Client;
using Ftx1.Protocol;
var root = Path.Combine(Path.GetTempPath(), "ftx1-package-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
var start = new ProcessStartInfo(Path.GetFullPath(args[0])) { UseShellExecute = false, CreateNoWindow = true }; foreach (var arg in new[] { "--headless", "--sim", "--port", "0", "--data-dir", root }) start.ArgumentList.Add(arg);
using var server = Process.Start(start) ?? throw new InvalidOperationException("Server did not start");
try
{
    async Task Wait(Func<bool> check) { using var cts = new CancellationTokenSource(15000); while (!check()) { if (server.HasExited) throw new InvalidOperationException("Packaged server exited: " + server.ExitCode); await Task.Delay(20, cts.Token); } }
    var file = Path.Combine(root, "desktop-connection.json"); await Wait(() => File.Exists(file)); var cfg = JsonSerializer.Deserialize(File.ReadAllBytes(file), ProtocolJson.Default.ClientSettings)!;
    await using var client = new StationConnection(cfg); await client.Start(); await Wait(() => client.Connected && client.State.GetValueOrDefault("radio_id") as string == "0840");
    await client.Send(new() { T = "set", Key = "af", Value = Wire.Element(88) }); await Wait(() => client.State.GetValueOrDefault("af") is 88);
    await client.Ptt(true); await Wait(() => client.State.GetValueOrDefault("tx") is true); await client.Ptt(false); await Wait(() => client.State.GetValueOrDefault("tx") is false);
    Console.WriteLine("RESULT OK: packaged server, TLS pin, device authentication, CAT, PTT on/off");
}
finally { if (!server.HasExited) { server.Kill(true); await server.WaitForExitAsync(); } Directory.Delete(root, true); }
