using System.Collections.ObjectModel;
using System.Globalization;
using System.IO.Ports;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ftx1.Core;
using Ftx1.Core.Cat;
using Ftx1.Core.Safety;
using Ftx1.Sim;
namespace Ftx1.App.ViewModels;

public sealed record FrequencyDigit(string Text, long Power);
public partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    readonly Func<string, string> text;
    readonly Action<Action> post;
    readonly ConfigStore store;
    readonly Func<SimRadio> createSimulator;
    Rig? rig;
    SimRadio? simulatorRadio;
    IDisposable? ptt;
    bool applying, disposed;
    readonly System.Diagnostics.Stopwatch txWatch = new();
    readonly Timer timer;
    public ObservableCollection<string> Ports { get; } = [];
    public ObservableCollection<string> CatLog { get; } = [];
    public ObservableCollection<FrequencyDigit> Digits { get; } = [];
    public IReadOnlyList<string> Modes => RadioControls.Modes;
    public IReadOnlyList<RadioControls.Band> Bands => RadioControls.Bands;
    public int[] BaudRates { get; } = [4800, 9600, 19200, 38400, 57600, 115200];
    public string[] PttMethods { get; } = ["TX1", "TX2", "RTS", "DTR"];
    [ObservableProperty] string catPort = "";
    [ObservableProperty] int baud = 38400;
    [ObservableProperty] string pttMethod = "TX1";
    [ObservableProperty] string pttPort = "";
    [ObservableProperty] bool simulator = true;
    [ObservableProperty] int maxTxSeconds = 180;
    [ObservableProperty] bool connectionOpen;
    [ObservableProperty] string status = "";
    [ObservableProperty] string notice = "";
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
    long freqA = 14074000;
    public bool CanConfigure => !ConnectionOpen;
    public string ConnectionLabel => text(ConnectionOpen ? "Disconnect" : "Connect");
    public MainViewModel(Func<string, string> text, Action<Action> post, ConfigStore? configStore = null, Func<SimRadio>? simulatorFactory = null)
    {
        this.text = text; this.post = post; store = configStore ?? new(); createSimulator = simulatorFactory ?? (() => new SimRadio());
        try { var c = store.Load(); CatPort = c.CatPort; Baud = c.CatBaud; PttMethod = c.PttMethod; PttPort = c.PttPort; Simulator = c.Simulator; MaxTxSeconds = c.MaxTxSeconds; }
        catch (Exception e) { Notice = text("ConfigError") + " " + e.Message; }
        Status = text("Disconnected"); RefreshPorts(); RenderFrequency();
        timer = new Timer(_ => post(() => { if (!disposed) TxTime = txWatch.IsRunning ? txWatch.Elapsed.ToString(@"mm\:ss", CultureInfo.InvariantCulture) : ""; }), null, 250, 250);
    }
    void Log(string line) { if (disposed) return; CatLog.Add(line); while (CatLog.Count > 500) CatLog.RemoveAt(0); }
    void Report(Exception e) { Notice = text("OperationError") + " " + text(e.Message); Log(Notice); }
    [RelayCommand] void RefreshPorts() { try { Ports.Clear(); foreach (var p in SerialPort.GetPortNames().Order()) Ports.Add(p); } catch (Exception e) { Report(e); } }
    partial void OnConnectionOpenChanged(bool value) { OnPropertyChanged(nameof(CanConfigure)); OnPropertyChanged(nameof(ConnectionLabel)); }
    [RelayCommand]
    async Task Connect()
    {
        if (ConnectionOpen) { await Disconnect(); return; }
        try
        {
            var cfg = new StationConfig { CatPort = CatPort, CatBaud = Baud, PttMethod = PttMethod, PttPort = PttPort, Simulator = Simulator, MaxTxSeconds = MaxTxSeconds };
            store.Save(cfg);
            ITransport transport;
            IPttLine? line = null;
            if (Simulator) { simulatorRadio = createSimulator(); transport = new SimTransport(simulatorRadio); }
            else { transport = new SerialTransport(CatPort, Baud); if (PttMethod is "RTS" or "DTR") line = new SerialPtt(PttPort, PttMethod); }
            // Simulator always uses CAT PTT, so no real RTS/DTR line is opened.
            rig = new Rig(transport, Simulator ? "TX1" : PttMethod, line, TimeSpan.FromSeconds(MaxTxSeconds));
            var current = rig;
            rig.Changed += diff => post(() => { if (!disposed && ReferenceEquals(rig, current)) Apply(diff); });
            rig.Raw += s => post(() => Log(s)); rig.Error += e => post(() => Report(e));
            rig.Transmit.Stopped += reason => post(() => { if (!ReferenceEquals(rig, current) || current.Transmit.Owner != null) return; ptt = null; txWatch.Stop(); Transmitting = false; if (reason != StopReason.Released) Notice = text("Stop" + reason); });
            await rig.Start(); ConnectionOpen = true; Notice = "";
        }
        catch (Exception e) { await Disconnect(); Report(e); }
    }
    void Apply(IReadOnlyDictionary<string, object> diff)
    {
        if (rig == null) return;
        var s = rig.State;
        double N(string k, double fallback = 0) => s.TryGetValue(k, out var v) ? Convert.ToDouble(v, CultureInfo.InvariantCulture) : fallback;
        bool On(string k) => s.TryGetValue(k, out var v) && (v is bool b ? b : Convert.ToInt32(v, CultureInfo.InvariantCulture) != 0);
        applying = true;
        try
        {
            Status = (On("connected") ? text("Connected") : text("Disconnected")) + " · " + s.GetValueOrDefault("radio_id", "") + " · " + text("Firmware") + " " + s.GetValueOrDefault("fw0", "");
            freqA = (long)N("freq_a", freqA); RenderFrequency(); FrequencyB = ((long)N("freq_b")).ToString("000,000,000", CultureInfo.InvariantCulture);
            Mode = Convert.ToString(s.GetValueOrDefault("mode", "USB"), CultureInfo.InvariantCulture)!;
            SelectedBand = Array.FindIndex(RadioControls.Bands, b => freqA >= b.Low && freqA <= b.High);
            Split = On("split"); Transmitting = On("tx");
            Af = N("af", Af); Rf = N("rf", Rf); Sql = N("sql", Sql); Mic = N("mic", Mic);
            MaxPower = N("pwr_head") == 2 ? 100 : 10; MinPower = N("pwr_head") == 2 ? 5 : 0.5; Power = N("power", Power);
            Meter = On("tx") ? string.Format(CultureInfo.InvariantCulture, text("TxMeters"), RadioControls.Meter(N("po"), RadioControls.PoCal), RadioControls.Meter(N("swr"), RadioControls.SwrCal), N("alc"))
                : string.Format(CultureInfo.InvariantCulture, text("SMeter"), RadioControls.Meter(N("smeter"), RadioControls.SCal));
        }
        finally { applying = false; }
    }
    void RenderFrequency()
    {
        var str = freqA.ToString("D9", CultureInfo.InvariantCulture); Digits.Clear(); long pow = 100000000;
        for (int i = 0; i < str.Length; i++, pow /= 10) Digits.Add(new FrequencyDigit(str[i].ToString(), pow));
    }
    public void TuneDigit(long power, int direction) { try { rig?.SetParam("freq_a", freqA + power * direction); } catch (Exception e) { Report(e); } }
    [RelayCommand] void TuneInput() { try { rig?.SetParam("freq_a", RadioControls.ParseFrequency(FrequencyInput)); FrequencyInput = ""; } catch (Exception e) { Report(e); } }
    void SetParam(string key, object value) { if (applying) return; try { rig?.SetParam(key, value); } catch (Exception e) { Report(e); } }
    partial void OnAfChanged(double value) => SetParam("af", value);
    partial void OnRfChanged(double value) => SetParam("rf", value);
    partial void OnSqlChanged(double value) => SetParam("sql", value);
    partial void OnPowerChanged(double value) => SetParam("power", value);
    partial void OnMicChanged(double value) => SetParam("mic", value);
    partial void OnModeChanged(string value) => SetParam("mode", value);
    partial void OnSplitChanged(bool value) => SetParam("split", value ? 1 : 0);
    partial void OnSelectedBandChanged(int value) { if (!applying && value >= 0) ActionCommand.Execute("band:" + value); }
    [RelayCommand]
    async Task Action(string action)
    {
        try
        {
            if (rig == null) return;
            if (action.StartsWith("band:", StringComparison.Ordinal)) await rig.Action("band", int.Parse(action[5..], CultureInfo.InvariantCulture));
            else await rig.Action(action);
        }
        catch (Exception e) { Report(e); }
    }
    [RelayCommand]
    void Toggle(string key)
    {
        if (rig == null) return; var st = rig.State; int current = Convert.ToInt32(st.GetValueOrDefault(key == "preamp" ? "preamp" + CatProtocol.BandType(freqA) : key, 0), CultureInfo.InvariantCulture);
        SetParam(key, key switch { "agc" => (current + 1) % 5, "preamp" => (current + 1) % (CatProtocol.BandType(freqA) == 0 ? 3 : 2), _ => current == 0 ? 1 : 0 });
    }
    public void BeginPtt()
    {
        if (ptt != null || rig == null) return;
        bool ok = false;
        try { ptt = rig.Ptt("manual"); ok = ptt != null; if (ok) { txWatch.Restart(); Notice = ""; } else Notice = text("TransmitBusy"); }
        catch (Exception e) { Report(e); }
        finally { if (!ok) EndPtt(); }
    }
    public void EndPtt() { try { ptt?.Dispose(); } finally { ptt = null; txWatch.Stop(); } }
    [RelayCommand] void Stop() { try { rig?.Transmit.Stop(); } finally { EndPtt(); if (rig != null) ActionCommand.Execute("tune_stop"); } }
    [RelayCommand] void SendRaw() { try { rig?.Send(RawCommand); RawCommand = ""; } catch (Exception e) { Report(e); } }
    [RelayCommand] void ClearLog() => CatLog.Clear();
    async Task Disconnect()
    {
        try { EndPtt(); if (rig != null) await rig.DisposeAsync(); }
        finally { rig = null; simulatorRadio?.Dispose(); simulatorRadio = null; ConnectionOpen = false; Status = text("Disconnected"); }
    }
    public async ValueTask DisposeAsync() { if (disposed) return; disposed = true; timer.Dispose(); try { await Disconnect(); } finally { EndPtt(); } }
}
