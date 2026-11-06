using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Novee2Mqtt.Ble;
using Novee2Mqtt.Core;
using Novee2Mqtt.Hass;
using Novee2Mqtt.Iot;
using Novee2Mqtt.Lan;
using Novee2Mqtt.Platform;
using Novee2Mqtt.Undocumented;
using Microsoft.Extensions.Logging;

namespace Novee2Mqtt.Devices;

/// <summary>
/// Holds every known device plus the API clients that are configured, and owns
/// the decisions about which transport to use for a given operation. The order
/// is LAN first, then IoT, then the Platform API: lowest latency and least
/// likely to be rate limited first. Bluetooth sits ahead of the cloud paths for
/// a device the cloud cannot reach, and behind them as a fallback otherwise.
/// </summary>
public sealed class ServiceState(ILogger<ServiceState> log, SceneCatalog sceneCatalog)
{
    private readonly Dictionary<string, Device> _devices = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _devicesLock = new(1, 1);
    private readonly Dictionary<string, SemaphoreSlim> _controlLocks = new(StringComparer.Ordinal);

    public LanClient? LanClient { get; set; }
    public BleClient? BleClient { get; set; }

    /// <summary>Why Bluetooth is off, when it is; shown by the API and the panel.</summary>
    public string? BleUnavailableReason { get; set; }
    public PlatformApiClient? PlatformClient { get; set; }
    public UndocumentedApiClient? UndocClient { get; set; }
    public IotClient? IotClient { get; set; }
    public HassClient? HassClient { get; set; }

    public string HassDiscoveryPrefix { get; set; } = "homeassistant";
    public TemperatureScale TemperatureScale { get; set; } = TemperatureScale.Celsius;

    public SceneCatalog SceneCatalog => sceneCatalog;

    // ---------------------------------------------------------------- registry

