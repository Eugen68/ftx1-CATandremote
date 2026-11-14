using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
namespace Ftx1.Mobile;
public sealed partial class MobileApp : Avalonia.Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    public static string Text(string key) => Current is { } app && app.TryFindResource(key, out var value) ? value?.ToString() ?? key : key;
    public override void OnFrameworkInitializationCompleted()
    {
        MobilePlatform.Initialize?.Invoke();
        if (ApplicationLifetime is ISingleViewApplicationLifetime lifetime) lifetime.MainView = new MobileView();
        base.OnFrameworkInitializationCompleted();
    }
}
