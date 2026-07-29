using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Novee2Mqtt.Core;
using Novee2Mqtt.Undocumented;
using Microsoft.Extensions.Logging;
using MQTTnet;

namespace Novee2Mqtt.Iot;

/// <summary>A decoded status message from the account's AWS IoT topic.</summary>
public sealed class IotPacket
{
    public required string Sku { get; init; }
    public required string DeviceId { get; init; }
    public bool? OnOff { get; init; }
    public byte? Brightness { get; init; }
    public DeviceColor? Color { get; init; }
    public int? ColorTemperatureKelvin { get; init; }

    /// <summary>Raw BLE frames carried in the <c>op.command</c> array.</summary>
    public List<byte[]> Commands { get; init; } = [];
}

/// <summary>
/// Talks to Govee's AWS IoT broker using the per-account client certificate from
/// the app API. This is the undocumented push channel: it delivers device state
/// within a second or two of a change, which neither the Platform API nor
/// polling can match.
/// </summary>
public sealed class IotClient : IAsyncDisposable
{
    private readonly ILogger<IotClient> _log;
    private readonly MqttConnection _connection;
    private readonly string _accountTopic;

    private IotClient(ILogger<IotClient> log, MqttConnection connection, string accountTopic)
    {
        _log = log;
        _connection = connection;
        _accountTopic = accountTopic;

        _connection.OnMessage = (_, payload, ct) => HandleMessageAsync(payload, ct);
        _connection.OnReconnected = ct => _connection.SubscribeAsync([_accountTopic], ct);
    }

    /// <summary>Invoked for each status packet. Set by the service before connecting.</summary>
    public Func<IotPacket, CancellationToken, Task>? PacketHandler { get; set; }

    public bool IsConnected => _connection.IsConnected;

    public static IotClient Create(
        ILogger<IotClient> log,
        IotKey iotKey,
        LoginAccountResponse account,
        string? amazonRootCaPath)
    {
        var options = new MqttClientOptionsBuilder()
            .WithClientId($"AP/{account.AccountId}/{Guid.NewGuid():N}")
            .WithTcpServer(iotKey.Endpoint, 8883)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(120))
            .WithCleanSession()
            .WithTlsOptions(tls =>
            {
                tls.UseTls();
                tls.WithClientCertificates(new X509Certificate2Collection(LoadClientCertificate(iotKey)));
                if (LoadTrustChain(log, amazonRootCaPath) is { } trustChain)
                {
                    tls.WithTrustChain(trustChain);
                }
            })
            .Build();

