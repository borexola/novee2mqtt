using System.Text.Json;
using System.Text.Json.Nodes;
using Novee2Mqtt.Core;
using Novee2Mqtt.Hass;
using Novee2Mqtt.Platform;
using Novee2Mqtt.Undocumented;

namespace Novee2Mqtt.Tests;

public static class TestData
{
    public static string Read(string name)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "test-data", name));
}

public class PlatformModelTests
{
    [Fact]
    public void ParsesDeviceList()
    {
        var response = Json.Deserialize<JsonNode>(TestData.Read("list_devices.json"));
        var devices = response["data"].Deserialize<List<HttpDeviceInfo>>(Json.Options)!;

        Assert.NotEmpty(devices);
        Assert.All(devices, d => Assert.False(string.IsNullOrEmpty(d.Sku)));
        Assert.All(devices, d => Assert.False(string.IsNullOrEmpty(d.Device)));
    }

    [Fact]
    public void RecognisesLightCapabilities()
    {
        var response = Json.Deserialize<JsonNode>(TestData.Read("list_devices.json"));
        var devices = response["data"].Deserialize<List<HttpDeviceInfo>>(Json.Options)!;

        var light = devices.First(d => d.SupportsRgb());

        Assert.True(light.SupportsBrightness());
        Assert.True(light.SupportsDynamicScenes());
        Assert.NotNull(light.CapabilityByInstance("powerSwitch"));
        Assert.NotNull(light.GetColorTemperatureRange());
    }

    /// <summary>
    /// Older Platform API responses omit the <c>type</c> field entirely, which
    /// must not stop the device being usable.
    /// </summary>
    [Fact]
    public void MissingDeviceTypeFallsBackToLight()
    {
        var response = Json.Deserialize<JsonNode>(TestData.Read("list_devices.json"));
        var info = response["data"].Deserialize<List<HttpDeviceInfo>>(Json.Options)![0];

        Assert.True(info.DeviceType.IsUnknown);

        var device = new Devices.Device(info.Sku, info.Device);
        device.SetHttpDeviceInfo(info);

        Assert.Equal(DeviceType.Light, device.GetDeviceType());
    }

    [Fact]
    public void CapabilityLookupIsCaseInsensitive()
    {
        var response = Json.Deserialize<JsonNode>(TestData.Read("list_devices.json"));
        var devices = response["data"].Deserialize<List<HttpDeviceInfo>>(Json.Options)!;
        var device = devices.First(d => d.CapabilityByInstance("powerSwitch") is not null);

        Assert.NotNull(device.CapabilityByInstance("POWERSWITCH"));
    }

    [Fact]
    public void ParsesDeviceState()
    {
        var response = Json.Deserialize<JsonNode>(TestData.Read("get_device_state.json"));
        var state = response["payload"].Deserialize<HttpDeviceState>(Json.Options)!;

        Assert.NotEmpty(state.Capabilities);
        Assert.NotNull(state.CapabilityByInstance("powerSwitch"));
    }

    [Fact]
    public void ParsesSceneCapabilities()
    {
        var response = Json.Deserialize<JsonNode>(TestData.Read("scenes.json"));
        var capabilities = response["payload"]?["capabilities"].Deserialize<List<DeviceCapability>>(Json.Options)!;

        var scenes = capabilities.Single(c => c.Kind == DeviceCapabilityKind.DynamicScene);
        var options = Assert.IsType<EnumParameters>(scenes.Parameters);

        Assert.NotEmpty(options.Options);
        Assert.All(options.Options, o => Assert.False(string.IsNullOrEmpty(o.Name)));
    }

    /// <summary>An unrecognised dataType must not break parsing of the whole device.</summary>
    [Fact]
    public void UnknownDataTypeFallsBackToOther()
    {
        const string json = """
            {"type":"devices.capabilities.range","instance":"weird","parameters":{"dataType":"QUANTUM","value":1}}
            """;

        var capability = Json.Deserialize<DeviceCapability>(json);

        var other = Assert.IsType<OtherParameters>(capability.Parameters);
        Assert.Equal("QUANTUM", other.Raw!["dataType"]!.GetValue<string>());
    }

