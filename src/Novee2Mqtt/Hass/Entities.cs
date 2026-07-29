using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Novee2Mqtt.Core;
using Novee2Mqtt.Devices;
using Novee2Mqtt.Platform;
using Microsoft.Extensions.Logging;

namespace Novee2Mqtt.Hass;

/// <summary>
/// One Home Assistant entity: the discovery payload to advertise it, and how to
/// publish its current state.
/// </summary>
public sealed class HassEntity
{
    public required string Integration { get; init; }
    public required string UniqueId { get; init; }
    public required JsonObject Config { get; init; }

    /// <summary>Null for entities that have no state, such as buttons and scenes.</summary>
    public Func<HassClient, CancellationToken, Task>? PublishState { get; init; }

    public string DiscoveryTopic(string prefix) => $"{prefix}/{Integration}/{UniqueId}/config";

    public Task NotifyStateAsync(HassClient client, CancellationToken cancellationToken)
        => PublishState?.Invoke(client, cancellationToken) ?? Task.CompletedTask;
}

/// <summary>
/// Factories for every entity type the bridge advertises.
/// </summary>
/// <remarks>
/// Topics and unique ids match the govee2mqtt bridge byte for byte, so an
/// existing Home Assistant install keeps its entities, history and automations.
/// Treat any edit here as a breaking change.
/// </remarks>
public static class Entities
{
    // ------------------------------------------------------------ shared parts

    /// <summary>The <c>device</c> block, which is what ties entities together in Home Assistant's registry.</summary>
    private static JsonObject DeviceBlock(Device device)
    {
        var json = new JsonObject
        {
            ["name"] = device.Name(),
            ["manufacturer"] = "Govee",
            ["model"] = device.Sku,
            ["via_device"] = Topics.ServiceIdentifier,
            ["identifiers"] = new JsonArray($"gv2mqtt-{Topics.TopicSafeId(device)}"),
        };

        if (device.RoomName() is { } room)
        {
            json["suggested_area"] = room;
        }
        return json;
    }

    /// <summary>The bridge's own device, which owns the global entities.</summary>
    private static JsonObject ServiceBlock() => new()
    {
        ["name"] = "Novee2Mqtt",
        ["manufacturer"] = "Novee2Mqtt",
        ["model"] = "Govee bridge",
        ["sw_version"] = VersionInfo.Version,
        ["identifiers"] = new JsonArray(Topics.ServiceIdentifier),
    };

    private static JsonObject Base(
        string uniqueId,
        JsonObject device,
        string? name = null,
        string? deviceClass = null,
        string? entityCategory = null,
        string? icon = null)
    {
        var json = new JsonObject
        {
            ["availability_topic"] = Topics.Availability,
            ["name"] = name,
            ["origin"] = new JsonObject
            {
                ["name"] = Topics.ServiceIdentifier,
                ["sw_version"] = VersionInfo.Version,
                ["url"] = "https://github.com/borexola/Novee2Mqtt",
            },
            ["device"] = device,
            ["unique_id"] = uniqueId,
        };

        if (deviceClass is not null) json["device_class"] = deviceClass;
        if (entityCategory is not null) json["entity_category"] = entityCategory;
        if (icon is not null) json["icon"] = icon;

        return json;
    }

    /// <summary>Re-reads the device before publishing, so state is never stale by a whole cycle.</summary>
    private static Func<HassClient, CancellationToken, Task> WithDevice(
        ServiceState state,
        string deviceId,
        Func<Device, HassClient, CancellationToken, Task> publish)
        => async (client, cancellationToken) =>
        {
            if (await state.GetDeviceByIdAsync(deviceId).ConfigureAwait(false) is { } device)
            {
                await publish(device, client, cancellationToken).ConfigureAwait(false);
            }
        };

