using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ftx1.Core;
using Ftx1.Core.Cat;
using Ftx1.Sim;
using Xunit;
namespace Ftx1.Core.Tests;

public class ProtocolTests
{
    static JsonElement[] Data(string name) => JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", name + ".json"))).RootElement.EnumerateArray().Select(x => x.Clone()).ToArray();
    public static IEnumerable<object[]> Frames() => Data("parsers").Select(x => new object[] { x });
    public static IEnumerable<object[]> Parameters() => Data("parameters").Select(x => new object[] { x });
    [Theory, MemberData(nameof(Frames))]
    public void FrameMatchesPython(JsonElement item)
    {
        var frame = item.GetProperty("frame").GetString()!;
        var result = CatProtocol.ParseFrame(frame); var expected = item.GetProperty("expected");
        Assert.Equal(expected.EnumerateObject().Count(), result.Count);
        foreach (var property in expected.EnumerateObject())
        {
            Assert.True(result.ContainsKey(property.Name));
            if (property.Value.ValueKind == JsonValueKind.Number) Assert.Equal(property.Value.GetDouble(), Convert.ToDouble(result[property.Name], CultureInfo.InvariantCulture));
            else if (property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False) Assert.Equal(property.Value.GetBoolean(), Assert.IsType<bool>(result[property.Name]));
            else Assert.Equal(property.Value.GetString(), Assert.IsType<string>(result[property.Name]));
        }
        int rule = item.GetProperty("rule").GetInt32(); if (rule >= 0) Assert.Matches(CatProtocol.Parsers[rule].Pattern, frame);
    }
    [Theory, MemberData(nameof(Parameters))]
    public void ParameterFormattingMatchesPython(JsonElement item)
    {
        var value = item.GetProperty("value");
        var state = new Dictionary<string, object> { { "pwr_head", item.GetProperty("head").GetInt32() }, { "mode", "USB" }, { "freq_a", 14074000 }, { "clar_tx", 1 }, { "clar_rx", 0 } };
        object v = value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetDouble();
        Assert.Equal(item.GetProperty("commands").EnumerateArray().Select(x => x.GetString()), CatCommands.SetParam(item.GetProperty("key").GetString()!, v, state));
        var queued = item.GetProperty("queued").EnumerateArray().Select(x => x.GetString()!).ToArray();
        if (queued.Length > 0 && !item.GetProperty("key").GetString()!.StartsWith("freq_", StringComparison.Ordinal))
            Assert.Equal(queued[^1], CatCommands.Readback(item.GetProperty("key").GetString()!, queued[0]));
    }
    public static IEnumerable<object[]> Queries() => Data("queries").Select(x => new object[] { x });
    public static IEnumerable<object[]> Actions() => Data("actions").Select(x => new object[] { x });
    [Theory, MemberData(nameof(Queries))] public void AllQueriesMatchPython(JsonElement item) => Assert.Equal(item.GetProperty("query").GetBoolean(), CatProtocol.IsQuery(item.GetProperty("command").GetString()!));
    [Theory, MemberData(nameof(Actions))]
    public void ActionsMatchPython(JsonElement item)
    {
        object? Value(JsonElement v) => v.ValueKind switch { JsonValueKind.Null => null, JsonValueKind.String => v.GetString(), JsonValueKind.Array => v.EnumerateArray().Select(x => Value(x)!).ToArray(), _ => v.GetInt32() };
        Assert.Equal(item.GetProperty("commands").EnumerateArray().Select(x => x.GetString()), CatCommands.Action(item.GetProperty("name").GetString()!, Value(item.GetProperty("arg")), new Dictionary<string, object> { { "mem_ch", 2 } }));
    }
    [Fact]
    public void SimulatorCommandTraceMatchesPython()
    {
        using var sim = new SimRadio();
        foreach (var item in Data("simulator")) Assert.Equal(item.GetProperty("reply").GetString(), sim.Handle(item.GetProperty("command").GetString()!));
    }
    [Theory]
    [InlineData("FA;", true)]
    [InlineData("FA014074000;", false)]
    [InlineData("AG0;", true)]
    [InlineData("AG0128;", false)]
    [InlineData("EX010101;", true)]
    [InlineData("EX010101+0012;", false)]
    [InlineData("VE0;", true)]
    [InlineData("KY05;", false)]
    [InlineData("MR00001;", true)]
    [InlineData("MT00001;", true)]
    [InlineData("MT00001NAME;", false)]
    [InlineData("RI0;", true)]
    public void QueryClassification(string cmd, bool query) => Assert.Equal(query, CatProtocol.IsQuery(cmd));
    [Theory]
    [InlineData("7.074", 7074000)]
    [InlineData("14200", 14200000)]
    [InlineData("145500000", 145500000)]
    [InlineData("7,074", 7074000)]
    public void FrequencyUnitsMatchBrowser(string text, long expected) => Assert.Equal(expected, RadioControls.ParseFrequency(text));
    [Theory]
    [InlineData(50000000, 0)]
    [InlineData(144000000, 1)]
    [InlineData(420000000, 2)]
    public void BandTypes(long f, int expected) => Assert.Equal(expected, CatProtocol.BandType(f));
    [Fact]
    public void OrderedFirmwareAndNumericTypes()
    {
        Assert.Equal("1.10", CatProtocol.ParseFrame("VE00110")["fw0"]);
        Assert.IsType<int>(CatProtocol.ParseFrame("FA014074000")["freq_a"]);
        Assert.IsType<double>(CatProtocol.ParseFrame("PC15.5")["power"]);
        Assert.Equal(50, CatProtocol.Ctcss.Length); Assert.Equal(64, CatProtocol.Parsers.Length);
    }
    [Fact]
    public void MeterCalibrationsMatchSource()
    { Assert.Equal(9, RadioControls.Meter(130, RadioControls.SCal)); Assert.Equal(92, RadioControls.Meter(200, RadioControls.PoCal)); Assert.Equal(1.5, RadioControls.Meter(65, RadioControls.SwrCal)); }
    [Fact]
    public void MemoryWritesPreserveTonesAndAmClearsName()
    {
        using var sim = new SimRadio(); sim.Handle("MC000002");
        var write = "MW00002014565000+000000412002";
        Assert.Equal("", sim.Handle(write)); Assert.Equal("CN00008;", sim.Handle("CN00"));
        sim.Handle("MT00002TEST"); sim.Handle("AM"); Assert.Equal("MT00002;", sim.Handle("MT00002"));
    }
}
