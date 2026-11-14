using System.Net.WebSockets;
namespace Ftx1.Protocol;
public static class SocketFrames
{
    public static async Task<byte[]?> Receive(WebSocket socket, CancellationToken token)
    {
        using var output = new MemoryStream(); var buffer = new byte[4096];
        while (true) { var r = await socket.ReceiveAsync(buffer.AsMemory(), token); if (r.MessageType == WebSocketMessageType.Close) return null; if (r.MessageType != WebSocketMessageType.Text) throw new InvalidDataException("BinaryNotAvailable"); if (output.Length + r.Count > ProtocolVersion.MaxMessageBytes) throw new InvalidDataException("MessageTooLarge"); output.Write(buffer, 0, r.Count); if (r.EndOfMessage) return output.ToArray(); }
    }
}
