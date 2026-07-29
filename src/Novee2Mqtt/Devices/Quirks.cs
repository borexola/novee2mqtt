using Novee2Mqtt.Core;
using Novee2Mqtt.Platform;

namespace Novee2Mqtt.Devices;

/// <summary>
/// Per-SKU corrections for devices whose Govee-reported metadata is wrong,
/// missing, or contradicted by what the device actually does.
/// </summary>
public sealed record Quirk
{
    public required string Sku { get; init; }
    public required string Icon { get; init; }
    public required DeviceType DeviceType { get; init; }

    public bool SupportsRgb { get; init; }
    public bool SupportsBrightness { get; init; }
    public (long Min, long Max)? ColorTempRange { get; init; }

    /// <summary>Set when the Platform API returns bogus metadata for this SKU.</summary>
    public bool AvoidPlatformApi { get; init; }

    public bool BleOnly { get; init; }
    public bool LanApiCapable { get; init; }

    public TemperatureUnits? PlatformTemperatureSensorUnits { get; init; }
    public HumidityUnits? PlatformHumiditySensorUnits { get; init; }

    /// <summary>
    /// True when every relevant packet from the AWS IoT subscription can be
    /// parsed and applied, so IoT is a trustworthy state source for this SKU.
    /// </summary>
    public bool IotApiSupported { get; init; }

    /// <summary>Work modes to surface as one-shot buttons rather than a slider.</summary>
    public string[]? ShowAsPresetButtons { get; init; }

    public bool ShouldShowModeAsPreset(string mode)
        => ShowAsPresetButtons is not null && ShowAsPresetButtons.Contains(mode, StringComparer.Ordinal);

    public override string ToString()
        => $"Quirk {{ sku: {Sku}, type: {DeviceType}, rgb: {SupportsRgb}, brightness: {SupportsBrightness}, " +
           $"color_temp: {(ColorTempRange is { } r ? $"{r.Min}-{r.Max}" : "none")}, lan: {LanApiCapable}, " +
           $"iot: {IotApiSupported}, ble_only: {BleOnly}, avoid_platform_api: {AvoidPlatformApi} }}";
}

public static class Quirks
{
    private const string Strip = "mdi:led-strip-variant";
    private const string StripAlt = "mdi:led-strip";
    private const string Flood = "mdi:light-flood-down";
    private const string StringLights = "mdi:string-lights";
    private const string Bulb = "mdi:light-bulb";
    private const string FloorLamp = "mdi:floor-lamp";
    private const string TvBack = "mdi:television-ambient-light";
    private const string Desk = "mdi:desk-lamp";
    private const string Hex = "mdi:hexagon-multiple";
    private const string Triangle = "mdi:triangle";
    private const string Nightlight = "mdi:lightbulb-night";
    private const string WallSconce = "mdi:wall-sconce";
    private const string OutdoorLamp = "mdi:outdoor-lamp";
    private const string Spotlight = "mdi:lightbulb-spot";

    private static readonly (long Min, long Max) DefaultColorTempRange = (2000, 9000);

    private static readonly Dictionary<string, Quirk> Table = Load();

    public static Quirk? Resolve(string sku) => Table.GetValueOrDefault(sku);

    private static Quirk Device(string sku, DeviceType deviceType, string icon)
        => new() { Sku = sku, DeviceType = deviceType, Icon = icon };

    /// <summary>A generic RGB+CCT light with working IoT support.</summary>
    private static Quirk Light(string sku, string icon) => new()
    {
        Sku = sku,
        DeviceType = DeviceType.Light,
        Icon = icon,
        SupportsRgb = true,
        SupportsBrightness = true,
        ColorTempRange = DefaultColorTempRange,
        IotApiSupported = true,
    };

    private static Quirk LanLight(string sku, string icon) => Light(sku, icon) with { LanApiCapable = true };

    private static Quirk SpaceHeater(string sku) => Device(sku, DeviceType.Heater, "mdi:heat-wave")
        with { PlatformTemperatureSensorUnits = TemperatureUnits.Fahrenheit };

    private static Quirk Thermometer(string sku) => Device(sku, DeviceType.Thermometer, "mdi:thermometer") with
    {
        PlatformTemperatureSensorUnits = TemperatureUnits.Fahrenheit,
        PlatformHumiditySensorUnits = HumidityUnits.RelativePercent,
    };

    private static Quirk Kettle(string sku) => Device(sku, DeviceType.Kettle, "mdi:kettle")
        with { PlatformTemperatureSensorUnits = TemperatureUnits.Fahrenheit };

    /// <summary>
    /// Fallback for a SKU we have no entry for that nonetheless answered LAN
    /// discovery: if it speaks the LAN protocol it is a light.
    /// </summary>
    public static Quirk GenericLanLight(string sku) => LanLight(sku, Bulb);

