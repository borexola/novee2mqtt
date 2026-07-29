using System.Net;
using System.Text.Json.Serialization;
using Novee2Mqtt.Ble;
using Novee2Mqtt.Core;
using Novee2Mqtt.Lan;
using Novee2Mqtt.Platform;
using Novee2Mqtt.Undocumented;

namespace Novee2Mqtt.Devices;

/// <summary>
/// The device state we report to Home Assistant, synthesized from whichever of
/// the LAN, Platform and IoT sources spoke most recently.
/// </summary>
public sealed class DeviceState
{
    [JsonPropertyName("on")] public bool On { get; init; }

    /// <summary>
    /// Whether the light function is on. For a device whose primary function is
    /// not a light (a humidifier's nightlight, say) this differs from <see cref="On"/>.
    /// </summary>
    [JsonPropertyName("light_on")] public bool? LightOn { get; init; }

    /// <summary>Whether Govee's cloud considers the device reachable.</summary>
    [JsonPropertyName("online")] public bool? Online { get; init; }

    [JsonPropertyName("kelvin")] public int Kelvin { get; init; }
    [JsonPropertyName("color")] public DeviceColor Color { get; init; }
    [JsonPropertyName("brightness")] public byte Brightness { get; init; }
    [JsonPropertyName("scene")] public string? Scene { get; init; }

    /// <summary>Which API this reading came from.</summary>
    [JsonPropertyName("source")] public string Source { get; init; } = "";

    [JsonPropertyName("updated")] public DateTimeOffset Updated { get; init; }
}

public sealed class UndocDeviceInfo
{
    public string? RoomName { get; init; }
    public required DeviceEntry Entry { get; init; }
}

/// <summary>
/// Govee never reports which scene is active, so the bridge remembers what it
/// last set and forgets it as soon as the colour changes underneath it.
/// </summary>
internal sealed record ActiveSceneInfo(string Name, DeviceColor Color, int Kelvin);