    [Fact]
    public void UnknownDeviceTypeRoundTrips()
    {
        var capability = Json.Deserialize<DeviceCapability>(
            """{"type":"devices.capabilities.brand_new","instance":"x"}""");

        Assert.Equal("devices.capabilities.brand_new", capability.Kind.Value);
        Assert.Contains("devices.capabilities.brand_new", Json.Serialize(capability));
    }

    /// <summary>Govee localises some enum names into an object; English is preferred.</summary>
    [Fact]
    public void LocalisedEnumOptionNamePrefersEnglish()
    {
        const string json = """
            {"dataType":"ENUM","options":[{"name":{"de":"Spiel","en":"Game"},"value":1}]}
            """;

        var parameters = Assert.IsType<EnumParameters>(Json.Deserialize<DeviceParameters>(json));

        Assert.Equal("Game", parameters.Options[0].Name);
    }

    [Fact]
    public void SegmentRangeUsesDisplayCountAndElementBase()
    {
        const string json = """
            {
              "sku":"H1","device":"d","deviceName":"n","type":"devices.types.light",
              "capabilities":[{
                "type":"devices.capabilities.segment_color_setting",
                "instance":"segmentedColorRgb",
                "parameters":{"dataType":"STRUCT","fields":[
                  {"fieldName":"segment","dataType":"Array",
                   "size":{"min":1,"max":15},"elementRange":{"min":0,"max":14}}
                ]}
              }]
            }
            """;

        var device = Json.Deserialize<HttpDeviceInfo>(json);
        var segments = device.SupportsSegmentedRgb();

        Assert.NotNull(segments);
        Assert.Equal(15, segments.Count);
        Assert.Equal(0, segments[0]);
        Assert.Equal(14, segments[^1]);
    }
}

public class UndocModelTests
{
    [Fact]
    public void ParsesDeviceListWithEmbeddedJson()
    {
        var response = Json.Deserialize<DevicesResponse>(TestData.Read("undoc-device-list.json"));

        Assert.NotEmpty(response.Devices);
        Assert.All(response.Devices, d => Assert.NotNull(d.DeviceExt.DeviceSettings));
    }

    [Fact]
    public void ExposesDeviceTopicForIotCapableDevices()
    {
        var response = Json.Deserialize<DevicesResponse>(TestData.Read("undoc-device-list.json"));

        Assert.Contains(response.Devices, d => d.Topic is not null);
    }

    [Fact]
    public void ParsesOneClickShortcuts()
    {
        var response = Json.Deserialize<OneClickResponse>(TestData.Read("undoc-one-click.json"));

        Assert.NotEmpty(response.Data.Components);
        Assert.Contains(response.Data.Components.SelectMany(c => c.OneClicks), oc => oc.IotRules.Count > 0);
    }

    [Fact]
    public void DecodesEmbeddedIotMessages()
    {
        var response = Json.Deserialize<OneClickResponse>(TestData.Read("undoc-one-click.json"));

        var messages = response.Data.Components
            .SelectMany(c => c.OneClicks)
            .SelectMany(oc => oc.IotRules)
            .SelectMany(r => r.Rule)
            .Where(e => e.IotMsg is not null)
            .ToList();

        Assert.NotEmpty(messages);
        Assert.All(messages, m => Assert.IsType<JsonObject>(m.IotMsg));
    }
}

public class WorkModeTests
{
    private static DeviceCapability LoadCapability(string file)
        => Json.Deserialize<DeviceCapability>(TestData.Read(file));

    /// <summary>
    /// A gapless set of unnamed values collapses into a range so it renders as a
    /// slider rather than eight separate buttons.
    /// </summary>
    [Fact]
    public void ContiguousValuesBecomeARange()
    {
        var capability = BuildCapability(
            """[{"value":1},{"value":2},{"value":3},{"value":4},{"value":5},{"value":6},{"value":7},{"value":8}]""");

        var parsed = ParsedWorkMode.WithCapability(capability);
        var mode = parsed.ModeByName("Normal")!;

        Assert.Equal(new ValueRange(1, 9), mode.ContiguousValueRange());
        Assert.False(mode.ShouldShowAsPreset());
    }

