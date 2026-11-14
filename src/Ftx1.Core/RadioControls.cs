using System.Globalization;
namespace Ftx1.Core;
public static class RadioControls
{
    public static readonly (double X, double Y)[] SCal = [(0, 0), (12, 1), (27, 2), (40, 3), (55, 4), (65, 5), (80, 6), (95, 7), (112, 8), (130, 9), (150, 10), (172, 11), (190, 12), (220, 13), (240, 14), (255, 15)];
    public static readonly (double X, double Y)[] PoCal = [(0, 0), (10, 0.8), (50, 8), (100, 26), (150, 54), (200, 92), (250, 140)];
    public static readonly (double X, double Y)[] SwrCal = [(0, 1), (12, 1), (39, 1.35), (65, 1.5), (89, 2), (242, 5), (255, 6)];
    public sealed record Band(string Name, long Low, long High, long Frequency, string Mode);
    public static readonly Band[] Bands = [new("160", 1810000, 2000000, 1850000, "LSB"), new("80", 3500000, 3800000, 3650000, "LSB"), new("60", 5250000, 5450000, 5357000, "USB"), new("40", 7000000, 7300000, 7100000, "LSB"), new("30", 10100000, 10150000, 10120000, "CW-U"), new("20", 14000000, 14350000, 14200000, "USB"), new("17", 18068000, 18168000, 18130000, "USB"), new("15", 21000000, 21450000, 21200000, "USB"), new("12", 24890000, 24990000, 24950000, "USB"), new("10", 28000000, 29700000, 28500000, "USB"), new("6", 50000000, 54000000, 50150000, "USB"), new("4", 69900000, 70500000, 70200000, "USB"), new("AIR", 108000000, 137000000, 118100000, "AM"), new("2", 144000000, 148000000, 145500000, "FM"), new("70", 430000000, 450000000, 433500000, "FM")];
    public static readonly string[] Modes = ["LSB", "USB", "CW-U", "CW-L", "AM", "AM-N", "FM", "FM-N", "DATA-U", "DATA-L", "DATA-FM", "RTTY-L", "RTTY-U", "PSK"];
    public static long ParseFrequency(string text)
    {
        var v = double.Parse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture);
        if (!double.IsFinite(v) || v <= 0) throw new FormatException("InvalidFrequency");
        if (v < 1000) v *= 1e6; else if (v < 1e6) v *= 1e3;
        return (long)Math.Clamp(Math.Round(v, MidpointRounding.ToEven), 30000, 470000000);
    }
    public static double Meter(double raw, (double X, double Y)[] points)
    {
        if (raw <= points[0].X) return points[0].Y;
        for (int i = 1; i < points.Length; i++) if (raw <= points[i].X) { var a = points[i - 1]; var b = points[i]; return a.Y + (b.Y - a.Y) * (raw - a.X) / (b.X - a.X); }
        return points[^1].Y;
    }
}
