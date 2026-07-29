using System.Text;
using Novee2Mqtt.Ble;

namespace Novee2Mqtt.Tests;

public class BlePacketTests
{
    [Fact]
    public void EncodesHumidifierModeWithChecksum()
    {
        byte[] expected = [0x33, 0x05, 0x01, 0x20, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 23];

        var encoded = PacketManager.Encode("H7160", new SetHumidifierMode(1, 0x20));

        Assert.Equal(expected, encoded);
    }

    [Fact]
    public void DecodesHumidifierMode()
    {
        byte[] frame = [0x33, 0x05, 0x01, 0x20, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 23];

        var decoded = PacketManager.Decode("H7160", frame);

        Assert.Equal(new SetHumidifierMode(1, 0x20), decoded);
    }

    [Fact]
    public void RoundTripsDevicePower()
    {
        var encoded = PacketManager.Encode(PacketManager.GenericLight, new SetDevicePower(true));

        Assert.Equal(new SetDevicePower(true), PacketManager.Decode(PacketManager.GenericLight, encoded));
    }

    [Fact]
    public void RoundTripsHumidifierNightlight()
    {
        var original = new SetHumidifierNightlight(On: true, Brightness: 100, R: 255, G: 69, B: 42);

        var encoded = PacketManager.Encode("H7160", original);

        Assert.Equal(original, PacketManager.Decode("H7160", encoded));
    }

    [Fact]
    public void UnknownFrameDecodesAsGeneric()
    {
        byte[] frame = [0x99, 0x99, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

        Assert.IsType<GenericPacket>(PacketManager.Decode("H7160", frame));
    }

    [Fact]
    public void UnsupportedSkuHasNoCodec()
    {
        Assert.False(PacketManager.TryEncode("H6072", new SetHumidifierMode(1, 1), out _));
    }

    /// <summary>
    /// Byte-for-byte check of the multi-frame scene upload against a capture from
    /// a real device, since a single wrong byte silently does nothing.
    /// </summary>
    [Fact]
    public void EncodesSceneCommand()
    {
        const string forestSceneParam =
            "AyYAAQAKAgH/GQG0CgoCyBQF//8AAP//////AP//lP8AFAGWAAAAACMAAg8FAgH/FAH7AAAB+goEBP8AtP8AR///4/8AAAAAAAAAABoAAAABAgH/BQHIFBQC7hQBAP8AAAAAAAAAAA==";
        const ushort forestSceneCode = 212;

        const string expected = """
            a3 00 01 07 02 03 26 00 01 00 0a 02 01 ff 19 01 b4 0a 0a d9
            a3 01 02 c8 14 05 ff ff 00 00 ff ff ff ff ff 00 ff ff 94 12
            a3 02 ff 00 14 01 96 00 00 00 00 23 00 02 0f 05 02 01 ff 0a
            a3 03 14 01 fb 00 00 01 fa 0a 04 04 ff 00 b4 ff 00 47 ff b3
            a3 04 ff e3 ff 00 00 00 00 00 00 00 00 1a 00 00 00 01 02 5d
            a3 05 01 ff 05 01 c8 14 14 02 ee 14 01 00 ff 00 00 00 00 92
            a3 ff 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 5c
            33 05 04 d4 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 e6
            """;

        var encoded = PacketManager.Encode(
            PacketManager.GenericLight,
            new SetSceneCode(forestSceneCode, forestSceneParam));

        Assert.Equal(expected.ReplaceLineEndings("\n"), FormatHex(encoded));
    }

    [Fact]
    public void SplitsSceneCommandIntoTwentyByteChunks()
    {
        var encoded = PacketManager.EncodeToBase64(
            PacketManager.GenericLight,
            new SetSceneCode(212, Convert.ToBase64String(new byte[100])));

        Assert.All(encoded, chunk => Assert.Equal(20, Convert.FromBase64String(chunk).Length));
    }

    private static string FormatHex(byte[] bytes)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < bytes.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(i % 20 == 0 ? '\n' : ' ');
            }
            builder.Append(bytes[i].ToString("x2"));
        }
        return builder.ToString();
    }
}

public class TargetHumidityTests
{
    [Theory]
    [InlineData(0, 128)]
    [InlineData(50, 178)]
    [InlineData(100, 228)]
    public void OffsetsPercentBy128(byte percent, byte raw)
    {
        Assert.Equal(raw, TargetHumidity.FromPercent(percent).Raw);
    }

    [Theory]
    [InlineData(128, 0)]
    [InlineData(178, 50)]
    [InlineData(228, 100)]
    public void RecoversPercentFromRaw(byte raw, byte percent)
    {
        Assert.Equal(percent, new TargetHumidity(raw).AsPercent());
    }
}
