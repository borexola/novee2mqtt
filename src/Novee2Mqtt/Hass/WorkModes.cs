using System.Text.Json.Nodes;
using Novee2Mqtt.Core;
using Novee2Mqtt.Devices;
using Novee2Mqtt.Platform;

namespace Novee2Mqtt.Hass;

/// <summary>A half-open range of mode values, <c>[Start, End)</c>.</summary>
public readonly record struct ValueRange(long Start, long End);

public sealed class WorkModeValue
{
    public required JsonNode? Value { get; init; }
    public string? Name { get; init; }
    public string ComputedLabel { get; set; } = "";
}

/// <summary>
/// One entry of a device's <c>workMode</c> capability, plus whatever parameter
/// that mode accepts — either a contiguous range (rendered as a slider) or a
/// fixed set of presets (rendered as buttons).
/// </summary>
public sealed class WorkMode
{
    public required string Name { get; init; }
    public required JsonNode? Value { get; init; }
    public JsonNode? DefaultValueNode { get; set; }
    public string Label { get; set; } = "";
    public List<WorkModeValue> Values { get; } = [];
    public ValueRange? Range { get; set; }

    public string EffectiveLabel => string.IsNullOrEmpty(Label) ? Name : Label;

    public void AddValues(EnumOption option)
    {
        DefaultValueNode = option.Extras.GetValueOrDefault("defaultValue");

        if (option.Extras.GetValueOrDefault("range") is JsonObject rangeNode
            && rangeNode["min"].AsInt64() is { } min
            && rangeNode["max"].AsInt64() is { } max)
        {
            Range = new ValueRange(min, max + 1);
            return;
        }

        if (option.Extras.GetValueOrDefault("options") is not JsonArray options)
        {
            return;
        }

        foreach (var entry in options)
        {
            if (entry is not JsonObject obj)
            {
                continue;
            }

            Values.Add(new WorkModeValue
            {
                Value = obj["value"]?.DeepClone(),
                Name = obj["name"].AsString(),
            });
        }

        if (ContiguousValueRange() is { } contiguous)
        {
            // A gapless set of unnamed values is better presented as a slider.
            Values.Clear();
            Range = contiguous;
        }
        else
        {
            foreach (var value in Values)
            {
                var optionName = value.Name ?? value.Value?.ToJsonString() ?? "";
                value.ComputedLabel = $"Activate {Name} Preset {optionName}";
            }
        }
    }

    public long DefaultValue()
        => DefaultValueNode.AsInt64()
           ?? Values.FirstOrDefault()?.Value.AsInt64()
           ?? Range?.Start
           ?? 0;

    /// <summary>
    /// The values as a gapless range, or null if any value is named or the set
    /// has holes — in which case they must be shown as individual presets.
    /// </summary>
    public ValueRange? ContiguousValueRange()
    {
        if (Range is { } range)
        {
            return range;
        }

        var values = new List<long>();
        foreach (var value in Values)
        {
            if (value.Name is not null)
            {
                return null;
            }
            if (value.Value.AsInt64() is not { } number)
            {
                return null;
            }
            values.Add(number);
        }

        if (values.Count == 0)
        {
            return null;
        }

        values.Sort();

        var expected = values[0];
        foreach (var value in values)
        {
            if (value != expected)
            {
                return null;
            }
            expected++;
        }

        return new ValueRange(values[0], values[^1] + 1);
    }

    public bool ShouldShowAsPreset() => ContiguousValueRange() is null && Values.Count == 0;
}

/// <summary>
/// The parsed <c>workMode</c> capability of a device. Govee models this as a
/// struct of two parallel enums — the mode list and the per-mode parameter
/// definitions — which this joins into something renderable.
/// </summary>
public sealed class ParsedWorkMode
{
    public SortedDictionary<string, WorkMode> Modes { get; } = new(StringComparer.Ordinal);

    public static ParsedWorkMode WithCapability(DeviceCapability capability)
    {
        var parsed = new ParsedWorkMode();

        var workModeField = capability.StructFieldByName("workMode")
            ?? throw new GoveeException($"workMode not found in capability {capability.Instance}");

        if (workModeField.FieldType is EnumParameters modeOptions)
        {
            foreach (var option in modeOptions.Options)
            {
                parsed.Modes[option.Name] = new WorkMode
                {
                    Name = option.Name,
                    Value = option.Value?.DeepClone(),
                };
            }
        }

        if (capability.StructFieldByName("modeValue")?.FieldType is EnumParameters valueOptions)
        {
            foreach (var option in valueOptions.Options)
            {
                if (parsed.Modes.TryGetValue(option.Name, out var mode))
                {
                    mode.AddValues(option);
                }
            }
        }

        return parsed;
    }

    public static ParsedWorkMode WithDevice(Device device)
    {
        var info = device.HttpDeviceInfo
            ?? throw new GoveeException("no platform state, so no known work mode");

        var capability = info.CapabilityByInstance("workMode")
            ?? throw new GoveeException("device has no workMode capability");

        var parsed = WithCapability(capability);
        parsed.AdjustForDevice(device.Sku);
        return parsed;
    }

    public static bool TryWithDevice(Device device, out ParsedWorkMode? parsed)
    {
        try
        {
            parsed = WithDevice(device);
            return true;
        }
        catch (GoveeException)
        {
            parsed = null;
            return false;
        }
    }

    /// <summary>Relabels modes whose Govee-supplied names are unhelpful in the UI.</summary>
    public void AdjustForDevice(string sku)
    {
        switch (sku)
        {
            case "H7160" or "H7143":
                if (Modes.TryGetValue("Manual", out var manual))
                {
                    manual.Label = "Manual: Mist Level";
                }
                break;

            case "H7131" or "H7173":
                if (Modes.TryGetValue("gearMode", out var gear))
                {
                    gear.Label = "Heat";
                }
                break;

            default:
                foreach (var mode in Modes.Values)
                {
                    mode.Label = mode.Name;
                }
                break;
        }
    }

    public WorkMode? ModeForValue(JsonNode? value)
        => Modes.Values.FirstOrDefault(m => Json.JsonEquals(m.Value, value));

    public WorkMode? ModeByName(string name) => Modes.GetValueOrDefault(name);

    public List<string> GetModeNames() => Modes.Values.Select(m => m.Name).Order(StringComparer.Ordinal).ToList();
}
