namespace Ftx1.Client;
public enum MobileEvent { Resume, Pause, ScreenOff, FocusGained, FocusLost, NetworkChanged, NetworkLost, NetworkAvailable, HeadsetDisconnected, ServiceStarted, ServiceStopped }
public sealed record MobileDecision(bool ReleasePtt, bool SuspendAudio, bool ResumeAudio, bool AbortConnection);
/// <summary>Platform-neutral safety policy. No lifecycle event can start or restore PTT.</summary>
public sealed class MobileLifecycle
{
    public bool Foreground { get; private set; }
    public bool ServiceRunning { get; private set; }
    public bool HasFocus { get; private set; }
    public bool HasNetwork { get; private set; } = true;
    public bool CanPtt => Foreground && ServiceRunning && HasFocus && HasNetwork;
    public MobileDecision Handle(MobileEvent e)
    {
        switch (e)
        {
            case MobileEvent.Resume: Foreground = true; break;
            case MobileEvent.Pause: case MobileEvent.ScreenOff: Foreground = false; break;
            case MobileEvent.FocusGained: HasFocus = true; break;
            case MobileEvent.FocusLost: HasFocus = false; break;
            case MobileEvent.NetworkLost: HasNetwork = false; break;
            case MobileEvent.NetworkAvailable: HasNetwork = true; break;
            case MobileEvent.ServiceStarted: ServiceRunning = true; break;
            case MobileEvent.ServiceStopped: ServiceRunning = false; break;
        }
        bool release = e is not (MobileEvent.Resume or MobileEvent.FocusGained or MobileEvent.NetworkAvailable or MobileEvent.ServiceStarted);
        bool suspend = !ServiceRunning || !HasFocus || e == MobileEvent.HeadsetDisconnected;
        bool resume = !suspend && e is MobileEvent.Resume or MobileEvent.FocusGained or MobileEvent.ServiceStarted;
        return new(release, suspend, resume, e is MobileEvent.NetworkChanged or MobileEvent.NetworkLost or MobileEvent.ServiceStopped);
    }
}
