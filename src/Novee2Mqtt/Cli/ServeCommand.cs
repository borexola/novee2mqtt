using System.Net.NetworkInformation;
using System.Net.Sockets;
using Novee2Mqtt.Core;
using Novee2Mqtt.Devices;
using Novee2Mqtt.Hass;
using Novee2Mqtt.Iot;
using Novee2Mqtt.Lan;
using Novee2Mqtt.Web;
using Microsoft.Extensions.Logging;

namespace Novee2Mqtt.Cli;

/// <summary>
/// The long-running bridge: discovers devices through every configured API,
/// keeps their state fresh, and mirrors them into Home Assistant over MQTT.
/// </summary>
public static class ServeCommand
{
    public static async Task<int> RunAsync(GoveeApp app, CancellationToken cancellationToken)
    {
        var log = app.LoggerFactory.CreateLogger("govee.serve");
        var state = app.State;

        log.LogInformation("Starting service. version {Version}", VersionInfo.Version);

        await LoadPlatformDevicesAsync(app, log, cancellationToken).ConfigureAwait(false);

        await using var iotClient = await LoadUndocDevicesAsync(app, log, cancellationToken).ConfigureAwait(false);

        await using var lanClient = await StartLanDiscoveryAsync(app, log, cancellationToken).ConfigureAwait(false);

        await ReportDeviceInventoryAsync(app, log).ConfigureAwait(false);

        using var poller = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, poller.Token);
        var pollTask = Task.Run(() => PeriodicStatePollAsync(state, log, linked.Token), CancellationToken.None);

        await using var hassClient = await StartHassAsync(app, log, cancellationToken).ConfigureAwait(false);

        var httpApp = HttpServer.Build(state, app.Cache, app.Options.HttpPort, app.LoggerFactory);
        LogWebUiAddresses(log, app.Options.HttpPort);

        try
        {
            await httpApp.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            log.LogInformation("Shutting down");
        }
        finally
        {
            await poller.CancelAsync().ConfigureAwait(false);
            try
            {
                await pollTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }
            await httpApp.DisposeAsync().ConfigureAwait(false);
        }

