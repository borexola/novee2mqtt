using System.Collections.Concurrent;
using System.Threading.Channels;
using Novee2Mqtt.Core;
using Microsoft.Extensions.Logging;
using Tmds.DBus.Protocol;

namespace Novee2Mqtt.Ble;

/// <summary>
/// Controls Govee lights over Bluetooth LE through the host's BlueZ daemon.
/// This is the path for devices that have no LAN API and no route to Govee's
/// cloud: the bridge connects to the light, writes the same 20-byte frames the
/// Govee app does, and listens for its replies.
///
/// Connections are opened on demand and dropped after a short idle period,
/// because a Govee light accepts a single BLE connection and holding it would
/// lock the Govee app out. Each connection reads the device's encrypt version
/// and, on firmware that asks for it, runs one of two handshakes that encrypt
/// every frame after it.
/// </summary>
public sealed class BleClient : IAsyncDisposable
{
    public const string ServiceUuid = "00010203-0405-0607-0809-0a0b0c0d1910";
    public const string ControlCharacteristicUuid = "00010203-0405-0607-0809-0a0b0c0d2b11";
    public const string NotifyCharacteristicUuid = "00010203-0405-0607-0809-0a0b0c0d2b10";
    public const string EncryptionCharacteristicUuid = "00010203-0405-0607-0809-0a0b0c0d2b12";

    private const int ConnectAttempts = 3;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ServicesTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan HandshakeConfirmTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FrameGap = TimeSpan.FromMilliseconds(40);
    private static readonly TimeSpan HoldCheckInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan HoldRetryMin = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HoldRetryMax = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan HoldStableAfter = TimeSpan.FromSeconds(30);

    private readonly ILogger _log;
    private readonly BleOptions _options;
    private readonly BlueZBus _bus;
    private readonly string _adapterPath;
    private readonly ConcurrentDictionary<string, BleScanResult> _seen = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, BleSession> _sessions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _discoveryLock = new(1, 1);
    private readonly List<IDisposable> _watches = [];
    private int _discoveryUsers;
    private bool _disposed;

    private BleClient(ILogger log, BleOptions options, BlueZBus bus, string adapterPath)
    {
        _log = log;
        _options = options;
        _bus = bus;
        _adapterPath = adapterPath;
    }

    public string AdapterName => _adapterPath[(_adapterPath.LastIndexOf('/') + 1)..];

    public BleOptions Options => _options;

    /// <summary>
    /// Raised with each status frame that is not the answer to a query: keep-alive
    /// replies, and anything the device reports by itself.
    /// </summary>
    public Func<BleDevice, byte[], Task>? StatusReported { get; set; }

    /// <summary>Every device BlueZ currently knows about, Govee or not.</summary>
    public IReadOnlyCollection<BleScanResult> Seen => _seen.Values.ToList();

    public static async Task<BleClient> StartAsync(ILogger<BleClient> log, BleOptions options, CancellationToken cancellationToken)
    {
        BlueZBus bus;
        try
        {
            bus = await BlueZBus.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Unreachable(ex);
        }

        try
        {
            var objects = await bus.GetManagedObjectsAsync(cancellationToken).ConfigureAwait(false);

            var adapters = objects
                .Where(o => o.Value.ContainsKey(BlueZBus.AdapterInterface))
                .Select(o => o.Key)
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();

            string adapterPath;
            if (options.Adapter is { } wanted)
            {
                adapterPath = adapters.FirstOrDefault(p => p.EndsWith("/" + wanted, StringComparison.Ordinal))
                    ?? throw new GoveeException(
                        $"Bluetooth adapter '{wanted}' not found. BlueZ knows: " +
                        (adapters.Count == 0 ? "none" : string.Join(", ", adapters.Select(p => p[(p.LastIndexOf('/') + 1)..]))));
            }
            else
            {
                adapterPath = adapters.FirstOrDefault()
                    ?? throw new GoveeException("No Bluetooth adapter is available to BlueZ on this host");
            }

            var adapter = objects[adapterPath][BlueZBus.AdapterInterface];
            if (adapter.TryGetValue("Powered", out var powered) && !powered.GetBool())
            {
                log.LogInformation("Bluetooth adapter {Adapter} is powered off; turning it on", adapterPath);
                try
                {
                    await bus.SetAsync(adapterPath, BlueZBus.AdapterInterface, "Powered", true, cancellationToken).ConfigureAwait(false);
                }
                catch (DBusErrorReplyException ex)
                {
                    throw new GoveeException($"Bluetooth adapter {adapterPath} is powered off and could not be powered on: {ex.ErrorMessage}", ex);
                }
            }

            var client = new BleClient(log, options, bus, adapterPath);

            foreach (var (path, interfaces) in objects)
            {
                if (interfaces.TryGetValue(BlueZBus.DeviceInterface, out var props))
                {
                    client.NoteDevice(path, props);
                }
            }

            await client.StartWatchingAsync().ConfigureAwait(false);

            log.LogInformation("Bluetooth ready on {Adapter} ({Address}); {Count} device(s) cached by BlueZ",
                client.AdapterName,
                adapter.TryGetValue("Address", out var address) ? address.GetString() : "?",
                client._seen.Count);

            return client;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            bus.Dispose();
            throw ex is GoveeException ? ex : Unreachable(ex);
        }
    }