    private static Dictionary<string, Quirk> Load()
    {
        Quirk[] quirks =
        [
            // Govee's metadata for these is wrong enough that the Platform API
            // must not be trusted for them.
            Light("H6141", Strip) with { AvoidPlatformApi = true },
            Light("H6159", Strip) with { AvoidPlatformApi = true },
            Light("H6003", Bulb) with { AvoidPlatformApi = true },

            // Lights whose IoT status packets we cannot interpret.
            Light("H6121", Strip) with { IotApiSupported = false },
            Light("H6154", Strip) with { IotApiSupported = false },
            Light("H6176", Strip) with { IotApiSupported = false },

            // BLE-only: the Platform API lists them, but they cannot be
            // controlled over the network at all.
            Light("H6102", Strip) with { AvoidPlatformApi = true, BleOnly = true },
            Light("H6053", Strip) with { AvoidPlatformApi = true, BleOnly = true },
            Light("H617C", Strip) with { AvoidPlatformApi = true, BleOnly = true },
            Light("H617E", Strip) with { AvoidPlatformApi = true, BleOnly = true },
            Light("H617F", Strip) with { AvoidPlatformApi = true, BleOnly = true },
            Light("H6119", Strip) with { AvoidPlatformApi = true, BleOnly = true },

            // Humidifier whose Platform API data is mangled; IoT works and
            // carries the nightlight and mist-level packets.
            Device("H7160", DeviceType.Humidifier, "mdi:air-humidifier") with
            {
                AvoidPlatformApi = true,
                IotApiSupported = true,
                SupportsRgb = true,
                SupportsBrightness = true,
            },

            SpaceHeater("H7130"),
            SpaceHeater("H7131") with { ShowAsPresetButtons = ["gearMode"], SupportsRgb = true, SupportsBrightness = true },
            SpaceHeater("H713A"),
            SpaceHeater("H713B"),
            SpaceHeater("H7132"),
            SpaceHeater("H7133") with { ShowAsPresetButtons = ["gearMode"], SupportsRgb = true, SupportsBrightness = true },
            SpaceHeater("H7134") with { ShowAsPresetButtons = ["gearMode"], ColorTempRange = DefaultColorTempRange, SupportsBrightness = true },
            SpaceHeater("H7135"),

            Device("H7172", DeviceType.IceMaker, "mdi:snowflake") with { IotApiSupported = false },

            Thermometer("H5051"),
            Thermometer("H5100"),
            Thermometer("H5103"),
            Thermometer("H5179"),

            Kettle("H7170"),
            Kettle("H7171") with { ShowAsPresetButtons = ["M1", "M2", "M3", "M4"] },
            Kettle("H7173") with { ShowAsPresetButtons = ["Tea", "Coffee", "DIY"] },

            // Lights listed as LAN API capable at
            // https://app-h5.govee.com/user-manual/wlan-guide
            LanLight("H6072", FloorLamp),
            LanLight("H619B", Strip),
            LanLight("H619C", Strip),
            LanLight("H619Z", Strip),
            LanLight("H7060", Flood),
            LanLight("H6046", TvBack),
            LanLight("H6047", TvBack),
            LanLight("H6051", Desk),
            LanLight("H6056", StripAlt),
            LanLight("H6059", Nightlight),
            LanLight("H6061", Hex),
            LanLight("H6062", Strip),
            LanLight("H6065", Strip),
            LanLight("H6066", Hex),
            LanLight("H6067", Triangle),
            LanLight("H6073", FloorLamp),
            LanLight("H6076", FloorLamp),
            LanLight("H6078", FloorLamp),
            LanLight("H6087", WallSconce),
            LanLight("H610A", Strip),
            LanLight("H610B", Strip),
            LanLight("H6117", Strip),
            // Listed both as broken-platform above and as LAN-capable here. The
            // LAN entry is the one that took effect in the original table, so it
            // is kept last here too rather than silently changing behaviour.
            LanLight("H6159", Strip),
            LanLight("H615E", Strip),
            LanLight("H6163", Strip),
            LanLight("H6168", TvBack),
            LanLight("H6172", Strip),
            LanLight("H6173", Strip),
            LanLight("H618A", Strip),
            LanLight("H618C", Strip),
            LanLight("H618E", Strip),
            LanLight("H618F", Strip),
            LanLight("H619A", Strip),
            LanLight("H619D", Strip),
            LanLight("H619E", Strip),
            LanLight("H61A0", Strip),
            LanLight("H61A1", Strip),
            LanLight("H61A2", Strip),
            LanLight("H61A3", Strip),
            LanLight("H61A5", Strip),
            LanLight("H61A8", Strip),
            LanLight("H61B2", TvBack),
            LanLight("H61E1", Strip),
            LanLight("H7012", StringLights),
            LanLight("H7013", StringLights),
            LanLight("H7021", StringLights),
            LanLight("H7028", StringLights),
            LanLight("H7041", StringLights),
            LanLight("H7042", StringLights),
            LanLight("H7050", Bulb),
            LanLight("H7051", Bulb),
            LanLight("H7052", StringLights),
            LanLight("H7055", Bulb),
            LanLight("H705A", OutdoorLamp),
            LanLight("H705B", OutdoorLamp),
            LanLight("H7061", Flood),
            LanLight("H7062", Flood),
            LanLight("H7065", Spotlight),
        ];

        // Later entries win, matching the original table's ordering.
        var map = new Dictionary<string, Quirk>(StringComparer.Ordinal);
        foreach (var quirk in quirks)
        {
            map[quirk.Sku] = quirk;
        }
        return map;
    }
}