        return 0;
    }

    /// <summary>
    /// Reports the addresses the web UI can actually be opened on. Host
    /// networking means the container shares the host's interfaces, so these
    /// are the URLs a browser elsewhere on the LAN needs.
    /// </summary>
    private static void LogWebUiAddresses(ILogger log, int port)
    {
        var addresses = new List<string>();

        try
        {
            foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (iface.NetworkInterfaceType == NetworkInterfaceType.Loopback
                    || iface.OperationalStatus != OperationalStatus.Up
                    // Docker's own bridges are visible under host networking but
                    // are never how someone reaches the UI.
                    || iface.Name.StartsWith("docker", StringComparison.OrdinalIgnoreCase)
                    || iface.Name.StartsWith("veth", StringComparison.OrdinalIgnoreCase)
                    || iface.Name.StartsWith("br-", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var unicast in iface.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        addresses.Add($"http://{unicast.Address}:{port}/");
                    }
                }
            }
        }
        catch (NetworkInformationException)
        {
            // Not fatal: the loopback URL below is still worth printing.
        }

        addresses.Add($"http://localhost:{port}/");

        log.LogInformation("Web UI ready at {Addresses}", string.Join("  ", addresses));
    }

    private static async Task LoadPlatformDevicesAsync(GoveeApp app, ILogger log, CancellationToken cancellationToken)
    {
        if (app.CreatePlatformClient() is not { } client)
        {
            return;
        }

        log.LogInformation("Querying platform API for device list");

        try
        {
            foreach (var info in await client.GetDevicesAsync(cancellationToken).ConfigureAwait(false))
            {
                await app.State.UpdateDeviceAsync(info.Sku, info.Device, d => d.SetHttpDeviceInfo(info))
                    .ConfigureAwait(false);
            }

            app.State.PlatformClient = client;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Losing the Platform API is survivable: LAN and IoT still work.
            log.LogError("Failed to query the platform API: {Message}", ex.Message);
            app.State.PlatformClient = client;
        }
    }

    private static async Task<IotClient?> LoadUndocDevicesAsync(GoveeApp app, ILogger log, CancellationToken cancellationToken)
    {
        if (app.CreateUndocClient() is not { } client)
        {
            return null;
        }

        log.LogInformation("Querying undocumented API for device + room list");

        Undocumented.LoginAccountResponse account;
        try
        {
            account = await client.LoginAccountAsync(cancellationToken).ConfigureAwait(false);

            var info = await client.GetDeviceListAsync(account.Token, cancellationToken).ConfigureAwait(false);
            var roomsByGroupId = info.Groups.ToDictionary(g => g.GroupId, g => g.GroupName);

            foreach (var entry in info.Devices)
            {
                var roomName = roomsByGroupId.GetValueOrDefault(entry.GroupId);
                await app.State.UpdateDeviceAsync(entry.Sku, entry.Device, d => d.SetUndocDeviceInfo(entry, roomName))
                    .ConfigureAwait(false);
            }

            app.State.UndocClient = client;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError("Failed to query the undocumented API: {Message}", ex.Message);
            return null;
        }

        try
        {
            var iotKey = await client.GetIotKeyAsync(account.Token, cancellationToken).ConfigureAwait(false);

            var iot = IotClient.Create(
                app.LoggerFactory.CreateLogger<IotClient>(), iotKey, account, app.Options.AmazonRootCa);

            iot.PacketHandler = app.State.ApplyIotPacketAsync;
            await iot.StartAsync(cancellationToken).ConfigureAwait(false);

            app.State.IotClient = iot;
            return iot;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Without IoT we fall back to polling, which still works.
            log.LogError("Failed to start the AWS IoT client: {Message}", ex.Message);
            return null;
        }
    }

    private static async Task<LanClient?> StartLanDiscoveryAsync(GoveeApp app, ILogger log, CancellationToken cancellationToken)
    {
        var options = app.Options.ToDiscoOptions();
        if (options.IsEmpty)
        {
            return null;
        }

        log.LogInformation("Starting LAN discovery");

        LanClient client;
        try
        {
            client = LanClient.Start(app.LoggerFactory.CreateLogger<LanClient>(), options);
        }
        catch (GoveeException ex)
        {
            log.LogError("{Message}", ex.Message);
            return null;
        }

        app.State.LanClient = client;

        _ = Task.Run(async () =>
        {
            await foreach (var lanDevice in client.Discovered.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                log.LogTrace("LAN disco: {Sku} {Device} at {Ip}", lanDevice.Sku, lanDevice.Device, lanDevice.Ip);

                await app.State.UpdateDeviceAsync(lanDevice.Sku, lanDevice.Device, d => d.SetLanDevice(lanDevice))
                    .ConfigureAwait(false);

                // Query status out of band so one slow device does not stall
                // discovery of the rest.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var status = await client.QueryStatusAsync(lanDevice, cancellationToken).ConfigureAwait(false);
                        await app.State.UpdateDeviceAsync(lanDevice.Sku, lanDevice.Device,
                            d => d.SetLanDeviceStatus(status)).ConfigureAwait(false);
                        await app.State.NotifyOfStateChangeAsync(lanDevice.Device, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        log.LogTrace("LAN status query for {Device} failed: {Message}", lanDevice.Device, ex.Message);
                    }
                }, CancellationToken.None);
            }
        }, CancellationToken.None);

        // Ten seconds, because that is the LAN status query timeout: waiting any
        // less would make the "didn't respond" warnings below mostly false alarms.
        log.LogInformation("Waiting 10 seconds for LAN API discovery");
        await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);

        return client;
    }

    private static async Task ReportDeviceInventoryAsync(GoveeApp app, ILogger log)
    {
        log.LogInformation("Devices returned from Govee's APIs");

        foreach (var device in await app.State.GetDevicesAsync().ConfigureAwait(false))
        {
            log.LogInformation("{Device}", device);

            if (device.LanDevice is { } lan)
            {
                log.LogInformation("  LAN API: ip={Ip}", lan.Ip);
            }

            if (device.HttpDeviceInfo is { } info)
            {
                log.LogInformation("  Platform API: {Kind}. supports_rgb={Rgb} supports_brightness={Brightness}",
                    info.DeviceType, info.SupportsRgb(), info.SupportsBrightness());

                var colorTemp = info.GetColorTemperatureRange();
                var segments = info.SupportsSegmentedRgb();
                log.LogInformation("                color_temp={ColorTemp} segments={Segments}",
                    colorTemp is { } ct ? $"{ct.Min}-{ct.Max}" : "none",
                    segments is null ? "none" : segments.Count.ToString());
            }

            if (device.UndocDeviceInfo is { } undoc)
            {
                log.LogInformation("  Undoc: room={Room} supports_iot={Iot} ble_only={BleOnly}",
                    undoc.RoomName ?? "none", undoc.Entry.Topic is not null, undoc.Entry.IsBleOnly);
            }

            if (device.ResolveQuirk() is { } quirk)
            {
                log.LogInformation("  {Quirk}", quirk);

                if (quirk.LanApiCapable && device.LanDevice is null)
                {
                    log.LogWarning("  This device should be available via the LAN API, but didn't respond to probing yet. Possible causes:");
                    log.LogWarning("  1) LAN API needs to be enabled in the Govee Home App.");
                    log.LogWarning("  2) The device is offline.");
                    log.LogWarning("  3) A network configuration issue is preventing communication.");
                    log.LogWarning("  4) The device needs a firmware update before it can enable LAN API.");
                    log.LogWarning("  5) The hardware version of the device is too old to enable the LAN API.");
                }
            }
            else if (device.HttpDeviceInfo is null)
            {
                log.LogWarning("  Unknown device type. Cannot map to Home Assistant.");
                if (app.State.PlatformClient is null)
                {
                    log.LogWarning("  Recommendation: configure your Govee API Key so that metadata can be fetched from Govee");
                }
            }
        }
    }

    private static async Task<HassClient?> StartHassAsync(GoveeApp app, ILogger log, CancellationToken cancellationToken)
    {
        if (app.Options.MqttHost is null)
        {
            log.LogWarning(
                "No MQTT broker configured, so Home Assistant integration is disabled. " +
                "Set --mqtt-host or $GOVEE_MQTT_HOST to enable it.");
            return null;
        }

        var hassOptions = new HassOptions
        {
            Host = app.Options.RequireMqttHost(),
            Port = app.Options.MqttPort,
            Username = app.Options.MqttUsername,
            Password = app.Options.MqttPassword,
            DiscoveryPrefix = app.Options.HassDiscoveryPrefix,
        };

        var client = HassClient.Create(
            app.LoggerFactory.CreateLogger<HassClient>(),
            hassOptions,
            app.State,
            app.CreateEntityEnumerator(),
            app.Cache);

        await client.StartAsync(cancellationToken).ConfigureAwait(false);
        app.State.HassClient = client;
        return client;
    }

    /// <summary>Re-reads devices that have gone quiet for longer than their poll interval.</summary>
    private static async Task PeriodicStatePollAsync(ServiceState state, ILogger log, CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);

        while (!cancellationToken.IsCancellationRequested)
        {
            foreach (var device in await state.GetDevicesAsync().ConfigureAwait(false))
            {
                try
                {
                    await PollSingleDeviceAsync(state, log, device, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    log.LogError("while polling {Device}: {Message}", device, ex.Message);
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task PollSingleDeviceAsync(ServiceState state, ILogger log, Device device, CancellationToken cancellationToken)
    {
        // BLE-only devices cannot be reached over the network at all.
        if (device.IsBleOnlyDevice() == true)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var pollInterval = device.PreferredPollInterval();

        if (device.LastPolled is { } lastPolled && now - lastPolled <= pollInterval)
        {
            return;
        }

        var deviceState = device.ComputeDeviceState();
        if (deviceState is not null && now - deviceState.Updated <= pollInterval)
        {
            return;
        }

        var needsPlatform = device.NeedsPlatformPoll();

        // A LAN device that has gone stale is almost certainly offline, and
        // burning Platform API quota on it will not help.
        if (device.LanDevice is not null && !needsPlatform)
        {
            log.LogTrace("LAN-available device {Device} needs a status update; it's likely offline.", device);
            return;
        }

        if (!needsPlatform && await state.PollIotApiAsync(device, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await state.PollPlatformApiAsync(device, cancellationToken).ConfigureAwait(false);
    }
}