    private static GoveeException Unreachable(Exception ex) => new(
        "Cannot reach BlueZ over the system D-Bus. In Docker, mount /run/dbus:/run/dbus:ro and check that " +
        "AppArmor allows it; the add-on needs host_dbus; and the host must run the bluetooth service. " +
        $"({ex.Message})", ex);

    private async Task StartWatchingAsync()
    {
        _watches.Add(await _bus.WatchInterfacesAddedAsync((path, interfaces) =>
        {
            if (interfaces.TryGetValue(BlueZBus.DeviceInterface, out var props))
            {
                NoteDevice(path, props);
            }
        }).ConfigureAwait(false));

        _watches.Add(await _bus.WatchInterfacesRemovedAsync((path, interfaces) =>
        {
            if (interfaces.Contains(BlueZBus.DeviceInterface) && BleNaming.AddressFromPath(path) is { } address)
            {
                _seen.TryRemove(address, out _);
                if (_sessions.TryGetValue(address, out var session))
                {
                    session.OnGone();
                }
            }
        }).ConfigureAwait(false));

        _watches.Add(await _bus.WatchPropertiesChangedAsync(_adapterPath, BlueZBus.DeviceInterface, (path, changed) =>
        {
            if (BleNaming.AddressFromPath(path) is not { } address)
            {
                return;
            }
            if (_seen.TryGetValue(address, out var existing))
            {
                _seen[address] = Merge(existing, changed);
            }
            if (changed.TryGetValue("Connected", out var connected) && !connected.GetBool()
                && _sessions.TryGetValue(address, out var session))
            {
                session.OnDisconnected();
            }
        }).ConfigureAwait(false));

        _watches.Add(await _bus.WatchPropertiesChangedAsync(_adapterPath, BlueZBus.CharacteristicInterface, (path, changed) =>
        {
            if (!changed.TryGetValue("Value", out var value))
            {
                return;
            }
            foreach (var session in _sessions.Values)
            {
                if (path == session.NotifyPath)
                {
                    session.OnValue(value.GetArray<byte>());
                    return;
                }
            }
        }).ConfigureAwait(false));
    }

    private void NoteDevice(string path, Dictionary<string, VariantValue> props)
    {
        if (BleNaming.AddressFromPath(path) is not { } address)
        {
            return;
        }
        var fresh = new BleScanResult(address, null, null, DateTimeOffset.UtcNow);
        _seen[address] = Merge(_seen.TryGetValue(address, out var existing) ? existing : fresh, props);
    }

    private static BleScanResult Merge(BleScanResult existing, Dictionary<string, VariantValue> changed)
    {
        var name = existing.Name;
        if (changed.TryGetValue("Name", out var n))
        {
            name = n.GetString();
        }
        else if (name is null && changed.TryGetValue("Alias", out var alias))
        {
            // The alias falls back to the address with dashes, which tells us nothing.
            var text = alias.GetString();
            if (BleNaming.NormalizeAddress(text) is null)
            {
                name = text;
            }
        }

        var rssi = changed.TryGetValue("RSSI", out var r) ? r.GetInt16() : existing.Rssi;
        var encrypted = changed.TryGetValue("ManufacturerData", out var data)
            ? ReadEncryptFlag(data) ?? existing.Encrypted
            : existing.Encrypted;

        return existing with { Name = name, Rssi = rssi, LastSeen = DateTimeOffset.UtcNow, Encrypted = encrypted };
    }

    private static bool? ReadEncryptFlag(VariantValue manufacturerData)
    {
        var entries = new List<KeyValuePair<ushort, byte[]>>();
        for (var i = 0; i < manufacturerData.Count; i++)
        {
            var entry = manufacturerData.GetDictionaryEntry(i);
            entries.Add(new(entry.Key.GetUInt16(), entry.Value.GetArray<byte>()));
        }
        return BleNaming.EncryptFlag(entries);
    }

    // ---------------------------------------------------------------- discovery

