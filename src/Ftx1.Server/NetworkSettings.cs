using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace Ftx1.Server;
public sealed record NetworkSettings(bool AllowLan = false, string LanAddress = "");
[JsonSerializable(typeof(NetworkSettings))]
public partial class NetworkJson : JsonSerializerContext { }
public static class NetworkSettingsStore
{
    public static NetworkSettings Load(string root) { var path = Path.Combine(root, "network.json"); return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllBytes(path), NetworkJson.Default.NetworkSettings) ?? new() : new(); }
    public static void Save(string root, NetworkSettings settings) => PrivateFiles.Write(Path.Combine(root, "network.json"), JsonSerializer.SerializeToUtf8Bytes(settings, NetworkJson.Default.NetworkSettings));
    public static IPAddress[] Addresses(NetworkSettings settings)
    {
        var defaults = ListenPolicy.DefaultAddresses(); if (!settings.AllowLan) return defaults;
        if (!IPAddress.TryParse(settings.LanAddress, out var ip) || !ListenPolicy.IsPrivateLan(ip)) throw new ArgumentException("InvalidLanAddress");
        return defaults.Append(ip).Distinct().ToArray();
    }
}
