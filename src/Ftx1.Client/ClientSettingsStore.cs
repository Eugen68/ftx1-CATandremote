using System.Text.Json;
using Ftx1.Protocol;
namespace Ftx1.Client;
public sealed class ClientSettingsStore(string? root = null)
{
    public string PathName { get; } = Path.Combine(root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FTX1-CATandRemote"), "desktop-connection.json");
    public ClientSettings? Load() => File.Exists(PathName) ? JsonSerializer.Deserialize(File.ReadAllBytes(PathName), ProtocolJson.Default.ClientSettings) : null;
    public void Save(ClientSettings settings)
    {
        StationConnection.ValidateSettings(settings); Directory.CreateDirectory(Path.GetDirectoryName(PathName)!); var tmp = PathName + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite); JsonSerializer.Serialize(stream, settings, ProtocolJson.Default.ClientSettings); stream.Flush(true); } File.Move(tmp, PathName, true); } finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }
}
