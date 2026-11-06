using System.Text.RegularExpressions;
using Novee2Mqtt.Core;

namespace Novee2Mqtt.Ble;

/// <summary>A Govee device reachable over Bluetooth LE, keyed by its Bluetooth address.</summary>
public sealed record BleDevice(string Address, string Sku, string Name)
{
    public override string ToString() => $"{Name} ({Address})";
}

/// <summary>A device seen during a Bluetooth scan.</summary>
public sealed record BleScanResult(string Address, string? Name, short? Rssi, DateTimeOffset LastSeen)
{
    /// <summary>
    /// The encrypt flag from the advertisement, or null when it carried none.
    /// The device's own encrypt version, read after connecting, wins over this.
    /// </summary>
    public bool? Encrypted { get; init; }

    /// <summary>The Govee SKU embedded in the advertised name, if this looks like a Govee device.</summary>
    public string? Sku => BleNaming.SkuFromName(Name);

    public bool LooksLikeGovee => Sku is not null;
}

/// <summary>
/// How a light expects its colour and brightness frames. Govee changed the
/// wire format several times, so the right one depends on the model.
/// </summary>
public enum BleColorMode : byte
{
    /// <summary>The original strips: <c>33 05 02 R G B</c>, white via a flag byte.</summary>
    Manual = 0x02,

    /// <summary>Bulbs such as the H6008: <c>33 05 0d R G B Kh Kl</c>.</summary>
    ManualWhite = 0x0d,

    /// <summary>Segmented strips and TV backlights: <c>33 05 15 01 R G B Kh Kl wR wG wB mask</c>.</summary>
    Segments = 0x15,
}

/// <summary>Which frame layouts a given SKU understands, and the limits that go with them.</summary>
public sealed record BleLightProfile(
    BleColorMode ColorMode,
    bool BrightnessAsPercent,
    int Segments = 16,
    int MinKelvin = 2000,
    int MaxKelvin = 9000)
{
    /// <summary>
    /// Overrides applied on top of the built-in tables, from
    /// <c>GOVEE_BLE_PROFILE</c>. Keyed by SKU.
    /// </summary>
    public static Dictionary<string, BleLightProfile> Overrides { get; } = new(StringComparer.OrdinalIgnoreCase);

    // The 0x0d bulbs, 2700 to 6500 K. The H6004, H6006 and H6008 are confirmed on
    // hardware; the rest share their row in the Govee app. H613A and H613D come
    // from GoveeBleMqttServer.
    private static readonly HashSet<string> BulbSkus = new(StringComparer.OrdinalIgnoreCase)
    {
        "H6004", "H6006", "H6008", "H6009", "H600A", "H600B", "H600C", "H600D",
        "H6010", "H6011", "H6013", "H613A", "H613D",
    };

    // TV backlights with 14 segments. The app's traced frames, with the Kelvin
    // floor a real H66A1 reports to the Platform API.
    private static readonly HashSet<string> BacklightSkus = new(StringComparer.OrdinalIgnoreCase)
    {
        "H66A0", "H66A1",
    };

    // Older firmware: 0x02 colour frames and a raw brightness byte.
    private static readonly HashSet<string> LegacySkus = new(StringComparer.OrdinalIgnoreCase)
    {
        "H6001", "H6002", "H6003", "H6107", "H6113", "H6127", "H6138", "H6139", "H613B",
        "H6141", "H6142", "H6159", "H6160", "H6181", "H6182",
    };

    public static BleLightProfile Resolve(string sku)
    {
        if (Overrides.TryGetValue(sku, out var overridden))
        {
            return overridden;
        }
        if (BulbSkus.Contains(sku))
        {
            return new(BleColorMode.ManualWhite, BrightnessAsPercent: true, MinKelvin: 2700, MaxKelvin: 6500);
        }
        if (BacklightSkus.Contains(sku))
        {
            return new(BleColorMode.Segments, BrightnessAsPercent: true, Segments: 14, MinKelvin: 2200, MaxKelvin: 6500);
        }
        if (LegacySkus.Contains(sku))
        {
            return new(BleColorMode.Manual, BrightnessAsPercent: false);
        }

        // Most current lights use the segmented layout. A model where this is
        // wrong can be corrected with GOVEE_BLE_PROFILE.
        return new(BleColorMode.Segments, BrightnessAsPercent: true);
    }

    /// <summary>
    /// Parses <c>SKU=mode[/byte|/percent],...</c>, where mode is the hex colour
    /// opcode: <c>02</c>, <c>0d</c> or <c>15</c>.
    /// </summary>
    public static Dictionary<string, BleLightProfile> ParseOverrides(string text)
    {
        var result = new Dictionary<string, BleLightProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = entry.IndexOf('=');
            if (eq <= 0)
            {
                throw new GoveeException($"BLE profile entry '{entry}' must look like SKU=mode[/byte|/percent]");
            }

            var sku = entry[..eq].Trim();
            var spec = entry[(eq + 1)..].Split('/', StringSplitOptions.TrimEntries);
            var mode = ParseColorMode(spec[0]);
            var percent = true;
            if (spec.Length > 1)
            {
                percent = spec[1].ToLowerInvariant() switch
                {
                    "percent" => true,
                    "byte" => false,
                    _ => throw new GoveeException($"BLE profile entry '{entry}': brightness scale must be byte or percent"),
                };
            }
            result[sku] = Resolve(sku) with { ColorMode = mode, BrightnessAsPercent = percent };
        }
        return result;
    }

    public static BleColorMode ParseColorMode(string text)
    {
        var value = text.Trim().ToLowerInvariant();
        if (value.StartsWith("0x", StringComparison.Ordinal))
        {
            value = value[2..];
        }
        return value switch
        {
            "02" or "2" or "manual" => BleColorMode.Manual,
            "0d" or "d" or "bulb" => BleColorMode.ManualWhite,
            "15" or "segments" or "segment" => BleColorMode.Segments,
            _ => throw new GoveeException($"unknown BLE colour mode '{text}', expected 02, 0d or 15"),
        };
    }
}

