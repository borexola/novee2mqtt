using System.Text;
using Novee2Mqtt.Devices;

namespace Novee2Mqtt.Hass;

/// <summary>
/// MQTT topic and identifier construction. The <c>gv2mqtt</c> prefix is
/// deliberate and predates this project's name: it keeps entity unique ids
/// identical to the govee2mqtt bridge, so an install migrating from it keeps its
/// entities, history and automations. Renaming it to match the application would
/// orphan every existing entity, so leave it alone.
/// </summary>
public static class Topics
{
    public const string ServiceIdentifier = "gv2mqtt";

    /// <summary>
    /// Lower-cases and replaces characters that are awkward inside a topic
    /// segment or an entity id.
    /// </summary>
    public static string TopicSafeString(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            builder.Append(c is ':' or ' ' or '\\' or '/' or '\'' or '"' ? '_' : char.ToLowerInvariant(c));
        }
        return builder.ToString();
    }

    /// <summary>Device id with separators stripped. Case is preserved.</summary>
    public static string TopicSafeId(Device device)
        => string.Concat(device.Id.Where(c => c is not (':' or ' ')));

    /// <summary>
    /// Shared by every entity so a single last-will message marks them all
    /// unavailable when the bridge dies.
    /// </summary>
    public static string Availability => "gv2mqtt/availability";

    public static string OneClick => "gv2mqtt/oneclick";

    public static string PurgeCache => "gv2mqtt/purge-caches";

    public static string LightState(Device device) => $"gv2mqtt/light/{TopicSafeId(device)}/state";

    public static string LightSegmentState(Device device, long segment)
        => $"gv2mqtt/light/{TopicSafeId(device)}/state/{segment}";

    public static string LightCommand(Device device) => $"gv2mqtt/light/{TopicSafeId(device)}/command";

    public static string LightSegmentCommand(Device device, long segment)
        => $"gv2mqtt/light/{TopicSafeId(device)}/command/{segment}";

    public static string SwitchInstanceState(Device device, string instance)
        => $"gv2mqtt/switch/{TopicSafeId(device)}/{instance}/state";

    public static string SwitchInstanceCommand(Device device, string instance)
        => $"gv2mqtt/switch/{TopicSafeId(device)}/command/{instance}";

    public static string SensorState(string uniqueId) => $"gv2mqtt/sensor/{uniqueId}/state";

    public static string SensorAttributes(string uniqueId) => $"gv2mqtt/sensor/{uniqueId}/attributes";

    public static string RequestPlatformData(Device device)
        => $"gv2mqtt/{TopicSafeId(device)}/request-platform-data";

    public static string NumberCommand(Device device, string modeName, string modeNumber)
        => $"gv2mqtt/number/{TopicSafeId(device)}/command/{TopicSafeString(modeName)}/{modeNumber}";

    public static string NumberState(Device device, string modeName)
        => $"gv2mqtt/number/{TopicSafeId(device)}/state/{TopicSafeString(modeName)}";

    public static string SetWorkMode(Device device) => $"gv2mqtt/{TopicSafeId(device)}/set-work-mode";

    public static string NotifyWorkMode(Device device) => $"gv2mqtt/{TopicSafeId(device)}/notify-work-mode";

    public static string SetModeScene(Device device) => $"gv2mqtt/{TopicSafeId(device)}/set-mode-scene";

    public static string NotifyModeScene(Device device) => $"gv2mqtt/{TopicSafeId(device)}/notify-mode-scene";

    public static string HumidifierSetTarget(Device device) => $"gv2mqtt/humidifier/{TopicSafeId(device)}/set-target";

    public static string HumidifierNotifyTarget(Device device) => $"gv2mqtt/humidifier/{TopicSafeId(device)}/notify-target";

    public static string HumidifierSetMode(Device device) => $"gv2mqtt/humidifier/{TopicSafeId(device)}/set-mode";

    public static string HumidifierNotifyMode(Device device) => $"gv2mqtt/humidifier/{TopicSafeId(device)}/notify-mode";

    public static string HumidifierState(Device device) => $"gv2mqtt/humidifier/{TopicSafeId(device)}/state";

    public static string SetTemperature(Device device, string instance, string units)
        => $"gv2mqtt/{TopicSafeId(device)}/set-temperature/{TopicSafeString(instance)}/{units}";

    public static string AdviseSetTemperature(Device device)
        => $"gv2mqtt/{TopicSafeId(device)}/advise-set-temperature";

    /// <summary>Home Assistant colour temperatures are in mireds; Govee uses kelvin.</summary>
    public static int MiredToKelvin(int mired) => mired == 0 ? 0 : 1000000 / mired;

    public static int KelvinToMired(int kelvin) => kelvin == 0 ? 0 : 1000000 / kelvin;

    /// <summary>Turns an instance name like <c>powerSwitch</c> into "Power Switch".</summary>
    public static string CamelCaseToSpaceSeparated(string camel)
    {
        if (camel.Length == 0)
        {
            return camel;
        }

        var builder = new StringBuilder();
        builder.Append(char.ToUpperInvariant(camel[0]));

        foreach (var c in camel.Skip(1))
        {
            if (char.IsUpper(c))
            {
                builder.Append(' ');
            }
            builder.Append(c);
        }

        return builder.ToString();
    }
}
