using Novee2Mqtt.Core;
using Novee2Mqtt.Lan;

namespace Novee2Mqtt.Ble;

/// <summary>
/// The light control and status frames as written to a device's GATT control
/// characteristic. Every frame is 20 bytes: a leading byte that says what kind
/// of message it is (0x33 write, 0xaa read), the body, zero padding, and an XOR
/// checksum. The layouts below are the ones the Govee app sends, as captured in
/// <see href="https://github.com/egold555/Govee-Reverse-Engineering"/> and traced
/// in the MIT-licensed
/// <see href="https://github.com/Brady-Woods/govee-ble-local">govee-ble-local</see>.
/// </summary>
public static class LightFrames
{
    public const byte Write = 0x33;
    public const byte Read = 0xaa;

    public const byte PowerOp = 0x01;
    public const byte BrightnessOp = 0x04;
    public const byte ColorOp = 0x05;

    // Raw brightness range of the older firmware that does not take a percentage.
    private const int RawBrightnessMin = 20;
    private const int RawBrightnessMax = 254;

    /// <summary>Bytes of the two-byte segment mask the app writes for up to 16 segments.</summary>
    private static (byte Low, byte High) SegmentMask(int segments)
    {
        var mask = (1 << Math.Clamp(segments, 1, 16)) - 1;
        return ((byte)(mask & 0xff), (byte)(mask >> 8));
    }

    public static byte[] Power(bool on) => PacketManager.Frame([Write, PowerOp, (byte)(on ? 1 : 0)]);

    public static byte[] Brightness(BleLightProfile profile, int percent)
    {
        var clamped = Math.Clamp(percent, 1, 100);
        var value = profile.BrightnessAsPercent
            ? clamped
            : RawBrightnessMin + (int)((clamped - 1) * (RawBrightnessMax - RawBrightnessMin + 3) / 100f);
        return PacketManager.Frame([Write, BrightnessOp, (byte)value]);
    }

    public static byte[] ColorRgb(BleLightProfile profile, DeviceColor color)
    {
        var (low, high) = SegmentMask(profile.Segments);
        return profile.ColorMode switch
        {
            BleColorMode.Manual => PacketManager.Frame([Write, ColorOp, 0x02, color.R, color.G, color.B]),
            BleColorMode.ManualWhite => PacketManager.Frame([Write, ColorOp, 0x0d, color.R, color.G, color.B]),
            _ => PacketManager.Frame([Write, ColorOp, 0x15, 0x01, color.R, color.G, color.B, 0, 0, 0, 0, 0, low, high]),
        };
    }

    public static byte[] ColorTemperature(BleLightProfile profile, int kelvin)
    {
        var k = NormalizeKelvin(kelvin, profile);
        var hi = (byte)(k >> 8);
        var lo = (byte)(k & 0xff);
        var tint = KelvinTint(k);
        var (low, high) = SegmentMask(profile.Segments);

        return profile.ColorMode switch
        {
            // No Kelvin on the wire: the white flag, then the tint picks the shade.
            BleColorMode.Manual => PacketManager.Frame([Write, ColorOp, 0x02, 0xff, 0xff, 0xff, 0x01, tint.R, tint.G, tint.B]),
            // The tint goes in both colour slots, as in the app's captures.
            BleColorMode.ManualWhite => PacketManager.Frame([Write, ColorOp, 0x0d, tint.R, tint.G, tint.B, hi, lo, tint.R, tint.G, tint.B]),
            _ => PacketManager.Frame([Write, ColorOp, 0x15, 0x01, 0xff, 0xff, 0xff, hi, lo, tint.R, tint.G, tint.B, low, high]),
        };
    }

    /// <summary>Clamps to the model's range, then lands on the app's 100 K grid.</summary>
    public static int NormalizeKelvin(int kelvin, BleLightProfile profile)
    {
        var clamped = Math.Clamp(kelvin, profile.MinKelvin, profile.MaxKelvin);
        var rounded = (clamped + 50) / 100 * 100;
        return Math.Clamp(rounded, profile.MinKelvin, profile.MaxKelvin);
    }

    /// <summary>
    /// Sent every two seconds while connected, because the firmware drops a
    /// link that has had no write for about ten. The device answers with its
    /// power state.
    /// </summary>
    public static byte[] KeepAlive() => QueryPower();

    public static byte[] QueryPower() => PacketManager.Frame([Read, PowerOp]);
    public static byte[] QueryBrightness() => PacketManager.Frame([Read, BrightnessOp]);

    /// <summary>The selector 1 asks for the mode report, which carries the colour.</summary>
    public static byte[] QueryColor() => PacketManager.Frame([Read, ColorOp, 0x01]);

    public static IEnumerable<byte[]> StatusQueries()
    {
        yield return QueryPower();
        yield return QueryBrightness();
        yield return QueryColor();
    }

    /// <summary>
    /// Applies a reply frame to a status. Only the fields the frame carries
    /// change, so a partial answer cannot wipe what is already known. Returns
    /// null for frames that are not status replies.
    /// </summary>
    public static LanDeviceStatus? ApplyReply(BleLightProfile profile, LanDeviceStatus current, ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 3 || frame[0] != Read)
        {
            return null;
        }

