using System.Net.WebSockets;
namespace Ftx1.Protocol;
public sealed record SocketFrame(byte[] Bytes, bool Binary);
public static class SocketFrames
{
    public static async Task<byte[]?> Receive(WebSocket socket, CancellationToken token)
    {
        var frame = await ReceiveFrame(socket, token); if (frame?.Binary == true) throw new InvalidDataException("ExpectedText"); return frame?.Bytes;
    }
    public static async Task<SocketFrame?> ReceiveFrame(WebSocket socket, CancellationToken token)
    {
        using var output = new MemoryStream(); var buffer = new byte[4096]; WebSocketMessageType? type = null;
        while (true)
        {
            var r = await socket.ReceiveAsync(buffer.AsMemory(), token); if (r.MessageType == WebSocketMessageType.Close) return null;
            type ??= r.MessageType; if (r.MessageType != type) throw new InvalidDataException("MixedFrameTypes");
            int limit = type == WebSocketMessageType.Binary ? AudioPacket.HeaderBytes + AudioPacket.MaxPayload : ProtocolVersion.MaxMessageBytes;
            if (output.Length + r.Count > limit) throw new InvalidDataException("MessageTooLarge"); output.Write(buffer, 0, r.Count);
            if (r.EndOfMessage) return new(output.ToArray(), type == WebSocketMessageType.Binary);
        }
    }
}
