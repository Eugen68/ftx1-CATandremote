using System.Globalization;
using System.Text.RegularExpressions;
namespace Ftx1.Core.Cat;

public static class CatCommands
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    public static int Clamp(object v, int lo, int hi) => Math.Clamp((int)Math.Clamp(Math.Round(Convert.ToDouble(v, Inv), MidpointRounding.ToEven), lo, hi), lo, hi);
    static int Get(IReadOnlyDictionary<string, object> st, string key, int fallback = 0) => st.TryGetValue(key, out var v) ? Convert.ToInt32(v, Inv) : fallback;
    public static List<string> SetParam(string key, object value, IReadOnlyDictionary<string, object> st)
    {
        int C(int lo, int hi) => Clamp(value, lo, hi);
        int Bit() => (long)Convert.ToDouble(value, Inv) != 0 ? 1 : 0;
        string F(string prefix, int lo, int hi, int width) => prefix + C(lo, hi).ToString("D" + width, Inv) + ";";
        string One(string prefix, int n) => prefix + n.ToString(Inv) + ";";
        var band = CatProtocol.BandType(Get(st, "freq_a"));
        string? cmd = key switch
        {
            "freq_a" => F("FA", 30000, 470000000, 9),
            "freq_b" => F("FB", 30000, 470000000, 9),
            "mode" or "mode_b" => CatProtocol.ModeCodes.TryGetValue(Convert.ToString(value, Inv) ?? "", out var code) && (!st.TryGetValue(key, out var old) || !Equals(old, value)) ? $"MD{(key == "mode_b" ? 1 : 0)}{code};" : null,
            "af" => F("AG0", 0, 255, 3),
            "rf" => F("RG0", 0, 255, 3),
            "sql" => F("SQ0", 0, 255, 3),
            "mic" => F("MG", 0, 100, 3),
            "vox" => One("VX", Bit()),
            "vox_gain" => F("VG", 0, 100, 3),
            "vox_delay" => F("VD", 0, 33, 2),
            "nb" => F("NL0", 0, 10, 3),
            "nr" => F("RL0", 0, 10, 2),
            "dnf" => One("BC0", Bit()),
            "notch" => $"BP00{Bit():D3};",
            "notch_freq" => $"BP01{Clamp(Convert.ToDouble(value, Inv) / 10, 1, 320):D3};",
            "contour" => $"CO00{Bit():D4};",
            "contour_freq" => F("CO01", 10, 3200, 4),
            "apf" => $"CO02{Bit():D4};",
            "apf_freq" => F("CO03", 0, 50, 4),
            "ifshift" => $"IS0{(C(-1200, 1200) != 0 ? 1 : 0)}{(C(-1200, 1200) >= 0 ? "+" : "-")}{Math.Abs(C(-1200, 1200)):D4};",
            "width" => F("SH00", 0, 23, 2),
            "agc" => One("GT0", C(0, 4)),
            "preamp" => $"PA{band}{C(0, band == 0 ? 2 : 1)};",
            "att" => One("RA0", Bit()),
            "narrow" => One("NA0", Bit()),
            "split" => One("ST", Bit()),
            "tx_vfo" => One("FT", Bit()),
            "vfo" => One("VS", Bit()),
            "dual" => Bit() != 0 ? "FR00;" : "FR01;",
            "proc" => One("PR0", Bit() != 0 ? 2 : 1),
            "proc_level" => F("PL", 0, 100, 3),
            "mon" => $"ML0{Bit():D3};",
            "mon_level" => F("ML1", 0, 100, 3),
            "key_speed" => F("KS", 4, 60, 3),
            "bk" => One("BI", Bit()),
            "key_pitch" => F("KP", 0, 75, 2),
            "keyer" => One("KR", Bit()),
            "bk_delay" => F("SD", 0, 33, 2),
            "clar_rx" => $"CF000{Bit()}{Get(st, "clar_tx")}000;",
            "clar_tx" => $"CF000{Get(st, "clar_rx")}{Bit()}000;",
            "clar" => $"CF001{(C(-9999, 9999) >= 0 ? "+" : "-")}{Math.Abs(C(-9999, 9999)):D4};",
            "lock" => One("LK", Bit()),
            "tone_mode" => One("CT0", C(0, 3)),
            "ctcss" => F("CN00", 0, 49, 3),
            "dcs" => F("CN01", 0, 103, 3),
            "rpt_shift" => One("OS0", C(0, 2)),
            "txw" => One("TS", Bit()),
            "amc" => F("AO", 0, 100, 3),
            "dimmer" => F("DA", 0, 20, 2),
            "meter_sel" => $"MS{C(0, 5)}0;",
            _ => null
        };
        if (key == "power")
        {
            var w = Convert.ToDouble(value, Inv);
            if (Get(st, "pwr_head") == 2) cmd = $"PC2{Clamp(w, 5, 100):D3};";
            else { w = Math.Clamp(w, 0.5, 10); cmd = "PC1" + (w == Math.Truncate(w) ? ((int)w).ToString("D3", Inv) : w.ToString("F1", Inv)) + ";"; }
        }
        return cmd is null ? [] : key == "agc" ? [cmd, "GT0;"] : [cmd];
    }
    static readonly Dictionary<string, string> Readbacks = new()
    {
        ["freq_a"] = "FA;",
        ["freq_b"] = "FB;",
        ["mode"] = "MD0;",
        ["mode_b"] = "MD1;",
        ["power"] = "PC;",
        ["mic"] = "MG;",
        ["vox"] = "VX;",
        ["vox_gain"] = "VG;",
        ["vox_delay"] = "VD;",
        ["split"] = "ST;",
        ["tx_vfo"] = "FT;",
        ["vfo"] = "VS;",
        ["dual"] = "FR;",
        ["proc_level"] = "PL;",
        ["key_speed"] = "KS;",
        ["bk"] = "BI;",
        ["key_pitch"] = "KP;",
        ["keyer"] = "KR;",
        ["bk_delay"] = "SD;",
        ["lock"] = "LK;",
        ["txw"] = "TS;",
        ["amc"] = "AO;",
        ["dimmer"] = "DA;",
        ["meter_sel"] = "MS;",
        ["notch"] = "BP00;",
        ["notch_freq"] = "BP01;",
        ["contour"] = "CO00;",
        ["contour_freq"] = "CO01;",
        ["apf"] = "CO02;",
        ["apf_freq"] = "CO03;",
        ["clar_rx"] = "CF000;",
        ["clar_tx"] = "CF000;",
        ["clar"] = "CF001;",
        ["ctcss"] = "CN00;",
        ["dcs"] = "CN01;"
    };
    public static string Readback(string key, string cmd) { if (Readbacks.TryGetValue(key, out var r)) return r; var q = Regex.Replace(cmd, @"[+\-0-9.]+;$", ""); return q.Length <= 3 ? cmd[..3] + ";" : q + ";"; }
    public static List<string> Action(string name, object? arg, IReadOnlyDictionary<string, object> state)
    {
        int N() => Convert.ToInt32(arg, Inv);
        return name switch
        {
            "a_to_b" => ["AB;"],
            "b_to_a" => ["BA;"],
            "swap" => ["SV;"],
            "band_up" => ["BU0;"],
            "band_down" => ["BD0;"],
            "up" => ["UP;"],
            "down" => ["DN;"],
            "tune" => ["AC003;"],
            "tune_ext" => ["AC103;"],
            "tune_stop" => ["AC000;"],
            "atas_tune" => ["AC123;"],
            "atas_up" => ["AC121;"],
            "atas_down" => ["AC122;"],
            "atas_stop" => ["AC120;"],
            "zero_in" => ["ZI0;"],
            "spot_on" => ["CS1;"],
            "spot_off" => ["CS0;"],
            "scan_up" => ["SC01;"],
            "scan_down" => ["SC02;"],
            "scan_stop" => ["SC00;"],
            "vfo_mode" => ["VM000;"],
            "mem_mode" => [$"MC0{Get(state, "mem_ch", 1):D5};"],
            "qmb_store" => ["QI;"],
            "qmb_recall" => ["QR;"],
            "clar_clear" => ["CF001+0000;"],
            "band" when arg != null => [$"BS0{N():D2};"],
            "cw_play" when arg != null => [$"KY0{N()};"],
            "cw_store" when arg is object[] a && a.Length == 2 => [$"KM{Convert.ToInt32(a[0], Inv)}{Regex.Replace(Convert.ToString(a[1], Inv)!.ToUpperInvariant()[..Math.Min(50, Convert.ToString(a[1], Inv)!.Length)], @"[^A-Z0-9 /?.,=+\-]", "")};"],
            "voice_play" when arg != null => [$"PB0{N()};"],
            "mem_ch" when arg != null => [$"MC0{N():D5};"],
            "power_off" => ["PS0;"],
            _ => []
        };
    }
}
