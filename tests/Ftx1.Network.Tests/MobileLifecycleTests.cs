using Ftx1.Client;
using Ftx1.Client.ViewModels;
using Ftx1.Audio;
using Xunit;
namespace Ftx1.Network.Tests;
public class MobileLifecycleTests
{
    [Theory]
    [InlineData(MobileEvent.Pause)] [InlineData(MobileEvent.ScreenOff)] [InlineData(MobileEvent.FocusLost)]
    [InlineData(MobileEvent.NetworkChanged)] [InlineData(MobileEvent.NetworkLost)] [InlineData(MobileEvent.HeadsetDisconnected)] [InlineData(MobileEvent.ServiceStopped)]
    public async Task EverySafetyEventReleasesActualServerPttWithoutRekeying(MobileEvent reason)
    {
        await using var fixture = new StationFixture(); await fixture.Start(); var token = fixture.Server.Tokens.Create("android-lifecycle"); var store = new ClientSettingsStore(fixture.Root); store.Save(new(fixture.Server.Address!.ToString(), fixture.Server.Certificate.Fingerprint, token.DeviceToken));
        await using var model = new MainViewModel(k => k, a => a(), store, () => new SimVoiceDevice()); await model.ConnectCommand.ExecuteAsync(null); await StationFixture.Wait(() => model.AudioReady, 10000);
        var lifecycle = new MobileLifecycle(); lifecycle.Handle(MobileEvent.Resume); lifecycle.Handle(MobileEvent.ServiceStarted); lifecycle.Handle(MobileEvent.FocusGained); Assert.True(lifecycle.CanPtt);
        model.BeginPtt(); await StationFixture.Wait(() => fixture.Radio.State["TX"] == "1"); var decision = lifecycle.Handle(reason); Assert.True(decision.ReleasePtt); if (decision.ReleasePtt) model.EndPtt(); if (decision.AbortConnection) model.InterruptConnection(); if (decision.SuspendAudio) await model.SuspendAudio();
        await StationFixture.Wait(() => fixture.Radio.State["TX"] == "0", 1000); lifecycle.Handle(MobileEvent.Resume); lifecycle.Handle(MobileEvent.FocusGained); lifecycle.Handle(MobileEvent.NetworkAvailable); await Task.Delay(300); Assert.Equal("0", fixture.Radio.State["TX"]);
    }
    [Fact]
    public void BackgroundReceptionNeedsServiceAndFocusButBackgroundPttIsAlwaysForbidden()
    {
        var lifecycle = new MobileLifecycle(); Assert.False(lifecycle.CanPtt); lifecycle.Handle(MobileEvent.Resume); lifecycle.Handle(MobileEvent.FocusGained);
        Assert.False(lifecycle.CanPtt); var pause = lifecycle.Handle(MobileEvent.Pause); Assert.True(pause.SuspendAudio);
        lifecycle.Handle(MobileEvent.ServiceStarted); lifecycle.Handle(MobileEvent.Resume); Assert.True(lifecycle.CanPtt);
        pause = lifecycle.Handle(MobileEvent.ScreenOff); Assert.True(pause.ReleasePtt); Assert.False(pause.SuspendAudio); Assert.False(lifecycle.CanPtt);
        var focus = lifecycle.Handle(MobileEvent.FocusLost); Assert.True(focus.SuspendAudio); Assert.False(lifecycle.CanPtt);
        focus = lifecycle.Handle(MobileEvent.FocusGained); Assert.True(focus.ResumeAudio); Assert.False(lifecycle.CanPtt);
    }
}
