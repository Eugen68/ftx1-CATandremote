using System.Text.Json;
using System.Text.Json.Serialization;
namespace Ftx1.Protocol;

public static class ProtocolVersion { public const int Current = 2; public const int MaxMessageBytes = 65536; public const byte Audio = 1, Waterfall = 2, Microphone = 3; }
public sealed record Message
{
    public int? Bitrate { get; init; }
    public long? AudioEpoch { get; init; }
    public bool? AudioAvailable { get; init; }
    public string T { get; init; } = "";
    public int? ProtocolVersion { get; init; }
    public string? DeviceToken { get; init; }
    public string? ServerVersion { get; init; }
    public Dictionary<string, JsonElement>? State { get; init; }
    public Dictionary<string, JsonElement>? Diff { get; init; }
    public string? Key { get; init; }
    public JsonElement? Value { get; init; }
    public string? Name { get; init; }
    public JsonElement? Arg { get; init; }
    public string? Ex { get; init; }
    public bool? Signed { get; init; }
    public int? Digits { get; init; }
    public bool? On { get; init; }
    public string? Owner { get; init; }
    public bool? You { get; init; }
    public string? Reason { get; init; }
    public string? Error { get; init; }
    public string? Detail { get; init; }
    public long? Ts { get; init; }
    public long? RequestId { get; init; }
    public string? Result { get; init; }
    public string? Command { get; init; }
    public int? TotMinutes { get; init; }
}
public sealed record PairRequest(string Code, string DeviceName, int ProtocolVersion = ProtocolVersion.Current);
public sealed record PairResponse(string DeviceToken, string DeviceId, int ProtocolVersion = ProtocolVersion.Current);
public sealed record PairingQr(string Address, string Fingerprint, string Code, int ProtocolVersion = ProtocolVersion.Current);
public sealed record ClientSettings(string Address, string Fingerprint, string DeviceToken);
public sealed record DeviceRecord(string Id, string Name, string TokenHash, DateTimeOffset Created);
public sealed record TxAudit(string Owner, string DeviceName, DateTimeOffset Started, long Frequency, double Duration, string Reason);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Message))]
[JsonSerializable(typeof(PairRequest))]
[JsonSerializable(typeof(PairResponse))]
[JsonSerializable(typeof(PairingQr))]
[JsonSerializable(typeof(ClientSettings))]
[JsonSerializable(typeof(List<DeviceRecord>))]
[JsonSerializable(typeof(TxAudit))]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(object[]))]
public partial class ProtocolJson : JsonSerializerContext { }
public static class Wire
{
    public static byte[] Encode(Message message) => JsonSerializer.SerializeToUtf8Bytes(message, ProtocolJson.Default.Message);
    public static Message Decode(ReadOnlySpan<byte> bytes) => JsonSerializer.Deserialize(bytes, ProtocolJson.Default.Message) ?? throw new JsonException();
    public static JsonElement Element(object? value) => value switch
    {
        null => JsonSerializer.SerializeToElement<string>(null!, ProtocolJson.Default.String),
        JsonElement e => e.Clone(),
        string s => JsonSerializer.SerializeToElement(s, ProtocolJson.Default.String),
        bool b => JsonSerializer.SerializeToElement(b, ProtocolJson.Default.Boolean),
        int n => JsonSerializer.SerializeToElement(n, ProtocolJson.Default.Int32),
        long n => JsonSerializer.SerializeToElement(n, ProtocolJson.Default.Int64),
        double n => JsonSerializer.SerializeToElement(n, ProtocolJson.Default.Double),
        object[] a => JsonSerializer.SerializeToElement(a, ProtocolJson.Default.ObjectArray),
        _ => throw new ArgumentException("UnsupportedValue")
    };
    public static object Object(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()!,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => value.TryGetInt32(out var i) ? (object)i : value.GetDouble(),
        JsonValueKind.Array => value.EnumerateArray().Select(Object).ToArray(),
        _ => throw new JsonException("InvalidValue")
    };
    public static Dictionary<string, JsonElement> State(IReadOnlyDictionary<string, object> state) => state.ToDictionary(p => p.Key, p => Element(p.Value));
}
