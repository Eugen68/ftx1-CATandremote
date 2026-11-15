using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Ftx1.Protocol;
namespace Ftx1.Server;

public static class PrivateFiles
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FTX1-CATandRemote");
    public static void Write(string path, ReadOnlySpan<byte> bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite); stream.Write(bytes); stream.Flush(true); } File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
public sealed class ServerCertificate
{
    public X509Certificate2 Certificate { get; }
    public string Fingerprint => Convert.ToHexString(SHA256.HashData(Certificate.RawData));
    public ServerCertificate(string root)
    {
        var path = Path.Combine(root, "server.pfx");
        if (File.Exists(path)) Certificate = new X509Certificate2(File.ReadAllBytes(path));
        else { using var rsa = RSA.Create(3072); var req = new CertificateRequest("CN=FTX1 station", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1); var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("localhost"); san.AddIpAddress(IPAddress.Loopback); req.CertificateExtensions.Add(san.Build()); req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false)); req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false)); using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(2)); var data = cert.Export(X509ContentType.Pfx); PrivateFiles.Write(path, data); Certificate = new X509Certificate2(data); }
    }
}
public sealed class DeviceTokens
{
    readonly object gate = new(); readonly string path; List<DeviceRecord> devices;
    public event Action<string>? Revoked;
    public DeviceTokens(string root) { path = Path.Combine(root, "devices.json"); devices = File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllBytes(path), ProtocolJson.Default.ListDeviceRecord) ?? [] : []; }
    static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));
    public IReadOnlyList<DeviceRecord> Devices { get { lock (gate) return devices.Select(d => d with { TokenHash = "" }).ToArray(); } }
    public PairResponse Create(string name)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)); var d = new DeviceRecord(Guid.NewGuid().ToString("N"), name[..Math.Min(80, name.Length)], Convert.ToHexString(Hash(token)), DateTimeOffset.UtcNow);
        lock (gate) { List<DeviceRecord> next=[..devices,d]; Save(next); devices=next; }
        return new(token, d.Id);
    }
    public DeviceRecord? Authenticate(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 256) return null; var hash = Hash(token); DeviceRecord? found = null;
        lock (gate) foreach (var d in devices) if (CryptographicOperations.FixedTimeEquals(Convert.FromHexString(d.TokenHash), hash)) found = d;
        return found;
    }
    public void Revoke(string id) { lock (gate) { var next=devices.Where(d=>d.Id!=id).ToList(); Save(next); devices=next; } Revoked?.Invoke(id); }
    void Save(List<DeviceRecord> next) => PrivateFiles.Write(path, JsonSerializer.SerializeToUtf8Bytes(next, ProtocolJson.Default.ListDeviceRecord));
}
public sealed class PairingCodes(DeviceTokens tokens, Func<DateTimeOffset>? clock = null)
{
    readonly object gate = new(); string? hash; DateTimeOffset expires;
    DateTimeOffset Now => (clock ?? (() => DateTimeOffset.UtcNow))();
    public string Issue() { var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)); lock (gate) { hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code))); expires = Now.AddMinutes(5); } return code; }
    public PairResponse? Consume(string code, string name)
    {
        if (code.Length > 256 || string.IsNullOrWhiteSpace(name)) return null;
        lock (gate) { if (hash == null || Now >= expires || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(hash), SHA256.HashData(Encoding.UTF8.GetBytes(code)))) return null; hash = null; return tokens.Create(name); }
    }
}
public sealed class AttemptLimiter
{
    readonly object gate = new(); readonly Dictionary<string, (int Count, DateTimeOffset Since)> failures = [];
    public bool Allowed(string ip) { lock (gate) return !failures.TryGetValue(ip, out var f) || DateTimeOffset.UtcNow - f.Since > TimeSpan.FromMinutes(5) || f.Count < 10; }
    public void Fail(string ip) { lock (gate) { if (failures.Count > 1024) failures.Remove(failures.MinBy(x => x.Value.Since).Key); var now = DateTimeOffset.UtcNow; var f = failures.GetValueOrDefault(ip, (0, now)); if (now - f.Item2 > TimeSpan.FromMinutes(5)) f = (0, now); failures[ip] = (f.Item1 + 1, f.Item2); } }
}
public static class ListenPolicy
{
    public static bool IsPrivateLan(IPAddress ip) { var b = ip.GetAddressBytes(); return b.Length == 4 && (b[0] == 10 || b[0] == 172 && b[1] >= 16 && b[1] <= 31 || b[0] == 192 && b[1] == 168); }
    public static string FirstPrivateAddress()
    {
        try { return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address).FirstOrDefault(IsPrivateLan)?.ToString() ?? ""; }
        catch (System.Net.NetworkInformation.NetworkInformationException) { return ""; }
    }
    public static IPAddress Advertise(IEnumerable<IPAddress> listening, IPAddress? preferred = null)
    {
        var addresses = listening.ToArray(); if (preferred != null && addresses.Contains(preferred)) return preferred;
        return addresses.FirstOrDefault(IsTailscale) ?? addresses.FirstOrDefault(IPAddress.IsLoopback) ?? throw new InvalidOperationException("NoPairingAddress");
    }
    public static bool IsTailscale(IPAddress ip) { var b = ip.GetAddressBytes(); return b.Length == 4 && b[0] == 100 && b[1] >= 64 && b[1] <= 127; }
    public static IPAddress[] DefaultAddresses()
    {
        try { return new[] { IPAddress.Loopback }.Concat(System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address).Where(IsTailscale)).Distinct().ToArray(); }
        catch (System.Net.NetworkInformation.NetworkInformationException) { return [IPAddress.Loopback]; }
    }
    public static void Validate(IEnumerable<IPAddress> addresses, bool allowOther) { if (!allowOther && addresses.Any(ip => !IPAddress.IsLoopback(ip) && !IsTailscale(ip))) throw new ArgumentException("PublicInterfaceDenied"); }
}
