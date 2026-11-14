using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ftx1.Audio;
using Ftx1.Core;
using Ftx1.Protocol;
namespace Ftx1.Client.ViewModels;

public sealed record FrequencyDigit(string Text, long Power);
public partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    readonly Func<string, string> text; readonly Action<Action> post; readonly ClientSettingsStore store; readonly Timer timer;
    ClientAudio? audio; readonly Func<IVoiceDevice>? audioFactory; readonly string audioPath;
    public ObservableCollection<AudioDeviceInfo> AudioInputs { get; } = [];
    public ObservableCollection<AudioDeviceInfo> AudioOutputs { get; } = [];
    public int[] AudioBitrates { get; } = [12000, 24000, 32000];
    [ObservableProperty] bool audioEnabled = true;
    [ObservableProperty] int audioBitrate = 24000;
    [ObservableProperty] AudioDeviceInfo? audioInput;
    [ObservableProperty] AudioDeviceInfo? audioOutput;
    [ObservableProperty] string audioQuality = "";
    StationConnection? connection; bool applying, disposed, held; long freqA = 14074000;
    public ObservableCollection<string> CatLog { get; } = []; public ObservableCollection<FrequencyDigit> Digits { get; } = [];
    public IReadOnlyList<string> Modes => RadioControls.Modes; public IReadOnlyList<RadioControls.Band> Bands => RadioControls.Bands;
    [ObservableProperty] string serverAddress = "wss://127.0.0.1:8443/ws";
    [ObservableProperty] string fingerprint = "";
    [ObservableProperty] string deviceToken = "";
    [ObservableProperty] string pairingText = "";
    [ObservableProperty] string deviceName = "Desktop";
    [ObservableProperty] bool connectionOpen;
    [ObservableProperty] string status = "";
    [ObservableProperty] string notice = "";
    [ObservableProperty] string latency = "";
    [ObservableProperty] bool totWarning;
    [ObservableProperty] string frequencyInput = "";
    [ObservableProperty] string frequencyB = "";
    [ObservableProperty] string mode = "USB";
    [ObservableProperty] int selectedBand = 5;
    [ObservableProperty] bool split;
    [ObservableProperty] bool transmitting;
    [ObservableProperty] string txTime = "";
    [ObservableProperty] string meter = "";
    [ObservableProperty] double af = 128;
    [ObservableProperty] double rf = 255;
    [ObservableProperty] double sql;
    [ObservableProperty] double power = 5;
    [ObservableProperty] double maxPower = 100;
    [ObservableProperty] double minPower = 0.5;
    [ObservableProperty] double mic = 50;
    [ObservableProperty] string rawCommand = "";
    readonly System.Diagnostics.Stopwatch txWatch = new();
    public bool AudioReady => audio?.Ready == true;
    public bool CanConfigure => !ConnectionOpen;
    public string ConnectionLabel => text(ConnectionOpen ? "Disconnect" : "Connect");
    public MainViewModel(Func<string, string> text, Action<Action> post, ClientSettingsStore? settingsStore = null, Func<IVoiceDevice>? audioFactory = null)
    {
        this.audioFactory = audioFactory; this.text = text; this.post = post; store = settingsStore ?? new();
        try { if (store.Load() is { } cfg) { ServerAddress = cfg.Address; Fingerprint = cfg.Fingerprint; DeviceToken = cfg.DeviceToken; } } catch (Exception e) { Notice = text("ConfigError") + " " + e.Message; }
        audioPath = Path.Combine(Path.GetDirectoryName(store.PathName)!, "desktop-audio.json");
        try { var prefs = AudioPreferencesStore.Load(audioPath); AudioEnabled = prefs.Enabled; AudioBitrate = prefs.Bitrate; AudioInputs.Add(new("", text("AudioDefault"))); AudioOutputs.Add(new("", text("AudioDefault"))); foreach (var d in AudioDevices.List(true)) AudioInputs.Add(d); foreach (var d in AudioDevices.List(false)) AudioOutputs.Add(d); AudioInput = AudioInputs.FirstOrDefault(d => d.Id == prefs.Input) ?? AudioInputs[0]; AudioOutput = AudioOutputs.FirstOrDefault(d => d.Id == prefs.Output) ?? AudioOutputs[0]; } catch { Notice = text("AudioUnavailable"); }
        Status = text("Disconnected"); RenderFrequency(); timer = new Timer(_ => post(() => { if (disposed) return; AudioQuality = audio is { Ready: true } ? string.Format(CultureInfo.InvariantCulture, text("AudioQualityFormat"), audio.Quality.BufferedMs, audio.Quality.TargetMs, audio.Quality.Lost, audio.Quality.Late) : text("AudioInactive"); Latency = string.Format(CultureInfo.InvariantCulture, text("LatencyFormat"), connection?.LatencyMs ?? 0); TxTime = txWatch.IsRunning ? txWatch.Elapsed.ToString(@"mm\:ss", CultureInfo.InvariantCulture) : ""; }), null, 250, 250);
    }
    void Log(string s) { if (disposed) return; CatLog.Add(s); while (CatLog.Count > 500) CatLog.RemoveAt(0); }
    void Report(Exception e) { Notice = text("OperationError") + " " + text(e.Message); Log(Notice); }
    async Task Send(Message m) { try { if (connection != null) await connection.Send(m); } catch (Exception e) { Report(e); } }
    void Queue(Message m) => _ = Send(m);
    partial void OnConnectionOpenChanged(bool value) { OnPropertyChanged(nameof(CanConfigure)); OnPropertyChanged(nameof(ConnectionLabel)); }
    [RelayCommand]
    async Task Connect()
    {
        if (ConnectionOpen) { await Disconnect(); return; }
        try
        {
            if(connection!=null){if(audio!=null){await audio.DisposeAsync();audio=null;}var stale=connection;connection=null;await stale.DisposeAsync();}
            var cfg = new ClientSettings(ServerAddress, Fingerprint, DeviceToken); store.Save(cfg); var current = new StationConnection(cfg); connection = current;
            current.Changed += diff => post(() => { if (!disposed && ReferenceEquals(connection, current)) Apply(); });
            current.Error += code => post(() => { if (ReferenceEquals(connection, current)) { Notice = text(code); if (code is "AuthenticationFailed" or "ProtocolMismatch") ConnectionOpen = false; } });
            current.Received += m => post(() =>
            {
                if (disposed || !ReferenceEquals(connection, current)) return;
                if (m.T == "catLine") Log(m.Command ?? "");
                if (m.T is "helloOk" or "tot") TotWarning = current.TotMinutes <= 0;
                if (m.T == "pttState") { Transmitting = m.You == true; if (Transmitting) txWatch.Start(); else { held = false; txWatch.Reset(); } if (m.Reason != null && m.Reason != "Released") Notice = text("Stop" + m.Reason); }
            });
            AudioPreferencesStore.Save(audioPath, new(AudioEnabled, AudioBitrate, AudioInput?.Id ?? "", AudioOutput?.Id ?? ""));
            Notice = "";
            if (AudioEnabled) { try { audio = new(current, audioFactory?.Invoke() ?? AudioDevices.Create(false, AudioInput?.Id, AudioOutput?.Id), AudioBitrate); audio.Error += code => post(() => Notice = text(code)); audio.Start(); } catch { if (audio != null) await audio.DisposeAsync(); audio = null; Notice = text("AudioUnavailable"); } }
            ConnectionOpen = true; await current.Start();
        }
        catch (Exception e) { await Disconnect(); Report(e); }
    }
    [RelayCommand]
    async Task Pair()
    {
        try { var qr = System.Text.Json.JsonSerializer.Deserialize(PairingText, ProtocolJson.Default.PairingQr) ?? throw new FormatException("InvalidPairing"); var pair = await StationConnection.Pair(qr, DeviceName); ServerAddress = qr.Address; Fingerprint = qr.Fingerprint; DeviceToken = pair.DeviceToken; store.Save(new(ServerAddress, Fingerprint, DeviceToken)); PairingText = ""; Notice = text("PairingSucceeded"); }
        catch (Exception e) { Report(e); }
    }
    void Apply()
    {
        if (connection == null) return; if(!connection.Connected){held=false;Transmitting=false;txWatch.Reset();} var s = connection.State;
        double N(string k, double fallback = 0) => s.TryGetValue(k, out var v) ? Convert.ToDouble(v, CultureInfo.InvariantCulture) : fallback;
        bool On(string k) => s.TryGetValue(k, out var v) && (v is bool b ? b : Convert.ToInt32(v, CultureInfo.InvariantCulture) != 0);
        applying = true; try
        {
            Status = (On("connected") ? text("Connected") : text("Disconnected")) + " · " + s.GetValueOrDefault("radio_id", "") + " · " + text("Firmware") + " " + s.GetValueOrDefault("fw0", "");
            freqA = (long)N("freq_a", freqA); RenderFrequency(); FrequencyB = ((long)N("freq_b")).ToString("000,000,000", CultureInfo.InvariantCulture); Mode = Convert.ToString(s.GetValueOrDefault("mode", "USB"), CultureInfo.InvariantCulture)!;
            SelectedBand = Array.FindIndex(RadioControls.Bands, b => freqA >= b.Low && freqA <= b.High); Split = On("split");
            Af = N("af", Af); Rf = N("rf", Rf); Sql = N("sql", Sql); Mic = N("mic", Mic); MaxPower = N("pwr_head") == 2 ? 100 : 10; MinPower = N("pwr_head") == 2 ? 5 : 0.5; Power = N("power", Power);
            Meter = On("tx") ? string.Format(CultureInfo.InvariantCulture, text("TxMeters"), RadioControls.Meter(N("po"), RadioControls.PoCal), RadioControls.Meter(N("swr"), RadioControls.SwrCal), N("alc")) : string.Format(CultureInfo.InvariantCulture, text("SMeter"), RadioControls.Meter(N("smeter"), RadioControls.SCal));
        }
        finally { applying = false; }
    }
    void RenderFrequency() { var str = freqA.ToString("D9", CultureInfo.InvariantCulture); Digits.Clear(); long pow = 100000000; foreach (var c in str) { Digits.Add(new(c.ToString(), pow)); pow /= 10; } }
    public void TuneDigit(long power, int direction) => SetParam("freq_a", Math.Clamp(freqA + power * direction, 30000, 470000000));
    [RelayCommand] void TuneInput() { try { SetParam("freq_a", RadioControls.ParseFrequency(FrequencyInput)); FrequencyInput = ""; } catch (Exception e) { Report(e); } }
    void SetParam(string key, object value) { if (!applying) Queue(new() { T = "set", Key = key, Value = Wire.Element(value) }); }
    partial void OnAfChanged(double value) => SetParam("af", value); partial void OnRfChanged(double value) => SetParam("rf", value); partial void OnSqlChanged(double value) => SetParam("sql", value); partial void OnPowerChanged(double value) => SetParam("power", value); partial void OnMicChanged(double value) => SetParam("mic", value); partial void OnModeChanged(string value) => SetParam("mode", value); partial void OnSplitChanged(bool value) => SetParam("split", value ? 1 : 0);
    partial void OnSelectedBandChanged(int value) { if (!applying && value >= 0) ActionCommand.Execute("band:" + value); }
    [RelayCommand] Task Action(string action) => Send(action.StartsWith("band:", StringComparison.Ordinal) ? new() { T = "act", Name = "band", Arg = Wire.Element(int.Parse(action[5..], CultureInfo.InvariantCulture)) } : new() { T = "act", Name = action });
    [RelayCommand] void Toggle(string key) { if (connection == null) return; var st = connection.State; int current = Convert.ToInt32(st.GetValueOrDefault(key == "preamp" ? "preamp" + RadioControlsBand() : key, 0), CultureInfo.InvariantCulture); SetParam(key, key switch { "agc" => (current + 1) % 5, "preamp" => (current + 1) % (RadioControlsBand() == 0 ? 3 : 2), _ => current == 0 ? 1 : 0 }); }
    int RadioControlsBand() => freqA >= 420000000 ? 2 : freqA >= 144000000 ? 1 : 0;
    public void BeginPtt() { if (held || connection?.Connected != true) return; held = true; _ = Press(); }
    async Task Press() { try { await connection!.Ptt(true); if (!held) await connection.Ptt(false); } catch (Exception e) { held = false; Report(e); } }
    public void EndPtt() { held = false; if (connection?.Connected == true) _ = Release(); }
    async Task Release() { try { await connection!.Ptt(false); } catch (Exception e) { Report(e); } }
    [RelayCommand] void Stop() { EndPtt(); Queue(new() { T = "act", Name = "tune_stop" }); }
    [RelayCommand] Task SetTot() => Send(new() { T = "setTot" });
    [RelayCommand] void SendRaw() { Queue(new() { T = "cat", Command = RawCommand }); RawCommand = ""; }
    [RelayCommand] void ClearLog() => CatLog.Clear();
    async Task Disconnect() { held = false; var old = connection; connection = null; try { if (audio != null) { await audio.DisposeAsync(); audio = null; } if (old != null) await old.DisposeAsync(); } finally { ConnectionOpen = false; Transmitting = false; txWatch.Reset(); Status = text("Disconnected"); } }
    public async ValueTask DisposeAsync() { if (disposed) return; disposed = true; timer.Dispose(); await Disconnect(); }
}
