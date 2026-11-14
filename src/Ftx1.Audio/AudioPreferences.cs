using System.Text.Json;
using System.Text.Json.Serialization;
namespace Ftx1.Audio;
public sealed record AudioPreferences(bool Enabled = true, int Bitrate = 24000, string Input = "", string Output = "");
[JsonSerializable(typeof(AudioPreferences))]
public partial class AudioPreferencesJson : JsonSerializerContext { }
public static class AudioPreferencesStore
{
    public static AudioPreferences Load(string path) => File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllBytes(path), AudioPreferencesJson.Default.AudioPreferences) ?? new() : new();
    public static void Save(string path, AudioPreferences prefs)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); var tmp = path + ".tmp";
        try { File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(prefs, AudioPreferencesJson.Default.AudioPreferences)); File.Move(tmp, path, true); } finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }
}
