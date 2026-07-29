using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Novee2Mqtt.Ble;
using Novee2Mqtt.Core;
using Novee2Mqtt.Devices;
using Novee2Mqtt.Platform;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Protocol;

namespace Novee2Mqtt.Hass;

public sealed class HassOptions
{
    public required string Host { get; init; }
    public int Port { get; init; } = 1883;
    public string? Username { get; init; }
    public string? Password { get; init; }
    public string DiscoveryPrefix { get; init; } = "homeassistant";
}

/// <summary>
/// The Home Assistant side of the bridge: publishes MQTT discovery configs and
/// entity state, and handles the commands Home Assistant sends back.
/// </summary>
public sealed class HassClient : IAsyncDisposable
{
    /// <summary>
    /// Home Assistant needs a moment to settle after it announces itself or after
    /// we reconnect; registering immediately gets entities dropped.
    /// </summary>
    private static readonly TimeSpan RegisterDelay = TimeSpan.FromSeconds(15);

    private readonly ILogger<HassClient> _log;
    private readonly MqttConnection _connection;
    private readonly HassOptions _options;
    private readonly ServiceState _state;
    private readonly EntityEnumerator _enumerator;
    private readonly GoveeCache _cache;
    private readonly MqttRouter _router = new();

    private HassClient(
        ILogger<HassClient> log,
        MqttConnection connection,
        HassOptions options,
        ServiceState state,
        EntityEnumerator enumerator,
        GoveeCache cache)
    {
        _log = log;
        _connection = connection;
        _options = options;
        _state = state;
        _enumerator = enumerator;
        _cache = cache;

        BuildRoutes();

        _connection.OnMessage = (topic, payload, ct) => DispatchAsync(topic, payload, ct);
        _connection.OnReconnected = async ct =>
        {
            await _connection.SubscribeAsync(_router.SubscriptionFilters, ct).ConfigureAwait(false);
            // Give Home Assistant the same settling time as at startup before
            // re-advertising everything.
            await Task.Delay(RegisterDelay, ct).ConfigureAwait(false);
            await SafeRegisterAsync(ct).ConfigureAwait(false);
        };
    }

    public static HassClient Create(
        ILogger<HassClient> log,
        HassOptions options,
        ServiceState state,
        EntityEnumerator enumerator,
        GoveeCache cache)
    {
        if (options.Username is null != (options.Password is null))
        {
            log.LogError("MQTT username and password either both need to be set, or both need to be unset");
        }

        var builder = new MqttClientOptionsBuilder()
            .WithClientId($"novee2mqtt/{Guid.NewGuid():N}")
            .WithTcpServer(options.Host, options.Port)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(120))
            .WithCleanSession()
            // A last will is what marks every entity unavailable if we die.
            .WithWillTopic(Topics.Availability)
            .WithWillPayload(Encoding.UTF8.GetBytes("offline"))
            .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
            .WithWillRetain(false);

        if (options.Username is not null)
        {
            builder = builder.WithCredentials(options.Username, options.Password ?? "");
        }