/// <summary>Settings for the Bluetooth transport.</summary>
public sealed class BleOptions
{
    /// <summary>Null means auto: on when a BlueZ adapter is reachable over D-Bus.</summary>
    public bool? Enabled { get; set; }

    /// <summary>Adapter name such as <c>hci0</c>; null picks the first one.</summary>
    public string? Adapter { get; set; }

    /// <summary>Try Bluetooth before the cloud APIs even for devices the cloud can reach.</summary>
    public bool Prefer { get; set; }

    /// <summary>
    /// Seconds a connection is kept open after the last command. Connecting is
    /// the slow part, 5 to 40 seconds on a real bulb, while a command on an open
    /// link is immediate. A light takes only one connection, though, so holding
    /// it locks the Govee app out over Bluetooth for this long.
    /// </summary>
    public int IdleSeconds { get; set; } = 600;

    /// <summary>Seconds each discovery scan lasts.</summary>
    public int ScanSeconds { get; set; } = 15;

    /// <summary>Explicit Govee device id → Bluetooth address, for when the derived address is wrong.</summary>
    public Dictionary<string, string> Addresses { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsDisabled => Enabled == false;

    public BleOptions ApplyEnvironmentOverrides()
    {
        if (Env.GetBool("GOVEE_BLE") is { } enabled)
        {
            Enabled = enabled;
        }
        if (Env.Get("GOVEE_BLE_ADAPTER") is { } adapter)
        {
            Adapter = adapter;
        }
        if (Env.GetBool("GOVEE_BLE_PREFER") is { } prefer)
        {
            Prefer = prefer;
        }
        if (Env.GetInt("GOVEE_BLE_IDLE_SECONDS") is { } idle)
        {
            IdleSeconds = idle;
        }
        if (Env.GetInt("GOVEE_BLE_SCAN_SECONDS") is { } scan)
        {
            ScanSeconds = scan;
        }
        if (Env.Get("GOVEE_BLE_ADDRESSES") is { } addresses)
        {
            foreach (var (id, address) in ParseAddresses(addresses))
            {
                Addresses[id] = address;
            }
        }
        if (Env.Get("GOVEE_BLE_PROFILE") is { } profiles)
        {
            foreach (var (sku, profile) in BleLightProfile.ParseOverrides(profiles))
            {
                BleLightProfile.Overrides[sku] = profile;
            }
        }
        return this;
    }

    /// <summary>Parses <c>deviceId=AA:BB:CC:DD:EE:FF,...</c>.</summary>
    public static Dictionary<string, string> ParseAddresses(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = entry.LastIndexOf('=');
            if (eq <= 0)
            {
                throw new GoveeException($"BLE address entry '{entry}' must look like <device id>=<bluetooth address>");
            }
            var address = BleNaming.NormalizeAddress(entry[(eq + 1)..])
                ?? throw new GoveeException($"'{entry[(eq + 1)..]}' is not a Bluetooth address");
            result[entry[..eq].Trim()] = address;
        }
        return result;
    }
}

/// <summary>How Govee devices identify themselves over Bluetooth.</summary>
public static class BleNaming
{
    private static readonly string[] BrandMarkers = ["ihoment_", "Govee_", "Minger_"];

