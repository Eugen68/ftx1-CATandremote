using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
namespace Ftx1.Server;
public partial class ServerApp : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            string Text(string key) => this.TryFindResource(key, out var value) ? value?.ToString() ?? key : key;
            var model = new AdminViewModel(Text); var window = new AdminWindow { DataContext = model }; desktop.MainWindow = window; desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var menu = new NativeMenu(); var show = new NativeMenuItem(Text("ShowServer")); show.Click += (_, _) => { window.Show(); window.Activate(); }; menu.Add(show);
            var quit = new NativeMenuItem(Text("ExitApplication")); quit.Click += async (_, _) => { try { await model.DisposeAsync(); } finally { desktop.Shutdown(); } }; menu.Add(quit);
            using var image = AssetLoader.Open(new Uri("avares://FTX1-Server/icon.png")); var tray = new TrayIcon { Icon = new WindowIcon(image), ToolTipText = Text("ServerTitle"), Menu = menu }; tray.Clicked += (_, _) => { window.Show(); window.Activate(); }; TrayIcon.SetIcons(this, new TrayIcons { tray });
            window.Closing += (_, e) => { e.Cancel = true; window.Hide(); }; desktop.Exit += (_, _) => { model.EmergencyStop(); tray.Dispose(); };
            window.Opened += async (_, _) => { await model.StartCommand.ExecuteAsync(null); if (Program.Arguments.Contains("--tray")) window.Hide(); };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
