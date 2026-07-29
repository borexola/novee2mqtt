using Novee2Mqtt.Core;

namespace Novee2Mqtt.Ble;

/// <summary>
/// Encodes and decodes Govee ptReal frames. Which frames a device understands
/// depends on its SKU, so codecs are registered against a SKU list; the
/// pseudo-SKU <c>Generic:Light</c> covers frames that every light accepts.
/// </summary>
public static class PacketManager
{
    public const string GenericLight = "Generic:Light";

    private sealed record Codec(
        string[] Skus,
        Type PacketType,
        Func<GoveeBlePacket, byte[]> Encode,
        Func<byte[], GoveeBlePacket?> Decode);

    private static readonly Codec[] Codecs =
    [
        new(["H7160"], typeof(SetHumidifierMode),
            p => Frame([0x33, 0x05, ((SetHumidifierMode)p).Mode, ((SetHumidifierMode)p).Param]),
            data => Unframe(data, [0x33, 0x05], 2, f => new SetHumidifierMode(f[0], f[1]))),

        new(["H7160"], typeof(NotifyHumidifierMode),
            p => Frame([0xaa, 0x05, 0x00, ((NotifyHumidifierMode)p).Mode, ((NotifyHumidifierMode)p).Param]),
            data => Unframe(data, [0xaa, 0x05, 0x00], 2, f => new NotifyHumidifierMode(f[0], f[1]))),

        new(["H7160"], typeof(HumidifierAutoMode),
            p => Frame([0xaa, 0x05, 0x03, ((HumidifierAutoMode)p).TargetHumidity.Raw]),
            data => Unframe(data, [0xaa, 0x05, 0x03], 1, f => new HumidifierAutoMode(new TargetHumidity(f[0])))),

        new(["H7160"], typeof(NotifyHumidifierNightlight),
            p => EncodeNightlight(0xaa, ((NotifyHumidifierNightlight)p).ToSet()),
            data => Unframe(data, [0xaa, 0x1b], 5,
                f => new NotifyHumidifierNightlight(f[0] != 0, f[1], f[2], f[3], f[4]))),

        new(["H7160"], typeof(SetHumidifierNightlight),
            p => EncodeNightlight(0x33, (SetHumidifierNightlight)p),
            data => Unframe(data, [0x33, 0x1b], 5,
                f => new SetHumidifierNightlight(f[0] != 0, f[1], f[2], f[3], f[4]))),

        new([GenericLight], typeof(SetDevicePower),
            p => Frame([0x33, 0x01, (byte)(((SetDevicePower)p).On ? 1 : 0)]),
            data => Unframe(data, [0x33, 0x01], 1, f => new SetDevicePower(f[0] != 0))),

        new([GenericLight], typeof(SetSceneCode),
            p => EncodeSceneCode((SetSceneCode)p),
            _ => null),
    ];

    private static byte[] EncodeNightlight(byte header, SetHumidifierNightlight p)
        => Frame([header, 0x1b, (byte)(p.On ? 1 : 0), p.Brightness, p.R, p.G, p.B]);

    /// <summary>
    /// Encodes <paramref name="packet"/> for the given SKU, or throws if that
    /// SKU has no codec for this packet type.
    /// </summary>
    public static byte[] Encode(string sku, GoveeBlePacket packet)
        => TryEncode(sku, packet, out var bytes)
            ? bytes!
            : throw new GoveeException($"sku {sku} has no codec for {packet.GetType().Name}");

    public static bool TryEncode(string sku, GoveeBlePacket packet, out byte[]? bytes)
    {
        var type = packet.GetType();
        foreach (var codec in Codecs)
        {
            if (codec.PacketType == type && codec.Skus.Contains(sku, StringComparer.Ordinal))
            {
                bytes = codec.Encode(packet);
                return true;
            }
        }
        bytes = null;
        return false;
    }

    /// <summary>
    /// Decodes a frame received from a device. Unrecognised frames come back as
    /// <see cref="GenericPacket"/> rather than throwing, since Govee sends
    /// plenty of traffic we have no interest in.
    /// </summary>
    public static GoveeBlePacket Decode(string sku, byte[] data)
    {
        foreach (var codec in Codecs)
        {
            if (!codec.Skus.Contains(sku, StringComparer.Ordinal))
            {
                continue;
            }

            var decoded = codec.Decode(data);
            if (decoded is not null)
            {
                return decoded;
            }
        }
        return new GenericPacket(data);
    }

