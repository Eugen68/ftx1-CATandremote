using System.Text.Json;
using global::Android.Security.Keystore;
using Ftx1.Client;
using Ftx1.Protocol;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;
namespace Ftx1.Android;
/// <summary>Token never goes to preferences/plain JSON. AES-GCM key is non-exportable in Android Keystore.</summary>
public sealed class AndroidSettingsStore(string root) : ClientSettingsStore(root)
{
    const string Alias = "FTX1.Remote.DeviceToken.v1";
    string EncryptedPath => Path.Combine(Path.GetDirectoryName(PathName)!, "paired-device.aes");
    static IKey Key()
    {
        using var store = KeyStore.GetInstance("AndroidKeyStore")!; store.Load(null);
        if (!store.ContainsAlias(Alias))
        {
            using var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, "AndroidKeyStore")!;
            using var spec = new KeyGenParameterSpec.Builder(Alias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt).SetBlockModes(KeyProperties.BlockModeGcm)!.SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)!.SetKeySize(256)!.Build()!;
            generator.Init(spec); generator.GenerateKey()?.Dispose();
        }
        return store.GetKey(Alias, null)!;
    }
    public override ClientSettings? Load()
    {
        if (!File.Exists(EncryptedPath)) return null;
        var data = File.ReadAllBytes(EncryptedPath); if (data.Length < 29 || data[0] != 1) throw new IOException("InvalidSecureSettings");
        using var cipher = Cipher.GetInstance("AES/GCM/NoPadding")!; using var key = Key(); using var spec = new GCMParameterSpec(128, data[1..13]);
        cipher.Init(CipherMode.DecryptMode, key, spec); var plain = cipher.DoFinal(data[13..])!;
        return JsonSerializer.Deserialize(plain, ProtocolJson.Default.ClientSettings);
    }
    public override void Save(ClientSettings settings)
    {
        StationConnection.ValidateSettings(settings); using var cipher = Cipher.GetInstance("AES/GCM/NoPadding")!; using var key = Key(); cipher.Init(CipherMode.EncryptMode, key);
        var iv = cipher.GetIV()!; if (iv.Length != 12) throw new IOException("InvalidSecureSettings"); var plain = JsonSerializer.SerializeToUtf8Bytes(settings, ProtocolJson.Default.ClientSettings); var encrypted = cipher.DoFinal(plain)!;
        var bytes = new byte[1 + iv.Length + encrypted.Length]; bytes[0] = 1; iv.CopyTo(bytes, 1); encrypted.CopyTo(bytes, 13); Directory.CreateDirectory(Path.GetDirectoryName(EncryptedPath)!);
        var temp = EncryptedPath + ".tmp"; try { File.WriteAllBytes(temp, bytes); File.Move(temp, EncryptedPath, true); } finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
