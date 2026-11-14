using System.Collections.ObjectModel;
using System.IO.Ports;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ftx1.Core;
using Ftx1.Protocol;
using Microsoft.Win32;
using QRCoder;
namespace Ftx1.Server;
public partial class AdminViewModel : ObservableObject, IAsyncDisposable
{
    readonly Func<string, string> text; readonly ConfigStore config = new(Path.Combine(PrivateFiles.Root, "station"), Path.Combine(PrivateFiles.Root, "station")); StationServer? server;
    public ObservableCollection<string> Ports { get; } = []; public ObservableCollection<DeviceRecord> Devices { get; } = [];
    public int[] BaudRates { get; } = [4800, 9600, 19200, 38400, 57600, 115200]; public string[] PttMethods { get; } = ["TX1", "TX2", "RTS", "DTR"];
    [ObservableProperty] string catPort = ""; [ObservableProperty] int baud = 38400; [ObservableProperty] string pttMethod = "TX1"; [ObservableProperty] string pttPort = ""; [ObservableProperty] bool simulator = true; [ObservableProperty] int maxTxSeconds = 180;
    [ObservableProperty] bool running; [ObservableProperty] bool autoStart; [ObservableProperty] string status = ""; [ObservableProperty] string pairingData = ""; [ObservableProperty] Bitmap? qrImage; [ObservableProperty] DeviceRecord? selectedDevice;
    public bool CanConfigure => !Running;
    public AdminViewModel(Func<string, string> text)
    {
        this.text = text; try { var c = config.Load(); CatPort = c.CatPort; Baud = c.CatBaud; PttMethod = c.PttMethod; PttPort = c.PttPort; Simulator = Program.Arguments.Contains("--sim") || c.Simulator; MaxTxSeconds = c.MaxTxSeconds; AutoStart = WindowsStartup.Enabled; } catch (Exception e) { Status = text("ConfigError") + " " + e.Message; }
        RefreshPorts();
    }
    partial void OnRunningChanged(bool value) => OnPropertyChanged(nameof(CanConfigure));
    partial void OnAutoStartChanged(bool value) { try { WindowsStartup.Set(value); } catch (Exception e) { Status = text("OperationError") + " " + e.Message; } }
    [RelayCommand] void RefreshPorts() { Ports.Clear(); foreach (var p in SerialPort.GetPortNames().Order()) Ports.Add(p); }
    [RelayCommand]
    async Task Start()
    {
        if (server != null) return;
        try { var cfg = new StationConfig { CatPort = CatPort, CatBaud = Baud, PttMethod = PttMethod, PttPort = PttPort, Simulator = Simulator, MaxTxSeconds = MaxTxSeconds }; config.Save(cfg); server = new(new(PrivateFiles.Root, cfg)); server.DevicesChanged += () => Dispatcher.UIThread.Post(RefreshDevices); await server.Start(); Running = true; Status = text("ServerRunning") + " " + server.Address + (ListenPolicy.DefaultAddresses().Any(ListenPolicy.IsTailscale) ? "" : " · " + text("NoTailscale")); RefreshDevices(); NewPairing(); }
        catch (Exception e) { Status = text("OperationError") + " " + e.Message; if (server != null) await server.DisposeAsync(); server = null; Running = false; }
    }
    [RelayCommand] async Task Stop() { var old = server; server = null; if (old != null) await old.DisposeAsync(); Running = false; Status = text("ServerStopped"); PairingData = ""; QrImage?.Dispose(); QrImage = null; }
    [RelayCommand] void NewPairing() { if (server == null) return; PairingData = server.CreateQr(); using var generator = new QRCodeGenerator(); using var data = generator.CreateQrCode(PairingData, QRCodeGenerator.ECCLevel.Q); using var qr = new PngByteQRCode(data); var old = QrImage; QrImage = new Bitmap(new MemoryStream(qr.GetGraphic(5))); old?.Dispose(); }
    void RefreshDevices() { Devices.Clear(); if (server != null) foreach (var d in server.Tokens.Devices) Devices.Add(d); }
    [RelayCommand] void Revoke() { try{if (server != null && SelectedDevice != null) { server.Tokens.Revoke(SelectedDevice.Id); RefreshDevices(); }}catch(Exception e){Status=text("OperationError")+" "+e.Message;} }
    public void EmergencyStop() => server?.Station.Transmit.Stop(Ftx1.Core.Safety.StopReason.Shutdown);
    public async ValueTask DisposeAsync() => await Stop();
}
public static class WindowsStartup
{
    const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public static bool Enabled { get { if (!OperatingSystem.IsWindows()) return false; using var key = Registry.CurrentUser.OpenSubKey(Key); return key?.GetValue("FTX1-CATandRemote") != null; } }
    public static void Set(bool enabled) { if (!OperatingSystem.IsWindows()) return; using var key = Registry.CurrentUser.CreateSubKey(Key); if (enabled) key.SetValue("FTX1-CATandRemote", "\"" + Environment.ProcessPath + "\" --tray"); else key.DeleteValue("FTX1-CATandRemote", false); }
}
