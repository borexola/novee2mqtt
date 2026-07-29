using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Novee2Mqtt.Core;

namespace Novee2Mqtt.Platform;

/// <summary>
/// A Govee device type string such as <c>devices.types.light</c>. Modelled as an
/// open enum: unknown values round-trip unchanged instead of failing to parse.
/// </summary>
[JsonConverter(typeof(DeviceTypeConverter))]
public readonly record struct DeviceType(string Value)
{
    public static readonly DeviceType Light = new("devices.types.light");
    public static readonly DeviceType AirPurifier = new("devices.types.air_purifier");
    public static readonly DeviceType Thermometer = new("devices.types.thermometer");
    public static readonly DeviceType Socket = new("devices.types.socket");
    public static readonly DeviceType Sensor = new("devices.types.sensor");
    public static readonly DeviceType Heater = new("devices.types.heater");
    public static readonly DeviceType Humidifier = new("devices.types.humidifier");
    public static readonly DeviceType Dehumidifier = new("devices.types.dehumidifier");
    public static readonly DeviceType IceMaker = new("devices.types.ice_maker");
    public static readonly DeviceType AromaDiffuser = new("devices.types.aroma_diffuser");
    public static readonly DeviceType Fan = new("devices.types.fan");
    public static readonly DeviceType Kettle = new("devices.types.kettle");

    /// <summary>True when Govee omitted the field, which older responses do.</summary>
    public bool IsUnknown => string.IsNullOrEmpty(Value);

    public override string ToString() => Value ?? "";
}

/// <summary>A capability type string such as <c>devices.capabilities.on_off</c>.</summary>
[JsonConverter(typeof(DeviceCapabilityKindConverter))]
public readonly record struct DeviceCapabilityKind(string Value)
{
    public static readonly DeviceCapabilityKind OnOff = new("devices.capabilities.on_off");
    public static readonly DeviceCapabilityKind Toggle = new("devices.capabilities.toggle");
    public static readonly DeviceCapabilityKind Range = new("devices.capabilities.range");
    public static readonly DeviceCapabilityKind Mode = new("devices.capabilities.mode");
    public static readonly DeviceCapabilityKind ColorSetting = new("devices.capabilities.color_setting");
    public static readonly DeviceCapabilityKind SegmentColorSetting = new("devices.capabilities.segment_color_setting");
    public static readonly DeviceCapabilityKind MusicSetting = new("devices.capabilities.music_setting");
    public static readonly DeviceCapabilityKind DynamicScene = new("devices.capabilities.dynamic_scene");
    public static readonly DeviceCapabilityKind WorkMode = new("devices.capabilities.work_mode");
    public static readonly DeviceCapabilityKind DynamicSetting = new("devices.capabilities.dynamic_setting");
    public static readonly DeviceCapabilityKind TemperatureSetting = new("devices.capabilities.temperature_setting");
    public static readonly DeviceCapabilityKind Online = new("devices.capabilities.online");
    public static readonly DeviceCapabilityKind Property = new("devices.capabilities.property");
    public static readonly DeviceCapabilityKind Event = new("devices.capabilities.event");

    public override string ToString() => Value ?? "";
}

public sealed class DeviceTypeConverter : JsonConverter<DeviceType>
{
    public override DeviceType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new(reader.GetString() ?? "");

    public override void Write(Utf8JsonWriter writer, DeviceType value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}

public sealed class DeviceCapabilityKindConverter : JsonConverter<DeviceCapabilityKind>
{
    public override DeviceCapabilityKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new(reader.GetString() ?? "");

