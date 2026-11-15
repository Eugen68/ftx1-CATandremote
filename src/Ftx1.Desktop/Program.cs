using Avalonia;
using Ftx1.Audio;
namespace Ftx1.Desktop;
internal static class Program
{
    [STAThread] public static void Main(string[] args) { if (AudioWorkerHost.TryRun(args)) return; BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
