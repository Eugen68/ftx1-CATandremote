using CommunityToolkit.Mvvm.ComponentModel;
namespace Ftx1.Mobile;
public sealed partial class MobileAudioState : ObservableObject
{
    bool syncing;
    [ObservableProperty] bool speakerSelected;
    [ObservableProperty] double phoneVolume;
    [ObservableProperty] string routeStatus = "";
    public string SpeakerLabel => MobileApp.Text(SpeakerSelected ? "MobileSpeakerSelected" : "MobileSpeakerChoose");
    public Action<double>? VolumeRequested { get; set; }
    partial void OnSpeakerSelectedChanged(bool value) => OnPropertyChanged(nameof(SpeakerLabel));
    partial void OnPhoneVolumeChanged(double value) { if (!syncing) VolumeRequested?.Invoke(Math.Clamp(value, 0, 100)); }
    public void Sync(bool selected, string route, double volume)
    {
        syncing = true;
        try { SpeakerSelected = selected; RouteStatus = route; PhoneVolume = Math.Clamp(volume, 0, 100); }
        finally { syncing = false; }
    }
}
