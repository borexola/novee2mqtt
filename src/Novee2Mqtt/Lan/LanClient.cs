using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Novee2Mqtt.Ble;
using Novee2Mqtt.Core;
using Microsoft.Extensions.Logging;

namespace Novee2Mqtt.Lan;

/// <summary>
/// Implements the Govee LAN protocol: UDP discovery on port 4001, replies on
/// 4002, and control on 4003. LAN control has the lowest latency and keeps
/// working when the internet connection is down, so it is preferred over both
/// cloud paths wherever a device supports it.
/// </summary>
public sealed class LanClient : IAsyncDisposable
{
    /// <summary>Port devices listen on for scan requests.</summary>
    private const int ScanPort = 4001;

    /// <summary>Port devices send their replies to. Must be bound exclusively.</summary>
    private const int ListenPort = 4002;

    /// <summary>Port devices listen on for control requests.</summary>
    private const int CommandPort = 4003;

    private static readonly IPAddress MulticastGroup = IPAddress.Parse("239.255.255.250");

    private readonly ILogger<LanClient> _log;
    private readonly DiscoOptions _options;
    private readonly UdpClient _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Channel<LanDevice> _discovered = Channel.CreateUnbounded<LanDevice>();
    private readonly List<ResponseListener> _listeners = [];
    private readonly SemaphoreSlim _listenersLock = new(1, 1);
    private Task? _receiveLoop;
    private Task? _discoveryLoop;

    private LanClient(ILogger<LanClient> log, DiscoOptions options, UdpClient listener)
    {
        _log = log;
        _options = options;
        _listener = listener;
    }

    /// <summary>Devices as they are discovered. Never completes while the client is alive.</summary>
    public ChannelReader<LanDevice> Discovered => _discovered.Reader;

    public static LanClient Start(ILogger<LanClient> log, DiscoOptions options)
    {
        UdpClient listener;
        try
        {
            listener = new UdpClient(new IPEndPoint(IPAddress.Any, ListenPort));
        }
        catch (SocketException ex)
        {
            throw new GoveeException(
                $"Cannot bind to UDP port {ListenPort}, which is required for the Govee LAN API to function. " +
                "The most likely cause is that another integration (perhaps `Govee LAN Control`, or " +
                "`homebridge-govee`) is already bound to that port. Both cannot run on the same host at the " +
                "same time. Consider disabling `Govee LAN Control` or setting `lanDisable` in `homebridge-govee`.",
                ex);
        }

        var client = new LanClient(log, options, listener);
        client._receiveLoop = Task.Run(() => client.ReceiveLoopAsync(client._shutdown.Token));
        client._discoveryLoop = Task.Run(() => client.DiscoveryLoopAsync(client._shutdown.Token));
        return client;
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await _listener.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogError("LAN receive failed: {Message}", ex.Message);
                continue;
            }

