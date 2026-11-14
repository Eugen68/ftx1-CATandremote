using System.Text;
using Ftx1.Core.Cat;
namespace Ftx1.Sim;

public sealed class SimTransport(SimRadio radio) : ITransport
{
    readonly object gate = new();
    string buffer = "";
    Action<byte[]?>? callback;
    int generation;
    bool closed = true;
    public bool Failed { get; private set; }
    public Func<string, bool>? RejectQuery { get; set; }
    public void Open(Action<byte[]?> onBytes) { lock (gate) { callback = onBytes; closed = false; Failed = false; generation++; } }
    public void Write(byte[] bytes)
    {
        lock (gate)
        {
            if (closed || Failed) throw new IOException("Simulator closed");
            buffer += Encoding.ASCII.GetString(bytes);
            int i; while ((i = buffer.IndexOf(';')) >= 0)
            {
                var cmd = buffer[..i]; buffer = buffer[(i + 1)..];
                var reply = RejectQuery?.Invoke(cmd) == true ? "?;" : radio.Handle(cmd);
                if (reply != "") _ = Reply(reply, generation);
            }
        }
    }
    async Task Reply(string text, int id) { await Task.Delay(4); Action<byte[]?>? cb; lock (gate) { if (closed || id != generation) return; cb = callback; } cb?.Invoke(Encoding.ASCII.GetBytes(text)); }
    public void Fail() { lock (gate) Failed = true; callback?.Invoke(null); }
    public void Close() { lock (gate) { closed = true; generation++; buffer = ""; } }
    public void Reopen() => Open(callback ?? throw new InvalidOperationException());
}