/// <summary>
/// Everything known about one physical device, accumulated from the LAN,
/// Platform, IoT and app APIs. Not thread-safe on its own; access is serialized
/// by <see cref="ServiceState"/>.
/// </summary>
public sealed class Device
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(900);

    public Device(string sku, string id)
    {
        Sku = sku;
        Id = id;
    }

    public string Sku { get; }
    public string Id { get; }

    public LanDevice? LanDevice { get; private set; }

    public LanDeviceStatus? LanDeviceStatus { get; private set; }
    public DateTimeOffset? LastLanDeviceStatusUpdate { get; private set; }

    public HttpDeviceInfo? HttpDeviceInfo { get; private set; }

    public HttpDeviceState? HttpDeviceState { get; private set; }
    public DateTimeOffset? LastHttpDeviceStateUpdate { get; private set; }

    public UndocDeviceInfo? UndocDeviceInfo { get; private set; }

    public LanDeviceStatus? IotDeviceStatus { get; private set; }
    public DateTimeOffset? LastIotDeviceStatusUpdate { get; private set; }

    public NotifyHumidifierNightlight? NightlightState { get; private set; }
    public byte? TargetHumidityPercent { get; private set; }
    public byte? HumidifierWorkMode { get; private set; }
    public Dictionary<byte, byte> HumidifierParamByMode { get; private set; } = new();

    public DateTimeOffset? LastPolled { get; private set; }

    private ActiveSceneInfo? _activeScene;

    public override string ToString() => $"{Name()} ({Id} {Sku})";

    /// <summary>Shallow copy, so callers can read a consistent view without holding the lock.</summary>
    public Device Snapshot()
    {
        var copy = (Device)MemberwiseClone();
        copy.HumidifierParamByMode = new Dictionary<byte, byte>(HumidifierParamByMode);
        return copy;
    }

    /// <summary>The name from the Govee app, or a synthesized one if we never got it.</summary>
    public string Name() => GoveeName() ?? ComputedName();

    public string? GoveeName()
    {
        var name = HttpDeviceInfo?.DeviceName;
        if (!string.IsNullOrEmpty(name))
        {
            return name;
        }
        name = UndocDeviceInfo?.Entry.DeviceName;
        return string.IsNullOrEmpty(name) ? null : name;
    }

    public string? RoomName() => UndocDeviceInfo?.RoomName;

    /// <summary>
    /// SKU plus the last four hex digits of the device id, which is what the
    /// device calls itself in a BLE scan and Govee's default app name.
    /// </summary>
    public string ComputedName()
    {
        var id = string.Concat(Id.Where(c => c != ':').Select(char.ToUpperInvariant));
        var suffix = id.Length <= 4 ? id : id[^4..];
        return $"{Sku}_{suffix}";
    }

    public IPAddress? IpAddress => LanDevice?.Address;

    /// <summary>A boiling kettle is worth watching more closely than a light.</summary>
    public TimeSpan PreferredPollInterval()
    {
        if (GetDeviceType() == DeviceType.Kettle && ComputeDeviceState()?.On == true)
        {
            return TimeSpan.FromSeconds(60);
        }
        return PollInterval;
    }

    public void SetLastPolled() => LastPolled = DateTimeOffset.UtcNow;

    public void SetNightlightState(NotifyHumidifierNightlight state) => NightlightState = state;

    public void SetTargetHumidity(byte percent) => TargetHumidityPercent = percent;

    public void SetHumidifierWorkModeAndParam(byte mode, byte param)
    {
        HumidifierWorkMode = mode;
        HumidifierParamByMode[mode] = param;
    }

    public void SetLanDevice(LanDevice device) => LanDevice = device;

    /// <returns>True if the status differs from what we previously held.</returns>
    public bool SetLanDeviceStatus(LanDeviceStatus status)
    {
        var changed = LanDeviceStatus is null || LanDeviceStatus != status;
        LanDeviceStatus = status;
        LastLanDeviceStatusUpdate = DateTimeOffset.UtcNow;
        ClearSceneIfColorChanged();
        return changed;
    }

    public void SetIotDeviceStatus(LanDeviceStatus status)
    {
        IotDeviceStatus = status;
        LastIotDeviceStatusUpdate = DateTimeOffset.UtcNow;
        ClearSceneIfColorChanged();
    }

    public void SetHttpDeviceInfo(HttpDeviceInfo info) => HttpDeviceInfo = info;

    public void SetHttpDeviceState(HttpDeviceState state)
    {
        HttpDeviceState = state;
        LastHttpDeviceStateUpdate = DateTimeOffset.UtcNow;
        ClearSceneIfColorChanged();
    }

    public void SetUndocDeviceInfo(DeviceEntry entry, string? roomName)
        => UndocDeviceInfo = new UndocDeviceInfo { Entry = entry, RoomName = roomName };

    /// <summary>Both the LAN and IoT sources report the same status shape.</summary>
    private DeviceState? FromStatus(LanDeviceStatus? status, DateTimeOffset? updated, string source, bool? lightOn)
        => status is null || updated is null
            ? null
            : new DeviceState
            {
                On = status.On,
                LightOn = lightOn,
                Online = null,
                Brightness = status.Brightness,
                Color = status.Color,
                Kelvin = status.ColorTemperatureKelvin,
                Scene = _activeScene?.Name,
                Source = source,
                Updated = updated.Value,
            };

    public DeviceState? ComputeIotDeviceState() => FromStatus(
        IotDeviceStatus, LastIotDeviceStatusUpdate, "AWS IoT API",
        GetDeviceType() == DeviceType.Light ? IotDeviceStatus?.On : NightlightState?.On);

    // A device that speaks the LAN protocol is a light, so its power state and
    // its light state are the same thing.
    public DeviceState? ComputeLanDeviceState() => FromStatus(
        LanDeviceStatus, LastLanDeviceStatusUpdate, "LAN API", LanDeviceStatus?.On);

    public DeviceState? ComputeHttpDeviceState()
    {
        if (LastHttpDeviceStateUpdate is not { } updated || HttpDeviceState is not { } state)
        {
            return null;
        }

        bool? online = null;
        var on = false;
        bool? lightOn = null;
        byte brightness = 0;
        var color = DeviceColor.Black;
        var kelvin = 0;

        var lightInstance = GetLightPowerToggleInstanceName();

        foreach (var cap in state.Capabilities)
        {
            var value = cap.State.Pointer("/value");

            if (value.AsInt64() is { } intValue)
            {
                if (lightInstance is not null && lightInstance == cap.Instance)
                {
                    lightOn = intValue != 0;
                }

                switch (cap.Instance)
                {
                    case "powerSwitch":
                        on = intValue != 0;
                        break;
                    case "colorRgb":
                        color = DeviceColor.FromPacked((uint)intValue);
                        break;
                    case "brightness":
                        brightness = (byte)Math.Clamp(intValue, 0, 255);
                        break;
                    case "colorTemperatureK":
                        kelvin = (int)intValue;
                        break;
                }
            }
            else if (cap.Instance == "online" && value is System.Text.Json.Nodes.JsonValue v
                     && v.TryGetValue<bool>(out var isOnline))
            {
                online = isOnline;
            }
        }

        return new DeviceState
        {
            On = on,
            LightOn = lightOn,
            Online = online,
            Brightness = brightness,
            Color = color,
            Kelvin = kelvin,
            Scene = _activeScene?.Name,
            Source = "PLATFORM API",
            Updated = updated,
        };
    }

    /// <summary>The most recently updated view across all sources.</summary>
    public DeviceState? ComputeDeviceState()
    {
        DeviceState? best = null;

        foreach (var candidate in new[] { ComputeLanDeviceState(), ComputeHttpDeviceState(), ComputeIotDeviceState() })
        {
            if (candidate is null)
            {
                continue;
            }
            if (best is null || candidate.Updated >= best.Updated)
            {
                best = candidate;
            }
        }

        return best;
    }

    public void SetActiveScene(string? scene)
    {
        if (scene is null)
        {
            _activeScene = null;
            return;
        }

        var current = ComputeDeviceState();
        _activeScene = new ActiveSceneInfo(scene, current?.Color ?? DeviceColor.Black, current?.Kelvin ?? 0);
    }

    /// <summary>
    /// Drops the remembered scene once the colour no longer matches what it was
    /// when the scene was applied — the only signal we get that something else
    /// changed the light.
    /// </summary>
    public void ClearSceneIfColorChanged()
    {
        if (_activeScene is not { } info)
        {
            return;
        }

        var state = ComputeDeviceState();
        var currentColor = state?.Color ?? DeviceColor.Black;
        var currentKelvin = state?.Kelvin ?? 0;

        if (currentColor != info.Color || currentKelvin != info.Kelvin)
        {
            _activeScene = null;
        }
    }

    public DeviceType GetDeviceType()
    {
        if (HttpDeviceInfo is { } info && !info.DeviceType.IsUnknown)
        {
            return info.DeviceType;
        }
        if (Quirks.Resolve(Sku) is { } quirk)
        {
            return quirk.DeviceType;
        }
        return DeviceType.Light;
    }

    /// <summary>Whether Platform API data is required to report this device correctly.</summary>
    public bool NeedsPlatformPoll()
    {
        if (!IotApiSupported())
        {
            return true;
        }

        if (Sku == "H7160")
        {
            return false;
        }

        var type = GetDeviceType();
        if (type == DeviceType.Light)
        {
            return false;
        }

        return true;
    }

    public bool PollableViaLan() => LanDevice is not null;

    public bool PollableViaIot()
    {
        if (!IotApiSupported())
        {
            return false;
        }
        return Sku == "H7160" || GetDeviceType() == DeviceType.Light;
    }

    public bool AvoidPlatformApi()
    {
        if (ResolveQuirk() is not { } quirk)
        {
            return false;
        }

        if (quirk.AvoidPlatformApi)
        {
            return true;
        }

        // Contradictory metadata: the Platform API says this is not a light, but
        // it answers the LAN protocol, which only lights do. Trust the device.
        if (LanDevice is not null && HttpDeviceInfo?.SupportsRgb() != true)
        {
            return true;
        }

        return false;
    }

    public Quirk? ResolveQuirk()
    {
        var quirk = Quirks.Resolve(Sku);
        if (quirk is not null)
        {
            return quirk;
        }

        // Unknown SKU, but it showed up via LAN discovery, so it is a light.
        return LanDevice is not null ? Quirks.GenericLanLight(Sku) : null;
    }

    public DeviceCapability? GetCapabilityByInstance(string instance)
        => HttpDeviceInfo?.CapabilityByInstance(instance);

    public DeviceCapabilityState? GetStateCapabilityByInstance(string instance)
        => HttpDeviceState?.CapabilityByInstance(instance);

    /// <summary>
    /// Which toggle controls just the light. For a device whose primary function
    /// is not lighting, powering on <c>powerSwitch</c> would start the appliance,
    /// so its nightlight toggle is used instead.
    /// </summary>
    public string? GetLightPowerToggleInstanceName()
    {
        if (GetDeviceType() == DeviceType.Light)
        {
            return "powerSwitch";
        }
        return GetCapabilityByInstance("nightlightToggle") is not null ? "nightlightToggle" : null;
    }

    public (long Min, long Max)? GetColorTemperatureRange()
    {
        if (ResolveQuirk() is { } quirk)
        {
            return quirk.ColorTempRange;
        }
        if (LanDevice is not null)
        {
            return (2000, 9000);
        }
        return HttpDeviceInfo?.GetColorTemperatureRange();
    }

    public bool SupportsBrightness()
    {
        if (ResolveQuirk() is { } quirk)
        {
            return quirk.SupportsBrightness;
        }
        if (LanDevice is not null)
        {
            return true;
        }
        return HttpDeviceInfo?.SupportsBrightness() ?? false;
    }

    public bool IotApiSupported() => ResolveQuirk()?.IotApiSupported ?? false;

    public bool SupportsRgb()
    {
        if (ResolveQuirk() is { } quirk)
        {
            return quirk.SupportsRgb;
        }
        if (LanDevice is not null)
        {
            return true;
        }
        return HttpDeviceInfo?.SupportsRgb() ?? false;
    }

    /// <returns>Null when we genuinely do not know.</returns>
    public bool? IsBleOnlyDevice()
    {
        if (ResolveQuirk() is { } quirk)
        {
            return quirk.BleOnly;
        }

        // Truly BLE-only devices are not returned by the Platform API unless a
        // quirk says otherwise.
        if (HttpDeviceInfo is not null)
        {
            return false;
        }

        return UndocDeviceInfo?.Entry.IsBleOnly;
    }

    public bool IsControllable() => IsBleOnlyDevice() != true;
}
