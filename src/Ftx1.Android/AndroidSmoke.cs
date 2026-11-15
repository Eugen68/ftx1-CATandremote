#if ANDROID_SMOKE
using System.Text;
using System.Text.Json;
using global::Android.Util;
using Ftx1.Client;
using Ftx1.Protocol;
using ZXing;
using ZXing.Common;
namespace Ftx1.Android;
/// <summary>Compiled only in the separate emulator-test APK, never in the distributed APK.</summary>
public static class AndroidSmoke
{
    public static async Task Run(MainActivity activity, string encoded)
    {
        Log.Info("FTX1Smoke", "START emulator integration");
        try
        {
            await Task.Delay(1000); string text = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            var matrix = new MultiFormatWriter().encode(text, BarcodeFormat.QR_CODE, 512, 512); var pixels = new byte[matrix.Width * matrix.Height];
            for (int y = 0; y < matrix.Height; y++) for (int x = 0; x < matrix.Width; x++) pixels[y * matrix.Width + x] = matrix[x, y] ? (byte)0 : (byte)255;
            var decoder = new BarcodeReaderGeneric { AutoRotate = true, Options = new DecodingOptions { TryHarder = true } };
            var decoded = decoder.Decode(pixels, matrix.Width, matrix.Height, RGBLuminanceSource.BitmapFormat.Gray8) ?? throw new Exception("QR decode failed");
            var scan = new PairingScan(); scan.Observe(decoded.Text,1000); Check(!scan.Ready,"First QR frame cannot confirm"); scan.Observe(decoded.Text,1200); scan.Observe(decoded.Text,1500); Check(scan.Ready && scan.Text==text,"Stable QR validation");
            var model = MobileRuntime.Model; model.PairingText = scan.Text!; await model.PairCommand.ExecuteAsync(null);
            var saved = new AndroidSettingsStore(MobileRuntime.Root).Load(); Check(saved != null && saved.DeviceToken == model.DeviceToken && model.DeviceToken.Length > 20, "Keystore roundtrip");
            Check(!File.Exists(new ClientSettingsStore(MobileRuntime.Root).PathName), "No plaintext token file");
            await MobileRuntime.ToggleConnection(); await Wait(() => model.NetworkConnected && model.AudioReady && MobileRuntime.Voice?.PlayedFrames > 5, 20000);
            model.RawCommand = "AG0077"; model.SendRawCommand.Execute(null); await Task.Delay(250); model.RawCommand = "AG0"; model.SendRawCommand.Execute(null); await Wait(() => model.CatLog.Any(s => s.Contains("AG0077", StringComparison.Ordinal)), 10000);
            MobileRuntime.Press(); await Wait(() => model.Transmitting); await Task.Delay(1300); Check(model.Transmitting, "Microphone stream keeps TX alive"); MobileRuntime.Release(); await Wait(() => !model.Transmitting);
            MobileRuntime.Press(); await Wait(() => model.Transmitting); MobileRuntime.Handle(MobileEvent.FocusLost); await Wait(() => !model.Transmitting); MobileRuntime.Handle(MobileEvent.FocusGained); await Wait(() => model.AudioReady);
            Check(!model.Transmitting, "Focus gain never rekeys");
            MobileRuntime.Handle(MobileEvent.NetworkChanged); await Wait(() => !model.NetworkConnected); await Wait(() => model.NetworkConnected && model.AudioReady, 15000); Check(!model.Transmitting, "Reconnect never rekeys");
            MobileRuntime.Press(); await Wait(() => model.Transmitting); Log.Info("FTX1Smoke", "READY_SLEEP");
            await Wait(() => !MobileRuntime.Lifecycle.Foreground, 15000); await Wait(() => !model.Transmitting);
            long captured = MobileRuntime.Voice!.CapturedFrames, played = MobileRuntime.Voice.PlayedFrames;
            await Task.Delay(4000);
            Check(ControlService.Active && ControlService.HasWakeLock && model.NetworkConnected && model.AudioReady, "Service and connection survive screen off");
            Check(MobileRuntime.Voice.CapturedFrames > captured + 80 && MobileRuntime.Voice.PlayedFrames > played + 80, "Native audio survives screen off");
            Log.Info("FTX1Smoke", "PASS_SCREEN_OFF"); await MobileRuntime.Disconnect(); await Wait(() => !ControlService.Active);
            Log.Info("FTX1Smoke", "PASS: QR pairing, Keystore, TLS CAT, native audio RX/TX, focus loss, reconnect, screen-off PTT release, foreground service and WakeLock");
        }
        catch (Exception error) { Log.Error("FTX1Smoke", "FAIL: " + error); try { await MobileRuntime.Disconnect(); } catch { } }
    }
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static async Task Wait(Func<bool> check, int timeout = 8000) { using var token = new CancellationTokenSource(timeout); while (!check()) await Task.Delay(30, token.Token); }
}
#endif