    public override void Write(Utf8JsonWriter writer, DeviceCapabilityKind value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}

public sealed class IntegerRange
{
    [JsonPropertyName("min")] public long Min { get; set; }
    [JsonPropertyName("max")] public long Max { get; set; }
    [JsonPropertyName("precision")] public long Precision { get; set; }
}

public sealed class ArraySize
{
    [JsonPropertyName("min")] public long Min { get; set; }
    [JsonPropertyName("max")] public long Max { get; set; }
}

public sealed class ElementRange
{
    [JsonPropertyName("min")] public long Min { get; set; }
    [JsonPropertyName("max")] public long Max { get; set; }
}

public sealed class ArrayOption
{
    [JsonPropertyName("value")] public long Value { get; set; }
}

/// <summary>
/// One option of an ENUM parameter. Govee sometimes localises <c>name</c> into
/// an object, and attaches extra keys (<c>range</c>, <c>options</c>,
/// <c>defaultValue</c>) that the work-mode parser needs, so those are kept in
/// <see cref="Extras"/>.
/// </summary>
[JsonConverter(typeof(EnumOptionConverter))]
public sealed class EnumOption
{
    public string Name { get; set; } = "";
    public JsonNode? Value { get; set; }
    public Dictionary<string, JsonNode?> Extras { get; set; } = new();
}

public sealed class EnumOptionConverter : JsonConverter<EnumOption>
{
    public override EnumOption Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var node = JsonNode.Parse(ref reader) as JsonObject
            ?? throw new JsonException("EnumOption must be an object");

        var result = new EnumOption();
        foreach (var (key, value) in node.ToList())
        {
            node.Remove(key);
            switch (key)
            {
                case "name":
                    result.Name = ExtractName(value);
                    break;
                case "value":
                    result.Value = value;
                    break;
                default:
                    result.Extras[key] = value;
                    break;
            }
        }
        return result;
    }

    /// <summary>
    /// Accepts both <c>"name": "On"</c> and the localised
    /// <c>"name": {"en": "On", "de": "Ein"}</c>, preferring English.
    /// </summary>
    private static string ExtractName(JsonNode? value) => value switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonObject obj => obj["en"]?.AsString()
            ?? obj.Select(kv => kv.Value.AsString()).FirstOrDefault(s => s is not null)
            ?? "Unknown",
        _ => "Unknown",
    };

    public override void Write(Utf8JsonWriter writer, EnumOption value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("name", value.Name);
        if (value.Value is not null)
        {
            writer.WritePropertyName("value");
            value.Value.WriteTo(writer, options);
        }
        foreach (var (key, extra) in value.Extras)
        {
            writer.WritePropertyName(key);
            if (extra is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                extra.WriteTo(writer, options);
            }
        }
        writer.WriteEndObject();
    }
}

/// <summary>
/// The <c>parameters</c> block of a capability, discriminated by
/// <c>dataType</c>. Unrecognised shapes fall back to <see cref="OtherParameters"/>
/// so a single new Govee data type cannot break device enumeration.
/// </summary>
[JsonConverter(typeof(DeviceParametersConverter))]
public abstract class DeviceParameters
{
    public long? EnumParameterByName(string name)
    {
        if (this is not EnumParameters e)
        {
            return null;
        }
        foreach (var option in e.Options)
        {
            if (option.Name == name && option.Value.AsInt64() is { } value)
            {
                return value;
            }
        }
        return null;
    }
}

public sealed class EnumParameters : DeviceParameters
{
    public List<EnumOption> Options { get; set; } = [];
}

public sealed class IntegerParameters : DeviceParameters
{
    public string? Unit { get; set; }
    public IntegerRange Range { get; set; } = new();
}

public sealed class StructParameters : DeviceParameters
{
    public List<StructField> Fields { get; set; } = [];
}

public sealed class ArrayParameters : DeviceParameters
{
    public ArraySize? Size { get; set; }
    public ElementRange? ElementRange { get; set; }
    public string? ElementType { get; set; }
    public List<ArrayOption> Options { get; set; } = [];
}

public sealed class OtherParameters : DeviceParameters
{
    public JsonObject? Raw { get; set; }
}

/// <summary>
/// A field of a STRUCT parameter. The field's own parameter definition is
/// inlined alongside <c>fieldName</c> rather than nested, hence the custom
/// converter.
/// </summary>
[JsonConverter(typeof(StructFieldConverter))]
public sealed class StructField
{
    public string FieldName { get; set; } = "";
    public DeviceParameters FieldType { get; set; } = new OtherParameters();
    public JsonNode? DefaultValue { get; set; }
    public bool Required { get; set; }
}

