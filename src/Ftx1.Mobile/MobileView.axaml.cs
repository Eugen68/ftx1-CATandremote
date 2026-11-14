using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
namespace Ftx1.Mobile;
public sealed partial class MobileView : UserControl
{
    double? tuneX;
    public MobileView() { AvaloniaXamlLoader.Load(this); DataContext = MobilePlatform.Model; }
    async void Connect(object? sender, RoutedEventArgs e) => await MobilePlatform.ToggleConnection();
    async void Disconnect(object? sender, RoutedEventArgs e) => await MobilePlatform.Disconnect();
    async void Scan(object? sender, RoutedEventArgs e) { await MobilePlatform.ScanQr(); }
    async void ResumeAudio(object? sender, RoutedEventArgs e) { await MobilePlatform.ResumeAudio(); }
    void PttDown(object? sender, PointerPressedEventArgs e) { e.Pointer.Capture(sender as IInputElement); MobilePlatform.Press(); e.Handled = true; }
    void PttUp(object? sender, PointerReleasedEventArgs e) { MobilePlatform.Release(); e.Pointer.Capture(null); e.Handled = true; }
    void PttLost(object? sender, PointerCaptureLostEventArgs e) => MobilePlatform.Release();
    long SelectedStep => this.FindControl<ComboBox>("Step")?.SelectedItem is long step ? step : 100;
    void TuneDown(object? sender, RoutedEventArgs e) => MobilePlatform.Model.TuneByStep(SelectedStep, -1);
    void TuneUp(object? sender, RoutedEventArgs e) => MobilePlatform.Model.TuneByStep(SelectedStep, 1);
    void TuneStart(object? sender, PointerPressedEventArgs e) => tuneX = e.GetPosition(this).X;
    void TuneEnd(object? sender, PointerReleasedEventArgs e) { if (tuneX is { } start) { double delta = e.GetPosition(this).X - start; int steps = (int)(delta / 20); if (steps != 0) MobilePlatform.Model.TuneByStep(SelectedStep * Math.Abs(steps), Math.Sign(steps)); } tuneX = null; }
}
