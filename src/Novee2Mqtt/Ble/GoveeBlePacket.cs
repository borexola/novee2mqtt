namespace Novee2Mqtt.Ble;

/// <summary>
/// A decoded Govee "ptReal" packet. These 20-byte frames are the BLE wire
/// format, which Govee also tunnels over the LAN API and over AWS IoT.
/// </summary>
public abstract record GoveeBlePacket;

/// <summary>A frame we have no codec for. Carried verbatim so it can be logged.</summary>
public sealed record GenericPacket(byte[] Data) : GoveeBlePacket
{
    public override string ToString() => Convert.ToHexString(Data);
}

public sealed record SetDevicePower(bool On) : GoveeBlePacket;

public sealed record SetHumidifierMode(byte Mode, byte Param) : GoveeBlePacket;

public sealed record NotifyHumidifierMode(byte Mode, byte Param) : GoveeBlePacket;

public sealed record HumidifierAutoMode(TargetHumidity TargetHumidity) : GoveeBlePacket;

public sealed record SetHumidifierNightlight(bool On, byte Brightness, byte R, byte G, byte B) : GoveeBlePacket
{
    public static SetHumidifierNightlight Default => new(false, 0, 0, 0, 0);

    public NotifyHumidifierNightlight ToNotify() => new(On, Brightness, R, G, B);
}

public sealed record NotifyHumidifierNightlight(bool On, byte Brightness, byte R, byte G, byte B) : GoveeBlePacket
{
    public SetHumidifierNightlight ToSet() => new(On, Brightness, R, G, B);
}

/// <summary>
/// Humidity target as the device encodes it: offset by 128, in 1% increments,
/// so 0% is 128 and 100% is 228.
/// </summary>
public readonly record struct TargetHumidity(byte Raw)
{
    public static TargetHumidity FromPercent(byte percent) => new((byte)(percent + 128));

    public byte AsPercent() => (byte)(Raw & 0x7f);
}

/// <summary>
/// Activates a scene by its numeric code, uploading the scene parameter blob
/// first. See
/// <see href="https://github.com/egold555/Govee-Reverse-Engineering/issues/11"/>.
/// </summary>
public sealed record SetSceneCode(ushort Code, string SceneParam) : GoveeBlePacket;