            try
            {
                await ProcessPacketAsync(received).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogError("LAN process packet from {Address}: {Message}", received.RemoteEndPoint.Address, ex.Message);
            }
        }
    }

    private async Task ProcessPacketAsync(UdpReceiveResult received)
    {
        var text = Encoding.UTF8.GetString(received.Buffer);
        _log.LogTrace("LAN packet from {Address}: {Payload}", received.RemoteEndPoint.Address, text);

        var envelope = Json.Deserialize<LanResponseEnvelope>(text);
        if (envelope.Msg is null)
        {
            return;
        }

        await _listenersLock.WaitAsync().ConfigureAwait(false);
        try
        {
            _listeners.RemoveAll(l => l.Completed);
            foreach (var listener in _listeners)
            {
                if (listener.Address.Equals(received.RemoteEndPoint.Address))
                {
                    listener.Channel.Writer.TryWrite(envelope.Msg);
                }
            }
        }
        finally
        {
            _listenersLock.Release();
        }

        if (envelope.Msg.Cmd == "scan" && envelope.Msg.Data is not null)
        {
            var device = envelope.Msg.Data.Deserialize<LanDevice>(Json.Options);
            if (device is not null)
            {
                // Devices report their own IP; trust the packet source instead so
                // that NAT or a stale report cannot make the device unreachable.
                device.Ip = received.RemoteEndPoint.Address.ToString();
                _discovered.Writer.TryWrite(device);
            }
        }
    }

    /// <summary>
    /// Re-broadcasts discovery with exponential backoff from 2s up to 60s, so a
    /// device that boots later still gets found without flooding the network.
    /// </summary>
    private async Task DiscoveryLoopAsync(CancellationToken cancellationToken)
    {
        var retryInterval = TimeSpan.FromSeconds(2);
        var maxRetry = TimeSpan.FromSeconds(60);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await SendScanAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError("LAN discovery broadcast failed: {Message}", ex.Message);
            }

            try
            {
                await Task.Delay(retryInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            retryInterval = retryInterval * 2 > maxRetry ? maxRetry : retryInterval * 2;
        }
    }

    private async Task SendScanAsync(CancellationToken cancellationToken)
    {
        var scan = BuildRequest("scan", new JsonObject { ["account_topic"] = "reserve" });

        foreach (var address in ResolveScanTargets())
        {
            try
            {
                await SendToAsync(address, ScanPort, scan, cancellationToken).ConfigureAwait(false);
                _log.LogTrace("Sent LAN discovery packet to {Address}", address);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError("Error broadcasting to {Address}: {Message}", address, ex.Message);
            }
        }
    }

    private List<IPAddress> ResolveScanTargets()
    {
        var addresses = new List<IPAddress>(_options.AdditionalAddresses);

        if (_options.EnableMulticast)
        {
            addresses.Add(MulticastGroup);
        }

        if (_options.GlobalBroadcast)
        {
            addresses.Add(IPAddress.Broadcast);
        }

        if (_options.BroadcastAllInterfaces)
        {
            foreach (var address in EnumerateInterfaceBroadcastAddresses())
            {
                addresses.Add(address);
            }
        }

        return addresses;
    }

    private IEnumerable<IPAddress> EnumerateInterfaceBroadcastAddresses()
    {
        NetworkInterface[] interfaces;
        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException ex)
        {
            _log.LogError("Enumerating network interfaces: {Message}", ex.Message);
            yield break;
        }

        foreach (var iface in interfaces)
        {
            if (iface.NetworkInterfaceType == NetworkInterfaceType.Loopback
                || iface.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            foreach (var unicast in iface.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.IPv4Mask is null)
                {
                    continue;
                }

                var addressBytes = unicast.Address.GetAddressBytes();
                var maskBytes = unicast.IPv4Mask.GetAddressBytes();
                var broadcastBytes = new byte[4];
                for (var i = 0; i < 4; i++)
                {
                    broadcastBytes[i] = (byte)(addressBytes[i] | (byte)~maskBytes[i]);
                }

                var broadcast = new IPAddress(broadcastBytes);
                _log.LogDebug("Adding broadcast {Broadcast} from interface {Interface}", broadcast, iface.Name);
                yield return broadcast;
            }
        }
    }

    private static byte[] BuildRequest(string cmd, JsonNode data)
    {
        var envelope = new JsonObject
        {
            ["msg"] = new JsonObject
            {
                ["cmd"] = cmd,
                ["data"] = data,
            },
        };
        return Encoding.UTF8.GetBytes(envelope.ToJsonString());
    }

    private async Task SendToAsync(IPAddress address, int port, byte[] payload, CancellationToken cancellationToken)
    {
        using var socket = new UdpClient(address.AddressFamily);

        if (IsMulticast(address))
        {
            socket.MulticastLoopback = false;
            try
            {
                socket.JoinMulticastGroup(address);
            }
            catch (SocketException ex)
            {
                // Joining is not required in order to send; on some hosts it fails
                // when no interface is explicitly bound.
                _log.LogTrace("JoinMulticastGroup({Address}) failed: {Message}", address, ex.Message);
            }
        }
        else
        {
            socket.EnableBroadcast = true;
        }

        await socket.SendAsync(payload, new IPEndPoint(address, port), cancellationToken).ConfigureAwait(false);
    }

    private static bool IsMulticast(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return address.IsIPv6Multicast;
        }
        var first = address.GetAddressBytes()[0];
        return first is >= 224 and <= 239;
    }

    public Task SendRequestAsync(IPAddress address, string cmd, JsonNode data, CancellationToken cancellationToken = default)
        => SendToAsync(address, CommandPort, BuildRequest(cmd, data), cancellationToken);

    public Task SendTurnAsync(LanDevice device, bool on, CancellationToken cancellationToken = default)
        => SendRequestAsync(device.Address, "turn", new JsonObject { ["value"] = on ? 1 : 0 }, cancellationToken);

    public Task SendBrightnessAsync(LanDevice device, int percent, CancellationToken cancellationToken = default)
        => SendRequestAsync(device.Address, "brightness", new JsonObject { ["value"] = percent }, cancellationToken);

    public Task SendColorRgbAsync(LanDevice device, DeviceColor color, CancellationToken cancellationToken = default)
        => SendRequestAsync(device.Address, "colorwc", new JsonObject
        {
            ["color"] = new JsonObject { ["r"] = color.R, ["g"] = color.G, ["b"] = color.B },
            ["colorTemInKelvin"] = 0,
        }, cancellationToken);

    public Task SendColorTemperatureAsync(LanDevice device, int kelvin, CancellationToken cancellationToken = default)
        => SendRequestAsync(device.Address, "colorwc", new JsonObject
        {
            ["color"] = new JsonObject { ["r"] = 0, ["g"] = 0, ["b"] = 0 },
            ["colorTemInKelvin"] = kelvin,
        }, cancellationToken);

    /// <summary>Sends raw BLE frames, base64 encoded, via the <c>ptReal</c> passthrough.</summary>
    public Task SendRealAsync(LanDevice device, IEnumerable<string> commands, CancellationToken cancellationToken = default)
    {
        var array = new JsonArray();
        foreach (var command in commands)
        {
            array.Add(command);
        }
        return SendRequestAsync(device.Address, "ptReal", new JsonObject { ["command"] = array }, cancellationToken);
    }

    public Task SendSceneAsync(LanDevice device, SetSceneCode scene, CancellationToken cancellationToken = default)
        => SendRealAsync(device, PacketManager.EncodeToBase64(PacketManager.GenericLight, scene), cancellationToken);

    /// <summary>
    /// Sends a request and waits for a matching reply, re-sending every
    /// <paramref name="retryInterval"/> until <paramref name="timeout"/> elapses.
    /// UDP requests to these devices are routinely dropped, so retrying is the
    /// difference between working and not.
    /// </summary>
    private async Task<TPayload> RequestAsync<TPayload>(
        IPAddress address,
        string expectedCmd,
        Func<CancellationToken, Task> send,
        TimeSpan retryInterval,
        TimeSpan timeout,
        CancellationToken cancellationToken)
        where TPayload : class
    {
        using var listener = await AddListenerAsync(address).ConfigureAwait(false);
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow <= deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await send(cancellationToken).ConfigureAwait(false);

            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attempt.CancelAfter(retryInterval);

            try
            {
                while (await listener.Channel.Reader.WaitToReadAsync(attempt.Token).ConfigureAwait(false))
                {
                    while (listener.Channel.Reader.TryRead(out var message))
                    {
                        if (message.Cmd == expectedCmd
                            && message.Data?.Deserialize<TPayload>(Json.Options) is { } payload)
                        {
                            return payload;
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // No reply within this attempt's window; try again.
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw new GoveeException($"timed out waiting for a LAN {expectedCmd} response from {address}");
    }

    /// <summary>
    /// Probes a single address and waits for it to identify itself. Used by
    /// <c>lan-control</c> and for devices that multicast cannot reach.
    /// </summary>
    public async Task<LanDevice> ScanIpAsync(IPAddress address, CancellationToken cancellationToken = default)
    {
        var scan = BuildRequest("scan", new JsonObject { ["account_topic"] = "reserve" });

        var device = await RequestAsync<LanDevice>(
            address,
            "scan",
            ct => SendToAsync(address, ScanPort, scan, ct),
            retryInterval: TimeSpan.FromSeconds(1),
            timeout: TimeSpan.FromSeconds(10),
            cancellationToken).ConfigureAwait(false);

        // Trust the address we probed over whatever the device reports.
        device.Ip = address.ToString();
        return device;
    }

    /// <summary>
    /// Asks a device for its current state. The request is re-sent every 350ms
    /// for up to 10 seconds, because UDP status requests are routinely dropped.
    /// </summary>
    public async Task<LanDeviceStatus> QueryStatusAsync(LanDevice device, CancellationToken cancellationToken = default)
    {
        var address = device.Address;

        var payload = await RequestAsync<LanDeviceStatusPayload>(
            address,
            "devStatus",
            ct =>
            {
                _log.LogTrace("Query status of {Address}", address);
                return SendRequestAsync(address, "devStatus", new JsonObject(), ct);
            },
            retryInterval: TimeSpan.FromMilliseconds(350),
            timeout: TimeSpan.FromSeconds(10),
            cancellationToken).ConfigureAwait(false);

        return payload.ToStatus();
    }

    private async Task<ResponseListener> AddListenerAsync(IPAddress address)
    {
        var listener = new ResponseListener(address, this);
        await _listenersLock.WaitAsync().ConfigureAwait(false);
        try
        {
            _listeners.Add(listener);
        }
        finally
        {
            _listenersLock.Release();
        }
        return listener;
    }

    private void RemoveListener(ResponseListener listener)
    {
        _listenersLock.Wait();
        try
        {
            _listeners.Remove(listener);
        }
        finally
        {
            _listenersLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        _listener.Dispose();

        foreach (var task in new[] { _receiveLoop, _discoveryLoop })
        {
            if (task is null)
            {
                continue;
            }
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown.
            }
        }

        _shutdown.Dispose();
        _listenersLock.Dispose();
    }

    private sealed class ResponseListener(IPAddress address, LanClient owner) : IDisposable
    {
        public IPAddress Address { get; } = address;

        public Channel<LanResponseMessage> Channel { get; } =
            System.Threading.Channels.Channel.CreateUnbounded<LanResponseMessage>();

        public bool Completed { get; private set; }

        public void Dispose()
        {
            Completed = true;
            Channel.Writer.TryComplete();
            owner.RemoveListener(this);
        }
    }
}
