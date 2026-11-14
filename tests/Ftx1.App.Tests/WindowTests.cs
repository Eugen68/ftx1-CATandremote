using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Ftx1.App;
using Ftx1.App.ViewModels;
using Ftx1.Core;
using Ftx1.Sim;
using Xunit;
[assembly: AvaloniaTestApplication(typeof(Ftx1.App.Tests.TestApp))]
namespace Ftx1.App.Tests;
public class TestApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
public class WindowTests
{
    static async Task Until(Func<bool> condition)
    { using var timeout = new CancellationTokenSource(5000); while (!condition()) await Task.Delay(10, timeout.Token); }
    static MainViewModel Model(string root, SimRadio radio) => new(key => key, a => Dispatcher.UIThread.Post(a), new ConfigStore(root, Path.Combine(root, "backup")), () => radio);
    [AvaloniaFact]
    public async Task RealWindowBindsControlsAndClosingReleasesPtt()
    {
        var root = Path.Combine(Path.GetTempPath(), "ftx1-window-" + Guid.NewGuid().ToString("N"));
        using var radio = new SimRadio(); var model = Model(root, radio); var window = new MainWindow(model);
        try
        {
            window.Show(); await model.ConnectCommand.ExecuteAsync(null);
            await Until(() => model.Status.Contains("0840", StringComparison.Ordinal));
            Assert.True(model.Simulator); Assert.Equal(9, model.Digits.Count);
            model.Af = 77; await Until(() => radio.State["AG0"] == "077");
            radio.Handle("AG0042"); await Until(() => model.Af == 42);
            model.BeginPtt(); Assert.Equal("1", radio.State["TX"]);
            window.Close(); await Until(() => !model.ConnectionOpen);
            Assert.Equal("0", radio.State["TX"]); Assert.Contains("KY00", radio.Received); Assert.Contains("TX0", radio.Received);
        }
        finally { await model.DisposeAsync(); window.Close(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [AvaloniaFact]
    public async Task FrequencyAndModeChangesTravelBothWays()
    {
        var root = Path.Combine(Path.GetTempPath(), "ftx1-vm-" + Guid.NewGuid().ToString("N"));
        using var radio = new SimRadio(); await using var model = Model(root, radio);
        try
        {
            await model.ConnectCommand.ExecuteAsync(null); await Until(() => model.Status.Contains("0840", StringComparison.Ordinal));
            model.FrequencyInput = "7.074"; model.TuneInputCommand.Execute(null); await Until(() => radio.State["FA"] == "007074000");
            Assert.Equal("007074000", string.Concat(model.Digits.Select(d => d.Text)));
            model.Mode = "FM"; await Until(() => radio.State["MD0"] == "4");
            radio.Handle("MD01"); await Until(() => model.Mode == "LSB");
            model.BeginPtt(); model.EndPtt(); Assert.Equal("0", radio.State["TX"]);
        }
        finally { await model.DisposeAsync(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [AvaloniaFact]
    public async Task MenuOpensSettingsAndConsoleSendsCommands()
    {
        var root = Path.Combine(Path.GetTempPath(), "ftx1-settings-" + Guid.NewGuid().ToString("N"));
        using var radio = new SimRadio(); var model = Model(root, radio); var window = new MainWindow(model);
        try
        {
            window.Show();
            var settings = window.ShowSettings(SettingsWindow.ConsoleTab);
            Assert.Same(settings, window.ShowSettings(SettingsWindow.ConnectionTab));     // a single instance
            Assert.Same(model, settings.DataContext);
            await model.ConnectCommand.ExecuteAsync(null); await Until(() => model.Status.Contains("0840", StringComparison.Ordinal));
            Assert.False(model.CanConfigure);                                               // settings locked while connected
            model.RawCommand = "AG0033;"; model.SendRawCommand.Execute(null);
            await Until(() => radio.State["AG0"] == "033");
            await Until(() => model.CatLog.Any(l => l.StartsWith(">AG0033", StringComparison.Ordinal)));
            model.RawCommand = "TX1;"; model.SendRawCommand.Execute(null);                   // raw transmit stays blocked
            await Task.Delay(200); Assert.Equal("0", radio.State["TX"]);
            window.Close(); await Until(() => !model.ConnectionOpen);
            Assert.False(settings.IsVisible);
        }
        finally { await model.DisposeAsync(); window.Close(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
