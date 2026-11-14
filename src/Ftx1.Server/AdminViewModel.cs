using System.Collections.ObjectModel;
using System.IO.Ports;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ftx1.Audio;
using Ftx1.Core;
using Ftx1.Protocol;
using Microsoft.Win32;
using QRCoder;
namespace Ftx1.Server;
public partial class AdminViewModel : ObservableObject, IAsyncDisposable
{
    readonly Func<string, string> text; readonly string root; readonly ConfigStore config; readonly Func<ServerOptions, StationServer> factory; StationServer? server;
    public ObservableCollection<AudioDeviceInfo> AudioInputs { get; } = [];
    public ObservableCollection<AudioDeviceInfo> AudioOutputs { get; } = [];
    [ObservableProperty] AudioDeviceInfo? audioInput;
    [ObservableProperty] AudioDeviceInfo? audioOutput;
    public ObservableCollection<string> Ports { get; } = []; public ObservableCollection<DeviceRecord> Devices { get; } = [];
    public int[] BaudRates { get; } = [4800, 9600, 19200, 38400, 57600, 115200]; public string[] PttMethods { get; } = ["TX1", "TX2", "RTS", "DTR"];
    [ObservableProperty] string catPort = ""; [ObservableProperty] int baud = 38400; [ObservableProperty] string pttMethod = "TX1"; [ObservableProperty] string pttPort = ""; [ObservableProperty] bool simulator = true; [ObservableProperty] int maxTxSeconds = 180;
    [ObservableProperty] bool running; [ObservableProperty] bool autoStart; [ObservableProperty] string status = ""; [ObservableProperty] string pairingData = ""; [ObservableProperty] Bitmap? qrImage; [ObservableProperty] DeviceRecord? selectedDevice;
    [ObservableProperty] bool busy;
    [ObservableProperty] string audioStatus = "";
    public bool CanConfigure => !Running && !Busy;
    public bool CanStart => !Running && !Busy;
    public bool CanStop => Running && !Busy;
    public AdminViewModel(Func<string, string> text, string? dataRoot = null, Func<ServerOptions, StationServer>? serverFactory = null)
    {
        this.text = text; root = dataRoot ?? PrivateFiles.Root; config = new(Path.Combine(root, "station"), Path.Combine(root, "station")); factory = serverFactory ?? (options => new StationServer(options)); AudioStatus = text("AudioNotStarted"); Status = text("ServerStopped"); try { var c = config.Load(); CatPort = c.CatPort; Baud = c.CatBaud; PttMethod = c.PttMethod; PttPort = c.PttPort; Simulator = Program.Arguments.Contains("--sim") || c.Simulator; MaxTxSeconds = c.MaxTxSeconds; AutoStart = WindowsStartup.Enabled; } catch (Exception e) { Status = text("ConfigError") + " " + e.Message; }
        try { var prefs = AudioPreferencesStore.Load(Path.Combine(root, "server-audio.json")); AudioInputs.Add(new("", text("AudioAutoUsb"))); AudioOutputs.Add(new("", text("AudioAutoUsb"))); foreach (var d in AudioDevices.List(true)) AudioInputs.Add(d); foreach (var d in AudioDevices.List(false)) AudioOutputs.Add(d); AudioInput = AudioInputs.FirstOrDefault(d => d.Id == prefs.Input) ?? AudioInputs[0]; AudioOutput = AudioOutputs.FirstOrDefault(d => d.Id == prefs.Output) ?? AudioOutputs[0]; } catch { Status = text("AudioUnavailable"); }
        RefreshPorts();
    }
    partial void OnRunningChanged(bool value) => ConfigurationChanged();
    partial void OnBusyChanged(bool value) => ConfigurationChanged();
    void ConfigurationChanged() { OnPropertyChanged(nameof(CanConfigure)); OnPropertyChanged(nameof(CanStart)); OnPropertyChanged(nameof(CanStop)); StartCommand.NotifyCanExecuteChanged(); StopCommand.NotifyCanExecuteChanged(); }
    void Log(Exception error) { try { Directory.CreateDirectory(root); File.AppendAllText(Path.Combine(root, "server-errors.log"), DateTimeOffset.Now.ToString("O") + " " + error + Environment.NewLine); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    void AudioChanged(bool ready, string detail) { AudioStatus = text(ready ? "AudioRunning" : "ServerAudioUnavailable") + " " + detail; }

    partial void OnAutoStartChanged(bool value) { try { WindowsStartup.Set(value); } catch (Exception e) { Status = text("OperationError") + " " + e.Message; } }
    [RelayCommand] void RefreshPorts() { Ports.Clear(); foreach (var p in SerialPort.GetPortNames().Order()) Ports.Add(p); }
    [RelayCommand(CanExecute = nameof(CanStart))]
    async Task Start()
    {
        if (server != null || Busy) return; Busy = true;
        try
        {
            var cfg = new StationConfig { CatPort = CatPort, CatBaud = Baud, PttMethod = PttMethod, PttPort = PttPort, Simulator = Simulator, MaxTxSeconds = MaxTxSeconds }; config.Save(cfg);
            AudioPreferencesStore.Save(Path.Combine(root, "server-audio.json"), new(Input: AudioInput?.Id ?? "", Output: AudioOutput?.Id ?? ""));
            var current = factory(new(root, cfg, AudioInput: AudioInput?.Id, AudioOutput: AudioOutput?.Id)); server = current;
            current.DevicesChanged += () => Dispatcher.UIThread.Post(() => { if (ReferenceEquals(server, current)) RefreshDevices(); });
            current.AudioStatusChanged += (ready, detail) => Dispatcher.UIThread.Post(() => { if (ReferenceEquals(server, current)) AudioChanged(ready, detail); });
            await current.Start(); Running = true; Status = text("ServerRunning") + " " + current.Address + ((current.ListeningAddresses?.Any(ListenPolicy.IsTailscale) == true) ? "" : " · " + text("NoTailscale")); AudioStatus = text("AudioNotStarted"); RefreshDevices(); NewPairing();
        }
        catch (Exception error)
        {
            Log(error); Status = text("OperationError") + " " + error.Message; var old = server; server = null;
            if (old != null) try { await old.DisposeAsync(); } catch (Exception cleanup) { Log(cleanup); }
            Running = false;
        }
        finally { Busy = false; }
    }
    [RelayCommand(CanExecute = nameof(CanStop))]
    async Task Stop()
    {
        if (Busy) return; Busy = true; var old = server; server = null; string? failure = null;
        try { if (old != null) await old.DisposeAsync(); }
        catch (Exception error) { Log(error); failure = error.Message; }
        finally
        {
            Running = false; Status = text("ServerStopped") + (failure == null ? "" : " · " + text("ServerCleanupWarning") + " " + failure);
            PairingData = ""; var image = QrImage; QrImage = null; try { image?.Dispose(); } catch (Exception error) { Log(error); } Devices.Clear(); AudioStatus = text("AudioNotStarted"); Busy = false;
        }
    }
    [RelayCommand] void TestAudio()
    {
        if (server == null) { AudioStatus = text("AudioStartServerFirst"); return; }
        try { server.CheckAudio(); AudioChanged(true, server.LastAudioStatus); }
        catch (Exception error) { Log(error); AudioChanged(false, AudioDiagnostic.Describe(error)); }
    }
    [RelayCommand] void NewPairing()
    {
        if (server == null) return; PairingData = server.CreateQr();
        try { using var generator = new QRCodeGenerator(); using var data = generator.CreateQrCode(PairingData, QRCodeGenerator.ECCLevel.Q); using var qr = new PngByteQRCode(data); var old = QrImage; QrImage = new Bitmap(new MemoryStream(qr.GetGraphic(5))); old?.Dispose(); }
        catch (Exception error) { Log(error); Status += " · " + text("QrImageFailed") + " " + error.Message; }
    }
    void RefreshDevices() { Devices.Clear(); if (server != null) foreach (var d in server.Tokens.Devices) Devices.Add(d); }
    [RelayCommand] void Revoke() { try{if (server != null && SelectedDevice != null) { server.Tokens.Revoke(SelectedDevice.Id); RefreshDevices(); }}catch(Exception e){Status=text("OperationError")+" "+e.Message;} }
    public void EmergencyStop() => server?.Station.Transmit.Stop(Ftx1.Core.Safety.StopReason.Shutdown);
    public async ValueTask DisposeAsync()
    {
        if (StartCommand.ExecutionTask is { IsCompleted: false } start) try { await start; } catch (Exception error) { Log(error); }
        if (StopCommand.ExecutionTask is { IsCompleted: false } stop) try { await stop; } catch (Exception error) { Log(error); }
        await Stop();
    }
}
public static class WindowsStartup
{
    const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public static bool Enabled { get { if (!OperatingSystem.IsWindows()) return false; using var key = Registry.CurrentUser.OpenSubKey(Key); return key?.GetValue("FTX1-CATandRemote") != null; } }
    public static void Set(bool enabled) { if (!OperatingSystem.IsWindows()) return; using var key = Registry.CurrentUser.CreateSubKey(Key); if (enabled) key.SetValue("FTX1-CATandRemote", "\"" + Environment.ProcessPath + "\" --tray"); else key.DeleteValue("FTX1-CATandRemote", false); }
}