    /// <summary>
    /// Pads a payload to 19 bytes and appends the XOR checksum, producing the
    /// 20-byte frame the devices expect.
    /// </summary>
    public static byte[] Frame(ReadOnlySpan<byte> payload)
    {
        var checksum = Checksum(payload);
        var frame = new byte[20];
        payload[..Math.Min(payload.Length, 19)].CopyTo(frame);
        frame[19] = checksum;
        return frame;
    }

    public static byte Checksum(ReadOnlySpan<byte> data)
    {
        byte checksum = 0;
        foreach (var b in data)
        {
            checksum ^= b;
        }
        return checksum;
    }

    /// <summary>
    /// Verifies the fixed prefix, extracts <paramref name="fieldCount"/> bytes,
    /// and requires everything after them (up to the checksum) to be zero.
    /// Returns null when the frame does not match.
    /// </summary>
    private static GoveeBlePacket? Unframe(
        byte[] data,
        ReadOnlySpan<byte> prefix,
        int fieldCount,
        Func<byte[], GoveeBlePacket> build)
    {
        if (data.Length == 0)
        {
            return null;
        }

        // The trailing byte is the checksum and is not part of the body.
        var body = data.AsSpan(0, data.Length - 1);

        if (body.Length < prefix.Length + fieldCount)
        {
            return null;
        }

        for (var i = 0; i < prefix.Length; i++)
        {
            if (body[i] != prefix[i])
            {
                return null;
            }
        }

        var fields = body.Slice(prefix.Length, fieldCount).ToArray();

        for (var i = prefix.Length + fieldCount; i < body.Length; i++)
        {
            if (body[i] != 0)
            {
                return null;
            }
        }

        return build(fields);
    }

    /// <summary>
    /// Builds the multi-frame upload that sets a scene: the base64 scene
    /// parameter blob is split across 0xa3 continuation lines, each checksummed
    /// separately, followed by a frame carrying the scene code itself.
    /// </summary>
    private static byte[] EncodeSceneCode(SetSceneCode scene)
    {
        var blob = Convert.FromBase64String(scene.SceneParam);

        var data = new List<byte> { 0xa3, 0x00, 0x01, 0x00 /* line count, back-patched */, 0x02 };
        byte numLines = 0;
        var lastLineMarker = 1;

        foreach (var b in blob)
        {
            if (data.Count % 19 == 0)
            {
                numLines++;
                data.Add(0xa3);
                lastLineMarker = data.Count;
                data.Add(numLines);
            }
            data.Add(b);
        }

        // The final line is flagged with 0xff instead of its line number.
        data[lastLineMarker] = 0xff;
        data[3] = (byte)(numLines + 1);

        var result = new List<byte>();
        for (var offset = 0; offset < data.Count; offset += 19)
        {
            var length = Math.Min(19, data.Count - offset);
            result.AddRange(Frame(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(data).Slice(offset, length)));
        }

        var hi = (byte)(scene.Code >> 8);
        var lo = (byte)(scene.Code & 0xff);
        result.AddRange(Frame([0x33, 0x05, 0x04, lo, hi]));

        return result.ToArray();
    }

    /// <summary>
    /// Splits raw frame bytes into the base64 strings that go into a
    /// <c>ptReal</c> command array, one per 20-byte frame.
    /// </summary>
    public static List<string> ToBase64Commands(byte[] bytes)
    {
        var result = new List<string>();
        for (var offset = 0; offset < bytes.Length; offset += 20)
        {
            var length = Math.Min(20, bytes.Length - offset);
            result.Add(Convert.ToBase64String(bytes, offset, length));
        }
        return result;
    }

    public static List<string> EncodeToBase64(string sku, GoveeBlePacket packet)
        => ToBase64Commands(Encode(sku, packet));

    /// <summary>Frames arbitrary bytes, for the `lan-control command` CLI escape hatch.</summary>
    public static List<string> RawToBase64(byte[] payload) => ToBase64Commands(Frame(payload));
}