    private static JsonArray ToJsonArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }
        return array;
    }

    // ------------------------------------------------------------------- light

    /// <summary>
    /// A light using Home Assistant's MQTT JSON schema. Devices with addressable
    /// segments get one of these per segment; those are optimistic, because Govee
    /// never reports per-segment state back.
    /// </summary>
    public static async Task<HassEntity> LightAsync(
        Device device,
        ServiceState state,
        long? segment,
        CancellationToken cancellationToken = default)
    {
        var quirk = device.ResolveQuirk();
        var deviceType = device.GetDeviceType();
        var isSegment = segment is not null;
        var id = Topics.TopicSafeId(device);

        var uniqueId = isSegment ? $"gv2mqtt-{id}-{segment}" : $"gv2mqtt-{id}";
        var stateTopic = isSegment ? Topics.LightSegmentState(device, segment!.Value) : Topics.LightState(device);

        var name = segment is { } n
            ? $"Segment {n + 1:000}"
            : deviceType == DeviceType.Humidifier ? "Night Light" : null;

        var config = Base(uniqueId, DeviceBlock(device), name);
        config["schema"] = "json";
        config["command_topic"] = isSegment
            ? Topics.LightSegmentCommand(device, segment!.Value)
            : Topics.LightCommand(device);
        config["state_topic"] = stateTopic;
        config["optimistic"] = isSegment;

        var colorModes = new JsonArray();
        if (isSegment || device.SupportsRgb())
        {
            colorModes.Add("rgb");
        }

        if (!isSegment && device.GetColorTemperatureRange() is { } range)
        {
            colorModes.Add("color_temp");
            // Converting kelvin to mireds inverts the ordering.
            config["min_mireds"] = Topics.KelvinToMired((int)range.Max);
            config["max_mireds"] = Topics.KelvinToMired((int)range.Min);
        }
        config["supported_color_modes"] = colorModes;

        config["brightness"] = isSegment
            || (quirk?.SupportsBrightness ?? false)
            || (device.HttpDeviceInfo?.SupportsBrightness() ?? false);
        config["brightness_scale"] = 100;
        config["effect"] = true;

        if (!isSegment)
        {
            var effects = await state.DeviceListScenesAsync(device, cancellationToken).ConfigureAwait(false);
            if (effects.Count > 0)
            {
                config["effect_list"] = ToJsonArray(effects);
            }

            // Only the primary light carries the device icon.
            if (deviceType == DeviceType.Light && quirk?.Icon is { } icon)
            {
                config["icon"] = icon;
            }
        }

        config["payload_available"] = "online";

        return new HassEntity
        {
            Integration = "light",
            UniqueId = uniqueId,
            Config = config,
            // Segment lights have no readable state; publishing one would fight
            // with whatever the user last set.
            PublishState = isSegment ? null : WithDevice(state, device.Id, (d, client, ct) =>
                client.PublishAsync(stateTopic, LightState(d.ComputeDeviceState()), ct)),
        };
    }

    private static JsonObject LightState(DeviceState? state)
    {
        if (state is null || state.LightOn != true)
        {
            return new JsonObject { ["state"] = "OFF" };
        }

        var payload = new JsonObject
        {
            ["state"] = "ON",
            ["brightness"] = state.Brightness,
            ["effect"] = state.Scene,
        };

        if (state.Kelvin == 0)
        {
            payload["color_mode"] = "rgb";
            payload["color"] = new JsonObject
            {
                ["r"] = state.Color.R,
                ["g"] = state.Color.G,
                ["b"] = state.Color.B,
            };
        }
        else
        {
            payload["color_mode"] = "color_temp";
            payload["color_temp"] = Topics.KelvinToMired(state.Kelvin);
        }

        return payload;
    }

    // ------------------------------------------------------------------ switch

    /// <summary>
    /// A switch backed by one of the device's toggle capabilities.
    /// </summary>
    /// <remarks>
    /// Govee returns no meaningful state for toggles other than <c>powerSwitch</c>
    /// (see <see href="https://developer.govee.com/discuss/6596e84c901fb900312d5968"/>),
    /// so those show as unknown in Home Assistant while still offering on and off
    /// buttons that reach the device.
    /// </remarks>
    public static HassEntity Switch(Device device, ServiceState state, ILogger log, DeviceCapability capability)
    {
        var instance = capability.Instance;
        var uniqueId = $"gv2mqtt-{Topics.TopicSafeId(device)}-{instance}";
        var stateTopic = Topics.SwitchInstanceState(device, instance);

        var config = Base(uniqueId, DeviceBlock(device), Topics.CamelCaseToSpaceSeparated(instance));
        config["command_topic"] = Topics.SwitchInstanceCommand(device, instance);
        config["state_topic"] = stateTopic;

        return new HassEntity
        {
            Integration = "switch",
            UniqueId = uniqueId,
            Config = config,
            PublishState = WithDevice(state, device.Id, async (d, client, ct) =>
            {
                if (instance == "powerSwitch")
                {
                    if (d.ComputeDeviceState() is { } deviceState)
                    {
                        await client.PublishAsync(stateTopic, deviceState.On ? "ON" : "OFF", ct).ConfigureAwait(false);
                    }
                    return;
                }

                if (d.GetStateCapabilityByInstance(instance) is not { } reported)
                {
                    return;
                }

                var value = reported.State.Pointer("/value");
                if (value.AsInt64() is { } number)
                {
                    await client.PublishAsync(stateTopic, number != 0 ? "ON" : "OFF", ct).ConfigureAwait(false);
                }
                else if (value.AsString() != "")
                {
                    log.LogWarning("Unhandled switch state for {Device} {Instance}: {State}",
                        d, instance, reported.State?.ToJsonString());
                }
            }),
        };
    }

    // --------------------------------------------------------- buttons, scenes

    private static HassEntity Button(string uniqueId, JsonObject config, string commandTopic, string? payloadPress = null)
    {
        config["command_topic"] = commandTopic;
        if (payloadPress is not null)
        {
            config["payload_press"] = payloadPress;
        }
        return new HassEntity { Integration = "button", UniqueId = uniqueId, Config = config };
    }

    /// <summary>A button on the bridge's own device, such as "Purge Caches".</summary>
    public static HassEntity GlobalButton(string name, string topic)
    {
        var uniqueId = $"global-{Topics.TopicSafeString(name)}";
        return Button(uniqueId, Base(uniqueId, ServiceBlock(), name), topic);
    }

    /// <summary>Activates a specific work-mode preset in one press.</summary>
    public static HassEntity WorkModePresetButton(Device device, string name, string modeName, long modeNumber, long value)
    {
        var uniqueId = $"gv2mqtt-{Topics.TopicSafeId(device)}-preset-{Topics.TopicSafeString(modeName)}-{modeNumber}-{value}";
        return Button(
            uniqueId,
            Base(uniqueId, DeviceBlock(device), name),
            Topics.NumberCommand(device, modeName, modeNumber.ToString(CultureInfo.InvariantCulture)),
            value.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Forces a Platform API state read, for diagnosing a device stuck on stale state.</summary>
    public static HassEntity RequestPlatformDataButton(Device device)
    {
        var uniqueId = $"gv2mqtt-{Topics.TopicSafeId(device)}-request-platform-data";
        return Button(
            uniqueId,
            Base(uniqueId, DeviceBlock(device), "Request Platform API State", entityCategory: "diagnostic"),
            Topics.RequestPlatformData(device));
    }

    /// <summary>A Home Assistant scene that triggers a Govee Tap-to-Run shortcut.</summary>
    public static HassEntity OneClickScene(string name)
    {
        var uniqueId = $"gv2mqtt-one-click-{UuidV5.CreateSimple(UuidV5.NamespaceDns, name)}";
        var config = Base(uniqueId, ServiceBlock(), name);
        config["command_topic"] = Topics.OneClick;
        config["payload_on"] = name;

        return new HassEntity { Integration = "scene", UniqueId = uniqueId, Config = config };
    }

    // ----------------------------------------------------------------- sensors

    /// <summary>A never-changing diagnostic on the bridge device, such as its version.</summary>
    public static HassEntity FixedDiagnostic(string name, string value)
    {
        var uniqueId = $"global-{Topics.TopicSafeString(name)}";
        var stateTopic = Topics.SensorState(uniqueId);

        var config = Base(uniqueId, ServiceBlock(), name, entityCategory: "diagnostic");
        config["state_topic"] = stateTopic;

        return new HassEntity
        {
            Integration = "sensor",
            UniqueId = uniqueId,
            Config = config,
            PublishState = (client, ct) => client.PublishAsync(stateTopic, value, ct),
        };
    }

    /// <summary>
    /// Exposes a Govee <c>property</c> capability as a sensor. Temperature and
    /// humidity get unit conversion; anything else is published verbatim.
    /// </summary>
    public static HassEntity CapabilitySensor(Device device, ServiceState state, ILogger log, DeviceCapability capability)
    {
        var instance = capability.Instance;
        var uniqueId = $"sensor-{Topics.TopicSafeId(device)}-{Topics.TopicSafeString(instance)}";
        var stateTopic = Topics.SensorState(uniqueId);

        var (unit, deviceClass, stateClass, name) = instance switch
        {
            "sensorTemperature" => (state.TemperatureScale.UnitOfMeasurement(),
                TemperatureConstants.DeviceClassTemperature, "measurement", "Temperature"),
            "sensorHumidity" => ("%", "humidity", "measurement", "Humidity"),
            "online" => (null, null, null, "Connected to Govee Cloud"),
            _ => (null, null, null, instance),
        };

        var config = Base(uniqueId, DeviceBlock(device), name, deviceClass, entityCategory: "diagnostic");
        config["state_topic"] = stateTopic;
        if (stateClass is not null) config["state_class"] = stateClass;
        if (unit is not null) config["unit_of_measurement"] = unit;

        return new HassEntity
        {
            Integration = "sensor",
            UniqueId = uniqueId,
            Config = config,
            PublishState = WithDevice(state, device.Id, (d, client, ct) =>
            {
                if (d.GetStateCapabilityByInstance(instance) is not { } reported)
                {
                    log.LogTrace("No state for {Device} {Instance}", d, instance);
                    return Task.CompletedTask;
                }

                var quirk = d.ResolveQuirk();
                var raw = reported.State.Pointer("/value");

                var value = instance switch
                {
                    // Govee reports these in Fahrenheit unless a quirk says otherwise.
                    "sensorTemperature" => raw.AsDouble() is { } t
                        ? new TemperatureValue(t, quirk?.PlatformTemperatureSensorUnits ?? TemperatureUnits.Fahrenheit)
                            .As(state.TemperatureScale).Value.ToString("F2", CultureInfo.InvariantCulture)
                        : "",
                    "sensorHumidity" => raw.AsDouble() is { } h
                        ? (quirk?.PlatformHumiditySensorUnits ?? HumidityUnits.RelativePercent)
                            .ToRelativePercent(h).ToString("F2", CultureInfo.InvariantCulture)
                        : "",
                    _ => reported.State?.ToJsonString() ?? "",
                };

                return client.PublishAsync(stateTopic, value, ct);
            }),
        };
    }

    /// <summary>
    /// Whether we are still hearing from a device, with the raw per-source state
    /// attached as attributes. The first thing to look at when a device goes quiet.
    /// </summary>
    public static HassEntity StatusDiagnostic(Device device, ServiceState state)
    {
        var uniqueId = $"sensor-{Topics.TopicSafeId(device)}-gv2mqtt-status";
        var stateTopic = Topics.SensorState(uniqueId);
        var attributesTopic = Topics.SensorAttributes(uniqueId);

        var config = Base(uniqueId, DeviceBlock(device), "Status", entityCategory: "diagnostic");
        config["state_topic"] = stateTopic;
        config["json_attributes_topic"] = attributesTopic;

        return new HassEntity
        {
            Integration = "sensor",
            UniqueId = uniqueId,
            Config = config,
            PublishState = WithDevice(state, device.Id, async (d, client, ct) =>
            {
                var deviceState = d.ComputeDeviceState();

                // Older than a poll interval plus slack means we have lost touch.
                var threshold = Device.PollInterval + TimeSpan.FromSeconds(30);
                var summary = deviceState is null
                    ? "Unknown"
                    : DateTimeOffset.UtcNow - deviceState.Updated > threshold ? "Missing" : "Available";

                await client.PublishAsync(stateTopic, summary, ct).ConfigureAwait(false);

                await client.PublishAsync(attributesTopic, new JsonObject
                {
                    ["iot"] = ToNode(d.ComputeIotDeviceState()),
                    ["lan"] = ToNode(d.ComputeLanDeviceState()),
                    ["http"] = ToNode(d.ComputeHttpDeviceState()),
                    ["platform_metadata"] = ToNode(d.HttpDeviceInfo),
                    ["platform_state"] = ToNode(d.HttpDeviceState),
                    ["overall"] = ToNode(deviceState),
                }, ct).ConfigureAwait(false);
            }),
        };

        static JsonNode? ToNode<T>(T? value)
            => value is null ? null : JsonSerializer.SerializeToNode(value, Json.Options);
    }

    // ----------------------------------------------------------------- numbers

    /// <summary>
    /// A slider for a work mode that accepts a contiguous range of values, such as
    /// a humidifier's mist level or a heater's gear setting.
    /// </summary>
    public static HassEntity WorkModeNumber(
        Device device,
        ServiceState state,
        ILogger log,
        string label,
        string modeName,
        JsonNode? workMode,
        ValueRange? range)
    {
        var uniqueId = $"gv2mqtt-{Topics.TopicSafeId(device)}-{Topics.TopicSafeString(modeName)}-number";
        var stateTopic = Topics.NumberState(device, modeName);
        var modeNumber = workMode.AsInt64()?.ToString(CultureInfo.InvariantCulture) ?? "work-mode-was-not-int";
        var mode = workMode?.DeepClone();

        var config = Base(uniqueId, DeviceBlock(device), label);
        config["command_topic"] = Topics.NumberCommand(device, modeName, modeNumber);
        config["state_topic"] = stateTopic;
        config["min"] = range?.Start ?? 0;
        config["max"] = range is { } r ? Math.Max(0, r.End - 1) : 255;
        config["step"] = 1;

        return new HassEntity
        {
            Integration = "number",
            UniqueId = uniqueId,
            Config = config,
            PublishState = WithDevice(state, device.Id, (d, client, ct) =>
            {
                // Only report a parameter while the device is actually in this
                // mode; otherwise the value belongs to a different one.
                if (d.GetStateCapabilityByInstance("workMode") is { } capability
                    && Json.JsonEquals(capability.State.Pointer("/value/workMode"), mode)
                    && capability.State.Pointer("/value/modeValue").AsInt64() is { } modeValue)
                {
                    return client.PublishAsync(stateTopic, modeValue.ToString(CultureInfo.InvariantCulture), ct);
                }

                // Devices we track over IoT report their mode parameters that way.
                if (mode.AsInt64() is { } number
                    && d.HumidifierParamByMode.TryGetValue((byte)number, out var param))
                {
                    return client.PublishAsync(stateTopic, param.ToString(CultureInfo.InvariantCulture), ct);
                }

                log.LogDebug("Don't know how to report state for {Device} workMode {Mode}", d, modeName);
                return Task.CompletedTask;
            }),
        };
    }

    /// <summary>The target temperature of a heater or kettle, in the user's preferred scale.</summary>
    public static HassEntity TargetTemperature(Device device, ServiceState state, ILogger log, DeviceCapability capability)
    {
        var instance = capability.Instance;
        var scale = state.TemperatureScale;
        var constraints = TemperatureConstraints.Parse(capability).As(scale.ToUnits());

        var uniqueId = $"{Topics.TopicSafeId(device)}-{Topics.TopicSafeString(instance)}";
        var stateTopic = Topics.AdviseSetTemperature(device);

        var config = Base(uniqueId, DeviceBlock(device), "Target Temperature",
            TemperatureConstants.DeviceClassTemperature, icon: "mdi:thermometer");

        // The units go in the command topic so the handler can interpret whatever
        // Home Assistant sends without re-reading configuration.
        config["command_topic"] = Topics.SetTemperature(device, instance, scale == TemperatureScale.Celsius ? "C" : "F");
        config["state_topic"] = stateTopic;
        config["min"] = Math.Floor(constraints.Min.Value);
        config["max"] = Math.Ceiling(constraints.Max.Value);
        config["step"] = 1;
        config["unit_of_measurement"] = scale.UnitOfMeasurement();

        return new HassEntity
        {
            Integration = "number",
            UniqueId = uniqueId,
            Config = config,
            PublishState = WithDevice(state, device.Id, (d, client, ct) =>
            {
                if (d.GetStateCapabilityByInstance(instance) is not { } reported)
                {
                    return Task.CompletedTask;
                }

                var units = TemperatureUnits.Celsius;
                if (TemperatureExtensions.TryParseScale(reported.State.Pointer("/value/unit").AsString(), out var reportedScale))
                {
                    units = reportedScale.ToUnits();
                }
                else if (d.ResolveQuirk()?.PlatformTemperatureSensorUnits is { } quirkUnits)
                {
                    units = quirkUnits;
                }

                var value = reported.State.Pointer("/value/targetTemperature").AsDouble() is { } target
                    ? new TemperatureValue(target, units).As(state.TemperatureScale).Value
                        .ToString("F2", CultureInfo.InvariantCulture)
                    : "";

                log.LogDebug("Reporting target temperature {Value} for {Device}", value, d);
                return client.PublishAsync(stateTopic, value, ct);
            }),
        };
    }

    // ----------------------------------------------------------------- selects

    /// <summary>Picks the device's work mode, e.g. Auto / Manual / Sleep.</summary>
    public static HassEntity WorkModeSelect(Device device, ParsedWorkMode workModes, ServiceState state)
    {
        var uniqueId = $"gv2mqtt-{Topics.TopicSafeId(device)}-workMode";
        var stateTopic = Topics.NotifyWorkMode(device);

        var config = Base(uniqueId, DeviceBlock(device), "Mode");
        config["command_topic"] = Topics.SetWorkMode(device);
        config["state_topic"] = stateTopic;
        config["options"] = ToJsonArray(workModes.GetModeNames());

        return new HassEntity
        {
            Integration = "select",
            UniqueId = uniqueId,
            Config = config,
            PublishState = WithDevice(state, device.Id, (d, client, ct) => PublishWorkModeAsync(d, client, stateTopic, ct)),
        };
    }

    /// <summary>
    /// Exposes scenes as a select for devices that are not lights, which have no
    /// Home Assistant light entity to carry an effect list.
    /// </summary>
    public static async Task<HassEntity?> SceneModeSelectAsync(
        Device device,
        ServiceState state,
        CancellationToken cancellationToken = default)
    {
        var scenes = await state.DeviceListScenesAsync(device, cancellationToken).ConfigureAwait(false);
        if (scenes.Count == 0)
        {
            return null;
        }

        var uniqueId = $"gv2mqtt-{Topics.TopicSafeId(device)}-mode-scene";
        var stateTopic = Topics.NotifyModeScene(device);

        var config = Base(uniqueId, DeviceBlock(device), "Mode/Scene");
        config["command_topic"] = Topics.SetModeScene(device);
        config["state_topic"] = stateTopic;
        config["options"] = ToJsonArray(scenes);

        return new HassEntity
        {
            Integration = "select",
            UniqueId = uniqueId,
            Config = config,
            PublishState = WithDevice(state, device.Id, (d, client, ct) =>
                d.ComputeDeviceState() is { } deviceState
                    ? client.PublishAsync(stateTopic, deviceState.Scene ?? "", ct)
                    : Task.CompletedTask),
        };
    }

    // -------------------------------------------------------------- humidifier

    /// <summary>
    /// A humidifier or dehumidifier.
    /// </summary>
    /// <remarks>See <see href="https://www.home-assistant.io/integrations/humidifier.mqtt"/>.</remarks>
    public static HassEntity Humidifier(Device device, ServiceState state)
    {
        var deviceType = device.GetDeviceType();
        var isHumidifier = deviceType == DeviceType.Humidifier || deviceType == DeviceType.Dehumidifier;

        var uniqueId = $"gv2mqtt-{Topics.TopicSafeId(device)}-humidifier";
        var stateTopic = Topics.HumidifierState(device);
        var targetStateTopic = Topics.HumidifierNotifyTarget(device);
        var modeStateTopic = Topics.HumidifierNotifyMode(device);

        byte? minHumidity = null;
        byte? maxHumidity = null;
        if (device.HttpDeviceInfo?.CapabilityByInstance("humidity")?.Parameters is IntegerParameters integer
            && integer.Unit == "unit.percent")
        {
            minHumidity = (byte)Math.Clamp(integer.Range.Min, 0, 100);
            maxHumidity = (byte)Math.Clamp(integer.Range.Max, 0, 100);
        }

        var config = Base(
            uniqueId,
            DeviceBlock(device),
            // When the humidifier is the device's whole purpose, let Home Assistant
            // use the device name rather than appending a suffix.
            isHumidifier ? null : "Humidifier",
            deviceType == DeviceType.Humidifier ? "humidifier" : "dehumidifier");

        // Power is just the normal power switch, so reuse its handler.
        config["command_topic"] = Topics.SwitchInstanceCommand(device, "powerSwitch");
        config["state_topic"] = stateTopic;
        config["target_humidity_command_topic"] = Topics.HumidifierSetTarget(device);
        config["target_humidity_state_topic"] = targetStateTopic;
        config["mode_command_topic"] = Topics.HumidifierSetMode(device);
        config["mode_state_topic"] = modeStateTopic;
        // Without IoT we get no state back, so Home Assistant has to assume its
        // commands took effect.
        config["optimistic"] = !(device.IotApiSupported() && state.IotClient is not null);

        if (minHumidity is { } min) config["min_humidity"] = min;
        if (maxHumidity is { } max) config["max_humidity"] = max;

        if (ParsedWorkMode.TryWithDevice(device, out var workModes) && workModes is not null)
        {
            config["modes"] = ToJsonArray(workModes.GetModeNames());
        }

        return new HassEntity
        {
            Integration = "humidifier",
            UniqueId = uniqueId,
            Config = config,
            PublishState = WithDevice(state, device.Id, async (d, client, ct) =>
            {
                await client.PublishAsync(stateTopic, d.ComputeDeviceState()?.On == true ? "ON" : "OFF", ct)
                    .ConfigureAwait(false);

                var humidity = d.TargetHumidityPercent;
                if (humidity is null)
                {
                    // Home Assistant leaves the target humidity control disabled
                    // until it has seen a value, so seed one. Storing it means we
                    // only do this once.
                    humidity = minHumidity ?? 0;
                    await state.UpdateDeviceAsync(d.Sku, d.Id, x => x.SetTargetHumidity(humidity.Value))
                        .ConfigureAwait(false);
                }

                await client.PublishAsync(targetStateTopic, humidity.Value.ToString(CultureInfo.InvariantCulture), ct)
                    .ConfigureAwait(false);

                await PublishWorkModeAsync(d, client, modeStateTopic, ct).ConfigureAwait(false);
            }),
        };
    }

    /// <summary>
    /// Publishes the active work mode's name. The IoT-reported mode is fresher
    /// than the Platform API's, so it wins where we have it.
    /// </summary>
    private static Task PublishWorkModeAsync(Device device, HassClient client, string topic, CancellationToken cancellationToken)
    {
        if (!ParsedWorkMode.TryWithDevice(device, out var workModes) || workModes is null)
        {
            return Task.CompletedTask;
        }

        var value = device.HumidifierWorkMode is { } iotMode
            ? JsonValue.Create((long)iotMode)
            : device.GetStateCapabilityByInstance("workMode")?.State.Pointer("/value/workMode");

        return workModes.ModeForValue(value) is { } mode
            ? client.PublishAsync(topic, mode.Name, cancellationToken)
            : Task.CompletedTask;
    }
}
