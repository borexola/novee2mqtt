using Novee2Mqtt.Core;
using Novee2Mqtt.Devices;
using Novee2Mqtt.Platform;
using Microsoft.Extensions.Logging;

namespace Novee2Mqtt.Hass;

/// <summary>
/// Turns the device registry into the set of Home Assistant entities to
/// advertise. Called both at registration time and on every state change, so it
/// must be cheap and deterministic.
/// </summary>
public sealed class EntityEnumerator(ILogger<EntityEnumerator> log, ServiceState state)
{
    public async Task<List<HassEntity>> EnumerateAllAsync(CancellationToken cancellationToken = default)
    {
        var entities = new List<HassEntity>
        {
            Entities.FixedDiagnostic("Version", VersionInfo.Version),
            Entities.GlobalButton("Purge Caches", Topics.PurgeCache),
        };

        if (state.UndocClient is { } undoc)
        {
            try
            {
                foreach (var oneClick in await undoc.ParseOneClicksAsync(cancellationToken).ConfigureAwait(false))
                {
                    entities.Add(Entities.OneClickScene(oneClick.Name));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning("Failed to parse one-clicks: {Message}", ex.Message);
            }
        }

        foreach (var device in await state.GetDevicesAsync().ConfigureAwait(false))
        {
            try
            {
                await AddDeviceEntitiesAsync(device, entities, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError("Enumerating entities for {Device}: {Message}", device, ex.Message);
            }
        }

        return entities;
    }

    public async Task<List<HassEntity>> EnumerateForDeviceAsync(Device device, CancellationToken cancellationToken = default)
    {
        var entities = new List<HassEntity>();
        await AddDeviceEntitiesAsync(device, entities, cancellationToken).ConfigureAwait(false);
        return entities;
    }

    private async Task AddDeviceEntitiesAsync(Device device, List<HassEntity> entities, CancellationToken cancellationToken)
    {
        if (!device.IsControllable())
        {
            return;
        }

        entities.Add(Entities.StatusDiagnostic(device, state));
        entities.Add(Entities.RequestPlatformDataButton(device));

        if (device.SupportsRgb() || device.GetColorTemperatureRange() is not null || device.SupportsBrightness())
        {
            entities.Add(await Entities.LightAsync(device, state, segment: null, cancellationToken).ConfigureAwait(false));
        }

        var deviceType = device.GetDeviceType();

        if (deviceType == DeviceType.Humidifier || deviceType == DeviceType.Dehumidifier)
        {
            entities.Add(Entities.Humidifier(device, state));
        }

        // Lights carry their scenes in the light entity's effect list; everything
        // else needs a separate select to reach them.
        if (deviceType != DeviceType.Light
            && await Entities.SceneModeSelectAsync(device, state, cancellationToken).ConfigureAwait(false) is { } scenes)
        {
            entities.Add(scenes);
        }

        if (device.HttpDeviceInfo is not { } info)
        {
            return;
        }

        foreach (var capability in info.Capabilities)
        {
            AddCapabilityEntity(device, capability, entities);
        }

        if (info.SupportsSegmentedRgb() is { } segments)
        {
            foreach (var segment in segments)
            {
                entities.Add(await Entities.LightAsync(device, state, segment, cancellationToken).ConfigureAwait(false));
            }
        }
    }

    private void AddCapabilityEntity(Device device, DeviceCapability capability, List<HassEntity> entities)
    {
        var kind = capability.Kind;

        if (kind == DeviceCapabilityKind.Toggle || kind == DeviceCapabilityKind.OnOff)
        {
            entities.Add(Entities.Switch(device, state, log, capability));
        }
        else if (kind == DeviceCapabilityKind.WorkMode)
        {
            AddWorkModeEntities(device, capability, entities);
        }
        else if (kind == DeviceCapabilityKind.Property)
        {
            entities.Add(Entities.CapabilitySensor(device, state, log, capability));
        }
        else if (kind == DeviceCapabilityKind.TemperatureSetting)
        {
            try
            {
                entities.Add(Entities.TargetTemperature(device, state, log, capability));
            }
            catch (GoveeException ex)
            {
                log.LogWarning("Skipping target temperature for {Device}: {Message}", device, ex.Message);
            }
        }
        else if (kind == DeviceCapabilityKind.ColorSetting
                 || kind == DeviceCapabilityKind.SegmentColorSetting
                 || kind == DeviceCapabilityKind.MusicSetting
                 || kind == DeviceCapabilityKind.Event
                 || kind == DeviceCapabilityKind.Mode
                 || kind == DeviceCapabilityKind.DynamicScene
                 // Brightness and humidity are surfaced by the light and
                 // humidifier entities respectively.
                 || (kind == DeviceCapabilityKind.Range && capability.Instance is "brightness" or "humidity"))
        {
            // Surfaced elsewhere, or not useful on its own.
        }
        else
        {
            log.LogWarning("Unhandled capability {Kind} {Instance} for {Device}", kind, capability.Instance, device);
        }
    }

    /// <summary>
    /// Renders a work-mode capability as a mode select plus, per mode, either a
    /// slider (contiguous values) or one button per preset.
    /// </summary>
    private void AddWorkModeEntities(Device device, DeviceCapability capability, List<HassEntity> entities)
    {
        ParsedWorkMode workModes;
        try
        {
            workModes = ParsedWorkMode.WithCapability(capability);
        }
        catch (GoveeException ex)
        {
            log.LogWarning("Cannot parse work modes for {Device}: {Message}", device, ex.Message);
            return;
        }

        workModes.AdjustForDevice(device.Sku);
        var quirk = device.ResolveQuirk();

        foreach (var workMode in workModes.Modes.Values)
        {
            if (workMode.Value.AsInt64() is not { } modeNumber)
            {
                continue;
            }

            var showAsPreset = workMode.ShouldShowAsPreset()
                || (quirk?.ShouldShowModeAsPreset(workMode.Name) ?? false);

            if (!showAsPreset)
            {
                entities.Add(Entities.WorkModeNumber(device, state, log, workMode.EffectiveLabel, workMode.Name,
                    workMode.Value, workMode.ContiguousValueRange()));
            }
            else if (workMode.Values.Count == 0)
            {
                entities.Add(Entities.WorkModePresetButton(device, $"Activate Mode: {workMode.EffectiveLabel}",
                    workMode.Name, modeNumber, workMode.DefaultValue()));
            }
            else
            {
                foreach (var value in workMode.Values)
                {
                    if (value.Value.AsInt64() is { } modeValue)
                    {
                        entities.Add(Entities.WorkModePresetButton(device, value.ComputedLabel,
                            workMode.Name, modeNumber, modeValue));
                    }
                }
            }
        }

        entities.Add(Entities.WorkModeSelect(device, workModes, state));
    }
}
