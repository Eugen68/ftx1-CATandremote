using System.IO.Ports;
namespace Ftx1.Core.Cat;

public interface ITransport { void Open(Action<byte[]?> onBytes); void Write(byte[] bytes); void Close(); bool Failed { get; } void Reopen(); }
public interface IPttLine : IDisposable { void Set(bool on); }

public sealed class SerialTransport(string port, int baud = 38400) : ITransport
{
    readonly object gate = new();
    SerialPort? serial;
    Thread? reader;
    volatile bool stopping;
    volatile bool failed;
    Action<byte[]?>? callback;
    public bool Failed => failed;
    public void Open(Action<byte[]?> onBytes)
    {
        callback = onBytes;
        var next = new SerialPort(port, baud, Parity.None, 8, StopBits.One)
        { ReadTimeout = 50, WriteTimeout = 500, Handshake = Handshake.None, RtsEnable = false, DtrEnable = false };
        try { next.Open(); } catch { next.Dispose(); failed = true; throw; }
        lock (gate) serial = next;
        stopping = false; failed = false;
        reader = new Thread(() => Read(next)) { IsBackground = true, Name = "FTX1 CAT RX" }; reader.Start();
    }
    void Read(SerialPort current)
    {
        var buffer = new byte[256];
        while (!stopping)
        {
            try { var n = current.Read(buffer, 0, buffer.Length); if (n > 0) callback?.Invoke(buffer[..n]); }
            catch (TimeoutException) { }
            catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException)
            { if (!stopping) { failed = true; callback?.Invoke(null); } return; }
        }
    }
    public void Write(byte[] bytes) { lock (gate) { try { (serial ?? throw new IOException("CAT closed")).Write(bytes, 0, bytes.Length); } catch { failed = true; throw; } } }
    public void Close()
    {
        stopping = true;
        lock (gate) { try { serial?.Dispose(); } finally { serial = null; } }
        if (reader != Thread.CurrentThread) reader?.Join(1000);
    }
    public void Reopen() { Close(); Open(callback ?? throw new InvalidOperationException()); }
}

public sealed class SerialPtt : IPttLine
{
    readonly SerialPort serial;
    readonly bool dtr;
    readonly object gate = new();
    public SerialPtt(string port, string line = "RTS")
    {
        dtr = line.Equals("DTR", StringComparison.OrdinalIgnoreCase);
        serial = new SerialPort(port, 9600) { RtsEnable = false, DtrEnable = false, ReadTimeout = 50, WriteTimeout = 500 };
        try { serial.Open(); Set(false); } catch { serial.Dispose(); throw; }
    }
    public void Set(bool on) { lock (gate) { if (dtr) serial.DtrEnable = on; else serial.RtsEnable = on; } }
    public void Dispose() { try { Set(false); } finally { serial.Dispose(); } }
}
