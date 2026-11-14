// Ported from ftx1-pult/server/cat.py; preserve rule order.
using System.Globalization;
using System.Text.RegularExpressions;
namespace Ftx1.Core.Cat;
public static class CatProtocol {
static int I(string s) => int.Parse(s, CultureInfo.InvariantCulture);
public static readonly IReadOnlyDictionary<string,string> Modes = new Dictionary<string,string> { ["1"] = "LSB", ["2"] = "USB", ["3"] = "CW-U", ["4"] = "FM", ["5"] = "AM", ["6"] = "RTTY-L", ["7"] = "CW-L", ["8"] = "DATA-L", ["9"] = "RTTY-U", ["A"] = "DATA-FM", ["B"] = "FM-N", ["C"] = "DATA-U", ["D"] = "AM-N", ["E"] = "PSK", ["F"] = "DATA-FM-N", ["H"] = "C4FM-DN", ["I"] = "C4FM-VW" };
public static readonly IReadOnlyDictionary<int,string> Meters = new Dictionary<int,string> { [1] = "s_main", [2] = "s_sub", [3] = "comp", [4] = "alc", [5] = "po", [6] = "swr", [7] = "id", [8] = "vdd" };
public static readonly IReadOnlyDictionary<string,string> ModeCodes = Modes.ToDictionary(x=>x.Value,x=>x.Key);
public static readonly double[] Ctcss = [67.0, 69.3, 71.9, 74.4, 77.0, 79.7, 82.5, 85.4, 88.5, 91.5, 94.8, 97.4, 100.0, 103.5, 107.2, 110.9, 114.8, 118.8, 123.0, 127.3, 131.8, 136.5, 141.3, 146.2, 151.4, 156.7, 159.8, 162.2, 165.5, 167.9, 171.3, 173.8, 177.3, 179.9, 183.5, 186.2, 189.9, 192.8, 196.6, 199.5, 203.5, 206.5, 210.7, 218.1, 225.7, 229.1, 233.6, 241.8, 250.3, 254.1];
public static int BandType(long freq) => freq >= 420_000_000 ? 2 : freq >= 144_000_000 ? 1 : 0;
public sealed record Parser(string Pattern, Func<string[], Dictionary<string,object>> Read);
public static readonly Parser[] Parsers = [
new("^FA(\\d{9})$", g => new Dictionary<string, object> { ["freq_a"] = I(g[1]) }),
new("^FB(\\d{9})$", g => new Dictionary<string, object> { ["freq_b"] = I(g[1]) }),
new("^MD0(.)$", g => new Dictionary<string, object> { ["mode"] = Modes.GetValueOrDefault(g[1], g[1]) }),
new("^MD1(.)$", g => new Dictionary<string, object> { ["mode_b"] = Modes.GetValueOrDefault(g[1], g[1]) }),
new("^TX(\\d)$", g => new Dictionary<string, object> { ["tx"] = (g[1] != "0") }),
new("^SM0(\\d{3})$", g => new Dictionary<string, object> { ["smeter"] = I(g[1]) }),
new("^SM1(\\d{3})$", g => new Dictionary<string, object> { ["smeter_b"] = I(g[1]) }),
new("^RM(\\d)(\\d{3})\\d*$", g => new Dictionary<string, object> { [Meters.GetValueOrDefault(I(g[1]), "m" + g[1])] = I(g[2]) }),
new("^PC([12])(.+)$", g => new Dictionary<string,object> { ["pwr_head"] = I(g[1]), ["power"] = double.Parse(g[2], CultureInfo.InvariantCulture) }),
new("^AG0(\\d{3})$", g => new Dictionary<string, object> { ["af"] = I(g[1]) }),
new("^RG0(\\d{3})$", g => new Dictionary<string, object> { ["rf"] = I(g[1]) }),
new("^SQ0(\\d{3})$", g => new Dictionary<string, object> { ["sql"] = I(g[1]) }),
new("^MG(\\d{3})$", g => new Dictionary<string, object> { ["mic"] = I(g[1]) }),
new("^VX(\\d)$", g => new Dictionary<string, object> { ["vox"] = I(g[1]) }),
new("^VG(\\d{3})$", g => new Dictionary<string, object> { ["vox_gain"] = I(g[1]) }),
new("^VD(\\d{2})$", g => new Dictionary<string, object> { ["vox_delay"] = I(g[1]) }),
new("^NL0(\\d{3})$", g => new Dictionary<string, object> { ["nb"] = I(g[1]) }),
new("^RL0(\\d{2})$", g => new Dictionary<string, object> { ["nr"] = I(g[1]) }),
new("^BC0(\\d)$", g => new Dictionary<string, object> { ["dnf"] = I(g[1]) }),
new("^BP00(\\d{3})$", g => new Dictionary<string, object> { ["notch"] = ((I(g[1]) != 0) ? 1 : 0) }),
new("^BP01(\\d{3})$", g => new Dictionary<string, object> { ["notch_freq"] = (I(g[1]) * 10) }),
new("^CO00(\\d{4})$", g => new Dictionary<string, object> { ["contour"] = ((I(g[1]) != 0) ? 1 : 0) }),
new("^CO01(\\d{4})$", g => new Dictionary<string, object> { ["contour_freq"] = I(g[1]) }),
new("^CO02(\\d{4})$", g => new Dictionary<string, object> { ["apf"] = ((I(g[1]) != 0) ? 1 : 0) }),
new("^CO03(\\d{4})$", g => new Dictionary<string, object> { ["apf_freq"] = I(g[1]) }),
new("^IS0(\\d)([+-])(\\d{4})$", g => new Dictionary<string, object> { ["ifshift_on"] = I(g[1]), ["ifshift"] = (I(g[3]) * ((g[2] == "-") ? -1 : 1)) }),
new("^SH0\\d(\\d{2})$", g => new Dictionary<string, object> { ["width"] = I(g[1]) }),
new("^GT0(\\d)\\d*$", g => new Dictionary<string, object> { ["agc"] = I(g[1]) }),
new("^PA([0-2])(\\d)$", g => new Dictionary<string, object> { ["preamp" + g[1]] = I(g[2]) }),
new("^RA0(\\d)$", g => new Dictionary<string, object> { ["att"] = I(g[1]) }),
new("^NA0(\\d)$", g => new Dictionary<string, object> { ["narrow"] = I(g[1]) }),
new("^FT(\\d)$", g => new Dictionary<string, object> { ["tx_vfo"] = I(g[1]) }),
new("^ST(\\d)$", g => new Dictionary<string, object> { ["split"] = ((I(g[1]) != 0) ? 1 : 0) }),
new("^VS(\\d)$", g => new Dictionary<string, object> { ["vfo"] = I(g[1]) }),
new("^FR(\\d{2})$", g => new Dictionary<string, object> { ["dual"] = ((g[1] == "00") ? 1 : 0) }),
new("^PR0(\\d)$", g => new Dictionary<string, object> { ["proc"] = ((g[1] == "2") ? 1 : 0) }),
new("^PL(\\d{3})$", g => new Dictionary<string, object> { ["proc_level"] = I(g[1]) }),
new("^ML0(\\d{3})$", g => new Dictionary<string, object> { ["mon"] = ((I(g[1]) != 0) ? 1 : 0) }),
new("^ML1(\\d{3})$", g => new Dictionary<string, object> { ["mon_level"] = I(g[1]) }),
new("^KS(\\d{3})$", g => new Dictionary<string, object> { ["key_speed"] = I(g[1]) }),
new("^BI(\\d)$", g => new Dictionary<string, object> { ["bk"] = I(g[1]) }),
new("^KP(\\d{2})$", g => new Dictionary<string, object> { ["key_pitch"] = I(g[1]) }),
new("^KR(\\d)$", g => new Dictionary<string, object> { ["keyer"] = I(g[1]) }),
new("^SD(\\d{2})$", g => new Dictionary<string, object> { ["bk_delay"] = I(g[1]) }),
new("^CF000(\\d)(\\d)\\d*$", g => new Dictionary<string, object> { ["clar_rx"] = I(g[1]), ["clar_tx"] = I(g[2]) }),
new("^CF001([+-]\\d{4})$", g => new Dictionary<string, object> { ["clar"] = I(g[1]) }),
new("^LK(\\d)$", g => new Dictionary<string, object> { ["lock"] = I(g[1]) }),
new("^AC(\\d)(\\d)(\\d)$", g => new Dictionary<string,object> { ["tuner"] = g[0][2..], ["tuning"] = g[3] != "0" }),
new("^CT0(\\d)$", g => new Dictionary<string, object> { ["tone_mode"] = I(g[1]) }),
new("^CN00(\\d{3})$", g => new Dictionary<string, object> { ["ctcss"] = I(g[1]) }),
new("^CN01(\\d{3})$", g => new Dictionary<string, object> { ["dcs"] = I(g[1]) }),
new("^OS0(\\d)$", g => new Dictionary<string, object> { ["rpt_shift"] = I(g[1]) }),
new("^TS(\\d)$", g => new Dictionary<string, object> { ["txw"] = I(g[1]) }),
new("^AO(\\d{3})$", g => new Dictionary<string, object> { ["amc"] = I(g[1]) }),
new("^AI(\\d)$", g => new Dictionary<string, object> { ["ai"] = I(g[1]) }),
new("^ID(\\d{4})$", g => new Dictionary<string, object> { ["radio_id"] = g[1] }),
new("^RI\\d{7}(\\d)\\d*$", g => new Dictionary<string, object> { ["busy"] = (g[1] == "1") }),
new("^VE(\\d)(\\d{2})-?\\.?(\\d{2})$", g => new Dictionary<string, object> { ["fw" + g[1]] = $"{I(g[2])}.{g[3]}" }),
new("^VE(\\d)(.+)$", g => new Dictionary<string, object> { ["fw" + g[1]] = g[2] }),
new("^MS(\\d)(\\d)$", g => new Dictionary<string, object> { ["meter_sel"] = I(g[1]) }),
new("^DA(\\d{2})\\d*$", g => new Dictionary<string, object> { ["dimmer"] = I(g[1]) }),
new("^SC0(\\d)$", g => new Dictionary<string, object> { ["scan"] = I(g[1]) }),
new("^VM0(\\d)$", g => new Dictionary<string, object> { ["vm"] = I(g[1]) }),
new("^MC0?(\\d{3,5})$", g => new Dictionary<string, object> { ["mem_ch"] = I(g[1]) }),
];
static readonly (Regex Rx, Parser Parser)[] Compiled = Parsers.Select(p => (new Regex(p.Pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)), p)).ToArray();
public static IReadOnlyDictionary<string,object> ParseFrame(string frame) { try { foreach(var (rx,p) in Compiled) { var m=rx.Match(frame); if(m.Success) return p.Read(m.Groups.Cast<Group>().Select(g=>g.Value).ToArray()); } } catch (Exception e) when(e is FormatException or OverflowException or RegexMatchTimeoutException) {} return new Dictionary<string,object>(); }
static readonly HashSet<string> Queries = ["ID", "IF", "OI", "TX", "FA", "FB", "PC", "MG", "VX", "VG", "VD", "FT", "VS", "FR", "PL", "KS", "BI", "KP", "KR", "SD", "LK", "AC", "TS", "AO", "AI", "MS", "DA", "ST"];
static readonly Dictionary<string,int> QueryLengths = new() { ["AG"] = 3, ["RG"] = 3, ["SQ"] = 3, ["SM"] = 3, ["RM"] = 3, ["MD"] = 3, ["NL"] = 3, ["RL"] = 3, ["BC"] = 3, ["BP"] = 4, ["CO"] = 4, ["IS"] = 3, ["SH"] = 3, ["GT"] = 3, ["PA"] = 3, ["RA"] = 3, ["NA"] = 3, ["PR"] = 3, ["ML"] = 3, ["CF"] = 5, ["CT"] = 3, ["CN"] = 4, ["OS"] = 3, ["VE"] = 3, ["EX"] = 8, ["SS"] = 4, ["SC"] = 3, ["MC"] = 3, ["VM"] = 3, ["MR"] = 7, ["MT"] = 7, ["MZ"] = 7, ["RI"] = 3 };
public static bool IsQuery(string command) { var c=command.TrimEnd(';'); return Queries.Contains(c) || (c.Length>=2 && QueryLengths.TryGetValue(c[..2],out var n) && c.Length==n); }
public static readonly string[] FastRx = ["SM0;", "RI0;"];
public static readonly string[] FastTx = ["RM5;", "RM6;", "RM4;", "RM3;"];
public static readonly string[] Medium = ["FA;", "FB;", "MD0;", "MD1;", "VS;", "ST;", "TX;"];
public static readonly string[] Slow = ["VM0;", "MC0;", "MD1;", "AG0;", "RG0;", "SQ0;", "PC;", "MG;", "VX;", "VG;", "VD;", "NL0;", "RL0;", "BC0;", "BP00;", "BP01;", "CO00;", "CO01;", "CO02;", "CO03;", "IS0;", "SH0;", "GT0;", "PA{band};", "RA0;", "NA0;", "FT;", "ST;", "VS;", "FR;", "PR0;", "PL;", "ML0;", "ML1;", "KS;", "BI;", "KP;", "KR;", "SD;", "CF000;", "CF001;", "LK;", "AC;", "CT0;", "CN00;", "CN01;", "OS0;", "TS;", "AO;", "RM8;", "RM7;", "SM1;"];
}