public sealed class DeviceParametersConverter : JsonConverter<DeviceParameters>
{
    public override DeviceParameters Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var node = JsonNode.Parse(ref reader) as JsonObject
            ?? throw new JsonException("parameters must be an object");
        return FromObject(node, options);
    }

    public static DeviceParameters FromObject(JsonObject node, JsonSerializerOptions options)
    {
        var dataType = node["dataType"]?.AsString();

        switch (dataType)
        {
            case "ENUM":
                return new EnumParameters
                {
                    Options = Deserialize<List<EnumOption>>(node["options"], options) ?? [],
                };

            case "INTEGER":
                return new IntegerParameters
                {
                    Unit = node["unit"]?.AsString(),
                    Range = Deserialize<IntegerRange>(node["range"], options) ?? new IntegerRange(),
                };

            case "STRUCT":
                return new StructParameters
                {
                    Fields = Deserialize<List<StructField>>(node["fields"], options) ?? [],
                };

            case "Array" or "ARRAY":
                return new ArrayParameters
                {
                    Size = Deserialize<ArraySize>(node["size"], options),
                    ElementRange = Deserialize<ElementRange>(node["elementRange"], options),
                    ElementType = node["elementType"]?.AsString(),
                    Options = Deserialize<List<ArrayOption>>(node["options"], options) ?? [],
                };

            default:
                return new OtherParameters { Raw = node.DeepClone() as JsonObject };
        }
    }

    private static T? Deserialize<T>(JsonNode? node, JsonSerializerOptions options)
        => node is null ? default : node.Deserialize<T>(options);

    public override void Write(Utf8JsonWriter writer, DeviceParameters value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        WriteBody(writer, value, options);
        writer.WriteEndObject();
    }

    /// <summary>
    /// Writes the parameter properties without the enclosing braces, so
    /// <see cref="StructFieldConverter"/> can inline them.
    /// </summary>
    public static void WriteBody(Utf8JsonWriter writer, DeviceParameters value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case EnumParameters e:
                writer.WriteString("dataType", "ENUM");
                writer.WritePropertyName("options");
                JsonSerializer.Serialize(writer, e.Options, options);
                break;

            case IntegerParameters i:
                writer.WriteString("dataType", "INTEGER");
                if (i.Unit is not null)
                {
                    writer.WriteString("unit", i.Unit);
                }
                writer.WritePropertyName("range");
                JsonSerializer.Serialize(writer, i.Range, options);
                break;

            case StructParameters s:
                writer.WriteString("dataType", "STRUCT");
                writer.WritePropertyName("fields");
                JsonSerializer.Serialize(writer, s.Fields, options);
                break;

            case ArrayParameters a:
                writer.WriteString("dataType", "Array");
                if (a.Size is not null)
                {
                    writer.WritePropertyName("size");
                    JsonSerializer.Serialize(writer, a.Size, options);
                }
                if (a.ElementRange is not null)
                {
                    writer.WritePropertyName("elementRange");
                    JsonSerializer.Serialize(writer, a.ElementRange, options);
                }
                if (a.ElementType is not null)
                {
                    writer.WriteString("elementType", a.ElementType);
                }
                writer.WritePropertyName("options");
                JsonSerializer.Serialize(writer, a.Options, options);
                break;

            case OtherParameters o when o.Raw is not null:
                foreach (var (key, child) in o.Raw)
                {
                    writer.WritePropertyName(key);
                    if (child is null)
                    {
                        writer.WriteNullValue();
                    }
                    else
                    {
                        child.WriteTo(writer, options);
                    }
                }
                break;
        }
    }
}

public sealed class StructFieldConverter : JsonConverter<StructField>
{
    public override StructField Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var node = JsonNode.Parse(ref reader) as JsonObject
            ?? throw new JsonException("struct field must be an object");

        return new StructField
        {
            FieldName = node["fieldName"]?.AsString() ?? "",
            DefaultValue = node["defaultValue"],
            Required = node["required"].AsInt64() is { } r ? r != 0 : node["required"] is JsonValue v && v.TryGetValue<bool>(out var b) && b,
            FieldType = DeviceParametersConverter.FromObject(node, options),
        };
    }

    public override void Write(Utf8JsonWriter writer, StructField value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("fieldName", value.FieldName);
        DeviceParametersConverter.WriteBody(writer, value.FieldType, options);
        if (value.DefaultValue is not null)
        {
            writer.WritePropertyName("defaultValue");
            value.DefaultValue.WriteTo(writer, options);
        }
        writer.WriteBoolean("required", value.Required);
        writer.WriteEndObject();
    }
}

