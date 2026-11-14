using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Ftx1.Desktop;
using Ftx1.Client;
using Ftx1.Audio;
using Ftx1.Client.ViewModels;
using Ftx1.Core;
using Ftx1.Server;
using Ftx1.Sim;
using System.Net;
using Ftx1.Core.Cat;
using Xunit;
[assembly: AvaloniaTestApplication(typeof(Ftx1.App.Tests.TestApp))]
namespace Ftx1.App.Tests;
public class TestApp { public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<Ftx1.Desktop.App>().UseHeadless(new AvaloniaHeadlessPlatformOptions()); }
public class WindowTests
{
    static async Task Wait(Func<bool> condition) { using var timeout = new CancellationTokenSource(6000); while (!condition()) await Task.Delay(10, timeout.Token); }
    [AvaloniaFact]
    public async Task WindowUsesProtocolAndClosingReleasesPtt()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); using var radio = new SimRadio(); await using var server = new StationServer(new(root, new StationConfig(), 0, [IPAddress.Loopback]), new SimTransport(radio)); await server.Start();
        var store = new ClientSettingsStore(root); var token = server.Tokens.Create("window"); store.Save(new(server.Address!.ToString(), server.Certificate.Fingerprint, token.DeviceToken));
        await using var model = new MainViewModel(k => k, a => Dispatcher.UIThread.Post(a), store, () => new SimVoiceDevice()); var window = new MainWindow(model); try { window.Show(); await model.ConnectCommand.ExecuteAsync(null); await Wait(() => model.Status.Contains("0840", StringComparison.Ordinal)); model.Af = 77; await Wait(() => radio.State["AG0"] == "077"); radio.Handle("AG0042"); await Wait(() => model.Af == 42); await Wait(() => model.AudioReady); model.BeginPtt(); await Wait(() => radio.State["TX"] == "1"); window.Close(); await Wait(() => radio.State["TX"] == "0"); Assert.True(server.Address != null); Assert.Contains("TX0", radio.Received); } finally { await model.DisposeAsync(); window.Close(); await server.DisposeAsync(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [AvaloniaFact]
    public async Task MenuAndConsoleRemainAvailable()
    {
        await using var model = new MainViewModel(k => k, a => Dispatcher.UIThread.Post(a), new ClientSettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")))); var window = new MainWindow(model); try { window.Show(); var settings = window.ShowSettings(SettingsWindow.ConsoleTab); Assert.Equal(SettingsWindow.ConsoleTab, settings.FindControl<TabControl>("Tabs")!.SelectedIndex); Assert.Same(model, settings.DataContext); Assert.Same(settings, window.ShowSettings(SettingsWindow.ConnectionTab)); settings.Close(); } finally { await model.DisposeAsync(); window.Close(); }
    }
    [AvaloniaFact]
    public void ServerAdministrationResourcesAndWindowLoad()
    {
        Assert.IsType<Avalonia.Controls.ResourceDictionary>(Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(new Uri("avares://FTX1-Server/Resources/Strings.ro.axaml")));
        Assert.True(Avalonia.Platform.AssetLoader.Exists(new Uri("avares://FTX1-Server/icon.png")));
        var admin=new AdminWindow();try{admin.Show();Assert.True(admin.IsVisible);}finally{admin.Close();}
    }
    sealed class CloseFault(SimRadio radio) : ITransport
    {
        readonly SimTransport inner = new(radio); public bool Failed => inner.Failed;
        public void Open(Action<byte[]?> cb) => inner.Open(cb); public void Write(byte[] data) => inner.Write(data); public void Reopen() => inner.Reopen();
        public void Close() { inner.Close(); throw new IOException("Injected cleanup failure"); }
    }
    [AvaloniaFact]
    public async Task StopCleanupFailureLeavesAdministrationOpenAndPortsEditableThenCanRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "ftx1-admin-" + Guid.NewGuid().ToString("N")); var radios = new List<SimRadio>(); int count = 0;
        await using var model = new AdminViewModel(k => k, root, options => { var radio = new SimRadio(); radios.Add(radio); return new StationServer(options with { Port = 0, Addresses = [IPAddress.Loopback] }, ++count == 1 ? new CloseFault(radio) : new SimTransport(radio)); });
        var window = new AdminWindow { DataContext = model };
        try
        {
            window.Show(); Assert.True(model.CanConfigure); await model.StartCommand.ExecuteAsync(null); Assert.True(model.Running, model.Status + (File.Exists(Path.Combine(root, "server-errors.log")) ? File.ReadAllText(Path.Combine(root, "server-errors.log")) : "")); Assert.False(model.CanConfigure);
            await model.StopCommand.ExecuteAsync(null); Assert.True(window.IsVisible); Assert.False(model.Running); Assert.True(model.CanConfigure); Assert.Null(model.QrImage); Assert.Contains("Injected cleanup failure", File.ReadAllText(Path.Combine(root, "server-errors.log")));
            await model.StartCommand.ExecuteAsync(null); Assert.True(model.Running, model.Status + (File.Exists(Path.Combine(root, "server-errors.log")) ? File.ReadAllText(Path.Combine(root, "server-errors.log")) : "")); await model.StopCommand.ExecuteAsync(null); Assert.True(window.IsVisible); Assert.True(model.CanConfigure);
        }
        finally { await model.DisposeAsync(); window.Close(); foreach (var radio in radios) radio.Dispose(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task MobileViewUsesSharedModelAndHasFourTabs()
    {
        await using var model = new MainViewModel(k => k, a => Dispatcher.UIThread.Post(a), new ClientSettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));
        Ftx1.Mobile.MobilePlatform.Model = model; var view = new Ftx1.Mobile.MobileView(); Assert.Same(model, view.DataContext); Assert.Equal(4, view.FindControl<TabControl>("Pages")!.ItemCount); Assert.NotNull(view.FindControl<Button>("Ptt"));
    }

}