    /// <summary>Mutates the named device under the registry lock, creating it if needed.</summary>
    /// <returns>True if the device was not known before this call.</returns>
    public async Task<bool> UpdateDeviceAsync(string sku, string id, Action<Device> update)
    {
        await _devicesLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var created = false;
            if (!_devices.TryGetValue(id, out var device))
            {
                device = new Device(sku, id);
                _devices[id] = device;
                created = true;
            }
            update(device);
            return created;
        }
        finally
        {
            _devicesLock.Release();
        }
    }

    public async Task<List<Device>> GetDevicesAsync()
    {
        await _devicesLock.WaitAsync().ConfigureAwait(false);
        try
        {
            return _devices.Values.Select(d => d.Snapshot()).ToList();
        }
        finally
        {
            _devicesLock.Release();
        }
    }

    public async Task<Device?> GetDeviceByIdAsync(string id)
    {
        await _devicesLock.WaitAsync().ConfigureAwait(false);
        try
        {
            return _devices.TryGetValue(id, out var device) ? device.Snapshot() : null;
        }
        finally
        {
            _devicesLock.Release();
        }
    }

    /// <summary>
    /// Finds a device by id, Govee name, computed name, topic-safe id, or IP
    /// address, ignoring case. Home Assistant addresses devices by the
    /// topic-safe id; humans use the name.
    /// </summary>
    public async Task<Device?> ResolveDeviceAsync(string label)
    {
        await _devicesLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_devices.TryGetValue(label, out var exact))
            {
                return exact.Snapshot();
            }

            foreach (var device in _devices.Values)
            {
                if (Matches(device, label))
                {
                    return device.Snapshot();
                }
            }
        }
        finally
        {
            _devicesLock.Release();
        }

        return null;

        static bool Matches(Device device, string label)
            => string.Equals(device.Name(), label, StringComparison.OrdinalIgnoreCase)
            || string.Equals(device.Id, label, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Topics.TopicSafeId(device), label, StringComparison.OrdinalIgnoreCase)
            || string.Equals(device.ComputedName(), label, StringComparison.OrdinalIgnoreCase)
            || (device.IpAddress is { } ip && string.Equals(ip.ToString(), label, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<Device> ResolveDeviceReadOnlyAsync(string label)
        => await ResolveDeviceAsync(label).ConfigureAwait(false)
           ?? throw new GoveeException($"device '{label}' not found");

    /// <summary>
    /// Resolves a device and takes its control lock. Home Assistant fans a single
    /// user action out into several commands, so serializing per device keeps
    /// them from interleaving with an unrelated request.
    /// </summary>
    public async Task<DeviceControlLease> ResolveDeviceForControlAsync(string label, CancellationToken cancellationToken = default)
    {
        var device = await ResolveDeviceReadOnlyAsync(label).ConfigureAwait(false);

        SemaphoreSlim controlLock;
        await _devicesLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_controlLocks.TryGetValue(device.Id, out controlLock!))
            {
                controlLock = new SemaphoreSlim(1, 1);
                _controlLocks[device.Id] = controlLock;
            }
        }
        finally
        {
            _devicesLock.Release();
        }

        await controlLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new DeviceControlLease(this, device, controlLock);
    }

    // ------------------------------------------------------------------ polling

    /// <summary>
    /// Asks the device for a status update over IoT. The reply arrives
    /// asynchronously, so the device is marked polled immediately to avoid
    /// re-asking every minute when it is simply offline.
    /// </summary>
    public async Task<bool> PollIotApiAsync(Device device, CancellationToken cancellationToken = default)
    {
        if (IotClient is not { } iot || device.UndocDeviceInfo is not { } info || !iot.IsDeviceCompatible(info.Entry))
        {
            return false;
        }

        log.LogInformation("Requesting update via IoT MQTT for {Device}", device);

        try
        {
            await iot.RequestStatusUpdateAsync(info.Entry, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError("IoT status request for {Device} failed: {Message}", device, ex.Message);
            return false;
        }

        await UpdateDeviceAsync(device.Sku, device.Id, d => d.SetLastPolled()).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> PollPlatformApiAsync(Device device, CancellationToken cancellationToken = default)
    {
        if (PlatformClient is not { } client)
        {
            log.LogTrace("{Device} wanted a status update, but no platform client is available", device);
            return false;
        }

        if (device.HttpDeviceInfo is not { } info)
        {
            return false;
        }

        log.LogInformation("Requesting update via Platform API for {Device}", device);

        var state = await client.GetDeviceStateAsync(info, cancellationToken).ConfigureAwait(false);

        await UpdateDeviceAsync(device.Sku, device.Id, d =>
        {
            d.SetHttpDeviceState(state);
            d.SetLastPolled();
        }).ConfigureAwait(false);

        await NotifyOfStateChangeAsync(device.Id, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Re-reads LAN status until <paramref name="accepted"/> is satisfied or five
    /// seconds elapse. Devices apply changes asynchronously, so the first reply
    /// after a command often still shows the old value.
    /// </summary>
    private async Task PollLanApiAsync(
        LanDevice lanDevice,
        Func<LanDeviceStatus, bool> accepted,
        CancellationToken cancellationToken)
    {
        if (LanClient is not { } client)
        {
            throw new GoveeException("no LAN client");
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        var changed = false;

        while (DateTime.UtcNow <= deadline)
        {
            var status = await client.QueryStatusAsync(lanDevice, cancellationToken).ConfigureAwait(false);
            var satisfied = accepted(status);

            await UpdateDeviceAsync(lanDevice.Sku, lanDevice.Device, d => changed |= d.SetLanDeviceStatus(status))
                .ConfigureAwait(false);

            if (satisfied)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
        }

        await NotifyOfStateChangeAsync(lanDevice.Device, changed, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs after a control lease is released. Devices reachable over LAN or IoT
    /// report their own changes, so only the Platform-API-only devices need an
    /// explicit re-read — after a delay, because Govee's state endpoint is not
    /// immediately coherent with a command.
    /// </summary>
    internal async Task PollAfterControlAsync(string id)
    {
        var device = await GetDeviceByIdAsync(id).ConfigureAwait(false);
        if (device is null)
        {
            return;
        }

        if (device.PollableViaLan())
        {
            return;
        }
        if (UsesBleFirst(device))
        {
            // The device answers status queries on the same connection, so no
            // settling delay is needed.
            try
            {
                await PollBleAsync(device).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                log.LogError("Polling {Device} failed: {Message}", device, ex.Message);
            }
            return;
        }
        if (device.PollableViaIot() && IotClient is not null)
        {
            return;
        }

        await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        log.LogInformation("Polling {Device} to get the latest state after control", device);
        try
        {
            await PollPlatformApiAsync(device).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.LogError("Polling {Device} failed: {Message}", device, ex.Message);
        }
    }

    // ------------------------------------------------------------------ control

    public async Task DeviceControlAsync(Device device, DeviceCapability capability, JsonNode? value, CancellationToken cancellationToken = default)
    {
        if (PlatformClient is { } client && device.HttpDeviceInfo is { } info)
        {
            log.LogInformation("Using Platform API to send {Value} control to {Device}", value?.ToJsonString(), device);
            await client.ControlDeviceAsync(info, capability, value, cancellationToken).ConfigureAwait(false);
            return;
        }

        throw new GoveeException($"Unable to use the Platform API to control {device}");
    }

    /// <summary>
    /// Applies a change using the best transport the device supports: LAN first
    /// for latency and offline operation, then the IoT push channel, then the
    /// rate-limited Platform API.
    /// </summary>
    /// <param name="lanSettled">
    /// Recognises the LAN status that means the change landed. Devices apply
    /// changes asynchronously, so the first reply often still shows the old value.
    /// </param>
    /// <param name="clearsScene">
    /// Set for changes that visibly override a scene, so our remembered scene
    /// name stops being reported.
    /// </param>
    private async Task ControlAsync(
        Device device,
        string what,
        Func<LanClient, LanDevice, Task>? lanAction,
        Func<LanDeviceStatus, bool>? lanSettled,
        Func<IotClient, Undocumented.DeviceEntry, Task>? iotAction,
        Func<PlatformApiClient, HttpDeviceInfo, Task>? platformAction,
        CancellationToken cancellationToken,
        bool clearsScene = false,
        BleAction? bleAction = null)
    {
        if (lanAction is not null && device.LanDevice is { } lanDevice && LanClient is { } lan)
        {
            log.LogInformation("Using LAN API to set {Device} {What}", device, what);
            await lanAction(lan, lanDevice).ConfigureAwait(false);

            if (lanSettled is not null)
            {
                await PollLanApiAsync(lanDevice, lanSettled, cancellationToken).ConfigureAwait(false);
            }

            await ClearSceneIfNeededAsync(device, clearsScene).ConfigureAwait(false);
            return;
        }

        var ble = bleAction is not null && device.BleDevice is not null && BleClient is not null;
        var iot = iotAction is not null && device.IotApiSupported() && IotClient is not null && device.UndocDeviceInfo is not null;
        var platform = platformAction is not null && PlatformClient is not null && device.HttpDeviceInfo is not null;

        // A device the cloud reports offline will not hear a cloud command, and
        // the IoT publish would not even fail: Bluetooth is the only real path.
        if (ble && (!iot && !platform || UsesBleFirst(device)))
        {
            try
            {
                await BleControlAsync(device, what, bleAction!, clearsScene, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (GoveeException ex) when (iot || platform)
            {
                // The offline verdict may be stale, and the light may be out of
                // Bluetooth range, so the cloud still deserves a try.
                log.LogWarning("Bluetooth control of {Device} failed ({Message}); trying the cloud", device, ex.Message);
                ble = false;
            }
        }

        try
        {
            if (iot)
            {
                log.LogInformation("Using IoT API to set {Device} {What}", device, what);
                await iotAction!(IotClient!, device.UndocDeviceInfo!.Entry).ConfigureAwait(false);
                return;
            }

            if (platform)
            {
                log.LogInformation("Using Platform API to set {Device} {What}", device, what);
                await platformAction!(PlatformClient!, device.HttpDeviceInfo!).ConfigureAwait(false);
                await ClearSceneIfNeededAsync(device, clearsScene).ConfigureAwait(false);
                return;
            }
        }
        catch (GoveeException ex) when (ble)
        {
            log.LogWarning("Cloud control of {Device} failed ({Message}); trying Bluetooth", device, ex.Message);
            await BleControlAsync(device, what, bleAction!, clearsScene, cancellationToken).ConfigureAwait(false);
            return;
        }

        throw new GoveeException($"Unable to control {what} for {device}");
    }

    /// <summary>
    /// A frame to write over Bluetooth, plus how the device's state looks once
    /// it has been applied. Bluetooth gives no acknowledgement, so the state is
    /// recorded optimistically and corrected by the device's next reply.
    /// </summary>
    internal sealed record BleAction(
        Func<BleClient, BleDevice, Task> Send,
        Func<LanDeviceStatus, LanDeviceStatus> Apply);

    /// <summary>
    /// Whether Bluetooth should be tried before the cloud: when configured to,
    /// or when the cloud itself says the device is unreachable.
    /// </summary>
    private bool UsesBleFirst(Device device)
    {
        if (device.BleDevice is null || BleClient is null)
        {
            return false;
        }
        if (BleClient.Options.Prefer || device.IsBleOnlyDevice() == true || device.CloudReportsOffline())
        {
            return true;
        }
        var iot = device.IotApiSupported() && IotClient is not null && device.UndocDeviceInfo is not null;
        var platform = PlatformClient is not null && device.HttpDeviceInfo is not null;
        return !iot && !platform;
    }

    private async Task BleControlAsync(Device device, string what, BleAction action, bool clearsScene, CancellationToken cancellationToken)
    {
        var bleDevice = device.BleDevice!;
        log.LogInformation("Using Bluetooth to set {Device} {What}", device, what);
        await action.Send(BleClient!, bleDevice).ConfigureAwait(false);

        await UpdateDeviceAsync(device.Sku, device.Id, d =>
        {
            d.SetBleDeviceStatus(action.Apply(CurrentBleStatus(d)));
        }).ConfigureAwait(false);

        await ClearSceneIfNeededAsync(device, clearsScene).ConfigureAwait(false);
    }

    /// <summary>Reads a device's state over Bluetooth and republishes it.</summary>
    public async Task<bool> PollBleAsync(Device device, CancellationToken cancellationToken = default)
    {
        if (BleClient is not { } ble || device.BleDevice is not { } bleDevice)
        {
            return false;
        }

        log.LogDebug("Requesting update via Bluetooth for {Device}", device);
        IReadOnlyList<byte[]> replies;
        try
        {
            replies = await ble.QueryStatusAsync(bleDevice, cancellationToken).ConfigureAwait(false);
        }
        catch (GoveeException ex)
        {
            log.LogWarning("Bluetooth status query for {Device} failed: {Message}", device, ex.Message);
            return false;
        }

        if (replies.Count == 0)
        {
            return false;
        }

        var changed = false;
        await UpdateDeviceAsync(device.Sku, device.Id, d =>
        {
            changed = ApplyBleReplies(d, replies);
            d.SetLastPolled();
        }).ConfigureAwait(false);
        await NotifyOfStateChangeAsync(device.Id, changed, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Merges replies into what is already known, so a device that answers only
    /// some queries does not lose the rest of its state.
    /// </summary>
    private static bool ApplyBleReplies(Device device, IEnumerable<byte[]> replies)
    {
        var profile = BleLightProfile.Resolve(device.Sku);
        var status = CurrentBleStatus(device);
        var any = false;

        foreach (var reply in replies)
        {
            if (LightFrames.ApplyReply(profile, status, reply) is { } updated)
            {
                status = updated;
                any = true;
            }
        }

        return any && device.SetBleDeviceStatus(status);
    }

    private static LanDeviceStatus CurrentBleStatus(Device device)
        => device.BleDeviceStatus ?? (device.ComputeDeviceState() is { } existing
            ? new LanDeviceStatus(existing.On, existing.Brightness, existing.Color, existing.Kelvin)
            : LanDeviceStatus.Empty);

    private readonly SemaphoreSlim _bleScanLock = new(1, 1);

    /// <summary>
    /// Scans for Bluetooth devices, attaches every device without a LAN path
    /// to the one at its expected address, and reports what was seen. Used at
    /// startup, by the periodic rescan, and from the panel's scan button.
    /// </summary>
    public async Task<BleScanReport> ScanBluetoothAsync(int? seconds = null, CancellationToken cancellationToken = default)
    {
        var ble = BleClient ?? throw new GoveeException(BleUnavailableReason ?? "Bluetooth is not available");
        var duration = Math.Clamp(seconds ?? ble.Options.ScanSeconds, 1, 120);

        await _bleScanLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            log.LogInformation("Scanning for Bluetooth devices for {Seconds} seconds", duration);
            string? error = null;
            try
            {
                await ble.ScanAsync(TimeSpan.FromSeconds(duration), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning("Bluetooth scan failed: {Message}", ex.Message);
                error = ex.Message;
            }

            var govee = ble.GoveeDevicesSeen();
            log.LogInformation("Bluetooth scan saw {Count} Govee device(s): {Names}",
                govee.Count, govee.Count == 0 ? "none" : string.Join(", ", govee.Select(g => g.Name)));

            var changed = false;
            var devices = await GetDevicesAsync().ConfigureAwait(false);
            foreach (var device in devices)
            {
                if (device.LanDevice is not null || device.BleDevice is not null
                    || device.GetDeviceType() != Platform.DeviceType.Light)
                {
                    continue;
                }
                if (ble.Resolve(device.Sku, device.Id, device.AccountBleAddress(), device.AccountBleName()) is not { } match)
                {
                    continue;
                }

                log.LogInformation("{Device} is reachable over Bluetooth as {Match}", device, match);
                await UpdateDeviceAsync(device.Sku, device.Id, d => d.SetBleDevice(match)).ConfigureAwait(false);
                changed = true;
            }

            if (changed)
            {
                devices = await GetDevicesAsync().ConfigureAwait(false);
                if (HassClient is { } hass)
                {
                    try
                    {
                        await hass.RegisterWithHassAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        log.LogError("Re-registering with Home Assistant failed: {Message}", ex.Message);
                    }
                }
            }

            var byAddress = devices.Where(d => d.BleDevice is not null)
                .GroupBy(d => d.BleDevice!.Address, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            var seen = govee.Select(g =>
            {
                byAddress.TryGetValue(g.Address, out var matched);
                var profile = BleLightProfile.Resolve(g.Sku ?? "");
                return new BleScanReport.SeenDevice(
                    g.Address, g.Name ?? "", g.Sku ?? "", g.Rssi,
                    matched?.Id, matched?.Name(),
                    $"{(byte)profile.ColorMode:x2}", profile.BrightnessAsPercent ? "percent" : "byte");
            }).ToList();

            // Devices the cloud cannot reach that Bluetooth did not find either.
            var missing = devices
                .Where(d => d.LanDevice is null && d.BleDevice is null
                            && (d.IsBleOnlyDevice() == true || d.CloudReportsOffline() || PlatformClient is null))
                .Select(d => new BleScanReport.MissingDevice(d.Id, d.Name(), d.Sku, d.ExpectedBleAddress()))
                .ToList();

            return new BleScanReport(ble.AdapterName, duration, seen, missing, changed, ble.Seen.Count, error);
        }
        finally
        {
            _bleScanLock.Release();
        }
    }

    /// <summary>Handles a status frame that arrived on a Bluetooth connection unprompted.</summary>
    public async Task ApplyBleFrameAsync(BleDevice bleDevice, byte[] frame, CancellationToken cancellationToken = default)
    {
        var device = (await GetDevicesAsync().ConfigureAwait(false))
            .FirstOrDefault(d => d.BleDevice?.Address == bleDevice.Address);
        if (device is null)
        {
            return;
        }

        var changed = false;
        await UpdateDeviceAsync(device.Sku, device.Id, d => changed = ApplyBleReplies(d, [frame])).ConfigureAwait(false);
        if (changed)
        {
            await NotifyOfStateChangeAsync(device.Id, cancellationToken).ConfigureAwait(false);
        }
    }

    private Task ClearSceneIfNeededAsync(Device device, bool clearsScene)
        => clearsScene
            ? UpdateDeviceAsync(device.Sku, device.Id, d => d.SetActiveScene(null))
            : Task.CompletedTask;

    /// <summary>Turns on only the light function, which for some devices is a nightlight.</summary>
    public async Task DeviceLightPowerOnAsync(Device device, bool on, CancellationToken cancellationToken = default)
    {
        if (await TryHumidifierSetNightlightAsync(device, p => p with { On = on }, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var instanceName = device.GetLightPowerToggleInstanceName()
            ?? throw new GoveeException(
                $"Don't know how to toggle just the light portion of {device}. " +
                "Please share the device metadata and state if you report this issue");

        await ControlAsync(device, "light power state",
            (lan, d) => lan.SendTurnAsync(d, on, cancellationToken),
            s => s.On == on,
            (iot, entry) => iot.SetPowerStateAsync(entry, on, cancellationToken),
            (client, info) => client.SetToggleStateAsync(info, instanceName, on, cancellationToken),
            cancellationToken,
            bleAction: new((ble, d) => ble.SendTurnAsync(d, on, cancellationToken), s => s with { On = on }))
            .ConfigureAwait(false);
    }

    public Task DevicePowerOnAsync(Device device, bool on, CancellationToken cancellationToken = default)
        => ControlAsync(device, "power state",
            (lan, d) => lan.SendTurnAsync(d, on, cancellationToken),
            s => s.On == on,
            (iot, entry) => iot.SetPowerStateAsync(entry, on, cancellationToken),
            (client, info) => client.SetPowerStateAsync(info, on, cancellationToken),
            cancellationToken,
            bleAction: new((ble, d) => ble.SendTurnAsync(d, on, cancellationToken), s => s with { On = on }));

    public async Task DeviceSetBrightnessAsync(Device device, byte percent, CancellationToken cancellationToken = default)
    {
        if (await TryHumidifierSetNightlightAsync(
                device, p => p with { Brightness = percent, On = true }, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await ControlAsync(device, "brightness",
            (lan, d) => lan.SendBrightnessAsync(d, percent, cancellationToken),
            s => s.Brightness == percent,
            (iot, entry) => iot.SetBrightnessAsync(entry, percent, cancellationToken),
            (client, info) => client.SetBrightnessAsync(info, percent, cancellationToken),
            cancellationToken,
            bleAction: new((ble, d) => ble.SendBrightnessAsync(d, percent, cancellationToken),
                s => s with { Brightness = percent, On = percent > 0 || s.On }))
            .ConfigureAwait(false);
    }

    public Task DeviceSetColorTemperatureAsync(Device device, int kelvin, CancellationToken cancellationToken = default)
        => ControlAsync(device, "color temperature",
            (lan, d) => lan.SendColorTemperatureAsync(d, kelvin, cancellationToken),
            s => s.ColorTemperatureKelvin == kelvin,
            (iot, entry) => iot.SetColorTemperatureAsync(entry, kelvin, cancellationToken),
            (client, info) => client.SetColorTemperatureAsync(info, kelvin, cancellationToken),
            cancellationToken, clearsScene: true,
            bleAction: new((ble, d) => ble.SendColorTemperatureAsync(d, kelvin, cancellationToken),
                s => s with { ColorTemperatureKelvin = kelvin, Color = DeviceColor.Black, On = true }));

    public async Task DeviceSetColorRgbAsync(Device device, DeviceColor color, CancellationToken cancellationToken = default)
    {
        if (await TryHumidifierSetNightlightAsync(
                device, p => p with { R = color.R, G = color.G, B = color.B, On = true }, cancellationToken)
            .ConfigureAwait(false))
        {
            return;
        }

        await ControlAsync(device, "color",
            (lan, d) => lan.SendColorRgbAsync(d, color, cancellationToken),
            s => s.Color == color,
            (iot, entry) => iot.SetColorRgbAsync(entry, color, cancellationToken),
            (client, info) => client.SetColorRgbAsync(info, color, cancellationToken),
            cancellationToken, clearsScene: true,
            bleAction: new((ble, d) => ble.SendColorRgbAsync(d, color, cancellationToken),
                s => s with { Color = color, ColorTemperatureKelvin = 0, On = true }))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Humidifier nightlights are not exposed as normal light capabilities; they
    /// are driven by a BLE frame over IoT. Returns false for any device that does
    /// not work that way, so the caller can fall through to the normal paths.
    /// </summary>
    private async Task<bool> TryHumidifierSetNightlightAsync(
        Device device,
        Func<SetHumidifierNightlight, SetHumidifierNightlight> apply,
        CancellationToken cancellationToken)
    {
        var current = device.NightlightState?.ToSet() ?? SetHumidifierNightlight.Default;
        var updated = apply(current);

        if (!PacketManager.TryEncode(device.Sku, updated, out var bytes) || bytes is null)
        {
            return false;
        }

        if (IotClient is not { } iot || device.UndocDeviceInfo is not { } undoc)
        {
            return false;
        }

        log.LogInformation("Using IoT API to set {Device} nightlight", device);
        await iot.SendRealAsync(undoc.Entry, PacketManager.ToBase64Commands(bytes), cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task HumidifierSetParameterAsync(Device device, long workMode, long value, CancellationToken cancellationToken = default)
    {
        var packet = new SetHumidifierMode((byte)workMode, (byte)value);

        if (PacketManager.TryEncode(device.Sku, packet, out var bytes) && bytes is not null
            && IotClient is { } iot && device.UndocDeviceInfo is { } undoc)
        {
            await iot.SendRealAsync(undoc.Entry, PacketManager.ToBase64Commands(bytes), cancellationToken).ConfigureAwait(false);
            return;
        }

        if (PlatformClient is { } client && device.HttpDeviceInfo is { } info)
        {
            await client.SetWorkModeAsync(info, workMode, value, cancellationToken).ConfigureAwait(false);
            return;
        }

        throw new GoveeException($"Unable to control workMode={workMode} for {device}");
    }

    public async Task DeviceSetTargetTemperatureAsync(
        Device device,
        string instanceName,
        TemperatureValue target,
        CancellationToken cancellationToken = default)
    {
        if (PlatformClient is { } client && device.HttpDeviceInfo is { } info)
        {
            log.LogInformation("Using Platform API to set {Device} target temperature to {Target}", device, target);
            await client.SetTargetTemperatureAsync(info, instanceName, target, cancellationToken).ConfigureAwait(false);
            return;
        }

        throw new GoveeException($"Unable to set the temperature for {device}");
    }

    public async Task DeviceSetSceneAsync(Device device, string scene, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(scene))
        {
            // The blank entry in the effect list means "no scene". There is
            // nothing to send, but stop reporting whichever scene was last set.
            await UpdateDeviceAsync(device.Sku, device.Id, d => d.SetActiveScene(null)).ConfigureAwait(false);
            await NotifyOfStateChangeAsync(device.Id, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!device.AvoidPlatformApi() && PlatformClient is { } client && device.HttpDeviceInfo is { } info)
        {
            log.LogInformation("Using Platform API to set {Device} to scene {Scene}", device, scene);
            await client.SetSceneByNameAsync(info, scene, cancellationToken).ConfigureAwait(false);
            await UpdateDeviceAsync(device.Sku, device.Id, d => d.SetActiveScene(scene)).ConfigureAwait(false);
            return;
        }

        if (device.LanDevice is { } lanDevice && LanClient is { } lan)
        {
            var code = await sceneCatalog.FindSceneCodeAsync(device.Sku, scene, cancellationToken).ConfigureAwait(false)
                ?? throw new GoveeException($"unable to set scene {scene} for {device}");

            log.LogInformation("Using LAN API to set {Device} to scene {Scene} (code {Code})", device, scene, code.Code);
            await lan.SendSceneAsync(lanDevice, code, cancellationToken).ConfigureAwait(false);
            await UpdateDeviceAsync(device.Sku, device.Id, d => d.SetActiveScene(scene)).ConfigureAwait(false);
            return;
        }

        throw new GoveeException($"Unable to set the scene for {device}");
    }

    /// <summary>
    /// Effect names to advertise for this device. Falls back to the app catalog
    /// when no API key is configured, which is the only way LAN-only setups get
    /// scenes at all.
    /// </summary>
    public async Task<List<string>> DeviceListScenesAsync(Device device, CancellationToken cancellationToken = default)
    {
        if (PlatformClient is { } client && device.HttpDeviceInfo is { } info)
        {
            return SceneUtils.SortAndDedup(await client.ListSceneNamesAsync(info, cancellationToken).ConfigureAwait(false));
        }

        // Without the Platform API, scenes can only be applied over the LAN, so
        // there is nothing to gain from fetching the catalog for other devices.
        if (device.LanDevice is null)
        {
            return [];
        }

        try
        {
            var names = await sceneCatalog.ListLanSceneNamesAsync(device.Sku, cancellationToken).ConfigureAwait(false);
            return SceneUtils.SortAndDedup(names);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogTrace("Don't know how to list scenes for {Device}: {Message}", device, ex.Message);
            return [];
        }
    }

    // ------------------------------------------------------------------- events

    /// <summary>
    /// Merges an AWS IoT status packet into the device and republishes its state.
    /// Fields arrive piecemeal, so anything absent keeps its previous value.
    /// </summary>
    public async Task ApplyIotPacketAsync(IotPacket packet, CancellationToken cancellationToken = default)
    {
        await UpdateDeviceAsync(packet.Sku, packet.DeviceId, device =>
        {
            var status = device.IotDeviceStatus ?? (device.ComputeDeviceState() is { } existing
                ? new LanDeviceStatus(existing.On, existing.Brightness, existing.Color, existing.Kelvin)
                : LanDeviceStatus.Empty);

            if (packet.Brightness is { } brightness)
            {
                // A brightness of zero is how these devices report "off".
                status = status with { Brightness = brightness, On = brightness != 0 };
            }
            if (packet.Color is { } color)
            {
                status = status with { Color = color, On = true };
            }
            if (packet.ColorTemperatureKelvin is { } kelvin)
            {
                status = status with { ColorTemperatureKelvin = kelvin, On = true };
            }

            foreach (var command in packet.Commands)
            {
                var decoded = PacketManager.Decode(packet.Sku, command);
                switch (decoded)
                {
                    case NotifyHumidifierNightlight nightlight:
                        status = status with
                        {
                            Brightness = nightlight.Brightness,
                            Color = new DeviceColor(nightlight.R, nightlight.G, nightlight.B),
                        };
                        device.SetNightlightState(nightlight);
                        break;

                    case HumidifierAutoMode auto:
                        device.SetTargetHumidity(auto.TargetHumidity.AsPercent());
                        break;

                    case NotifyHumidifierMode mode:
                        device.SetHumidifierWorkModeAndParam(mode.Mode, mode.Param);
                        break;

                    case GenericPacket:
                    case SetHumidifierMode:
                    case SetHumidifierNightlight:
                        // Undecodable frames, and echoes of commands we sent.
                        break;

                    default:
                        log.LogWarning("Taking no action for {Packet} for {Sku}", decoded, packet.Sku);
                        break;
                }
            }

            // Checked last: the explicit on/off wins over anything synthesized above.
            if (packet.OnOff is { } on)
            {
                status = status with { On = on };
            }

            device.SetIotDeviceStatus(status);
        }).ConfigureAwait(false);

        await NotifyOfStateChangeAsync(packet.DeviceId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Republishes a device's entity states to Home Assistant.</summary>
    public async Task NotifyOfStateChangeAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        var device = await GetDeviceByIdAsync(deviceId).ConfigureAwait(false);
        if (device is null)
        {
            log.LogWarning("Cannot find device {DeviceId} to notify state change", deviceId);
            return;
        }

        _lastNotified[deviceId] = DateTimeOffset.UtcNow;

        if (HassClient is { } hass)
        {
            await hass.AdviseHassOfDeviceStateAsync(device, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Republishes when the state actually changed, and otherwise only as an
    /// occasional heartbeat. LAN devices answer discovery every minute whether
    /// or not anything changed, and Home Assistant does not need to hear about
    /// every one of those.
    /// </summary>
    public Task NotifyOfStateChangeAsync(string deviceId, bool changed, CancellationToken cancellationToken = default)
    {
        if (!changed
            && _lastNotified.TryGetValue(deviceId, out var last)
            && DateTimeOffset.UtcNow - last < HeartbeatInterval)
        {
            return Task.CompletedTask;
        }

        return NotifyOfStateChangeAsync(deviceId, cancellationToken);
    }

    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(5);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _lastNotified = new(StringComparer.Ordinal);
}

/// <summary>What a Bluetooth scan found, for the log, the API and the panel.</summary>
public sealed record BleScanReport(
    [property: JsonPropertyName("adapter")] string Adapter,
    [property: JsonPropertyName("seconds")] int Seconds,
    [property: JsonPropertyName("seen")] List<BleScanReport.SeenDevice> Seen,
    [property: JsonPropertyName("missing")] List<BleScanReport.MissingDevice> Missing,
    [property: JsonPropertyName("changed")] bool Changed,
    [property: JsonPropertyName("other_devices")] int OtherDevices,
    [property: JsonPropertyName("error")] string? Error)
{
    /// <summary>A Govee device that advertised during the scan.</summary>
    public sealed record SeenDevice(
        [property: JsonPropertyName("address")] string Address,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("sku")] string Sku,
        [property: JsonPropertyName("rssi")] short? Rssi,
        [property: JsonPropertyName("device_id")] string? DeviceId,
        [property: JsonPropertyName("device_name")] string? DeviceName,
        [property: JsonPropertyName("color_mode")] string ColorMode,
        [property: JsonPropertyName("brightness_scale")] string BrightnessScale);

    /// <summary>A device that needs Bluetooth but was not found.</summary>
    public sealed record MissingDevice(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("sku")] string Sku,
        [property: JsonPropertyName("expected_address")] string? ExpectedAddress);
}

/// <summary>
/// Held for the duration of a control operation. Releasing it schedules the
/// follow-up poll that reconciles whatever the device actually did.
/// </summary>
public sealed class DeviceControlLease(ServiceState state, Device device, SemaphoreSlim controlLock) : IDisposable
{
    private bool _released;

    public Device Device => device;

    public static implicit operator Device(DeviceControlLease lease) => lease.Device;

    public override string ToString() => device.ToString();

    public void Dispose()
    {
        if (_released)
        {
            return;
        }
        _released = true;
        controlLock.Release();

        _ = Task.Run(() => state.PollAfterControlAsync(device.Id));
    }
}
