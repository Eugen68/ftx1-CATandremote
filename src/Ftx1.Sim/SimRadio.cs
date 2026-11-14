using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace Ftx1.Sim;

/// <summary>CAT-only port of SimRadio and _sim_mem in server/sim.py.</summary>
public sealed class SimRadio : IDisposable
{
    sealed class Memory
    {
        public long f { get; set; }
        public string md { get; set; } = "2";
        public int ctm { get; set; }
        public int tone { get; set; }
        public int dcs { get; set; }
        public int sh { get; set; }
        public int clar { get; set; }
        public int rx { get; set; }
        public int tx { get; set; }
        public string name { get; set; } = ""; public int split { get; set; }
        public long? txf { get; set; }
    }
    readonly object gate = new();
    readonly Dictionary<string, string> state;
    readonly Dictionary<int, Memory> memories;
    readonly Dictionary<string, string> menu = [], keyer = [];
    readonly Dictionary<string, string> morse;
    readonly Dictionary<string, int> cursor = new() { { "0", 1 }, { "1", 1 } };
    readonly CancellationTokenSource cancel = new();
    readonly List<string> received = [];
    int vm, txGeneration;
    bool disposed;
    public double TxLevel { get; set; }
    public IReadOnlyList<string> Received { get { lock (gate) return received.ToArray(); } }
    public IReadOnlyDictionary<string, string> State { get { lock (gate) return new Dictionary<string, string>(state); } }
    public SimRadio()
    {
        using var stream = typeof(SimRadio).Assembly.GetManifestResourceStream("Ftx1.Sim.seed.json")!;
        using var seed = JsonDocument.Parse(stream);
        state = seed.RootElement.GetProperty("state").Deserialize<Dictionary<string, string>>()!;
        memories = seed.RootElement.GetProperty("memories").Deserialize<Dictionary<int, Memory>>()!;
        morse = seed.RootElement.GetProperty("morse").Deserialize<Dictionary<string, string>>()!;
    }
    static int I(string value) => int.Parse(value, CultureInfo.InvariantCulture);
    static long L(string value) => long.Parse(value, CultureInfo.InvariantCulture);
    static string Dig(long n, int width) => n.ToString("D" + width, CultureInfo.InvariantCulture);
    static Match Match(string c, string pattern) => Regex.Match(c, pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    void Later(double seconds, Action done) { var token = cancel.Token; _ = Task.Run(async () => { try { await Task.Delay(TimeSpan.FromSeconds(seconds), token); lock (gate) done(); } catch (OperationCanceledException) { } }); }
    public string Handle(string cmd)
    {
        lock (gate)
        {
            received.Add(cmd);
            if (cmd == "ID") return "ID0840;";
            if (cmd.StartsWith("VE", StringComparison.Ordinal) && cmd.Length == 3) return new Dictionary<string, string> { { "VE0", "VE00110;" }, { "VE1", "VE10107;" }, { "VE2", "VE20103;" }, { "VE3", "VE30104;" }, { "VE4", "VE40102;" } }.GetValueOrDefault(cmd, "?;");
            if (Match(cmd, @"^MD0.$").Success) state["SH0"] = "000";
            var signal = 60 + 40 * Math.Sin(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 3000.0);
            if (cmd == "RI0") return $"RI0000000{(state["TX"] == "0" && signal > I(state["SQ0"]) ? 1 : 0)};";
            if (cmd is "SM0" or "SM1") return cmd + Dig(state["TX"] != "0" ? 0 : Math.Max(0, (int)signal + Random.Shared.Next(0, 12)), 3) + ";";
            var m = Match(cmd, @"^RM(\d)$");
            if (m.Success)
            {
                bool tx = state["TX"] != "0"; var pw = state["PC"][0] == '2' ? I(state["PC"][1..]) : 10;
                int v = I(m.Groups[1].Value) switch { 5 => tx ? (int)Math.Min(255, TxLevel * 900 * pw / 50) : 0, 6 => tx ? 30 : 0, 4 => tx ? (int)Math.Min(255, TxLevel * 300) : 0, 3 => tx ? (int)Math.Min(255, TxLevel * 400) : 0, 8 => 196, 7 => tx ? 120 : 20, _ => 0 };
                return "RM" + m.Groups[1].Value + Dig(v, 3) + "000;";
            }
            m = Match(cmd, @"^EX(\d{6})(.*)$");
            if (m.Success) { var ex = m.Groups[1].Value; var val = m.Groups[2].Value; if (val != "") { menu[ex] = val; return ""; } return "EX" + ex + menu.GetValueOrDefault(ex, "0") + ";"; }
            var mem = MemoryCommand(cmd); if (mem != null) return mem;
            switch (cmd)
            {
                case "AB": state["FB"] = state["FA"]; return "";
                case "BA": state["FA"] = state["FB"]; return "";
                case "SV": (state["FA"], state["FB"]) = (state["FB"], state["FA"]); return "";
                case "UP": state["FA"] = Dig(L(state["FA"]) + 100, 9); return "";
                case "DN": state["FA"] = Dig(L(state["FA"]) - 100, 9); return "";
            }
            if (cmd.StartsWith("BS0", StringComparison.Ordinal))
            {
                long[] bands = [1840000, 3650000, 5357000, 7074000, 10136000, 14074000, 18100000, 21074000, 24915000, 28074000, 50313000, 70200000, 118100000, 145500000, 433500000];
                var index = I(cmd.Substring(3, 2)); if (index < bands.Length) state["FA"] = Dig(bands[index], 9); return "";
            }
            m = Match(cmd, @"^KM([1-5])(.*)$");
            if (m.Success) { var slot = m.Groups[1].Value; var text = m.Groups[2].Value; if (text != "") { keyer[slot] = text; return ""; } return "KM" + slot + keyer.GetValueOrDefault(slot, "") + ";"; }
            m = Match(cmd, @"^KY0([0-5])$");
            if (m.Success)
            {
                var slot = m.Groups[1].Value;
                if (slot == "0") { state["TX"] = "0"; txGeneration++; return ""; }
                var text = keyer.GetValueOrDefault(slot, "");
                if (text != "" && state["MD0"] is "3" or "7" && state["BI"] == "1")
                {
                    var generation = ++txGeneration; state["TX"] = "1";
                    double units = 0; bool gap = false;
                    foreach (Match token in Regex.Matches(text, @"<[A-Z]{2}>|."))
                    {
                        if (token.Value == " ") { if (gap) units += 4; gap = false; continue; }
                        if (!morse.TryGetValue(token.Value, out var code)) continue;
                        units += code.Sum(c => c == '.' ? 1 : 3) + code.Length - 1 + 3; gap = true;
                    }
                    Later(0.3 + units * 1.2 / Math.Max(4, I(state["KS"])), () => { if (txGeneration == generation) state["TX"] = "0"; });
                }
                return "";
            }
            if (cmd is "BU0" or "BD0" or "ZI0" or "QI" or "QR" || new[] { "KY", "KM", "PB", "CS", "SC", "VM", "MC", "PS" }.Any(cmd.StartsWith)) return "";
            if (state.TryGetValue(cmd, out var read)) return cmd + read + ";";
            foreach (var key in state.Keys.OrderByDescending(k => k.Length)) if (cmd.StartsWith(key, StringComparison.Ordinal) && cmd.Length > key.Length)
                {
                    var val = cmd[key.Length..]; if (key == "TX") { val = val == "0" ? "0" : "1"; txGeneration++; }
                    if (key == "PC" && val[0] == '2') val = "2" + Dig(Math.Clamp((int)double.Parse(val[1..], CultureInfo.InvariantCulture), 5, 100), 3);
                    state[key] = val;
                    if (key == "AC" && val.EndsWith('3')) Later(2.5, () => state["AC"] = "000");
                    return "";
                }
            return "?;";
        }
    }
    string? MemoryCommand(string cmd)
    {
        var m = Match(cmd, @"^MR(\d{5})$");
        if (m.Success)
        {
            if (!memories.TryGetValue(I(m.Groups[1].Value), out var c)) return "?;";
            var ct = c.ctm switch { 1 => 2, 2 => 1, _ => 0 }; return "MR" + m.Groups[1].Value + Dig(c.f, 9) + (c.clar < 0 ? "-" : "+") + Dig(Math.Abs(c.clar), 4) + c.rx + c.tx + c.md + "1" + ct + "00" + c.sh + ";";
        }
        m = Match(cmd, @"^MW(\d{5})(\d{9})([+-])(\d{4})(\d)(\d)(\w)(\d)(\d)(\d\d)(\d)$");
        if (m.Success)
        {
            var g = m.Groups; if (g[8].Value != "1" || g[9].Value == "3") return "?;";
            int ch = I(g[1].Value); if (!memories.TryGetValue(ch, out var c)) c = new Memory { tone = 12, txf = L(g[2].Value) };
            c.f = L(g[2].Value); c.md = g[7].Value; c.clar = I(g[4].Value) * (g[3].Value == "-" ? -1 : 1); c.rx = I(g[5].Value); c.tx = I(g[6].Value); c.ctm = g[9].Value switch { "1" => 2, "2" => 1, _ => 0 }; c.sh = I(g[11].Value); memories[ch] = c; return "";
        }
        if (cmd.StartsWith("MW", StringComparison.Ordinal)) return "?;";
        m = Match(cmd, @"^MT(\d{5})(.*)$");
        if (m.Success) { if (!memories.TryGetValue(I(m.Groups[1].Value), out var c)) return "?;"; var name = m.Groups[2].Value; if (name != "") { c.name = name[..Math.Min(12, name.Length)].TrimEnd(); return ""; } return "MT" + m.Groups[1].Value + c.name + ";"; }
        m = Match(cmd, @"^MZ(\d{5})(?:([01])(\d{9}))?$");
        if (m.Success) { if (!memories.TryGetValue(I(m.Groups[1].Value), out var c)) return "?;"; if (m.Groups[2].Success) { c.split = I(m.Groups[2].Value); c.txf = L(m.Groups[3].Value); return ""; } return "MZ" + m.Groups[1].Value + c.split + Dig(c.txf ?? c.f, 9) + ";"; }
        m = Match(cmd, @"^MC([01])(\d{5})$");
        if (m.Success)
        {
            var side = m.Groups[1].Value; int ch = I(m.Groups[2].Value); cursor[side] = ch;
            if (!memories.TryGetValue(ch, out var c)) return "?;";
            if (side == "0") { vm = 1; state["FA"] = Dig(c.f, 9); state["MD0"] = c.md; state["CT0"] = c.ctm.ToString(CultureInfo.InvariantCulture); state["CN00"] = Dig(c.tone, 3); state["CN01"] = Dig(c.dcs, 3); state["OS0"] = c.sh.ToString(CultureInfo.InvariantCulture); }
            return "";
        }
        if (cmd is "MC0" or "MC1") return cmd + Dig(cursor[cmd[2].ToString()], 5) + ";";
        if (cmd == "VM0") return "VM0" + (vm != 0 ? "11" : "00") + ";";
        if (cmd == "VM000") { vm = 0; return ""; }
        if (cmd == "AM")
        {
            memories.TryGetValue(cursor["0"], out var old);
            memories[cursor["0"]] = new Memory { f = L(state["FA"]), md = state["MD0"], ctm = I(state["CT0"]), tone = I(state["CN00"]), dcs = I(state["CN01"]), sh = I(state["OS0"]), split = old?.split ?? 0, txf = old?.txf ?? 0 }; return "";
        }
        return null;
    }
    public void Dispose() { lock (gate) { if (disposed) return; disposed = true; cancel.Cancel(); state["TX"] = "0"; txGeneration++; cancel.Dispose(); } }
}
