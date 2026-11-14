using System.Text.Json;
using System.Text.RegularExpressions;
namespace Ftx1.Core;

public sealed record StationConfig
{
    public string CatPort { get; init; } = "";
    public int CatBaud { get; init; } = 38400;
    public string PttMethod { get; init; } = "TX1";
    public string PttPort { get; init; } = "";
    public bool Simulator { get; init; } = true;
    public int MaxTxSeconds { get; init; } = 180;
    public void Validate()
    {
        if (MaxTxSeconds is < 10 or > 600) throw new ArgumentException("InvalidTxLimit");
        if (!new[] { 4800, 9600, 19200, 38400, 57600, 115200 }.Contains(CatBaud)) throw new ArgumentException("InvalidBaud");
        if (!new[] { "TX1", "TX2", "RTS", "DTR" }.Contains(PttMethod)) throw new ArgumentException("InvalidPtt");
        bool Port(string s) => Regex.IsMatch(s, @"^(COM[1-9][0-9]*|/dev/[A-Za-z0-9._/-]+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        if (!Simulator && !Port(CatPort)) throw new ArgumentException("InvalidCatPort");
        if (!Simulator && PttMethod is "RTS" or "DTR" && (!Port(PttPort) || PttPort.Equals(CatPort, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("InvalidPttPort");
    }
}
public sealed class ConfigStore
{
    readonly string preferred, fallback;
    public string? ActivePath { get; private set; }
    public ConfigStore(string? directory = null, string? fallbackDirectory = null)
    {
        preferred = Path.Combine(directory ?? AppContext.BaseDirectory, "config.json");
        fallback = Path.Combine(fallbackDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FTX1-CATandRemote"), "config.json");
    }
    public StationConfig Load()
    {
        foreach (var path in new[] { preferred, fallback }) if (File.Exists(path))
            {
                var cfg = JsonSerializer.Deserialize<StationConfig>(File.ReadAllText(path)) ?? throw new JsonException("InvalidConfig");
                cfg.Validate(); ActivePath = path; return cfg;
            }
        return new();
    }
    public void Save(StationConfig config)
    {
        config.Validate();
        try { Write(preferred, config); ActivePath = preferred; }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException) { Write(fallback, config); ActivePath = fallback; }
    }
    static void Write(string path, StationConfig cfg)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, cfg, new JsonSerializerOptions { WriteIndented = true }); stream.Flush(true);
            }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