    [Fact]
    public void ValuesWithHolesStayAsPresets()
    {
        var capability = BuildCapability("""[{"value":1},{"value":2},{"value":4}]""");

        var mode = ParsedWorkMode.WithCapability(capability).ModeByName("Normal")!;

        Assert.Null(mode.ContiguousValueRange());
        Assert.Equal(3, mode.Values.Count);
        Assert.Equal("Activate Normal Preset 1", mode.Values[0].ComputedLabel);
    }

    [Fact]
    public void ParsesRangesAndDefaults()
    {
        var parsed = ParsedWorkMode.WithCapability(LoadCapability("work-mode-issue-81.json"));

        Assert.Equal(new ValueRange(40, 81), parsed.ModeByName("Auto")!.ContiguousValueRange());
        Assert.Equal(new ValueRange(1, 10), parsed.ModeByName("Manual")!.ContiguousValueRange());
        Assert.Null(parsed.ModeByName("Custom")!.ContiguousValueRange());
    }

    [Fact]
    public void DistinguishesSliderModesFromPresetModes()
    {
        var parsed = ParsedWorkMode.WithCapability(LoadCapability("work-mode-issue-93.json"));

        Assert.False(parsed.ModeByName("FanSpeed")!.ShouldShowAsPreset());
        Assert.True(parsed.ModeByName("Auto")!.ShouldShowAsPreset());
        Assert.Equal(new ValueRange(1, 9), parsed.ModeByName("FanSpeed")!.ContiguousValueRange());
    }

    [Fact]
    public void DefaultValueFallsBackToRangeStart()
    {
        var parsed = ParsedWorkMode.WithCapability(LoadCapability("work-mode-issue-100.json"));
        parsed.AdjustForDevice("H7173");

        Assert.Equal(0, parsed.ModeByName("Boiling")!.DefaultValue());
        Assert.Equal(1, parsed.ModeByName("DIY")!.DefaultValue());
        Assert.Equal(new ValueRange(1, 5), parsed.ModeByName("Tea")!.ContiguousValueRange());
    }

    [Fact]
    public void RelabelsKettleGearMode()
    {
        var parsed = ParsedWorkMode.WithCapability(LoadCapability("work-mode-issue-100.json"));
        parsed.AdjustForDevice("H7173");

        // H7173 has no gearMode, but the default branch must not clobber labels
        // for devices that do have one.
        var heater = ParsedWorkMode.WithCapability(LoadCapability("work-mode-issue-93.json"));
        heater.AdjustForDevice("H7131");

        Assert.All(parsed.Modes.Values, m => Assert.False(string.IsNullOrEmpty(m.EffectiveLabel)));
        Assert.All(heater.Modes.Values, m => Assert.False(string.IsNullOrEmpty(m.EffectiveLabel)));
    }

    [Fact]
    public void ModeNamesAreSorted()
    {
        var parsed = ParsedWorkMode.WithCapability(LoadCapability("work-mode-issue-93.json"));

        var names = parsed.GetModeNames();

        Assert.Equal(names.Order(StringComparer.Ordinal), names);
    }

    [Fact]
    public void LooksUpModeByValue()
    {
        var parsed = ParsedWorkMode.WithCapability(LoadCapability("work-mode-issue-81.json"));

        Assert.Equal("Auto", parsed.ModeForValue(JsonValue.Create(3L))!.Name);
    }

    private static DeviceCapability BuildCapability(string optionsJson) => Json.Deserialize<DeviceCapability>($$"""
        {
          "type": "devices.capabilities.work_mode",
          "instance": "workMode",
          "parameters": {
            "dataType": "STRUCT",
            "fields": [
              {
                "fieldName": "workMode",
                "dataType": "ENUM",
                "options": [{"name": "Normal", "value": 1}]
              },
              {
                "fieldName": "modeValue",
                "dataType": "ENUM",
                "options": [{"name": "Normal", "value": null, "options": {{optionsJson}}}]
              }
            ]
          }
        }
        """);
}
