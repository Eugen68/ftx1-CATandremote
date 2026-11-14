using global::Android.App;
using global::Android.Content;
using global::Android.Content.PM;
using global::Android.OS;
using Avalonia;
using Avalonia.Android;
using Ftx1.Client;
namespace Ftx1.Android;
[Activity(Name = "ro.eugen68.ftx1remote.MainActivity", Label = "FTX1 Remote", Theme = "@style/Theme.Ftx1", MainLauncher = true, Exported = true, ScreenOrientation = ScreenOrientation.Portrait,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode | ConfigChanges.KeyboardHidden)]
public sealed class MainActivity : AvaloniaMainActivity
{
    public static MainActivity? Current { get; private set; }
    TaskCompletionSource<bool>? permissions;
    protected override AppBuilder CreateAppBuilder() => AppBuilder.Configure<MobileApp>().UseAndroid();
    protected override void OnCreate(Bundle? state) { Current = this; global::Android.Util.Log.Info("FTX1Smoke", "Activity starting"); base.OnCreate(state); global::Android.Util.Log.Info("FTX1Smoke", "Activity ready");
#if ANDROID_SMOKE
        if (Intent?.GetStringExtra("smokeConfig") is { } config) _ = AndroidSmoke.Run(this, config);
#endif
    }
    protected override void OnResume() { Current = this; base.OnResume(); MobileRuntime.Handle(MobileEvent.Resume); }
    protected override void OnPause() { MobileRuntime.Handle(MobileEvent.Pause); base.OnPause(); }
    protected override void OnDestroy() { if (MobileRuntime.Initialized) MobileRuntime.Model.EndPtt(); if (ReferenceEquals(Current, this)) Current = null; permissions?.TrySetResult(false); base.OnDestroy(); }
    public async Task<bool> EnsureAudioPermissions()
    {
        var required = new List<string> { global::Android.Manifest.Permission.RecordAudio };
        if (OperatingSystem.IsAndroidVersionAtLeast(31)) required.Add(global::Android.Manifest.Permission.BluetoothConnect);
        if (OperatingSystem.IsAndroidVersionAtLeast(33)) required.Add(global::Android.Manifest.Permission.PostNotifications);
        await Request(required.ToArray());
        return CheckSelfPermission(global::Android.Manifest.Permission.RecordAudio) == Permission.Granted && (!OperatingSystem.IsAndroidVersionAtLeast(33) || CheckSelfPermission(global::Android.Manifest.Permission.PostNotifications) == Permission.Granted);
    }
    public async Task ScanQr()
    {
        MobileRuntime.Release();
        if (!await Request([global::Android.Manifest.Permission.Camera])) { MobileRuntime.Model.Notice = MobileApp.Text("MobileCameraDenied"); return; }
        StartActivityForResult(new Intent(this, typeof(QrActivity)), 102);
    }
    async Task<bool> Request(string[] names)
    {
        var missing = names.Where(p => CheckSelfPermission(p) != Permission.Granted).ToArray(); if (missing.Length == 0) return true;
        if (permissions != null) return false;
        var waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); permissions = waiter;
        try { RequestPermissions(missing, 101); await waiter.Task; return names.All(p => CheckSelfPermission(p) == Permission.Granted); }
        finally { permissions = null; }
    }
    public override void OnRequestPermissionsResult(int requestCode, string[] names, Permission[] grants) { base.OnRequestPermissionsResult(requestCode, names, grants); if (requestCode == 101) permissions?.TrySetResult(grants.All(g => g == Permission.Granted)); }
    protected override async void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode == 102 && resultCode == Result.Ok && data?.GetStringExtra("pairing") is { } pairing) { MobileRuntime.Model.PairingText = pairing; await MobileRuntime.Model.PairCommand.ExecuteAsync(null); }
    }
}
