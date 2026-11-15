using Ftx1.Client.ViewModels;
namespace Ftx1.Mobile;
public static class MobilePlatform
{
    public static Action? Initialize { get; set; }
    public static MainViewModel Model { get; set; } = null!;
    public static Func<Task> ToggleConnection { get; set; } = () => Task.CompletedTask;
    public static Func<Task> Disconnect { get; set; } = () => Task.CompletedTask;
    public static Func<Task> ScanQr { get; set; } = () => Task.CompletedTask;
    public static Func<Task> ResumeAudio { get; set; } = () => Task.CompletedTask;
    public static MobileAudioState Audio { get; } = new();
    public static Func<Task> ToggleSpeaker { get; set; } = () => Task.CompletedTask;
    public static Action Press { get; set; } = () => { };
    public static Action Release { get; set; } = () => { };
}
