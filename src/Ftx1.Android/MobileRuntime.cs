using global::Android.Content;
using global::Android.OS;
using Avalonia.Threading;
using Ftx1.Client;
using Ftx1.Client.ViewModels;
namespace Ftx1.Android;
/// <summary>Process-owned session, independent from the Activity and its view lifetime.</summary>
public static class MobileRuntime
{
    static readonly SemaphoreSlim audioGate = new(1, 1); static bool busy;
    public static bool Initialized => Model != null;
    public static MainViewModel Model { get; private set; } = null!;
    public static MobileLifecycle Lifecycle { get; } = new();
    public static AndroidVoiceDevice? Voice { get; private set; }
    public static string Root => global::Android.App.Application.Context.FilesDir!.AbsolutePath;
    public static void Initialize()
    {
        if (Model != null) return;
        Model = new(MobileApp.Text, a => Dispatcher.UIThread.Post(a), new AndroidSettingsStore(Root), () => Voice = new AndroidVoiceDevice());
        Model.DeviceName = global::Android.OS.Build.Model ?? "Android";
    }
    public static async Task ToggleConnection()
    {
        if (busy) return;
        if (Model.ConnectionOpen) { await Disconnect(); return; }
        var activity = MainActivity.Current; if (activity == null) return;
        if (!await activity.EnsureAudioPermissions()) { Model.Notice = MobileApp.Text("MobilePermissionsDenied"); return; }
        busy = true;
        try
        {
            await ControlService.EnsureStarted(activity);
            if (!Lifecycle.HasFocus) throw new InvalidOperationException("MobileFocusUnavailable");
            await audioGate.WaitAsync(); try { await Model.ConnectCommand.ExecuteAsync(null); } finally { audioGate.Release(); }
            if (!Model.ConnectionOpen) activity.StopService(new Intent(activity, typeof(ControlService)));
        }
        catch (Exception) { Model.Notice = MobileApp.Text("MobileFocusUnavailable"); await Disconnect(); }
        finally { busy = false; }
    }
    public static async Task Disconnect()
    {
        Model.EndPtt(); await audioGate.WaitAsync(); try { await Model.DisconnectSession(); } finally { audioGate.Release(); } MainActivity.Current?.StopService(new Intent(MainActivity.Current, typeof(ControlService)));
        global::Android.App.Application.Context.StopService(new Intent(global::Android.App.Application.Context, typeof(ControlService)));
    }
    public static void Handle(MobileEvent e)
    {
        var decision = Lifecycle.Handle(e); if (!Initialized) return;
        if (decision.ReleasePtt) Model.EndPtt();
        if (decision.AbortConnection) Model.InterruptConnection();
        _ = ApplyAudio(decision);
    }
    static async Task ApplyAudio(MobileDecision decision)
    {
        await audioGate.WaitAsync();
        try { if (decision.SuspendAudio) await Model.SuspendAudio(); else if (decision.ResumeAudio && Lifecycle.HasFocus && Lifecycle.ServiceRunning) await Model.ResumeAudio(); }
        catch (Exception) { Model.Notice = MobileApp.Text("AudioUnavailable"); }
        finally { audioGate.Release(); }
    }
    public static async Task ResumeAudio() { await audioGate.WaitAsync(); try { if (Lifecycle.HasFocus && Lifecycle.ServiceRunning) await Model.ResumeAudio(); } finally { audioGate.Release(); } }
    public static void Press()
    {
        if (!Lifecycle.CanPtt || !Model.AudioReady) { Model.Notice = MobileApp.Text("MobilePttUnavailable"); return; }
        Model.BeginPtt(); Haptic(25);
    }
    public static void Release() { Model.EndPtt(); Haptic(12); }
    static void Haptic(long duration) { try { using var vibrator = (Vibrator?)global::Android.App.Application.Context.GetSystemService(Context.VibratorService); vibrator?.Vibrate(VibrationEffect.CreateOneShot(duration, VibrationEffect.DefaultAmplitude)); } catch (Exception) { } }
}