public sealed class DeviceCapability
{
    [JsonPropertyName("type")] public DeviceCapabilityKind Kind { get; set; }
    [JsonPropertyName("instance")] public string Instance { get; set; } = "";
    [JsonPropertyName("parameters")] public DeviceParameters? Parameters { get; set; }
    [JsonPropertyName("alarmType")] public long? AlarmType { get; set; }
    [JsonPropertyName("eventState")] public JsonNode? EventState { get; set; }

    public long? EnumParameterByName(string name) => Parameters?.EnumParameterByName(name);

    public StructField? StructFieldByName(string name)
        => Parameters is StructParameters s ? s.Fields.FirstOrDefault(f => f.FieldName == name) : null;
}

public sealed class DeviceCapabilityState
{
    [JsonPropertyName("type")] public DeviceCapabilityKind Kind { get; set; }
    [JsonPropertyName("instance")] public string Instance { get; set; } = "";
    [JsonPropertyName("state")] public JsonNode? State { get; set; }
}

public sealed class HttpDeviceState
{
    [JsonPropertyName("sku")] public string Sku { get; set; } = "";
    [JsonPropertyName("device")] public string Device { get; set; } = "";
    [JsonPropertyName("capabilities")] public List<DeviceCapabilityState> Capabilities { get; set; } = [];

    public DeviceCapabilityState? CapabilityByInstance(string instance)
        => Capabilities.FirstOrDefault(c => string.Equals(c.Instance, instance, StringComparison.OrdinalIgnoreCase));
}

public sealed class HttpDeviceInfo
{
    [JsonPropertyName("sku")] public string Sku { get; set; } = "";
    [JsonPropertyName("device")] public string Device { get; set; } = "";
    [JsonPropertyName("deviceName")] public string DeviceName { get; set; } = "";
    [JsonPropertyName("type")] public DeviceType DeviceType { get; set; }
    [JsonPropertyName("capabilities")] public List<DeviceCapability> Capabilities { get; set; } = [];

    public DeviceCapability? CapabilityByInstance(string instance)
        => Capabilities.FirstOrDefault(c => string.Equals(c.Instance, instance, StringComparison.OrdinalIgnoreCase));

    public bool SupportsRgb() => CapabilityByInstance("colorRgb") is not null;

    public bool SupportsBrightness() => CapabilityByInstance("brightness") is not null;

    public bool SupportsDynamicScenes() => Capabilities.Any(c => c.Kind == DeviceCapabilityKind.DynamicScene);

    /// <summary>
    /// Returns the zero-based segment indices this device exposes, or null if it
    /// has no segmented colour support.
    /// </summary>
    /// <remarks>
    /// The <c>size</c> block holds the 1-based display indices while
    /// <c>elementRange</c> holds the actual indices. The reported
    /// <c>elementRange.max</c> is unreliable, so the count comes from
    /// <c>size</c> instead — see
    /// <see href="https://developer.govee.com/discuss/6599afb91cb48d002dbed2b8"/>.
    /// </remarks>
    public IReadOnlyList<long>? SupportsSegmentedRgb()
    {
        var field = CapabilityByInstance("segmentedColorRgb")?.StructFieldByName("segment");
        if (field?.FieldType is not ArrayParameters { Size: { } size, ElementRange: { } elementRange })
        {
            return null;
        }

        var count = Math.Max(0, 1 + size.Max - size.Min);
        return Enumerable.Range(0, (int)count).Select(i => elementRange.Min + i).ToList();
    }

    public (long Min, long Max)? SupportsSegmentedBrightness()
    {
        var field = CapabilityByInstance("segmentedBrightness")?.StructFieldByName("brightness");
        return field?.FieldType is IntegerParameters i ? (i.Range.Min, i.Range.Max) : null;
    }

    public (long Min, long Max)? GetColorTemperatureRange()
        => CapabilityByInstance("colorTemperatureK")?.Parameters is IntegerParameters i
            ? (i.Range.Min, i.Range.Max)
            : null;
}