        switch (frame[1])
        {
            case PowerOp:
                return current with { On = frame[2] != 0 };

            case BrightnessOp:
                return current with { Brightness = BrightnessPercent(profile, frame[2]) };

            case ColorOp:
                return ApplyModeReport(current, frame);

            default:
                return null;
        }
    }

    private static byte BrightnessPercent(BleLightProfile profile, byte raw)
    {
        if (profile.BrightnessAsPercent)
        {
            return Math.Min(raw, (byte)100);
        }

        var step = (RawBrightnessMax - RawBrightnessMin + 3) / 100f;
        var clamped = Math.Clamp((int)raw, RawBrightnessMin, RawBrightnessMax);
        return (byte)Math.Clamp((int)Math.Ceiling((clamped - RawBrightnessMin) / step) + 1, 1, 100);
    }

    /// <summary>
    /// Decodes <c>aa 05 mode ...</c>. A report in any other mode, such as a
    /// scene, is a valid answer that says nothing about colour.
    /// </summary>
    private static LanDeviceStatus ApplyModeReport(LanDeviceStatus current, ReadOnlySpan<byte> frame)
    {
        switch (frame[2])
        {
            case 0x0d when frame.Length >= 11:
            {
                var color = new DeviceColor(frame[3], frame[4], frame[5]);
                var kelvin = (short)((frame[6] << 8) | frame[7]);
                if (kelvin > 0)
                {
                    return current with { Color = DeviceColor.Black, ColorTemperatureKelvin = kelvin };
                }
                // A negative Kelvin means the colour is shown as a tint.
                var shown = kelvin < 0 ? new DeviceColor(frame[8], frame[9], frame[10]) : color;
                return current with { Color = shown, ColorTemperatureKelvin = 0 };
            }

            // This report carries no colour: those live in the segment groups.
            case 0x15 when frame.Length >= 6:
            {
                var kelvin = (short)((frame[4] << 8) | frame[5]);
                return kelvin > 0
                    ? current with { Color = DeviceColor.Black, ColorTemperatureKelvin = kelvin }
                    : current with { ColorTemperatureKelvin = 0 };
            }

            // With the white flag set there is no Kelvin on the wire, so keep ours.
            case 0x02 when frame.Length >= 7:
                return frame[6] == 0x01
                    ? current
                    : current with { Color = new DeviceColor(frame[3], frame[4], frame[5]), ColorTemperatureKelvin = 0 };

            default:
                return current;
        }
    }

    /// <summary>
    /// The RGB the app pairs with a colour temperature, or black off the 100 K
    /// grid or outside 1000 to 10000 K, which is also what the app sends.
    /// </summary>
    public static DeviceColor KelvinTint(int kelvin)
    {
        if (kelvin is < 1000 or > 10000 || kelvin % 100 != 0)
        {
            return DeviceColor.Black;
        }
        return DeviceColor.FromPacked(KelvinTints[(kelvin - 1000) / 100]);
    }

    /// <summary>1000 K to 10000 K in 100 K steps, from the Govee app's table.</summary>
    private static readonly uint[] KelvinTints =
    [
        0xFF3300, 0xFF4500, 0xFF5200, 0xFF5D00, 0xFF6600, 0xFF6F00, 0xFF7600, 0xFF7C00,
        0xFF8200, 0xFF8700, 0xFF8D0B, 0xFF921D, 0xFF9829, 0xFF9D33, 0xFFA23C, 0xFFA645,
        0xFFAA4D, 0xFFAE54, 0xFFB25B, 0xFFB662, 0xFFB969, 0xFFBD6F, 0xFFC076, 0xFFC37C,
        0xFFC682, 0xFFC987, 0xFFCB8D, 0xFFCE92, 0xFFD097, 0xFFD39C, 0xFFD5A1, 0xFFD7A6,
        0xFFD9AB, 0xFFDBAF, 0xFFDDB4, 0xFFDFB8, 0xFFE1BC, 0xFFE2C0, 0xFFE4C4, 0xFFE5C8,
        0xFFE7CC, 0xFFE8D0, 0xFFEAD3, 0xFFEBD7, 0xFFEDDA, 0xFFEEDE, 0xFFEFE1, 0xFFF0E4,
        0xFFF1E7, 0xFFF3EA, 0xFFF4ED, 0xFFF5F0, 0xFFF6F3, 0xFFF7F7, 0xFFF8F8, 0xFFF9FB,
        0xFFF9FD, 0xFEFAFF, 0xFCF8FF, 0xFAF7FF, 0xF7F5FF, 0xF5F4FF, 0xF3F3FF, 0xF1F1FF,
        0xEFF0FF, 0xEEEFFF, 0xECEEFF, 0xEAEDFF, 0xE9ECFF, 0xE7EAFF, 0xE5E9FF, 0xE4E9FF,
        0xE3E8FF, 0xE1E7FF, 0xE0E6FF, 0xDFE5FF, 0xDDE4FF, 0xDCE3FF, 0xDBE2FF, 0xDAE2FF,
        0xD9E1FF, 0xD8E0FF, 0xD7DFFF, 0xD6DFFF, 0xD5DEFF, 0xD4DDFF, 0xD3DDFF, 0xD2DCFF,
        0xD1DCFF, 0xD0DBFF, 0xCFDAFF,
    ];
}