        var connection = new MqttConnection(log, $"MQTT broker {options.Host}:{options.Port}", builder.Build());
        return new HassClient(log, connection, options, state, enumerator, cache);
    }

    /// <summary>Whether the broker connection is currently up.</summary>
    public bool IsConnected => _connection.IsConnected;

    public Task PublishAsync(string topic, string payload, CancellationToken cancellationToken = default)
        => _connection.PublishAsync(topic, payload, cancellationToken);

    public Task PublishAsync(string topic, JsonNode payload, CancellationToken cancellationToken = default)
        => _connection.PublishAsync(topic, payload.ToJsonString(), cancellationToken);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _connection.ConnectAsync(TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
            _ = Task.Run(() => InitialRegistrationAsync(_connection.ShutdownToken), CancellationToken.None);
        }
        catch (GoveeException ex)
        {
            // A broker that is down at startup must not kill the bridge: the
            // supervisor keeps retrying, and OnReconnected registers everything
            // once it comes up. mqtt:need means this mostly covers broker restarts.
            _log.LogError("{Message}; will keep retrying in the background", ex.Message);
        }

        _connection.StartSupervisor();
    }

    private async Task InitialRegistrationAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Give LAN discovery a chance to populate state before we tell Home
            // Assistant what exists.
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            await _connection.SubscribeAsync(_router.SubscriptionFilters, cancellationToken).ConfigureAwait(false);
            await Task.Delay(RegisterDelay, cancellationToken).ConfigureAwait(false);
            await SafeRegisterAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutting down before registration completed.
        }
        catch (Exception ex)
        {
            _log.LogError("Initial registration failed: {Message}", ex.Message);
        }
    }

    private async Task SafeRegisterAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RegisterWithHassAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError("register_with_hass failed: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Publishes every entity's discovery config, then marks the bridge online
    /// and reports initial state. Configs are spaced out because Home Assistant
    /// processes them serially and drops them under load.
    /// </summary>
    public async Task RegisterWithHassAsync(CancellationToken cancellationToken = default)
    {
        var entities = await _enumerator.EnumerateAllAsync(cancellationToken).ConfigureAwait(false);

        _log.LogTrace("register_with_hass: registering {Count} entities", entities.Count);

        foreach (var entity in entities)
        {
            await PublishAsync(entity.DiscoveryTopic(_state.HassDiscoveryPrefix), entity.Config, cancellationToken)
                .ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
        }

        var settleDelay = TimeSpan.FromMilliseconds(10 * entities.Count);
        _log.LogInformation("Waiting {Delay} for Home Assistant to settle on {Count} entity configs",
            settleDelay, entities.Count);
        await Task.Delay(settleDelay, cancellationToken).ConfigureAwait(false);

        await PublishAsync(Topics.Availability, "online", cancellationToken).ConfigureAwait(false);

        await NotifyAllAsync(entities, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Republishes just one device's entity states, after a state change.</summary>
    public async Task AdviseHassOfDeviceStateAsync(Device device, CancellationToken cancellationToken = default)
    {
        if (!_connection.IsConnected)
        {
            return;
        }

        var entities = await _enumerator.EnumerateForDeviceAsync(device, cancellationToken).ConfigureAwait(false);
        await NotifyAllAsync(entities, cancellationToken).ConfigureAwait(false);
    }

    private async Task NotifyAllAsync(List<HassEntity> entities, CancellationToken cancellationToken)
    {
        foreach (var entity in entities)
        {
            try
            {
                await entity.NotifyStateAsync(this, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError("Publishing state for {UniqueId}: {Message}", entity.UniqueId, ex.Message);
            }
        }
    }

    // ---------------------------------------------------------------- routing

    private async Task DispatchAsync(string topic, string payload, CancellationToken cancellationToken)
    {
        if (!await _router.DispatchAsync(topic, payload, cancellationToken).ConfigureAwait(false))
        {
            _log.LogTrace("No route for {Topic}", topic);
        }
    }

    private void BuildRoutes()
    {
        _router.Add($"{_options.DiscoveryPrefix}/status", OnHomeAssistantStatusAsync);
        _router.Add("gv2mqtt/light/:id/command", OnLightCommandAsync);
        _router.Add("gv2mqtt/light/:id/command/:segment", OnLightSegmentCommandAsync);
        _router.Add("gv2mqtt/switch/:id/command/:instance", OnSwitchCommandAsync);
        _router.Add(Topics.OneClick, OnOneClickAsync);
        _router.Add(Topics.PurgeCache, OnPurgeCachesAsync);
        _router.Add("gv2mqtt/:id/request-platform-data", OnRequestPlatformDataAsync);
        _router.Add("gv2mqtt/number/:id/command/:mode_name/:work_mode", OnNumberCommandAsync);
        _router.Add("gv2mqtt/humidifier/:id/set-mode", OnSetWorkModeAsync);
        _router.Add("gv2mqtt/:id/set-work-mode", OnSetWorkModeAsync);
        _router.Add("gv2mqtt/humidifier/:id/set-target", OnHumidifierSetTargetAsync);
        _router.Add("gv2mqtt/:id/set-temperature/:instance/:units", OnSetTemperatureAsync);
        _router.Add("gv2mqtt/:id/set-mode-scene", OnSetModeSceneAsync);
    }

    /// <summary>Home Assistant restarted, so everything must be advertised again.</summary>
    private async Task OnHomeAssistantStatusAsync(RouteContext context, CancellationToken cancellationToken)
    {
        _log.LogInformation(
            "Home Assistant status changed: {Status}, waiting {Delay} before re-registering entities",
            context.Payload, RegisterDelay);

        await Task.Delay(RegisterDelay, cancellationToken).ConfigureAwait(false);
        await RegisterWithHassAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task OnLightCommandAsync(RouteContext context, CancellationToken cancellationToken)
    {
        using var lease = await _state.ResolveDeviceForControlAsync(context.Param("id"), cancellationToken).ConfigureAwait(false);
        var device = lease.Device;

        var command = Json.Deserialize<HassLightCommand>(context.Payload);
        _log.LogInformation("Command for {Device}: {Payload}", device, context.Payload);

        var isLight = device.GetDeviceType() == DeviceType.Light;

        if (command.State == "OFF")
        {
            // Devices whose primary function is not lighting have no separate
            // light power; zero brightness is the closest equivalent.
            await (isLight
                ? _state.DeviceLightPowerOnAsync(device, false, cancellationToken)
                : _state.DeviceSetBrightnessAsync(device, 0, cancellationToken)).ConfigureAwait(false);
            return;
        }

        var powerOn = true;

        if (command.Brightness is { } brightness)
        {
            await _state.DeviceSetBrightnessAsync(device, brightness, cancellationToken).ConfigureAwait(false);
            powerOn = false;
        }

        if (!string.IsNullOrEmpty(command.Effect))
        {
            // Colour properties conflict with a scene, so ignore them when one is
            // requested. Brightness, applied above, is fine.
            await _state.DeviceSetSceneAsync(device, command.Effect, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (command.Color is { } color)
        {
            await _state.DeviceSetColorRgbAsync(device, new DeviceColor(color.R, color.G, color.B), cancellationToken)
                .ConfigureAwait(false);
            powerOn = false;
        }

        if (command.ColorTemp is { } colorTemp)
        {
            await _state.DeviceSetColorTemperatureAsync(device, Topics.MiredToKelvin(colorTemp), cancellationToken)
                .ConfigureAwait(false);
            powerOn = false;
        }

        if (!powerOn)
        {
            return;
        }

        if (isLight)
        {
            await _state.DeviceLightPowerOnAsync(device, true, cancellationToken).ConfigureAwait(false);
        }
        else if (command.Brightness is null)
        {
            // No guaranteed way to power on the light portion of a non-light
            // device other than giving it a brightness.
            await _state.DeviceSetBrightnessAsync(device, 100, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task OnLightSegmentCommandAsync(RouteContext context, CancellationToken cancellationToken)
    {
        using var lease = await _state.ResolveDeviceForControlAsync(context.Param("id"), cancellationToken).ConfigureAwait(false);
        var device = lease.Device;

        if (!long.TryParse(context.Param("segment"), out var segment))
        {
            throw new GoveeException($"invalid segment '{context.Param("segment")}'");
        }

        var command = Json.Deserialize<HassLightCommand>(context.Payload);
        _log.LogInformation("Command for {Device} segment {Segment}: {Payload}", device, segment, context.Payload);

        if (_state.PlatformClient is not { } client || device.HttpDeviceInfo is not { } info)
        {
            throw new GoveeException($"set segments for {device}: the Platform API is not available");
        }

        if (command.Brightness is { } brightness)
        {
            await client.SetSegmentBrightnessAsync(info, segment, brightness, cancellationToken).ConfigureAwait(false);
        }

        // Deliberately nothing for state == "OFF": setting segment brightness to
        // zero powers the whole device on, so turning off an area would flash the
        // lights back on.

        if (command.Color is { } color)
        {
            await client.SetSegmentRgbAsync(info, segment, new DeviceColor(color.R, color.G, color.B), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task OnSwitchCommandAsync(RouteContext context, CancellationToken cancellationToken)
    {
        var id = context.Param("id");
        var instance = context.Param("instance");
        _log.LogInformation("{Instance} for {Id}: {Command}", instance, id, context.Payload);

        using var lease = await _state.ResolveDeviceForControlAsync(id, cancellationToken).ConfigureAwait(false);
        var device = lease.Device;

        var on = context.Payload switch
        {
            "ON" or "on" => true,
            "OFF" or "off" => false,
            _ => throw new GoveeException($"invalid command '{context.Payload}' for {id}"),
        };

        if (instance == "powerSwitch")
        {
            await _state.DevicePowerOnAsync(device, on, cancellationToken).ConfigureAwait(false);
        }
        else if (_state.PlatformClient is { } client && device.HttpDeviceInfo is { } info)
        {
            await client.SetToggleStateAsync(info, instance, on, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            throw new GoveeException($"Don't know how to {context.Payload} for {id} {instance}");
        }
    }

    private async Task OnOneClickAsync(RouteContext context, CancellationToken cancellationToken)
    {
        var name = context.Payload;
        _log.LogInformation("Activating one-click: {Name}", name);

        var undoc = _state.UndocClient ?? throw new GoveeException("Undoc API client is not available");
        var iot = _state.IotClient ?? throw new GoveeException("AWS IoT client is not available");

        var items = await undoc.ParseOneClicksAsync(cancellationToken).ConfigureAwait(false);
        var item = items.FirstOrDefault(i => i.Name == name)
            ?? throw new GoveeException($"didn't find one-click '{name}'");

        await iot.ActivateOneClickAsync(item, cancellationToken).ConfigureAwait(false);
    }

    private async Task OnPurgeCachesAsync(RouteContext context, CancellationToken cancellationToken)
    {
        _log.LogInformation("Purging caches");
        _cache.Purge();
        await RegisterWithHassAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task OnRequestPlatformDataAsync(RouteContext context, CancellationToken cancellationToken)
    {
        var device = await _state.ResolveDeviceReadOnlyAsync(context.Param("id")).ConfigureAwait(false);
        _log.LogInformation("Request Platform API State for {Device}", device);

        if (!await _state.PollPlatformApiAsync(device, cancellationToken).ConfigureAwait(false))
        {
            _log.LogWarning("Unable to poll the Platform API for {Device}", device);
        }
    }

    private async Task OnNumberCommandAsync(RouteContext context, CancellationToken cancellationToken)
    {
        var id = context.Param("id");
        var modeName = context.Param("mode_name");
        var value = ParseLong(context.Payload, $"number payload for {id} {modeName}");
        var workMode = ParseLong(context.Param("work_mode"), $"work mode for {id}");

        _log.LogInformation("{ModeName} for {Id}: {Value}", modeName, id, value);

        using var lease = await _state.ResolveDeviceForControlAsync(id, cancellationToken).ConfigureAwait(false);
        await _state.HumidifierSetParameterAsync(lease.Device, workMode, value, cancellationToken).ConfigureAwait(false);
    }

    private async Task OnSetWorkModeAsync(RouteContext context, CancellationToken cancellationToken)
    {
        var id = context.Param("id");
        var modeName = context.Payload;
        _log.LogInformation("Set work mode for {Id}: {Mode}", id, modeName);

        using var lease = await _state.ResolveDeviceForControlAsync(id, cancellationToken).ConfigureAwait(false);
        var device = lease.Device;

        var workMode = ParsedWorkMode.WithDevice(device).ModeByName(modeName)
            ?? throw new GoveeException($"mode {modeName} not found");

        var modeNumber = workMode.Value.AsInt64()
            ?? throw new GoveeException("expected workMode to be a number");

        await _state.HumidifierSetParameterAsync(device, modeNumber, workMode.DefaultValue(), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task OnHumidifierSetTargetAsync(RouteContext context, CancellationToken cancellationToken)
    {
        var id = context.Param("id");
        var percent = ParseLong(context.Payload, $"target humidity for {id}");

        _log.LogInformation("Set target humidity for {Id}: {Percent}", id, percent);

        using var lease = await _state.ResolveDeviceForControlAsync(id, cancellationToken).ConfigureAwait(false);
        var device = lease.Device;

        var useIot = device.PollableViaIot() && _state.IotClient is not null;

        if (!useIot && device.HttpDeviceInfo?.CapabilityByInstance("humidity") is { } capability)
        {
            await _state.DeviceControlAsync(device, capability, JsonValue.Create(percent), cancellationToken)
                .ConfigureAwait(false);

            // Running optimistically: remember the requested value so we have
            // something to report back. Releasing the control lease schedules a
            // poll that reconciles the device's real state.
            await _state.UpdateDeviceAsync(device.Sku, device.Id,
                d => d.SetTargetHumidity((byte)Math.Clamp(percent, 0, 255))).ConfigureAwait(false);
            return;
        }

        var autoMode = ParsedWorkMode.WithDevice(device).ModeByName("Auto")
            ?? throw new GoveeException("mode Auto not found");

        var modeNumber = autoMode.Value.AsInt64()
            ?? throw new GoveeException("expected workMode to be a number");

        var target = TargetHumidity.FromPercent((byte)Math.Clamp(percent, 0, 100));

        await _state.HumidifierSetParameterAsync(device, modeNumber, target.Raw, cancellationToken).ConfigureAwait(false);
    }

    private async Task OnSetTemperatureAsync(RouteContext context, CancellationToken cancellationToken)
    {
        var id = context.Param("id");
        _log.LogInformation("Command: set-temperature for {Id}: {Value}", id, context.Payload);

        using var lease = await _state.ResolveDeviceForControlAsync(id, cancellationToken).ConfigureAwait(false);

        var scale = TemperatureExtensions.ParseScale(context.Param("units"));
        var target = TemperatureValue.Parse(context.Payload, scale);

        await _state.DeviceSetTargetTemperatureAsync(lease.Device, context.Param("instance"), target, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task OnSetModeSceneAsync(RouteContext context, CancellationToken cancellationToken)
    {
        using var lease = await _state.ResolveDeviceForControlAsync(context.Param("id"), cancellationToken).ConfigureAwait(false);
        await _state.DeviceSetSceneAsync(lease.Device, context.Payload, cancellationToken).ConfigureAwait(false);
    }

    private static long ParseLong(string text, string what)
        => long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new GoveeException($"invalid {what}: '{text}'");

    public async ValueTask DisposeAsync()
    {
        try
        {
            await PublishAsync(Topics.Availability, "offline", CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best effort; the last will covers an ungraceful exit.
        }

        await _connection.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>The JSON light schema payload Home Assistant sends.</summary>
    private sealed class HassLightCommand
    {
        [JsonPropertyName("state")] public string State { get; set; } = "";
        [JsonPropertyName("color_temp")] public int? ColorTemp { get; set; }
        [JsonPropertyName("color")] public HassColor? Color { get; set; }
        [JsonPropertyName("effect")] public string? Effect { get; set; }
        [JsonPropertyName("brightness")] public byte? Brightness { get; set; }
    }

    private sealed class HassColor
    {
        [JsonPropertyName("r")] public byte R { get; set; }
        [JsonPropertyName("g")] public byte G { get; set; }
        [JsonPropertyName("b")] public byte B { get; set; }
    }
}
