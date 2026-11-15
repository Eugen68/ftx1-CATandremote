using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
namespace Ftx1.Mobile;
public sealed partial class MobileView : UserControl
{
    double? tuneX; IPointer? pttPointer;
    public MobileView()
    {
        AvaloniaXamlLoader.Load(this); DataContext = MobilePlatform.Model;
        this.FindControl<StackPanel>("PhoneAudio")!.DataContext = MobilePlatform.Audio;
        var ptt = this.FindControl<Button>("Ptt")!;
        ptt.AddHandler(InputElement.PointerPressedEvent, PttDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        ptt.AddHandler(InputElement.PointerReleasedEvent, PttUp, RoutingStrategies.Tunnel, handledEventsToo: true);
        ptt.AddHandler(InputElement.PointerCaptureLostEvent, PttLost, handledEventsToo: true);
        DetachedFromVisualTree += (_, _) => ReleasePtt();
    }
    async void Connect(object? sender, RoutedEventArgs e) => await MobilePlatform.ToggleConnection();
    async void Disconnect(object? sender, RoutedEventArgs e) => await MobilePlatform.Disconnect();
    async void Scan(object? sender, RoutedEventArgs e) { await MobilePlatform.ScanQr(); }
    async void ResumeAudio(object? sender, RoutedEventArgs e) { await MobilePlatform.ResumeAudio(); }
    async void ChooseSpeaker(object? sender, RoutedEventArgs e) { await MobilePlatform.ToggleSpeaker(); }
    void PttDown(object? sender, PointerPressedEventArgs e)
    {
        if (pttPointer != null || e.Pointer.Type == PointerType.Mouse && !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        pttPointer = e.Pointer; e.Pointer.Capture(sender as IInputElement); MobilePlatform.Press(); e.Handled = true;
    }
    void PttUp(object? sender, PointerReleasedEventArgs e) { if (ReferenceEquals(e.Pointer, pttPointer)) ReleasePtt(); e.Handled = true; }
    void PttLost(object? sender, PointerCaptureLostEventArgs e) { if (ReferenceEquals(e.Pointer, pttPointer)) ReleasePtt(); }
    void ReleasePtt() { var pointer = pttPointer; pttPointer = null; MobilePlatform.Release(); pointer?.Capture(null); }
    long SelectedStep => this.FindControl<ComboBox>("Step")?.SelectedItem is long step ? step : 100;
    void TuneDown(object? sender, RoutedEventArgs e) => MobilePlatform.Model.TuneByStep(SelectedStep, -1);
    void TuneUp(object? sender, RoutedEventArgs e) => MobilePlatform.Model.TuneByStep(SelectedStep, 1);
    void TuneStart(object? sender, PointerPressedEventArgs e) => tuneX = e.GetPosition(this).X;
    void TuneEnd(object? sender, PointerReleasedEventArgs e) { if (tuneX is { } start) { double delta = e.GetPosition(this).X - start; int steps = (int)(delta / 20); if (steps != 0) MobilePlatform.Model.TuneByStep(SelectedStep * Math.Abs(steps), Math.Sign(steps)); } tuneX = null; }
}