    /// <summary>Runs an LE discovery for <paramref name="duration"/> and returns the Govee devices seen.</summary>
    public async Task<IReadOnlyList<BleScanResult>> ScanAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        await StartDiscoveryAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await StopDiscoveryAsync().ConfigureAwait(false);
        }

        return GoveeDevicesSeen();
    }

    public IReadOnlyList<BleScanResult> GoveeDevicesSeen()
        => _seen.Values.Where(d => d.LooksLikeGovee).OrderBy(d => d.Name, StringComparer.Ordinal).ToList();

    private async Task StartDiscoveryAsync(CancellationToken cancellationToken)
    {
        await _discoveryLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_discoveryUsers > 0)
            {
                _discoveryUsers++;
                return;
            }

            try
            {
                await _bus.SetDiscoveryFilterAsync(_adapterPath, cancellationToken).ConfigureAwait(false);
            }
            catch (DBusErrorReplyException ex)
            {
                _log.LogDebug("SetDiscoveryFilter: {Error}", ex.ErrorMessage);
            }

            try
            {
                await _bus.CallAsync(_adapterPath, BlueZBus.AdapterInterface, "StartDiscovery", cancellationToken).ConfigureAwait(false);
                _log.LogDebug("Bluetooth discovery started");
            }
            catch (DBusErrorReplyException ex) when (BlueZBus.IsBenign(ex))
            {
                // Already discovering.
            }
            catch (DBusErrorReplyException ex)
            {
                throw new GoveeException($"Bluetooth discovery failed to start: {ex.ErrorMessage}", ex);
            }

            // Counted only now: a failed start must not make later scans think
            // discovery is already running.
            _discoveryUsers++;
        }
        finally
        {
            _discoveryLock.Release();
        }
    }

    private async Task StopDiscoveryAsync()
    {
        await _discoveryLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_discoveryUsers == 0 || --_discoveryUsers > 0)
            {
                return;
            }

            try
            {
                await _bus.CallAsync(_adapterPath, BlueZBus.AdapterInterface, "StopDiscovery", CancellationToken.None).ConfigureAwait(false);
                _log.LogDebug("Bluetooth discovery stopped");
            }
            catch (DBusErrorReplyException ex) when (BlueZBus.IsBenign(ex))
            {
                // Nothing to stop.
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogDebug("StopDiscovery: {Message}", ex.Message);
            }
        }
        finally
        {
            _discoveryLock.Release();
        }
    }

    // ---------------------------------------------------------------- matching

    /// <summary>
    /// Finds the Bluetooth device for a Govee device, trying in order: an
    /// address from the configuration, the address and name the Govee account
    /// reports, the addresses the device id implies, and finally a device
    /// advertising the same model and id suffix.
    /// </summary>
    public BleDevice? Resolve(string sku, string deviceId, string? accountAddress = null, string? accountName = null)
    {
        if (_options.Addresses.TryGetValue(deviceId, out var configured))
        {
            return new BleDevice(configured, sku, NameFor(configured, sku, deviceId));
        }

        if (accountAddress is not null && BleNaming.NormalizeAddress(accountAddress) is { } known && _seen.ContainsKey(known))
        {
            return new BleDevice(known, sku, NameFor(known, sku, deviceId));
        }

        if (!string.IsNullOrEmpty(accountName)
            && _seen.Values.FirstOrDefault(d => string.Equals(d.Name, accountName, StringComparison.OrdinalIgnoreCase)) is { } named)
        {
            return new BleDevice(named.Address, sku, accountName);
        }

        var candidates = BleNaming.AddressCandidates(deviceId).ToList();
        foreach (var candidate in candidates)
        {
            if (_seen.TryGetValue(candidate, out var seen)
                && (seen.Sku is null || seen.Sku.Equals(sku, StringComparison.OrdinalIgnoreCase)))
            {
                return new BleDevice(candidate, sku, NameFor(candidate, sku, deviceId));
            }
        }

        // Govee's advertised name ends with the last two octets of an address.
        var suffixes = candidates.Select(c => c.Replace(":", "")[^4..]).ToList();
        foreach (var seen in _seen.Values)
        {
            if (seen.Name is { } name
                && seen.Sku is { } seenSku
                && seenSku.Equals(sku, StringComparison.OrdinalIgnoreCase)
                && suffixes.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase)))
            {
                return new BleDevice(seen.Address, sku, name);
            }
        }

        return null;
    }

    private string NameFor(string address, string sku, string deviceId)
        => _seen.TryGetValue(address, out var seen) && seen.Name is { } name
            ? name
            : $"{sku}_{deviceId.Replace(":", "").ToUpperInvariant()[^Math.Min(4, deviceId.Replace(":", "").Length)..]}";

    /// <summary>The most likely address for a device id, for messages. The Govee account's is better.</summary>
    public static string? ExpectedAddress(string deviceId) => BleNaming.AddressCandidates(deviceId).FirstOrDefault();

    // ------------------------------------------------------------------ control

    public Task SendTurnAsync(BleDevice device, bool on, CancellationToken cancellationToken = default)
        => SendFramesAsync(device, [LightFrames.Power(on)], cancellationToken);

    public Task SendBrightnessAsync(BleDevice device, int percent, CancellationToken cancellationToken = default)
        => SendFramesAsync(device, [LightFrames.Brightness(BleLightProfile.Resolve(device.Sku), percent)], cancellationToken);

    public Task SendColorRgbAsync(BleDevice device, DeviceColor color, CancellationToken cancellationToken = default)
        => SendFramesAsync(device, [LightFrames.ColorRgb(BleLightProfile.Resolve(device.Sku), color)], cancellationToken);

    public Task SendColorTemperatureAsync(BleDevice device, int kelvin, CancellationToken cancellationToken = default)
        => SendFramesAsync(device, [LightFrames.ColorTemperature(BleLightProfile.Resolve(device.Sku), kelvin)], cancellationToken);

    /// <summary>Writes raw frames to the control characteristic, connecting first if needed.</summary>
    public Task SendFramesAsync(BleDevice device, IEnumerable<byte[]> frames, CancellationToken cancellationToken = default)
        => Session(device).WriteAsync(frames, cancellationToken);

    /// <summary>
    /// Asks the device for its power, brightness and colour, and returns the
    /// replies it sent. Empty means it answered nothing. The caller merges them
    /// with <see cref="LightFrames.ApplyReply"/>, because a device may answer
    /// only some of the queries.
    /// </summary>
    public Task<IReadOnlyList<byte[]>> QueryStatusAsync(BleDevice device, CancellationToken cancellationToken = default)
        => Session(device).QueryStatusAsync(cancellationToken);

    /// <summary>
    /// With this on, the link stays up and is rebuilt whenever it drops, so
    /// commands do not have to wait for a connection. Turning it off releases
    /// the light straight away, so the Govee app can reach it again.
    /// </summary>
    public Task SetKeepConnectedAsync(BleDevice device, bool keep)
        => Session(device).SetKeepConnectedAsync(keep);

    public Task DisconnectAsync(BleDevice device)
        => _sessions.TryGetValue(device.Address, out var session) ? session.DisconnectAsync() : Task.CompletedTask;

    private BleSession Session(BleDevice device)
        => _sessions.GetOrAdd(device.Address, _ => new BleSession(this, device));

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        foreach (var session in _sessions.Values)
        {
            session.StopHolding();
            await session.DisconnectAsync().ConfigureAwait(false);
        }
        foreach (var watch in _watches)
        {
            watch.Dispose();
        }
        _bus.Dispose();
    }

    // ------------------------------------------------------------------ session

    /// <summary>Thrown for a device this client cannot talk to, where retrying would not help.</summary>
    private sealed class BleUnsupportedException(string message) : GoveeException(message);

    private sealed class PendingReply(byte op)
    {
        public byte Op { get; } = op;
        public TaskCompletionSource<byte[]> Reply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>One device's connection, serialized so frames never interleave.</summary>
    private sealed class BleSession(BleClient owner, BleDevice device)
    {
        private readonly SemaphoreSlim _lock = new(1, 1);
        private readonly string _devicePath = BleNaming.DevicePath(owner._adapterPath, device.Address);

        private string? _controlPath;
        private volatile bool _connected;
        private volatile IBleCipher? _cipher;
        private string? _unsupported;
        private volatile Channel<byte[]>? _handshake;
        private volatile PendingReply? _pending;
        private DateTimeOffset _lastActivity;
        private DateTimeOffset _connectedAt;
        private CancellationTokenSource? _keepAlive;
        private CancellationTokenSource? _holder;
        private volatile bool _keepConnected;

        public string? NotifyPath { get; private set; }

        public async Task WriteAsync(IEnumerable<byte[]> frames, CancellationToken cancellationToken)
        {
            await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

                foreach (var frame in frames)
                {
                    await WriteFrameAsync(frame, cancellationToken).ConfigureAwait(false);
                    await Task.Delay(FrameGap, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is DBusExceptionBase or TimeoutException)
            {
                throw new GoveeException($"Bluetooth control of {device} failed: {ex.Message}", ex);
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task<IReadOnlyList<byte[]>> QueryStatusAsync(CancellationToken cancellationToken)
        {
            await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

                var replies = new List<byte[]>();
                if (NotifyPath is null)
                {
                    return replies;
                }

                foreach (var query in LightFrames.StatusQueries())
                {
                    var pending = new PendingReply(query[1]);
                    _pending = pending;

                    await WriteFrameAsync(query, cancellationToken).ConfigureAwait(false);

                    try
                    {
                        replies.Add(await pending.Reply.Task.WaitAsync(ReplyTimeout, cancellationToken).ConfigureAwait(false));
                    }
                    catch (TimeoutException)
                    {
                        owner._log.LogDebug("{Device} did not answer {Query}", device, Convert.ToHexString(query, 0, 2));
                        if (replies.Count == 0)
                        {
                            break; // silent on the first query: do not hold up commands waiting for the rest
                        }
                    }
                }

                return replies;
            }
            catch (Exception ex) when (ex is DBusExceptionBase or TimeoutException)
            {
                throw new GoveeException($"Bluetooth status read of {device} failed: {ex.Message}", ex);
            }
            finally
            {
                _pending = null;
                _lock.Release();
            }
        }

        /// <summary>Called on the D-Bus thread with a notification from the device.</summary>
        public void OnValue(byte[] data)
        {
            owner._log.LogTrace("{Device} <- {Frame}", device, Convert.ToHexString(data));

            if (_handshake is { } handshake)
            {
                handshake.Writer.TryWrite(data);
                return;
            }

            var frame = _cipher is { } cipher ? cipher.Unwrap(data) : data;

            // Late handshake retransmits and the like fail these.
            if (frame is not { Length: 20 } || PacketManager.Checksum(frame.AsSpan(0, 19)) != frame[19])
            {
                return;
            }

            owner._log.LogTrace("{Device} <= {Frame}", device, Convert.ToHexString(frame));

            if (frame[0] != LightFrames.Read)
            {
                // The acknowledgement of a write proves receipt and nothing else,
                // but a non-zero result is an outright refusal.
                if (frame[0] == LightFrames.Write && frame[2] != 0)
                {
                    owner._log.LogWarning("{Device} refused command {Command:x2} with result {Result}", device, frame[1], frame[2]);
                }
                return;
            }

            if (_pending is { } pending && pending.Op == frame[1])
            {
                pending.Reply.TrySetResult(frame);
                return;
            }

            if (owner.StatusReported is { } handler)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await handler(device, frame).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        owner._log.LogDebug("BLE status handler for {Device}: {Message}", device, ex.Message);
                    }
                });
            }
        }

        public void OnDisconnected()
        {
            if (_connected)
            {
                owner._log.LogDebug("{Device} disconnected", device);
            }
            _connected = false;
            _cipher = null;
            _keepAlive?.Cancel();
        }

        public void OnGone()
        {
            OnDisconnected();
            _controlPath = null;
            NotifyPath = null;
        }

        public async Task SetKeepConnectedAsync(bool keep)
        {
            if (keep == _keepConnected)
            {
                return;
            }

            _keepConnected = keep;
            StopHolding();

            if (keep)
            {
                var holder = new CancellationTokenSource();
                _holder = holder;
                _ = Task.Run(() => HoldLinkAsync(holder.Token), CancellationToken.None);
                return;
            }

            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                await DisconnectAsync().ConfigureAwait(false);
            }
            finally
            {
                _lock.Release();
            }
        }

        public void StopHolding()
        {
            _holder?.Cancel();
            _holder = null;
        }

        /// <summary>Reconnects whenever the link is down, backing off while the light cannot be reached.</summary>
        private async Task HoldLinkAsync(CancellationToken cancellationToken)
        {
            var retry = HoldRetryMin;
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (!_connected)
                    {
                        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
                        try
                        {
                            // A link that dies young counts as a failure, or a light that
                            // keeps dropping us would be reconnected to in a tight loop.
                            var lasted = DateTimeOffset.UtcNow - _connectedAt;
                            retry = lasted >= HoldStableAfter
                                ? HoldRetryMin
                                : retry * 2 > HoldRetryMax ? HoldRetryMax : retry * 2;

                            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is GoveeException or DBusExceptionBase or TimeoutException)
                        {
                            if (_unsupported is not null)
                            {
                                owner._log.LogWarning("Cannot keep {Device} connected: {Message}", device, ex.Message);
                                return;
                            }

                            owner._log.LogDebug("Could not keep {Device} connected, retrying in {Retry}: {Message}", device, retry, ex.Message);
                        }
                        finally
                        {
                            _lock.Release();
                        }
                    }

                    await Task.Delay(_connected ? HoldCheckInterval : retry, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Turned off, or shutting down.
            }
        }

        public async Task DisconnectAsync()
        {
            var wasConnected = _connected;
            OnDisconnected();
            if (wasConnected)
            {
                await DisconnectQuietlyAsync().ConfigureAwait(false);
            }
        }

        private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
        {
            if (_connected && _controlPath is not null)
            {
                return;
            }

            // Reconnecting would only get the same answer, slowly.
            if (_unsupported is { } reason)
            {
                throw new GoveeException(reason);
            }

            // LE connects fail transiently as a matter of course, with errors
            // such as le-connection-abort-by-local. BlueZ can also forget the
            // device between attempts, so each one checks it is still known.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await EnsureSeenAsync(cancellationToken).ConfigureAwait(false);
                    await ConnectOnceAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (BleUnsupportedException ex)
                {
                    _unsupported = ex.Message;
                    await DisconnectQuietlyAsync().ConfigureAwait(false);
                    throw;
                }
                catch (GoveeException ex) when (attempt < ConnectAttempts)
                {
                    owner._log.LogInformation("Connecting to {Device}, attempt {Attempt}: {Message}", device, attempt, ex.Message);
                    await DisconnectQuietlyAsync().ConfigureAwait(false);
                    await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken).ConfigureAwait(false);
                }
                catch (GoveeException)
                {
                    await DisconnectQuietlyAsync().ConfigureAwait(false);
                    throw;
                }
            }
        }

        /// <summary>BlueZ will not connect to an address it does not currently know.</summary>
        private async Task EnsureSeenAsync(CancellationToken cancellationToken)
        {
            if (owner._seen.ContainsKey(device.Address))
            {
                return;
            }

            owner._log.LogInformation("{Device} is not known to BlueZ right now; scanning for it", device);
            await owner.StartDiscoveryAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(owner._options.ScanSeconds);
                while (!owner._seen.ContainsKey(device.Address) && DateTimeOffset.UtcNow < deadline)
                {
                    await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                await owner.StopDiscoveryAsync().ConfigureAwait(false);
            }

            if (!owner._seen.ContainsKey(device.Address))
            {
                throw new GoveeException($"{device} is not advertising within Bluetooth range of {owner.AdapterName}");
            }
        }

        private async Task ConnectOnceAsync(CancellationToken cancellationToken)
        {
            await ReleaseStaleLinkAsync(cancellationToken).ConfigureAwait(false);

            owner._log.LogInformation("Connecting to {Device} over Bluetooth", device);
            _cipher = null;

            try
            {
                await owner._bus.CallAsync(_devicePath, BlueZBus.DeviceInterface, "Connect", cancellationToken, ConnectTimeout)
                    .ConfigureAwait(false);
            }
            catch (DBusErrorReplyException ex) when (BlueZBus.IsBenign(ex))
            {
                // Already connected, or connecting.
            }
            catch (DBusErrorReplyException ex)
            {
                throw new GoveeException($"Bluetooth connect to {device} failed: {ex.ErrorMessage}", ex);
            }
            catch (TimeoutException ex)
            {
                throw new GoveeException($"Bluetooth connect to {device} timed out; is it in range and not connected to a phone?", ex);
            }

            await WaitForServicesAsync(cancellationToken).ConfigureAwait(false);

            var objects = await owner._bus.GetManagedObjectsAsync(cancellationToken).ConfigureAwait(false);
            var encryptionPath = FindGoveeCharacteristic(objects, EncryptionCharacteristicUuid);
            _controlPath = FindGoveeCharacteristic(objects, ControlCharacteristicUuid);
            NotifyPath = FindGoveeCharacteristic(objects, NotifyCharacteristicUuid);

            if (_controlPath is null)
            {
                throw new BleUnsupportedException($"{device} does not expose the Govee control characteristic; is it a Govee light?");
            }

            var notifying = await StartNotifyAsync(cancellationToken).ConfigureAwait(false);

            var version = await ReadEncryptVersionAsync(encryptionPath, cancellationToken).ConfigureAwait(false);
            if (version != 0)
            {
                if (!notifying)
                {
                    throw new GoveeException($"{device} needs an encrypted session but its notifications did not start");
                }

                _cipher = version == 1
                    ? await HandshakeAsync(cancellationToken).ConfigureAwait(false)
                    : await HandshakeGcmAsync(cancellationToken).ConfigureAwait(false);
            }

            _connected = true;
            _lastActivity = DateTimeOffset.UtcNow;
            _connectedAt = _lastActivity;
            StartKeepAlive();
            owner._log.LogInformation("Connected to {Device}{Mode}", device, version == 0 ? "" : $" (encrypted, version {version})");
        }

        private async Task<bool> StartNotifyAsync(CancellationToken cancellationToken)
        {
            if (NotifyPath is null)
            {
                return false;
            }

            try
            {
                await owner._bus.CallAsync(NotifyPath, BlueZBus.CharacteristicInterface, "StartNotify", cancellationToken, TimeSpan.FromSeconds(10))
                    .ConfigureAwait(false);
                return true;
            }
            catch (DBusErrorReplyException ex) when (BlueZBus.IsBenign(ex))
            {
                return true; // already notifying
            }
            catch (Exception ex) when (ex is DBusExceptionBase or TimeoutException)
            {
                owner._log.LogDebug("StartNotify for {Device}: {Message}; state will be assumed", device, ex.Message);
                return false;
            }
        }

        /// <summary>
        /// The device's encrypt version decides, as in the Govee app: 0 or no
        /// characteristic falls back to the advertised flag, 1 is the AES and
        /// RC4 scheme, and 2 is AES-GCM.
        /// </summary>
        private async Task<int> ReadEncryptVersionAsync(string? encryptionPath, CancellationToken cancellationToken)
        {
            var version = 0;
            if (encryptionPath is not null)
            {
                try
                {
                    var data = await owner._bus.ReadValueAsync(encryptionPath, cancellationToken).ConfigureAwait(false);
                    version = data.Length >= 2 && data[0] is 1 or 2 ? data[1] : 0;
                }
                catch (Exception ex) when (ex is DBusExceptionBase or TimeoutException)
                {
                    throw new GoveeException($"Reading the encrypt version of {device} failed: {ex.Message}", ex);
                }
            }

            if (version > 2)
            {
                throw new BleUnsupportedException($"{device} uses encrypt version {version}, which the bridge does not support");
            }

            if (version != 0)
            {
                return version;
            }
            return owner._seen.TryGetValue(device.Address, out var seen) && seen.Encrypted == true ? 1 : 0;
        }

        private async Task<IBleCipher> HandshakeAsync(CancellationToken cancellationToken)
        {
            var replies = Channel.CreateUnbounded<byte[]>();
            _handshake = replies;
            try
            {
                await WriteRawAsync(GoveeCipher.BuildHandshakeFrame(GoveeCipher.RequestSessionKey), cancellationToken).ConfigureAwait(false);

                byte[]? key = null;
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    deadline.CancelAfter(HandshakeTimeout);
                    try
                    {
                        while (key is null)
                        {
                            key = GoveeCipher.ParseSessionKey(await replies.Reader.ReadAsync(deadline.Token).ConfigureAwait(false));
                        }
                    }
                    catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new GoveeException($"{device} did not answer the encryption handshake", ex);
                    }
                }

                await WriteRawAsync(GoveeCipher.BuildHandshakeFrame(GoveeCipher.ConfirmSession), cancellationToken).ConfigureAwait(false);

                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    deadline.CancelAfter(HandshakeConfirmTimeout);
                    try
                    {
                        while (!GoveeCipher.IsConfirmReply(await replies.Reader.ReadAsync(deadline.Token).ConfigureAwait(false)))
                        {
                            // The device repeats its first reply; skip those.
                        }
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        owner._log.LogDebug("{Device} sent no handshake confirmation; continuing", device);
                    }
                }

                return new GoveeCipher(key);
            }
            finally
            {
                _handshake = null;
            }
        }

        private async Task<IBleCipher> HandshakeGcmAsync(CancellationToken cancellationToken)
        {
            var replies = Channel.CreateUnbounded<byte[]>();
            _handshake = replies;
            try
            {
                var txIvKey = GoveeGcmCipher.NewIvKey();
                await WriteRawAsync(GoveeGcmCipher.BuildHandshakeRequest(GoveeGcmCipher.NewIv(), txIvKey), cancellationToken)
                    .ConfigureAwait(false);

                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(HandshakeTimeout);

                try
                {
                    while (true)
                    {
                        var reply = await replies.Reader.ReadAsync(deadline.Token).ConfigureAwait(false);
                        if (GoveeGcmCipher.ParseHandshakeResponse(reply) is not { } response)
                        {
                            owner._log.LogDebug("{Device} sent {Reply} during the handshake", device, Convert.ToHexString(reply));
                            continue;
                        }

                        if (!GoveeGcmCipher.IdentityMatches(response.DeviceInfo, device.Address))
                        {
                            owner._log.LogInformation("{Device} identified itself as {Info}", device, Convert.ToHexString(response.DeviceInfo));
                        }

                        return new GoveeGcmCipher(GoveeGcmCipher.DeriveSessionKey(response.DeviceInfo), txIvKey, response.RxIvKey);
                    }
                }
                catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new GoveeException($"{device} did not answer the encryption handshake", ex);
                }
            }
            finally
            {
                _handshake = null;
            }
        }

        /// <summary>
        /// BlueZ keeps a link up after the program that made it dies. That link
        /// carries an encrypted session this run has no key for, so nothing
        /// would work over it. Dropping it gives the next handshake a clean start.
        /// </summary>
        private async Task ReleaseStaleLinkAsync(CancellationToken cancellationToken)
        {
            Dictionary<string, VariantValue> props;
            try
            {
                props = await owner._bus.GetAllAsync(_devicePath, BlueZBus.DeviceInterface, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is DBusExceptionBase or TimeoutException)
            {
                return; // Connect will report anything that matters
            }

            if (!props.TryGetValue("Connected", out var connected) || !connected.GetBool())
            {
                return;
            }

            owner._log.LogInformation("{Device} still has a link from before; starting it again", device);
            await DisconnectQuietlyAsync().ConfigureAwait(false);
        }

        private async Task WaitForServicesAsync(CancellationToken cancellationToken)
        {
            var deadline = DateTimeOffset.UtcNow + ServicesTimeout;
            while (DateTimeOffset.UtcNow < deadline)
            {
                var props = await owner._bus.GetAllAsync(_devicePath, BlueZBus.DeviceInterface, cancellationToken).ConfigureAwait(false);
                if (props.TryGetValue("Connected", out var connected) && !connected.GetBool())
                {
                    throw new GoveeException($"{device} dropped the Bluetooth connection while its services were being read");
                }
                if (props.TryGetValue("ServicesResolved", out var resolved) && resolved.GetBool())
                {
                    return;
                }
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
            throw new GoveeException($"{device} connected but its GATT services did not resolve in time");
        }

        /// <summary>
        /// Looks only under the first Govee service: the same UUIDs can also
        /// sit under the firmware update service.
        /// </summary>
        private string? FindGoveeCharacteristic(BlueZBus.ManagedObjects objects, string uuid)
        {
            var service = objects
                .Where(o => o.Key.StartsWith(_devicePath + "/", StringComparison.Ordinal)
                            && o.Value.TryGetValue(BlueZBus.ServiceInterface, out var props)
                            && props.TryGetValue("UUID", out var id)
                            && string.Equals(id.GetString(), ServiceUuid, StringComparison.OrdinalIgnoreCase))
                .Select(o => o.Key)
                .OrderBy(path => path, StringComparer.Ordinal)
                .FirstOrDefault();

            if (service is null)
            {
                return null;
            }

            foreach (var (path, interfaces) in objects)
            {
                if (path.StartsWith(service + "/", StringComparison.Ordinal)
                    && interfaces.TryGetValue(BlueZBus.CharacteristicInterface, out var props)
                    && props.TryGetValue("UUID", out var value)
                    && string.Equals(value.GetString(), uuid, StringComparison.OrdinalIgnoreCase))
                {
                    return path;
                }
            }
            return null;
        }

        private async Task WriteFrameAsync(byte[] frame, CancellationToken cancellationToken)
        {
            owner._log.LogTrace("{Device} -> {Frame}", device, Convert.ToHexString(frame));

            try
            {
                await WriteRawAsync(_cipher?.Wrap(frame) ?? frame, cancellationToken).ConfigureAwait(false);
            }
            catch (DBusErrorReplyException ex) when (IsDisconnected(ex))
            {
                OnDisconnected();
                owner._log.LogInformation("{Device} dropped the connection; reconnecting", device);
                await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
                await WriteRawAsync(_cipher?.Wrap(frame) ?? frame, cancellationToken).ConfigureAwait(false);
            }

            _lastActivity = DateTimeOffset.UtcNow;
        }

        private static bool IsDisconnected(DBusErrorReplyException ex)
            => ex.ErrorName is "org.bluez.Error.NotConnected" or "org.freedesktop.DBus.Error.UnknownObject"
               || ex.ErrorMessage.Contains("Not connected", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Always without a response: the characteristic does not allow write
        /// requests, and some firmware refuses them outright.
        /// </summary>
        private async Task WriteRawAsync(byte[] wire, CancellationToken cancellationToken)
        {
            try
            {
                await owner._bus.WriteValueAsync(_controlPath!, wire, "command", cancellationToken).ConfigureAwait(false);
            }
            catch (DBusErrorReplyException ex) when (ex.ErrorName.EndsWith(".InvalidArgs", StringComparison.Ordinal))
            {
                // Very old BlueZ rejects the type option.
                await owner._bus.WriteValueAsync(_controlPath!, wire, null, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task DisconnectQuietlyAsync()
        {
            try
            {
                await owner._bus.CallAsync(_devicePath, BlueZBus.DeviceInterface, "Disconnect", CancellationToken.None, TimeSpan.FromSeconds(10))
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                owner._log.LogDebug("Disconnecting {Device}: {Message}", device, ex.Message);
            }
        }

        /// <summary>
        /// Writes every couple of seconds, as the app does, because the firmware
        /// drops an idle link, and drops the connection once nothing has been
        /// sent for a while.
        /// </summary>
        private void StartKeepAlive()
        {
            _keepAlive?.Cancel();
            var cts = new CancellationTokenSource();
            _keepAlive = cts;

            _ = Task.Run(async () =>
            {
                try
                {
                    while (!cts.IsCancellationRequested && _connected)
                    {
                        await Task.Delay(KeepAliveInterval, cts.Token).ConfigureAwait(false);

                        if (!await _lock.WaitAsync(0, cts.Token).ConfigureAwait(false))
                        {
                            continue; // a command is in flight, which is keep-alive enough
                        }

                        try
                        {
                            if (!_keepConnected && DateTimeOffset.UtcNow - _lastActivity > TimeSpan.FromSeconds(owner._options.IdleSeconds))
                            {
                                owner._log.LogDebug("{Device} idle; disconnecting", device);
                                await DisconnectAsync().ConfigureAwait(false);
                                return;
                            }

                            if (_connected)
                            {
                                var keepAlive = LightFrames.KeepAlive();
                                owner._log.LogTrace("{Device} -> {Frame} (keep-alive)", device, Convert.ToHexString(keepAlive));
                                await WriteRawAsync(_cipher?.Wrap(keepAlive) ?? keepAlive, cts.Token).ConfigureAwait(false);
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            owner._log.LogDebug("Keep-alive to {Device} failed: {Message}", device, ex.Message);
                            OnDisconnected();
                            return;
                        }
                        finally
                        {
                            _lock.Release();
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Disconnected or disposed.
                }
            }, CancellationToken.None);
        }
    }
}
