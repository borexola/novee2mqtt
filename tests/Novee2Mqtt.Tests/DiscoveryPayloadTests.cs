using System.Text.Json.Nodes;
using Novee2Mqtt.Core;
using Novee2Mqtt.Devices;
using Novee2Mqtt.Hass;
using Novee2Mqtt.Lan;
using Novee2Mqtt.Platform;
using Novee2Mqtt.Undocumented;
using Microsoft.Extensions.Logging.Abstractions;

namespace Novee2Mqtt.Tests;

/// <summary>
/// The discovery payloads are the contract with Home Assistant. Topics and
/// unique ids in particular must stay byte-identical to the govee2mqtt bridge,
/// otherwise a migrating install loses its entity history.
/// </summary>
public class DiscoveryPayloadTests : IDisposable
{
    private const string LightId = "AA:BB:CC:DD:EE:FF:11:22";
    private const string LightTopicId = "AABBCCDDEEFF1122";

    private readonly string _cacheDir = Path.Combine(Path.GetTempPath(), "govee-tests-" + Guid.NewGuid().ToString("N"));
    private readonly GoveeCache _cache;
    private readonly HttpClient _httpClient = new();
    private readonly ServiceState _state;
    private readonly EntityEnumerator _enumerator;

    public DiscoveryPayloadTests()
    {
        Directory.CreateDirectory(_cacheDir);
        _cache = new GoveeCache(NullLogger<GoveeCache>.Instance, _cacheDir);

        var catalog = new SceneCatalog(NullLogger<SceneCatalog>.Instance, _httpClient, _cache);
        _state = new ServiceState(NullLogger<ServiceState>.Instance, catalog);
        _enumerator = new EntityEnumerator(NullLogger<EntityEnumerator>.Instance, _state);
    }

