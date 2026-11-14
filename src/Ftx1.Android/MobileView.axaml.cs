using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
namespace Ftx1.Android;
public sealed partial class MobileView : UserControl
{
    double? tuneX;
    public MobileView() { AvaloniaXamlLoader.Load(this); DataContext = MobileRuntime.Model; }
    async void Connect(object? sender, RoutedEventArgs e) => await MobileRuntime.ToggleConnection();
    async void Disconnect(object? sender, RoutedEventArgs e) => await MobileRuntime.Disconnect();
    async void Scan(object? sender, RoutedEventArgs e) { if (MainActivity.Current is { } activity) await activity.ScanQr(); }
    async void ResumeAudio(object? sender, RoutedEventArgs e) { if (MobileRuntime.Lifecycle.HasFocus && ControlService.Active) await MobileRuntime.ResumeAudio(); }
    void PttDown(object? sender, PointerPressedEventArgs e) { e.Pointer.Capture(sender as IInputElement); MobileRuntime.Press(); e.Handled = true; }
    void PttUp(object? sender, PointerReleasedEventArgs e) { MobileRuntime.Release(); e.Pointer.Capture(null); e.Handled = true; }
    void PttLost(object? sender, PointerCaptureLostEventArgs e) => MobileRuntime.Release();
    long SelectedStep => this.FindControl<ComboBox>("Step")?.SelectedItem is long step ? step : 100;
    void TuneDown(object? sender, RoutedEventArgs e) => MobileRuntime.Model.TuneByStep(SelectedStep, -1);
    void TuneUp(object? sender, RoutedEventArgs e) => MobileRuntime.Model.TuneByStep(SelectedStep, 1);
    void TuneStart(object? sender, PointerPressedEventArgs e) => tuneX = e.GetPosition(this).X;
    void TuneEnd(object? sender, PointerReleasedEventArgs e) { if (tuneX is { } start) { double delta = e.GetPosition(this).X - start; int steps = (int)(delta / 20); if (steps != 0) MobileRuntime.Model.TuneByStep(SelectedStep * Math.Abs(steps), Math.Sign(steps)); } tuneX = null; }
}
