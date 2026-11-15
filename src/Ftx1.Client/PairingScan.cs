using System.Text.Json;
using Ftx1.Protocol;
namespace Ftx1.Client;

/// <summary>Confirms repeated valid reads; never initiates pairing or launches a URL.</summary>
public sealed class PairingScan
{
    string? candidate; long first, last, counted; int matches;
    public string? Text { get; private set; }
    public string? Address { get; private set; }
    public bool Ready => Text != null;
    public void Observe(string? text, long now)
    {
        if (candidate != null && now - last > 1500) Reset();
        if (!TryParse(text, out var qr)) return;
        if (candidate != text) { Reset(); candidate = text; first = now; counted = now - 100; }
        last = now;
        if (now - counted >= 100) { matches++; counted = now; }
        if (matches >= 3 && now - first >= 350) { Text = text; Address = qr!.Address; }
    }
    public void Reset() { candidate = Text = Address = null; matches = 0; first = last = counted = 0; }
    public static bool TryParse(string? text, out PairingQr? qr)
    {
        qr = null; if (string.IsNullOrWhiteSpace(text) || text.Length > 4096) return false;
        try
        {
            var value = JsonSerializer.Deserialize(text, ProtocolJson.Default.PairingQr);
            if (value == null || value.ProtocolVersion != ProtocolVersion.Current || value.Code == null || value.Code.Length != 64 || !value.Code.All(Uri.IsHexDigit) || value.Address == null || value.Fingerprint == null) return false;
            StationConnection.ValidateSettings(new(value.Address, value.Fingerprint, "")); qr = value; return true;
        }
        catch (Exception error) when (error is JsonException or ArgumentException) { return false; }
    }
}

public static class CameraFrame
{
    public static byte[] Gray(byte[] bytes, int width, int height, int rowStride, int pixelStride)
    {
        if (width <= 0 || height <= 0 || pixelStride <= 0 || rowStride < checked((width - 1) * pixelStride + 1) || bytes.Length < checked((height - 1) * rowStride + (width - 1) * pixelStride + 1)) throw new ArgumentException("InvalidCameraPlane");
        var gray = new byte[checked(width * height)];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) gray[y * width + x] = bytes[y * rowStride + x * pixelStride];
        return gray;
    }
    // TextureView already compensates sensor orientation; undo stretch, then rotate for display.
    public static float[] PreviewCorners(int width, int height, int sensor, int displayDegrees, int viewWidth, int viewHeight)
    {
        float w = sensor % 180 == 0 ? width : height, h = sensor % 180 == 0 ? height : width;
        bool swap = displayDegrees % 180 != 0; float scale = Math.Min(viewWidth / (swap ? h : w), viewHeight / (swap ? w : h));
        double angle = -displayDegrees * Math.PI / 180; float c = (float)Math.Cos(angle), s = (float)Math.Sin(angle);
        float[] result = new float[6], input = [-w / 2, -h / 2, w / 2, -h / 2, -w / 2, h / 2];
        for (int i = 0; i < 6; i += 2) { result[i] = viewWidth / 2f + scale * (input[i] * c - input[i + 1] * s); result[i + 1] = viewHeight / 2f + scale * (input[i] * s + input[i + 1] * c); }
        return result;
    }
}