        // The account and device topics are credentials, so keep them out of logs.
        var connection = new MqttConnection(log, $"Govee AWS IoT {iotKey.Endpoint}:8883", options)
        {
            TopicsAreSensitive = true,
        };
        return new IotClient(log, connection, account.Topic);
    }

    /// <summary>
    /// Converts the base64 PKCS#12 blob from the app API into a certificate with
    /// its private key, for TLS client authentication.
    /// </summary>
    private static X509Certificate2 LoadClientCertificate(IotKey iotKey)
    {
        try
        {
            return X509CertificateLoader.LoadPkcs12(
                Convert.FromBase64String(iotKey.P12),
                iotKey.P12Pass,
                X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);
        }
        catch (Exception ex)
        {
            throw new GoveeException($"Failed to load the Govee IoT client certificate: {ex.Message}", ex);
        }
    }

    private static X509Certificate2Collection? LoadTrustChain(ILogger log, string? amazonRootCaPath)
    {
        // Amazon Root CA 1 is in the default trust store on any normal base
        // image, so falling back to system validation is fine.
        if (string.IsNullOrWhiteSpace(amazonRootCaPath) || !File.Exists(amazonRootCaPath))
        {
            return null;
        }

        try
        {
            var collection = new X509Certificate2Collection();
            collection.ImportFromPemFile(amazonRootCaPath);
            return collection.Count > 0 ? collection : null;
        }
        catch (Exception ex)
        {
            log.LogWarning("Could not load {Path}, falling back to the system trust store: {Message}",
                amazonRootCaPath, ex.Message);
            return null;
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _connection.ConnectAsync(TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
        await _connection.SubscribeAsync([_accountTopic], cancellationToken).ConfigureAwait(false);
        _connection.StartSupervisor();
    }

    private async Task HandleMessageAsync(string payload, CancellationToken cancellationToken)
    {
        IotPacket? packet;
        try
        {
            packet = ParsePacket(payload);
        }
        catch (Exception ex)
        {
            _log.LogError("Decoding IoT packet: {Message} {Payload}", ex.Message, payload);
            return;
        }

        if (packet is not null && PacketHandler is { } handler)
        {
            await handler(packet, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Parses a status message. The sku and device id appear at either the top
    /// level or inside <c>state</c> depending on the device, so both are checked.
    /// </summary>
    internal static IotPacket? ParsePacket(string payload)
    {
        var raw = Json.Deserialize<IotRawPacket>(payload);

        var sku = raw.Sku ?? raw.State?.Sku;
        var device = raw.Device ?? raw.State?.Device;

        if (sku is null || device is null)
        {
            return null;
        }

        var commands = new List<byte[]>();
        foreach (var encoded in raw.Op?.Command ?? [])
        {
            // Ignore frames we cannot decode rather than dropping the packet.
            var buffer = new byte[((encoded.Length / 4) + 1) * 3];
            if (Convert.TryFromBase64String(encoded, buffer, out var written))
            {
                commands.Add(buffer[..written]);
            }
        }

        return new IotPacket
        {
            Sku = sku,
            DeviceId = device,
            OnOff = raw.State?.OnOff is { } onOff ? onOff != 0 : null,
            Brightness = raw.State?.Brightness,
            Color = raw.State?.Color is { } c ? new DeviceColor(c.R, c.G, c.B) : null,
            ColorTemperatureKelvin = raw.State?.ColorTemperatureKelvin,
            Commands = commands,
        };
    }

    public bool IsDeviceCompatible(DeviceEntry device) => device.Topic is not null;

    private Task PublishAsync(DeviceEntry device, string cmd, int type, int cmdVersion, JsonObject? data, CancellationToken cancellationToken)
    {
        var msg = new JsonObject
        {
            ["cmd"] = cmd,
            ["cmdVersion"] = cmdVersion,
            ["transaction"] = $"v_{SceneCatalog.MillisecondTimestamp()}000",
            ["type"] = type,
        };

        if (data is not null)
        {
            msg["data"] = data;
        }

        return _connection.PublishAsync(
            device.RequireTopic(),
            new JsonObject { ["msg"] = msg }.ToJsonString(),
            cancellationToken);
    }

    public Task RequestStatusUpdateAsync(DeviceEntry device, CancellationToken cancellationToken = default)
        => PublishAsync(device, "status", type: 0, cmdVersion: 2, data: null, cancellationToken);

    public Task SetPowerStateAsync(DeviceEntry device, bool on, CancellationToken cancellationToken = default)
    {
        // The H5080/H5083 sockets use a different encoding for their power values.
        var value = device.Sku switch
        {
            "H5080" or "H5083" => on ? 17 : 16,
            _ => on ? 1 : 0,
        };

        return PublishAsync(device, "turn", 1, 0, new JsonObject { ["val"] = value }, cancellationToken);
    }

    public Task SetBrightnessAsync(DeviceEntry device, int percent, CancellationToken cancellationToken = default)
        => PublishAsync(device, "brightness", 1, 0, new JsonObject { ["val"] = percent }, cancellationToken);

    public Task SetColorTemperatureAsync(DeviceEntry device, int kelvin, CancellationToken cancellationToken = default)
        => PublishAsync(device, "colorwc", 1, 0, ColorPayload(DeviceColor.Black, kelvin), cancellationToken);

    public Task SetColorRgbAsync(DeviceEntry device, DeviceColor color, CancellationToken cancellationToken = default)
        => PublishAsync(device, "colorwc", 1, 0, ColorPayload(color, 0), cancellationToken);

    private static JsonObject ColorPayload(DeviceColor color, int kelvin) => new()
    {
        ["color"] = new JsonObject { ["r"] = color.R, ["g"] = color.G, ["b"] = color.B },
        ["colorTemInKelvin"] = kelvin,
    };

    /// <summary>Sends raw BLE frames, base64 encoded, via the <c>ptReal</c> passthrough.</summary>
    public Task SendRealAsync(DeviceEntry device, IEnumerable<string> commands, CancellationToken cancellationToken = default)
    {
        var array = new JsonArray();
        foreach (var command in commands)
        {
            array.Add(command);
        }

        return PublishAsync(device, "ptReal", 1, 0, new JsonObject { ["command"] = array }, cancellationToken);
    }

    /// <summary>
    /// Replays the captured IoT messages that make up a Tap-to-Run shortcut,
    /// publishing each to the device topic it was recorded against.
    /// </summary>
    public async Task ActivateOneClickAsync(ParsedOneClick item, CancellationToken cancellationToken = default)
    {
        foreach (var entry in item.Entries)
        {
            foreach (var message in entry.Messages)
            {
                await _connection.PublishAsync(entry.Topic, message.ToJsonString(), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public ValueTask DisposeAsync() => _connection.DisposeAsync();

    private sealed class IotRawPacket
    {
        [JsonPropertyName("sku")] public string? Sku { get; set; }
        [JsonPropertyName("device")] public string? Device { get; set; }
        [JsonPropertyName("state")] public IotStateUpdate? State { get; set; }
        [JsonPropertyName("op")] public IotOpData? Op { get; set; }
    }

    private sealed class IotStateUpdate
    {
        [JsonPropertyName("onOff")] public int? OnOff { get; set; }
        [JsonPropertyName("brightness")] public byte? Brightness { get; set; }
        [JsonPropertyName("color")] public IotColor? Color { get; set; }
        [JsonPropertyName("colorTemInKelvin")] public int? ColorTemperatureKelvin { get; set; }
        [JsonPropertyName("sku")] public string? Sku { get; set; }
        [JsonPropertyName("device")] public string? Device { get; set; }
    }

    private sealed class IotColor
    {
        [JsonPropertyName("r")] public byte R { get; set; }
        [JsonPropertyName("g")] public byte G { get; set; }
        [JsonPropertyName("b")] public byte B { get; set; }
    }

    private sealed class IotOpData
    {
        [JsonPropertyName("command")] public List<string> Command { get; set; } = [];
    }
}
