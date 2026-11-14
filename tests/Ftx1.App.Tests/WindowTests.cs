using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Ftx1.Desktop;
using Ftx1.Client;
using Ftx1.Client.ViewModels;
using Ftx1.Core;
using Ftx1.Server;
using Ftx1.Sim;
using System.Net;
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
        await using var model = new MainViewModel(k => k, a => Dispatcher.UIThread.Post(a), store); var window = new MainWindow(model); try { window.Show(); await model.ConnectCommand.ExecuteAsync(null); await Wait(() => model.Status.Contains("0840", StringComparison.Ordinal)); model.Af = 77; await Wait(() => radio.State["AG0"] == "077"); radio.Handle("AG0042"); await Wait(() => model.Af == 42); model.BeginPtt(); await Wait(() => radio.State["TX"] == "1"); window.Close(); await Wait(() => radio.State["TX"] == "0"); Assert.True(server.Address != null); Assert.Contains("TX0", radio.Received); } finally { await model.DisposeAsync(); window.Close(); await server.DisposeAsync(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [AvaloniaFact]
    public async Task MenuAndConsoleRemainAvailable()
    {
        await using var model = new MainViewModel(k => k, a => Dispatcher.UIThread.Post(a), new ClientSettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")))); var window = new MainWindow(model); try { window.Show(); var settings = window.ShowSettings(SettingsWindow.ConsoleTab); Assert.Equal(SettingsWindow.ConsoleTab, settings.FindControl<TabControl>("Tabs")!.SelectedIndex); Assert.Same(model, settings.DataContext); Assert.Same(settings, window.ShowSettings(SettingsWindow.ConnectionTab)); settings.Close(); } finally { await model.DisposeAsync(); window.Close(); }
    }
}
