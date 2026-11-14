namespace Ftx1.Audio;
public sealed record AudioDeviceInfo(string Id, string Name) { public override string ToString() => Name; }
public interface IVoiceDevice : IDisposable
{
    event Action<short[]>? Captured;
    event Action<Exception>? Failed;
    void Start();
    void Play(short[] samples);
    void Clear();
}
