using System.Net.NetworkInformation;
using System.Net.Sockets;
using Novee2Mqtt.Core;
using Novee2Mqtt.Devices;
using Novee2Mqtt.Hass;
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

        await using var cloud = new CloudServices(app, log);
        await cloud.LoadAsync(cancellationToken).ConfigureAwait(false);

        await using var lanClient = await StartLanDiscoveryAsync(app, log, cancellationToken).ConfigureAwait(false);

        await ReportDeviceInventoryAsync(app, log).ConfigureAwait(false);

        using var background = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, background.Token);
        var pollTask = Task.Run(() => PeriodicStatePollAsync(state, log, linked.Token), CancellationToken.None);
        var cloudTask = Task.Run(() => CloudMaintenanceAsync(app, cloud, log, linked.Token), CancellationToken.None);

        await using var hassClient = await StartHassAsync(app, log, cancellationToken).ConfigureAwait(false);

        var httpApp = HttpServer.Build(state, app.Cache, app.Options.HttpPort, app.LoggerFactory);
        LogWebUiAddresses(log, app.Options.HttpPort);

        try
        {
            // Returns normally once the token is cancelled; it does not throw.
            await httpApp.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            log.LogInformation("Shutting down");

            await background.CancelAsync().ConfigureAwait(false);
            foreach (var task in new[] { pollTask, cloudTask })
            {
                try
                {
                    await task.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected.
                }
            }
            await httpApp.DisposeAsync().ConfigureAwait(false);
        }

        return 0;
    }

    /// <summary>
    /// Retries whichever cloud APIs failed at startup, with backoff, and once
    /// everything is up re-reads the device lists hourly so devices added to
    /// the account appear without a restart.
    /// </summary>
    private static async Task CloudMaintenanceAsync(GoveeApp app, CloudServices cloud, ILogger log, CancellationToken cancellationToken)
    {
        var retry = TimeSpan.FromMinutes(1);
        var maxRetry = TimeSpan.FromHours(1);
        var refreshInterval = TimeSpan.FromHours(1);

        while (!cancellationToken.IsCancellationRequested)
        {
            var complete = cloud.IsComplete;
            await Task.Delay(complete ? refreshInterval : retry, cancellationToken).ConfigureAwait(false);

            bool changed;
            if (complete)
            {
                changed = await cloud.RefreshAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                log.LogInformation("Retrying the cloud APIs that failed earlier");
                changed = await cloud.LoadAsync(cancellationToken).ConfigureAwait(false);

                if (cloud.IsComplete)
                {
                    log.LogInformation("All configured cloud APIs are now available");
                    retry = TimeSpan.FromMinutes(1);
                }
                else
                {
                    retry = retry * 2 > maxRetry ? maxRetry : retry * 2;
                }
            }

            if (changed && app.State.HassClient is { } hass)
            {
                log.LogInformation("Device information changed; re-registering with Home Assistant");
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
            try
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

                            var changed = false;
                            await app.State.UpdateDeviceAsync(lanDevice.Sku, lanDevice.Device,
                                d => changed = d.SetLanDeviceStatus(status)).ConfigureAwait(false);

                            // Devices answer every discovery round; only tell Home
                            // Assistant when the answer differs from last time.
                            await app.State.NotifyOfStateChangeAsync(lanDevice.Device, changed, cancellationToken)
                                .ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            // Shutting down.
                        }
                        catch (Exception ex)
                        {
                            log.LogTrace("LAN status query for {Device} failed: {Message}", lanDevice.Device, ex.Message);
                        }
                    }, CancellationToken.None);
                }
            }
            catch (OperationCanceledException)
            {
                // Shutting down.
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

        // A LAN device that has gone stale is probably offline, in which case
        // the cloud will not know any better. Still ask, but only hourly, so a
        // device whose LAN path broke is not left reporting stale state
        // forever while the Platform API quota is spared.
        if (device.LanDevice is not null && !needsPlatform)
        {
            if (device.LastPolled is { } polled && now - polled < TimeSpan.FromHours(1))
            {
                return;
            }
            log.LogDebug("LAN device {Device} has gone quiet; asking the cloud APIs instead", device);
        }

        if (!needsPlatform && await state.PollIotApiAsync(device, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await state.PollPlatformApiAsync(device, cancellationToken).ConfigureAwait(false);
    }
}
