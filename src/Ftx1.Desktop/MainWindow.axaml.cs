using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Ftx1.Client.ViewModels;
using Ftx1.Audio;
namespace Ftx1.Desktop;

// View-only input adaptation. CAT, ownership and lifecycle logic stay in the VM/Core.
public partial class MainWindow : Window
{
    readonly MainViewModel model;
    bool closing;
    public MainWindow() : this(null) { }
    public MainWindow(MainViewModel? viewModel)
    {
        AvaloniaXamlLoader.Load(this);
        string Text(string key) => this.TryFindResource(key, out var value) ? value?.ToString() ?? key : key;
        model = viewModel ?? new MainViewModel(Text, a => Dispatcher.UIThread.Post(a), audioDeviceFactory: (input, output) => AudioProcessDevice.CreateSelf(false, input, output, Path.Combine(Path.GetDirectoryName(new Ftx1.Client.ClientSettingsStore().PathName)!, "client-audio-worker.log")), audioDeviceList: AudioDevices.List); DataContext = model;
        AddHandler(KeyDownEvent, KeyPressed, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, KeyReleased, RoutingStrategies.Tunnel);
        Deactivated += (_, _) => model.EndPtt();
        Closing += async (_, e) => { if (closing) return; e.Cancel = true; settings?.Close(); try { await model.DisposeAsync(); } finally { closing = true; Close(); } };
    }
    void DigitWheel(object? sender, PointerWheelEventArgs e) { if (sender is Button { Tag: long power }) { model.TuneDigit(power, e.Delta.Y > 0 ? 1 : -1); e.Handled = true; } }
    void PttPressed(object? sender, PointerPressedEventArgs e) { if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return; e.Pointer.Capture(sender as Avalonia.Input.IInputElement); model.BeginPtt(); e.Handled = true; }
    void PttReleased(object? sender, PointerReleasedEventArgs e) { try { model.EndPtt(); } finally { e.Pointer.Capture(null); } }
    void PttCaptureLost(object? sender, PointerCaptureLostEventArgs e) => model.EndPtt();
    void KeyPressed(object? sender, KeyEventArgs e) { if (e.Key == Key.F2) { ShowSettings(SettingsWindow.ConnectionTab); e.Handled = true; } else if (e.Key == Key.F12) { ShowSettings(SettingsWindow.ConsoleTab); e.Handled = true; } else if (e.Key == Key.Escape) { model.StopCommand.Execute(null); e.Handled = true; } else if (e.Key == Key.Space && e.Source is not TextBox && e.Source is not NumericUpDown) { model.BeginPtt(); e.Handled = true; } }
    void KeyReleased(object? sender, KeyEventArgs e) { if (e.Key == Key.Space) model.EndPtt(); }
    void FrequencyKey(object? sender, KeyEventArgs e) { if (e.Key == Key.Enter) { model.TuneInputCommand.Execute(null); e.Handled = true; } }
    SettingsWindow? settings;
    public SettingsWindow ShowSettings(int tab)
    {
        // A single, non-modal settings window: the console can stay open while operating.
        if (settings == null) { settings = new SettingsWindow(model); settings.Closed += (_, _) => settings = null; settings.Show(this); }
        else settings.Activate();
        settings.ShowTab(tab);
        return settings;
    }
    void OpenSettings(object? sender, RoutedEventArgs e) => ShowSettings(SettingsWindow.ConnectionTab);
    void OpenConsole(object? sender, RoutedEventArgs e) => ShowSettings(SettingsWindow.ConsoleTab);
    void ExitClicked(object? sender, RoutedEventArgs e) => Close();
}
