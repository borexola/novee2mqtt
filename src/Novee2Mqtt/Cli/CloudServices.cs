using Novee2Mqtt.Iot;
using Novee2Mqtt.Platform;
using Novee2Mqtt.Undocumented;
using Microsoft.Extensions.Logging;

namespace Novee2Mqtt.Cli;

/// <summary>
/// Loads the device lists from the cloud APIs and starts the AWS IoT push
/// channel. Each piece is attempted independently, so a failure in one at
/// startup (Govee's login endpoint rate-limits readily) does not cost the
/// others, and <see cref="LoadAsync"/> can be called again later to pick up
/// whatever is still missing, or to refresh the lists.
/// </summary>
internal sealed class CloudServices(GoveeApp app, ILogger log) : IAsyncDisposable
{
    private readonly PlatformApiClient? _platform = app.CreatePlatformClient();
    private readonly UndocumentedApiClient? _undoc = app.CreateUndocClient();

    private IotClient? _iot;
    private bool _platformLoaded;
    private bool _undocLoaded;

    /// <summary>True once every configured API has delivered what it is for.</summary>
    public bool IsComplete
        => (_platform is null || _platformLoaded)
           && (_undoc is null || (_undocLoaded && _iot is not null));

    /// <summary>
    /// Loads whatever has not loaded yet. Failures are logged, not thrown.
    /// </summary>
    /// <returns>
    /// True when the device registry changed in a way Home Assistant should
    /// hear about: a list loaded for the first time, or new devices appeared.
    /// </returns>
    public async Task<bool> LoadAsync(CancellationToken cancellationToken)
    {
        var changed = false;

        if (_platform is not null && !_platformLoaded)
        {
            changed |= await LoadPlatformAsync(_platform, cancellationToken).ConfigureAwait(false);
        }

        if (_undoc is not null)
        {
            if (!_undocLoaded)
            {
                changed |= await LoadUndocAsync(_undoc, cancellationToken).ConfigureAwait(false);
            }

            if (_undocLoaded && _iot is null)
            {
                await StartIotAsync(_undoc, cancellationToken).ConfigureAwait(false);
            }
        }

        return changed;
    }

    /// <summary>
    /// Re-reads the device lists so devices added to the account since startup
    /// show up without a restart. The IoT connection is left alone.
    /// </summary>
    public Task<bool> RefreshAsync(CancellationToken cancellationToken)
    {
        _platformLoaded = false;
        _undocLoaded = false;
        return LoadAsync(cancellationToken);
    }

    private async Task<bool> LoadPlatformAsync(PlatformApiClient client, CancellationToken cancellationToken)
    {
        log.LogInformation("Querying platform API for device list");

        try
        {
            var added = false;
            foreach (var info in await client.GetDevicesAsync(cancellationToken).ConfigureAwait(false))
            {
                added |= await app.State.UpdateDeviceAsync(info.Sku, info.Device, d => d.SetHttpDeviceInfo(info))
                    .ConfigureAwait(false);
            }

            var first = app.State.PlatformClient is null;
            app.State.PlatformClient = client;
            _platformLoaded = true;
            return first || added;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Losing the Platform API is survivable: LAN and IoT still work, and
            // the client is kept so that polling works once Govee is back.
            log.LogError("Failed to query the platform API: {Message}", ex.Message);
            app.State.PlatformClient = client;
            return false;
        }
    }

    private async Task<bool> LoadUndocAsync(UndocumentedApiClient client, CancellationToken cancellationToken)
    {
        log.LogInformation("Querying undocumented API for device + room list");

        try
        {
            var account = await client.LoginAccountAsync(cancellationToken).ConfigureAwait(false);
            var info = await client.GetDeviceListAsync(account.Token, cancellationToken).ConfigureAwait(false);
            var roomsByGroupId = info.Groups.ToDictionary(g => g.GroupId, g => g.GroupName);

            var added = false;
            foreach (var entry in info.Devices)
            {
                var roomName = roomsByGroupId.GetValueOrDefault(entry.GroupId);
                added |= await app.State.UpdateDeviceAsync(entry.Sku, entry.Device, d => d.SetUndocDeviceInfo(entry, roomName))
                    .ConfigureAwait(false);
            }

            var first = app.State.UndocClient is null;
            app.State.UndocClient = client;
            _undocLoaded = true;
            return first || added;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError("Failed to query the undocumented API: {Message}", ex.Message);
            return false;
        }
    }

    private async Task StartIotAsync(UndocumentedApiClient client, CancellationToken cancellationToken)
    {
        IotClient? iot = null;
        try
        {
            var account = await client.LoginAccountAsync(cancellationToken).ConfigureAwait(false);
            var iotKey = await client.GetIotKeyAsync(account.Token, cancellationToken).ConfigureAwait(false);

            iot = IotClient.Create(app.LoggerFactory.CreateLogger<IotClient>(), iotKey, account, app.Options.AmazonRootCa);
            iot.PacketHandler = app.State.ApplyIotPacketAsync;
            await iot.StartAsync(cancellationToken).ConfigureAwait(false);

            _iot = iot;
            app.State.IotClient = iot;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Without IoT we fall back to polling, which still works.
            log.LogError("Failed to start the AWS IoT client: {Message}", ex.Message);
            if (iot is not null)
            {
                await iot.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_iot is not null)
        {
            await _iot.DisposeAsync().ConfigureAwait(false);
        }
    }
}
