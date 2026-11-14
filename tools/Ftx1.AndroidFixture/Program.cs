using System.Text.Json;
using Ftx1.Core;
using Ftx1.Protocol;
using Ftx1.Server;
string root = Path.GetFullPath(args[0]); Directory.CreateDirectory(root);
await using var server = new StationServer(new(root, new StationConfig { Simulator = true }, 8843)); await server.Start();
var qr = JsonSerializer.Deserialize(server.CreateQr(), ProtocolJson.Default.PairingQr)!;
File.WriteAllBytes(Path.Combine(root, "pairing.json"), JsonSerializer.SerializeToUtf8Bytes(qr with { Address = "wss://10.0.2.2:8843/ws" }, ProtocolJson.Default.PairingQr));
Console.WriteLine("Android fixture ready"); await Task.Delay(Timeout.Infinite);