    public void Dispose()
    {
        _cache.Dispose();
        _httpClient.Dispose();
        try
        {
            Directory.Delete(_cacheDir, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private async Task<Device> AddDeviceAsync(string json, string sku, string id)
    {
        // Built by substitution rather than interpolation: JSON's runs of closing
        // braces collide with raw-string interpolation delimiters.
        var info = Json.Deserialize<HttpDeviceInfo>(json.Replace("$SKU", sku).Replace("$ID", id));
        await _state.UpdateDeviceAsync(sku, id, d => d.SetHttpDeviceInfo(info));
        return (await _state.GetDeviceByIdAsync(id))!;
    }

    private Task<Device> AddLightAsync(string sku = "H6072", string id = LightId) => AddDeviceAsync("""
        {
          "sku": "$SKU",
          "device": "$ID",
          "deviceName": "Living Room Lamp",
          "type": "devices.types.light",
          "capabilities": [
            {"type":"devices.capabilities.on_off","instance":"powerSwitch",
             "parameters":{"dataType":"ENUM","options":[{"name":"on","value":1},{"name":"off","value":0}]}},
            {"type":"devices.capabilities.range","instance":"brightness",
             "parameters":{"dataType":"INTEGER","range":{"min":1,"max":100,"precision":1}}},
            {"type":"devices.capabilities.color_setting","instance":"colorRgb",
             "parameters":{"dataType":"INTEGER","range":{"min":0,"max":16777215,"precision":1}}},
            {"type":"devices.capabilities.color_setting","instance":"colorTemperatureK",
             "parameters":{"dataType":"INTEGER","range":{"min":2000,"max":9000,"precision":1}}},
            {"type":"devices.capabilities.property","instance":"sensorTemperature",
             "parameters":{"dataType":"INTEGER","range":{"min":0,"max":100,"precision":1}}}
          ]
        }
        """, sku, id);

    private static HassEntity Single(IEnumerable<HassEntity> entities, string integration)
        => entities.Single(e => e.Integration == integration);

    private static string Str(HassEntity entity, string key) => entity.Config[key]!.GetValue<string>();

    [Fact]
    public async Task LightUsesTheOriginalTopicsAndUniqueId()
    {
        var light = Single(await _enumerator.EnumerateForDeviceAsync(await AddLightAsync()), "light");

        Assert.Equal($"gv2mqtt-{LightTopicId}", light.UniqueId);
        Assert.Equal($"gv2mqtt/light/{LightTopicId}/command", Str(light, "command_topic"));
        Assert.Equal($"gv2mqtt/light/{LightTopicId}/state", Str(light, "state_topic"));
        Assert.Equal("gv2mqtt/availability", Str(light, "availability_topic"));
        Assert.Equal("json", Str(light, "schema"));
    }

    [Fact]
    public async Task LightAdvertisesRgbAndColorTemperature()
    {
        var light = Single(await _enumerator.EnumerateForDeviceAsync(await AddLightAsync()), "light");

        var modes = light.Config["supported_color_modes"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Contains("rgb", modes);
        Assert.Contains("color_temp", modes);

        // 2000K-9000K, and the mired conversion inverts the ordering.
        Assert.Equal(Topics.KelvinToMired(9000), light.Config["min_mireds"]!.GetValue<int>());
        Assert.Equal(Topics.KelvinToMired(2000), light.Config["max_mireds"]!.GetValue<int>());
        Assert.True(light.Config["brightness"]!.GetValue<bool>());
        Assert.Equal(100, light.Config["brightness_scale"]!.GetValue<int>());
    }

    [Fact]
    public async Task DeviceBlockIdentifiesTheDeviceStably()
    {
        var light = Single(await _enumerator.EnumerateForDeviceAsync(await AddLightAsync()), "light");
        var device = light.Config["device"]!.AsObject();

        Assert.Equal("Living Room Lamp", device["name"]!.GetValue<string>());
        Assert.Equal("Govee", device["manufacturer"]!.GetValue<string>());
        Assert.Equal("H6072", device["model"]!.GetValue<string>());
        Assert.Equal($"gv2mqtt-{LightTopicId}", device["identifiers"]!.AsArray()[0]!.GetValue<string>());
        Assert.Equal("gv2mqtt", device["via_device"]!.GetValue<string>());
    }

    [Fact]
    public async Task PowerSwitchBecomesASwitchEntity()
    {
        var entities = await _enumerator.EnumerateForDeviceAsync(await AddLightAsync());
        var toggle = entities.Single(e => e.UniqueId == $"gv2mqtt-{LightTopicId}-powerSwitch");

        Assert.Equal("switch", toggle.Integration);
        Assert.Equal("Power Switch", Str(toggle, "name"));
        Assert.Equal($"gv2mqtt/switch/{LightTopicId}/command/powerSwitch", Str(toggle, "command_topic"));
        Assert.Equal($"gv2mqtt/switch/{LightTopicId}/powerSwitch/state", Str(toggle, "state_topic"));
    }

    [Fact]
    public async Task PropertyCapabilityBecomesADiagnosticSensor()
    {
        var entities = await _enumerator.EnumerateForDeviceAsync(await AddLightAsync());
        var sensor = entities.Single(e => e.UniqueId == $"sensor-{LightTopicId}-sensortemperature");

        Assert.Equal("Temperature", Str(sensor, "name"));
        Assert.Equal("temperature", Str(sensor, "device_class"));
        Assert.Equal("measurement", Str(sensor, "state_class"));
        Assert.Equal("diagnostic", Str(sensor, "entity_category"));
        Assert.Equal("°C", Str(sensor, "unit_of_measurement"));
    }

    [Fact]
    public async Task EveryDeviceGetsStatusDiagnosticsAndARefreshButton()
    {
        var entities = await _enumerator.EnumerateForDeviceAsync(await AddLightAsync());

        var status = entities.Single(e => e.UniqueId == $"sensor-{LightTopicId}-gv2mqtt-status");
        Assert.Equal("Status", Str(status, "name"));
        Assert.Equal($"gv2mqtt/sensor/{status.UniqueId}/attributes", Str(status, "json_attributes_topic"));

        var button = entities.Single(e => e.UniqueId.EndsWith("request-platform-data", StringComparison.Ordinal));
        Assert.Equal("button", button.Integration);
        Assert.Equal($"gv2mqtt/{LightTopicId}/request-platform-data", Str(button, "command_topic"));
    }

    [Fact]
    public async Task BleOnlyDevicesAreNotAdvertised()
    {
        // H6102 is quirked as BLE-only: nothing can reach it over the network.
        var device = await AddLightAsync("H6102", "11:22:33:44:55:66:77:88");

        Assert.Empty(await _enumerator.EnumerateForDeviceAsync(device));
    }

    [Fact]
    public async Task SegmentedLightsGetOneOptimisticLightPerSegment()
    {
        const string id = "AA:00:00:00:00:00:00:01";
        var device = await AddDeviceAsync("""
            {
              "sku":"$SKU","device":"$ID","deviceName":"Strip","type":"devices.types.light",
              "capabilities":[
                {"type":"devices.capabilities.segment_color_setting","instance":"segmentedColorRgb",
                 "parameters":{"dataType":"STRUCT","fields":[
                   {"fieldName":"segment","dataType":"Array","size":{"min":1,"max":3},"elementRange":{"min":0,"max":2}}
                 ]}}
              ]
            }
            """, "H6072", id);

        var lights = (await _enumerator.EnumerateForDeviceAsync(device))
            .Where(e => e.Integration == "light").ToList();

        // One primary light plus three segments.
        Assert.Equal(4, lights.Count);

        var segment = lights.Single(l => l.UniqueId == "gv2mqtt-AA00000000000001-0");
        Assert.Equal("Segment 001", Str(segment, "name"));
        Assert.True(segment.Config["optimistic"]!.GetValue<bool>());
        Assert.Equal("gv2mqtt/light/AA00000000000001/command/0", Str(segment, "command_topic"));

        // Segment lights are optimistic, so they must not publish state.
        Assert.Null(segment.PublishState);
    }

    [Fact]
    public async Task GlobalEntitiesDescribeTheBridgeItself()
    {
        var entities = await _enumerator.EnumerateAllAsync();

        var version = entities.Single(e => e.UniqueId == "global-version");
        var purge = entities.Single(e => e.UniqueId == "global-purge_caches");

        Assert.Equal("sensor", version.Integration);
        Assert.Equal("gv2mqtt", version.Config["device"]!["identifiers"]!.AsArray()[0]!.GetValue<string>());
        Assert.Equal("button", purge.Integration);
        Assert.Equal("gv2mqtt/purge-caches", Str(purge, "command_topic"));
    }

    [Fact]
    public async Task DiscoveryTopicUsesIntegrationAndUniqueId()
    {
        var light = Single(await _enumerator.EnumerateForDeviceAsync(await AddLightAsync()), "light");

        Assert.Equal(
            $"homeassistant/light/gv2mqtt-{LightTopicId}/config",
            light.DiscoveryTopic(_state.HassDiscoveryPrefix));
    }

    [Fact]
    public async Task LightStateReportsRgbWhenNoColorTemperatureIsSet()
    {
        var device = await AddLightAsync();

        await _state.UpdateDeviceAsync(device.Sku, device.Id, d => d.SetIotDeviceStatus(
            new LanDeviceStatus(On: true, Brightness: 42, Color: new DeviceColor(1, 2, 3), ColorTemperatureKelvin: 0)));

        var state = (await _state.GetDeviceByIdAsync(device.Id))!.ComputeDeviceState()!;

        Assert.True(state.On);
        Assert.Equal(42, state.Brightness);
        Assert.Equal(new DeviceColor(1, 2, 3), state.Color);
        Assert.Equal("AWS IoT API", state.Source);
    }

    [Fact]
    public async Task NewerSourceWinsWhenMultipleApisReport()
    {
        var device = await AddLightAsync();

        await _state.UpdateDeviceAsync(device.Sku, device.Id, d =>
        {
            d.SetIotDeviceStatus(new LanDeviceStatus(true, 10, new DeviceColor(1, 1, 1), 0));
            d.SetLanDeviceStatus(new LanDeviceStatus(true, 20, new DeviceColor(2, 2, 2), 0));
        });

        Assert.Equal("LAN API", (await _state.GetDeviceByIdAsync(device.Id))!.ComputeDeviceState()!.Source);
    }
}

public class MqttRouterTests
{
    [Fact]
    public async Task MatchesLiteralAndParameterSegments()
    {
        var router = new MqttRouter();
        string? captured = null;

        router.Add("gv2mqtt/light/:id/command", (ctx, _) =>
        {
            captured = ctx.Param("id");
            return Task.CompletedTask;
        });

        Assert.True(await router.DispatchAsync("gv2mqtt/light/ABC123/command", "{}", CancellationToken.None));
        Assert.Equal("ABC123", captured);
    }

    [Fact]
    public async Task DoesNotMatchDifferentSegmentCounts()
    {
        var router = new MqttRouter();
        router.Add("gv2mqtt/light/:id/command", (_, _) => Task.CompletedTask);

        Assert.False(await router.DispatchAsync("gv2mqtt/light/ABC123/command/0", "{}", CancellationToken.None));
    }

    [Fact]
    public async Task DistinguishesRoutesThatDifferOnlyByLiteral()
    {
        var router = new MqttRouter();
        var hit = "";

        router.Add("gv2mqtt/light/:id/command", (_, _) => { hit = "light"; return Task.CompletedTask; });
        router.Add("gv2mqtt/humidifier/:id/set-mode", (_, _) => { hit = "humidifier"; return Task.CompletedTask; });

        await router.DispatchAsync("gv2mqtt/humidifier/X/set-mode", "Auto", CancellationToken.None);

        Assert.Equal("humidifier", hit);
    }

    [Fact]
    public void RewritesParametersAsSingleLevelWildcards()
    {
        var router = new MqttRouter();
        router.Add("gv2mqtt/number/:id/command/:mode_name/:work_mode", (_, _) => Task.CompletedTask);

        Assert.Equal(["gv2mqtt/number/+/command/+/+"], router.SubscriptionFilters);
    }
}

public class IotPacketTests
{
    [Fact]
    public void ReadsSkuAndDeviceFromTheTopLevel()
    {
        var packet = Iot.IotClient.ParsePacket("""
            {"sku":"H6072","device":"AA:BB","state":{"onOff":1,"brightness":50}}
            """);

        Assert.NotNull(packet);
        Assert.Equal("H6072", packet.Sku);
        Assert.True(packet.OnOff);
        Assert.Equal<byte?>(50, packet.Brightness);
    }

    /// <summary>Some devices report their identity inside <c>state</c> instead.</summary>
    [Fact]
    public void FallsBackToIdentityInsideState()
    {
        var packet = Iot.IotClient.ParsePacket("""
            {"state":{"sku":"H7160","device":"CC:DD","colorTemInKelvin":4000}}
            """);

        Assert.NotNull(packet);
        Assert.Equal("H7160", packet.Sku);
        Assert.Equal("CC:DD", packet.DeviceId);
        Assert.Equal(4000, packet.ColorTemperatureKelvin);
    }

    [Fact]
    public void IgnoresPacketsWithNoIdentity()
    {
        Assert.Null(Iot.IotClient.ParsePacket("""{"state":{"brightness":10}}"""));
    }

    [Fact]
    public void DecodesBase64CommandFrames()
    {
        var frame = Convert.ToBase64String(Ble.PacketManager.Encode("H7160", new Ble.SetHumidifierMode(1, 0x20)));

        var packet = Iot.IotClient.ParsePacket($$$"""
            {"sku":"H7160","device":"AA","state":{},"op":{"command":["{{{frame}}}"]}}
            """);

        Assert.NotNull(packet);
        Assert.Equal(new Ble.SetHumidifierMode(1, 0x20), Ble.PacketManager.Decode("H7160", packet.Commands.Single()));
    }

    [Fact]
    public void SkipsUndecodableCommandFrames()
    {
        var packet = Iot.IotClient.ParsePacket("""
            {"sku":"H7160","device":"AA","state":{},"op":{"command":["not base64!!"]}}
            """);

        Assert.NotNull(packet);
        Assert.Empty(packet.Commands);
    }
}