    /// <summary>
    /// Reads the SKU from an advertised name, as the Govee app does. Names come
    /// as <c>Govee_H66A1_B2A4</c> or <c>GBK_H619A_3197</c>, as <c>GVH5075_1234</c>,
    /// as <c>GVH6008979D</c> with nothing between the SKU and the address, and
    /// on older models as <c>GV61881A2B</c> with the H left out.
    /// </summary>
    public static string? SkuFromName(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        if (BrandMarkers.Any(m => name.Contains(m, StringComparison.Ordinal)) || name.StartsWith("GBK_", StringComparison.Ordinal))
        {
            var parts = name.TrimEnd('_').Split('_');
            return parts.Length == 3 && parts[1].Length > 0 ? parts[1].ToUpperInvariant() : null;
        }

        if (name.StartsWith("GVH", StringComparison.Ordinal) || name.StartsWith("GVR", StringComparison.Ordinal))
        {
            var separator = name.IndexOf('_');
            if (separator > 2)
            {
                return name[2..separator].ToUpperInvariant();
            }
            return separator < 0 && name.Length >= 11 ? name[2..7].ToUpperInvariant() : null;
        }

        return name.StartsWith("GV", StringComparison.Ordinal) && name.Length >= 10
            ? ("H" + name[2..6]).ToUpperInvariant()
            : null;
    }

    /// <summary>
    /// The advertised encrypt flag. Each entry is a BlueZ manufacturer-data
    /// pair; Govee's company id is the flags byte plus a 0x88 marker, and its
    /// value starts with 0xec followed by the pact fields. BlueZ can hold more
    /// than one entry for an address, so the flag only counts if they all
    /// agree. Returns null when no entry carries it.
    /// </summary>
    public static bool? EncryptFlag(IEnumerable<KeyValuePair<ushort, byte[]>> manufacturerData)
    {
        bool? flag = null;
        foreach (var (companyId, value) in manufacturerData)
        {
            var flags = companyId & 0xff;
            if (companyId >> 8 != 0x88 || value.Length < 4 || value[0] != 0xec || (flags & 0x0f) == 0)
            {
                continue;
            }

            var encrypted = (flags & 0x40) != 0;
            if (flag is not null && flag != encrypted)
            {
                return null;
            }
            flag = encrypted;
        }
        return flag;
    }

    /// <summary>
    /// The last six octets of a device id. This is the Bluetooth address on some
    /// models and the WiFi MAC, one below it, on others, so prefer the address
    /// the Govee account reports. Returns null for ids of any other shape.
    /// </summary>
    public static string? MacFromDeviceId(string deviceId)
    {
        var parts = deviceId.Split(':');
        if (parts.Length != 8 || parts.Any(p => p.Length != 2))
        {
            return null;
        }
        return NormalizeAddress(string.Join(':', parts[2..]));
    }

    /// <summary>The addresses a device id could advertise from, most likely first.</summary>
    public static IEnumerable<string> AddressCandidates(string deviceId)
    {
        if (MacFromDeviceId(deviceId) is not { } mac)
        {
            yield break;
        }
        yield return Increment(mac);
        yield return mac;
    }

    private static string Increment(string address)
    {
        var value = Convert.ToUInt64(address.Replace(":", ""), 16);
        return NormalizeAddress(((value + 1) & 0xffff_ffff_ffffUL).ToString("X12"))!;
    }

    /// <summary>Upper-cases and validates <c>AA:BB:CC:DD:EE:FF</c>; accepts dashes and no separators too.</summary>
    public static string? NormalizeAddress(string text)
    {
        var hex = string.Concat(text.Where(Uri.IsHexDigit)).ToUpperInvariant();
        if (hex.Length != 12 || string.Concat(text.Where(c => c != ':' && c != '-' && !Uri.IsHexDigit(c))).Length != 0)
        {
            return null;
        }
        return string.Join(':', Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)));
    }

    /// <summary>The BlueZ object path for a device address under an adapter.</summary>
    public static string DevicePath(string adapterPath, string address)
        => $"{adapterPath}/dev_{address.Replace(':', '_')}";

    /// <summary>The address a BlueZ device path encodes, or null if it is not a device path.</summary>
    public static string? AddressFromPath(string path)
    {
        var index = path.LastIndexOf("/dev_", StringComparison.Ordinal);
        if (index < 0)
        {
            return null;
        }
        var rest = path[(index + 5)..];
        return rest.Contains('/') ? null : NormalizeAddress(rest.Replace('_', ':'));
    }
}
