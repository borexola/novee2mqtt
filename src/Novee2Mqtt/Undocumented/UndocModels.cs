using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Novee2Mqtt.Core;

namespace Novee2Mqtt.Undocumented;

/// <summary>
/// Reads a field whose value is a JSON document embedded in a string, which is
/// how the app API nests <c>deviceSettings</c>, <c>iotMsg</c> and friends.
/// </summary>
public sealed class EmbeddedJsonConverter<T> : JsonConverter<T?>
{
    public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return default;
        }

        // Some entries are already objects rather than encoded strings.
        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
        {
            return JsonSerializer.Deserialize<T>(ref reader, options);
        }

        var text = reader.GetString();
        return string.IsNullOrEmpty(text) ? default : JsonSerializer.Deserialize<T>(text, options);
    }

    public override void Write(Utf8JsonWriter writer, T? value, JsonSerializerOptions options)
        => writer.WriteStringValue(value is null ? null : JsonSerializer.Serialize(value, options));
}

/// <summary>Accepts 0/1, true/false, or null for fields Govee models inconsistently.</summary>
public sealed class BooleanIntConverter : JsonConverter<bool>
{
    public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.True => true,
            JsonTokenType.False => false,
            JsonTokenType.Null => false,
            JsonTokenType.Number => reader.GetDouble() != 0,
            JsonTokenType.String => reader.GetString() is { } s && s != "0"
                && !s.Equals("false", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };

    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options)
        => writer.WriteBooleanValue(value);
}

/// <summary>The AWS IoT connection material handed out by the app API.</summary>
public sealed class IotKey
{
    [JsonPropertyName("endpoint")] public string Endpoint { get; set; } = "";
    [JsonPropertyName("log")] public string Log { get; set; } = "";
    [JsonPropertyName("p12")] public string P12 { get; set; } = "";
    [JsonPropertyName("p12Pass")] public string P12Pass { get; set; } = "";
}

public sealed class LoginAccountResponse
{
    [JsonPropertyName("A")] public string A { get; set; } = "";
    [JsonPropertyName("B")] public string B { get; set; } = "";
    [JsonPropertyName("accountId")] public long AccountId { get; set; }
    [JsonPropertyName("client")] public string Client { get; set; } = "";
    [JsonPropertyName("refreshToken")] public string? RefreshToken { get; set; }
    [JsonPropertyName("token")] public string Token { get; set; } = "";
    [JsonPropertyName("tokenExpireCycle")] public long TokenExpireCycle { get; set; }

    /// <summary>The per-account MQTT topic. Acts as a credential; keep it out of logs.</summary>
    [JsonPropertyName("topic")] public string Topic { get; set; } = "";
}

public sealed class DevicesResponse
{
    [JsonPropertyName("devices")] public List<DeviceEntry> Devices { get; set; } = [];
    [JsonPropertyName("groups")] public List<GroupEntry> Groups { get; set; } = [];
}

public sealed class GroupEntry
{
    [JsonPropertyName("groupId")] public long GroupId { get; set; }
    [JsonPropertyName("groupName")] public string GroupName { get; set; } = "";
}

public sealed class DeviceEntry
{
    [JsonPropertyName("device")] public string Device { get; set; } = "";
    [JsonPropertyName("sku")] public string Sku { get; set; } = "";
    [JsonPropertyName("deviceName")] public string DeviceName { get; set; } = "";
    [JsonPropertyName("groupId")] public long GroupId { get; set; }
    [JsonPropertyName("deviceExt")] public DeviceEntryExt DeviceExt { get; set; } = new();

    /// <summary>
    /// The per-device MQTT topic used to publish commands over AWS IoT.
    /// BLE-only devices have none.
    /// </summary>
    public string? Topic => DeviceExt.DeviceSettings?.Topic;

    public string RequireTopic()
        => Topic ?? throw new GoveeException($"device {Device} has no topic, is it a BLE-only device?");

    /// <summary>Devices with no wifi name never joined the network and can only be reached over BLE.</summary>
    public bool IsBleOnly => DeviceExt.DeviceSettings?.WifiName is null;
}

public sealed class DeviceEntryExt
{
    [JsonPropertyName("deviceSettings")]
    [JsonConverter(typeof(EmbeddedJsonConverter<DeviceSettings>))]
    public DeviceSettings? DeviceSettings { get; set; }
}

/// <summary>
/// The interesting subset of the embedded settings blob; unknown fields are
/// ignored during deserialization.
/// </summary>
public sealed class DeviceSettings
{
    [JsonPropertyName("wifiName")] public string? WifiName { get; set; }
    [JsonPropertyName("topic")] public string? Topic { get; set; }
}

public sealed class LightEffectLibraryResponse
{
    [JsonPropertyName("data")] public LightEffectLibraryData Data { get; set; } = new();
}

public sealed class LightEffectLibraryData
{
    [JsonPropertyName("categories")] public List<LightEffectCategory> Categories { get; set; } = [];
}

public sealed class LightEffectCategory
{
    [JsonPropertyName("scenes")] public List<LightEffectScene> Scenes { get; set; } = [];
}

public sealed class LightEffectScene
{
    [JsonPropertyName("sceneId")] public long SceneId { get; set; }
    [JsonPropertyName("sceneName")] public string SceneName { get; set; } = "";
    [JsonPropertyName("lightEffects")] public List<LightEffectEntry> LightEffects { get; set; } = [];
}

public sealed class LightEffectEntry
{
    /// <summary>Note the spelling: Govee's own field is <c>scenceParamId</c>.</summary>
    [JsonPropertyName("scenceParamId")] public long SceneParamId { get; set; }

    /// <summary>Base64-encoded effect definition uploaded to the device.</summary>
    [JsonPropertyName("scenceParam")] public string SceneParam { get; set; } = "";

    /// <summary>Zero means the effect cannot be activated over the LAN.</summary>
    [JsonPropertyName("sceneCode")] public int SceneCode { get; set; }
}

public sealed class OneClickResponse
{
    [JsonPropertyName("data")] public OneClickData Data { get; set; } = new();
}

public sealed class OneClickData
{
    [JsonPropertyName("components")] public List<OneClickComponent> Components { get; set; } = [];
}

public sealed class OneClickComponent
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("oneClicks")] public List<OneClick> OneClicks { get; set; } = [];
}

public sealed class OneClick
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("iotRules")] public List<OneClickIotRule> IotRules { get; set; } = [];
}

public sealed class OneClickIotRule
{
    [JsonPropertyName("deviceObj")] public OneClickIotRuleDevice DeviceObj { get; set; } = new();
    [JsonPropertyName("rule")] public List<OneClickIotRuleEntry> Rule { get; set; } = [];
}

public sealed class OneClickIotRuleDevice
{
    [JsonPropertyName("topic")] public string? Topic { get; set; }
}

public sealed class OneClickIotRuleEntry
{
    [JsonPropertyName("iotMsg")]
    [JsonConverter(typeof(EmbeddedJsonConverter<JsonNode>))]
    public JsonNode? IotMsg { get; set; }
}

/// <summary>
/// A Tap-to-Run shortcut flattened into the MQTT publishes needed to trigger it.
/// </summary>
public sealed class ParsedOneClick
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("entries")] public List<ParsedOneClickEntry> Entries { get; set; } = [];
}

public sealed class ParsedOneClickEntry
{
    [JsonPropertyName("topic")] public string Topic { get; set; } = "";
    [JsonPropertyName("msgs")] public List<JsonNode> Messages { get; set; } = [];
}
