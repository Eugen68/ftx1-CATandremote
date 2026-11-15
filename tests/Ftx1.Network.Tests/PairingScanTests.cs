using System.Text.Json;
using Ftx1.Client;
using Ftx1.Protocol;
using Xunit;
namespace Ftx1.Network.Tests;
public class PairingScanTests
{
    static string Code(string host = "192.168.55.167") => JsonSerializer.Serialize(new PairingQr($"wss://{host}:8443/ws", new string('A',64), new string('B',64)), ProtocolJson.Default.PairingQr);
    [Fact] public void FirstFrameDoesNotConfirmAndRepeatedStableReadsDo()
    {
        var scan = new PairingScan(); var code = Code(); scan.Observe(code, 1000); Assert.False(scan.Ready);
        scan.Observe(code, 1150); Assert.False(scan.Ready); scan.Observe(code, 1300); Assert.False(scan.Ready);
        scan.Observe(code, 1450); Assert.True(scan.Ready); Assert.Equal(code, scan.Text); Assert.Equal("wss://192.168.55.167:8443/ws", scan.Address);
    }
    [Fact] public void DifferentOrStaleFramesCannotBeCombined()
    {
        var scan = new PairingScan(); scan.Observe(Code(), 1000); scan.Observe(Code(), 1300); scan.Observe(Code("192.168.55.178"), 1450); Assert.False(scan.Ready);
        scan.Observe(Code("192.168.55.178"), 1600); scan.Observe(Code("192.168.55.178"), 1900); Assert.True(scan.Ready);
        scan.Observe(null, 3500); Assert.False(scan.Ready); scan.Observe(Code(), 3600); Assert.False(scan.Ready);
    }
    [Theory] [InlineData("https://example.com")] [InlineData("{}")] [InlineData("{broken")]
    public void InvalidCodesNeverEnableConfirmation(string text) { var scan = new PairingScan(); foreach(var t in new long[] {1000,1200,1500,1800}) scan.Observe(text,t); Assert.False(scan.Ready); }
    [Fact] public void WrongProtocolOrMissingPairCodeIsRejected()
    {
        Assert.False(PairingScan.TryParse(Code().Replace("\"protocolVersion\":2", "\"protocolVersion\":99"), out _));
        var invalid = JsonSerializer.Serialize(new PairingQr("wss://192.168.55.167:8443/ws", new string('A',64), ""), ProtocolJson.Default.PairingQr); Assert.False(PairingScan.TryParse(invalid,out _));
    }
    [Fact] public void PaddedStridedYuvPlaneCopiesOnlyLuminanceAndAllowsNoLastRowPadding()
    {
        byte[] plane = [1,99,2,99,3,88,88,88,4,99,5,99,6];
        Assert.Equal(new byte[]{1,2,3,4,5,6}, CameraFrame.Gray(plane,3,2,8,2));
        Assert.Throws<ArgumentException>(() => CameraFrame.Gray(plane[..^1],3,2,8,2));
    }
    [Theory] [InlineData(0,0)] [InlineData(90,0)] [InlineData(270,0)] [InlineData(90,90)] [InlineData(0,90)] [InlineData(90,180)] [InlineData(90,270)]
    public void PreviewKeepsImageAspectAndFitsViewport(int sensor,int display)
    {
        var p=CameraFrame.PreviewCorners(1280,720,sensor,display,500,800);
        double Distance(int a,int b) => Math.Sqrt(Math.Pow(p[a]-p[b],2)+Math.Pow(p[a+1]-p[b+1],2));
        double ratio=Distance(0,2)/Distance(0,4); Assert.InRange(ratio,(sensor%180==0?1280d/720:720d/1280)-.001,(sensor%180==0?1280d/720:720d/1280)+.001);
        for(int i=0;i<6;i+=2){Assert.InRange(p[i],-.01f,500.01f);Assert.InRange(p[i+1],-.01f,800.01f);}
    }
}
