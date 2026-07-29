using System.Net;
using System.Text.Json.Serialization;
using Novee2Mqtt.Core;
using Novee2Mqtt.Undocumented;

namespace Novee2Mqtt.Lan;

/// <summary>
/// A device that answered a LAN discovery probe.
/// See <see href="https://app-h5.govee.com/user-manual/wlan-guide"/>.
/// </summary>
public sealed class LanDevice
{
    [JsonPropertyName("ip")] public string Ip { get; set; } = "";
    [JsonPropertyName("device")] public string Device { get; set; } = "";
    [JsonPropertyName("sku")] public string Sku { get; set; } = "";
    [JsonPropertyName("bleVersionHard")] public string BleVersionHard { get; set; } = "";
    [JsonPropertyName("bleVersionSoft")] public string BleVersionSoft { get; set; } = "";
    [JsonPropertyName("wifiVersionHard")] public string WifiVersionHard { get; set; } = "";
    [JsonPropertyName("wifiVersionSoft")] public string WifiVersionSoft { get; set; } = "";

    [JsonIgnore]
    public IPAddress Address => IPAddress.TryParse(Ip, out var address) ? address : IPAddress.None;
}

/// <summary>Status as reported by a device's <c>devStatus</c> reply.</summary>
public sealed record LanDeviceStatus(bool On, byte Brightness, DeviceColor Color, int ColorTemperatureKelvin)
{
    public static LanDeviceStatus Empty => new(false, 0, DeviceColor.Black, 0);
}

public sealed class DiscoOptions
{
    /// <summary>Use the protocol's multicast group 239.255.255.250.</summary>
    public bool EnableMulticast { get; set; } = true;

    /// <summary>Extra unicast or broadcast addresses to probe directly.</summary>
    public List<IPAddress> AdditionalAddresses { get; set; } = [];

    /// <summary>Probe the broadcast address of every non-loopback interface.</summary>
    public bool BroadcastAllInterfaces { get; set; }

    /// <summary>Probe the global broadcast address 255.255.255.255.</summary>
    public bool GlobalBroadcast { get; set; }

    public int DiscoveryTimeoutSeconds { get; set; } = 3;

    public bool IsEmpty => !EnableMulticast && AdditionalAddresses.Count == 0
        && !BroadcastAllInterfaces && !GlobalBroadcast;

    /// <summary>
    /// Applies the GOVEE_LAN_* environment overrides on top of whatever was
    /// passed on the command line.
    /// </summary>
    public DiscoOptions ApplyEnvironmentOverrides()
    {
        if (Env.GetBool("GOVEE_LAN_NO_MULTICAST") is { } noMulticast)
        {
            EnableMulticast = !noMulticast;
        }
        if (Env.GetBool("GOVEE_LAN_BROADCAST_ALL") is { } broadcastAll)
        {
            BroadcastAllInterfaces = broadcastAll;
        }
        if (Env.GetBool("GOVEE_LAN_BROADCAST_GLOBAL") is { } globalBroadcast)
        {
            GlobalBroadcast = globalBroadcast;
        }
        if (Env.Get("GOVEE_LAN_SCAN") is { } scan)
        {
            foreach (var entry in scan.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!IPAddress.TryParse(entry, out var address))
                {
                    throw new GoveeException($"parsing GOVEE_LAN_SCAN entry '{entry}' as an IP address");
                }
                AdditionalAddresses.Add(address);
            }
        }
        if (Env.GetInt("GOVEE_LAN_DISCO_TIMEOUT") is { } timeout)
        {
            DiscoveryTimeoutSeconds = timeout;
        }

        return this;
    }
}

internal sealed class LanResponseEnvelope
{
    [JsonPropertyName("msg")] public LanResponseMessage? Msg { get; set; }
}

internal sealed class LanResponseMessage
{
    [JsonPropertyName("cmd")] public string Cmd { get; set; } = "";
    [JsonPropertyName("data")] public System.Text.Json.Nodes.JsonNode? Data { get; set; }
}

internal sealed class LanDeviceStatusPayload
{
    [JsonPropertyName("onOff")]
    [JsonConverter(typeof(BooleanIntConverter))]
    public bool On { get; set; }

    [JsonPropertyName("brightness")] public byte Brightness { get; set; }
    [JsonPropertyName("color")] public LanColor Color { get; set; } = new();
    [JsonPropertyName("colorTemInKelvin")] public int ColorTemperatureKelvin { get; set; }

    public LanDeviceStatus ToStatus()
        => new(On, Brightness, new DeviceColor(Color.R, Color.G, Color.B), ColorTemperatureKelvin);
}

internal sealed class LanColor
{
    [JsonPropertyName("r")] public byte R { get; set; }
    [JsonPropertyName("g")] public byte G { get; set; }
    [JsonPropertyName("b")] public byte B { get; set; }
}
