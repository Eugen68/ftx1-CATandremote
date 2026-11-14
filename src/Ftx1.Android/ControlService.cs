using global::Android.App;
using global::Android.Content;
using global::Android.Content.PM;
using global::Android.Media;
using global::Android.Net;
using global::Android.OS;
using Avalonia.Threading;
using Ftx1.Client;
namespace Ftx1.Android;
[Service(Name = "ro.eugen68.ftx1remote.ControlService", Exported = false, ForegroundServiceType = ForegroundService.TypeMicrophone | ForegroundService.TypeMediaPlayback)]
public sealed class ControlService : Service, AudioManager.IOnAudioFocusChangeListener
{
    const string ChannelId = "ftx1.connection"; const int NotificationId = 41;
    static ControlService? instance;
    public static bool HasWakeLock => instance?.wake?.IsHeld == true;
    public static bool Active { get; private set; }
    static TaskCompletionSource<bool>? ready;
    PowerManager.WakeLock? wake; AudioManager? audio; AudioFocusRequestClass? focus; ConnectivityManager? connectivity;
    NetworkEvents? networkEvents; RouteEvents? routeEvents; ScreenEvents? screenEvents; Timer? timer; bool stopping;
    public static async Task EnsureStarted(Context context)
    {
        if (Active) return; ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        context.StartForegroundService(new Intent(context, typeof(ControlService)));
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(8));
    }
    public override IBinder? OnBind(Intent? intent) => null;
    public override void OnCreate()
    {
        base.OnCreate(); instance = this;
        using var manager = (NotificationManager)GetSystemService(NotificationService)!;
        manager.CreateNotificationChannel(new NotificationChannel(ChannelId, MobileApp.Text("MobileNotificationChannel"), NotificationImportance.Low));
        var notification = BuildNotification();
        if (OperatingSystem.IsAndroidVersionAtLeast(30)) StartForeground(NotificationId, notification, ForegroundService.TypeMicrophone | ForegroundService.TypeMediaPlayback);
        else if (OperatingSystem.IsAndroidVersionAtLeast(29)) StartForeground(NotificationId, notification, ForegroundService.TypeMediaPlayback);
        else StartForeground(NotificationId, notification);
        using var power = (PowerManager)GetSystemService(PowerService)!; wake = power.NewWakeLock(WakeLockFlags.Partial, "FTX1:AudioSession"); wake!.SetReferenceCounted(false); wake.Acquire();
        audio = (AudioManager)GetSystemService(AudioService)!; audio.Mode = Mode.InCommunication;
        using var attrs = new AudioAttributes.Builder().SetUsage(AudioUsageKind.VoiceCommunication)!.SetContentType(AudioContentType.Speech)!.Build()!;
        focus = new AudioFocusRequestClass.Builder(AudioFocus.Gain).SetAudioAttributes(attrs)!.SetWillPauseWhenDucked(true)!.SetOnAudioFocusChangeListener(this, new Handler(Looper.MainLooper!))!.Build()!;
        bool focused = audio.RequestAudioFocus(focus) == AudioFocusRequest.Granted;
        Active = true; MobileRuntime.Handle(MobileEvent.ServiceStarted); MobileRuntime.Handle(focused ? MobileEvent.FocusGained : MobileEvent.FocusLost);
        SelectHeadset();
        routeEvents = new(this); audio.RegisterAudioDeviceCallback(routeEvents, new Handler(Looper.MainLooper!));
        screenEvents = new(); var filter = new IntentFilter(Intent.ActionScreenOff); filter.AddAction(AudioManager.ActionAudioBecomingNoisy);
        if (OperatingSystem.IsAndroidVersionAtLeast(33)) RegisterReceiver(screenEvents, filter, ReceiverFlags.NotExported); else RegisterReceiver(screenEvents, filter);
        connectivity = (ConnectivityManager)GetSystemService(ConnectivityService)!; networkEvents = new(); connectivity.RegisterDefaultNetworkCallback(networkEvents);
        timer = new Timer(_ => Dispatcher.UIThread.Post(Update), null, 1000, 1000); ready?.TrySetResult(true);
    }
    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId) { if (intent?.Action == "disconnect") { _ = MobileRuntime.Disconnect(); StopSelf(); } return StartCommandResult.NotSticky; }
    Notification BuildNotification()
    {
        var open = PendingIntent.GetActivity(this, 0, new Intent(this, typeof(MainActivity)), PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;
        var stop = PendingIntent.GetService(this, 1, new Intent(this, typeof(ControlService)).SetAction("disconnect"), PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;
        string detail = string.Format(System.Globalization.CultureInfo.InvariantCulture, MobileApp.Text(MobileRuntime.Model.AudioReady ? "MobileNotificationFormat" : "MobileNotificationPausedFormat"), MobileRuntime.Model.CurrentFrequency / 1000000d, MobileRuntime.Model.Mode);
        return new Notification.Builder(this, ChannelId).SetSmallIcon(Resource.Drawable.ic_radio)!.SetContentTitle(MobileApp.Text(MobileRuntime.Model.NetworkConnected ? "MobileNotificationTitle" : "MobileNotificationReconnecting"))!.SetContentText(detail)!.SetContentIntent(open)!.SetOngoing(true)!.SetCategory(global::Android.App.Notification.CategoryService)!.AddAction(new Notification.Action.Builder(null, MobileApp.Text("Disconnect"), stop).Build())!.Build();
    }
    void Update() { if (!Active || stopping) return; if (!MobileRuntime.Model.ConnectionOpen) { StopSelf(); return; } using var manager = (NotificationManager)GetSystemService(NotificationService)!; manager.Notify(NotificationId, BuildNotification()); }
    public void OnAudioFocusChange(AudioFocus change) => Dispatcher.UIThread.Post(() => MobileRuntime.Handle(change == AudioFocus.Gain ? MobileEvent.FocusGained : MobileEvent.FocusLost));
    void SelectHeadset()
    {
        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(31))
            {
                if (CheckSelfPermission(global::Android.Manifest.Permission.BluetoothConnect) != Permission.Granted) return;
                var devices = audio!.AvailableCommunicationDevices;
                var preferred = devices?.FirstOrDefault(d => d.Type == AudioDeviceType.BluetoothSco) ?? devices?.FirstOrDefault(d => d.Type is AudioDeviceType.WiredHeadset or AudioDeviceType.WiredHeadphones or AudioDeviceType.UsbHeadset);
                if (preferred != null) audio.SetCommunicationDevice(preferred);
            }
            else if (audio!.IsBluetoothScoAvailableOffCall && (audio.GetDevices(GetDevicesTargets.All)?.Any(d => d.Type == AudioDeviceType.BluetoothSco) == true)) {
#pragma warning disable CS0618
                audio.StartBluetoothSco(); audio.BluetoothScoOn = true;
#pragma warning restore CS0618
            }
        }
        catch (Java.Lang.SecurityException) { }
    }
    public override void OnTaskRemoved(Intent? rootIntent) { _ = MobileRuntime.Disconnect(); StopSelf(); base.OnTaskRemoved(rootIntent); }
    public override void OnDestroy()
    {
        stopping = true; Active = false; instance = null; timer?.Dispose(); MobileRuntime.Handle(MobileEvent.ServiceStopped); _ = MobileRuntime.Model.DisconnectSession();
        if (screenEvents != null) UnregisterReceiver(screenEvents); if (networkEvents != null) connectivity?.UnregisterNetworkCallback(networkEvents); if (routeEvents != null) audio?.UnregisterAudioDeviceCallback(routeEvents);
        if (focus != null) audio?.AbandonAudioFocusRequest(focus);
        if (OperatingSystem.IsAndroidVersionAtLeast(31)) audio?.ClearCommunicationDevice();
        else {
#pragma warning disable CS0618
            audio?.StopBluetoothSco(); if (audio != null) audio.BluetoothScoOn = false;
#pragma warning restore CS0618
        }
        if (audio != null) audio.Mode = Mode.Normal; if (wake?.IsHeld == true) wake.Release(); wake?.Dispose(); StopForeground(StopForegroundFlags.Remove); base.OnDestroy();
    }
    sealed class ScreenEvents : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent) => MobileRuntime.Handle(intent?.Action == Intent.ActionScreenOff ? MobileEvent.ScreenOff : MobileEvent.HeadsetDisconnected);
    }
    sealed class NetworkEvents : ConnectivityManager.NetworkCallback
    {
        Network? current; string? transports;
        public override void OnCapabilitiesChanged(Network network, NetworkCapabilities capabilities) { string next = string.Join(",", new[] { TransportType.Wifi, TransportType.Cellular, TransportType.Ethernet, TransportType.Vpn }.Where(capabilities.HasTransport)); bool changed = transports != null && transports != next; transports = next; if (changed) Dispatcher.UIThread.Post(() => MobileRuntime.Handle(MobileEvent.NetworkChanged)); }
        public override void OnAvailable(Network network) { bool changed = current != null && !current.Equals(network); current = network; Dispatcher.UIThread.Post(() => { MobileRuntime.Handle(MobileEvent.NetworkAvailable); if (changed) MobileRuntime.Handle(MobileEvent.NetworkChanged); }); }
        public override void OnLost(Network network) { if (current?.Equals(network) != true) return; current = null; Dispatcher.UIThread.Post(() => MobileRuntime.Handle(MobileEvent.NetworkLost)); }
    }
    sealed class RouteEvents(ControlService owner) : AudioDeviceCallback
    {
        public override void OnAudioDevicesAdded(AudioDeviceInfo[]? addedDevices) { if (addedDevices?.Any(d => d.Type is AudioDeviceType.BluetoothSco or AudioDeviceType.WiredHeadset or AudioDeviceType.UsbHeadset) == true) Dispatcher.UIThread.Post(() => { MobileRuntime.Release(); owner.SelectHeadset(); }); }
        public override void OnAudioDevicesRemoved(AudioDeviceInfo[]? removedDevices) { if (removedDevices?.Any(d => d.Type is AudioDeviceType.BluetoothSco or AudioDeviceType.BluetoothA2dp or AudioDeviceType.WiredHeadset or AudioDeviceType.UsbHeadset or AudioDeviceType.WiredHeadphones) == true) Dispatcher.UIThread.Post(() => MobileRuntime.Handle(MobileEvent.HeadsetDisconnected)); }
    }
}
