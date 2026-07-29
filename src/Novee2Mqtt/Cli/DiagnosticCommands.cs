using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Novee2Mqtt.Ble;
using Novee2Mqtt.Core;
using Novee2Mqtt.Iot;
using Novee2Mqtt.Lan;
using Novee2Mqtt.Platform;
using Microsoft.Extensions.Logging;

namespace Novee2Mqtt.Cli;

/// <summary>
/// The one-shot commands used for setup and troubleshooting, as opposed to the
/// long-running <c>serve</c>.
/// </summary>
public static class DiagnosticCommands
{
    /// <summary>Lists devices from every configured source, merged.</summary>
    public static async Task<int> ListAsync(GoveeApp app, ArgParser parser, CancellationToken cancellationToken)
    {
        var state = app.State;
        var skipLan = parser.HasFlag("skip-lan");

        LanClient? lanClient = null;
        Task? discovery = null;

        if (!skipLan)
        {
            var options = app.Options.ToDiscoOptions();
            if (options.IsEmpty)
            {
                throw new GoveeException("Discovery options are empty");
            }

            Console.Error.WriteLine(
                $"Waiting {options.DiscoveryTimeoutSeconds} seconds for LAN discovery, use --skip-lan to skip...");

            lanClient = LanClient.Start(app.LoggerFactory.CreateLogger<LanClient>(), options);

            var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(options.DiscoveryTimeoutSeconds));

            discovery = ConsumeDiscoveryAsync(app, lanClient, deadline.Token);
        }

        if (app.CreatePlatformClient() is { } platform)
        {
            foreach (var info in await platform.GetDevicesAsync(cancellationToken).ConfigureAwait(false))
            {
                await state.UpdateDeviceAsync(info.Sku, info.Device, d => d.SetHttpDeviceInfo(info)).ConfigureAwait(false);
            }
        }

        if (app.CreateUndocClient() is { } undoc)
        {
            var account = await undoc.LoginAccountAsync(cancellationToken).ConfigureAwait(false);
            var info = await undoc.GetDeviceListAsync(account.Token, cancellationToken).ConfigureAwait(false);
            var rooms = info.Groups.ToDictionary(g => g.GroupId, g => g.GroupName);

            foreach (var entry in info.Devices)
            {
                await state.UpdateDeviceAsync(entry.Sku, entry.Device,
                    d => d.SetUndocDeviceInfo(entry, rooms.GetValueOrDefault(entry.GroupId))).ConfigureAwait(false);
            }
        }

        if (discovery is not null)
        {
            await discovery.ConfigureAwait(false);
        }

        if (lanClient is not null)
        {
            await lanClient.DisposeAsync().ConfigureAwait(false);
        }

        var devices = (await state.GetDevicesAsync().ConfigureAwait(false))
            .OrderBy(d => d.RoomName() ?? "", StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.Name(), StringComparer.OrdinalIgnoreCase);

        foreach (var device in devices)
        {
            var room = device.RoomName() is { } name ? $"({name})" : "";
            Console.WriteLine($"{device.Sku,-7} {device.Id} {device.IpAddress?.ToString() ?? "",-15} {device.Name()} {room}");
        }

        return 0;
    }

    private static async Task ConsumeDiscoveryAsync(GoveeApp app, LanClient client, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var lanDevice in client.Discovered.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await app.State.UpdateDeviceAsync(lanDevice.Sku, lanDevice.Device, d => d.SetLanDevice(lanDevice))
                    .ConfigureAwait(false);

                try
                {
                    var status = await client.QueryStatusAsync(lanDevice, cancellationToken).ConfigureAwait(false);
                    await app.State.UpdateDeviceAsync(lanDevice.Sku, lanDevice.Device, d => d.SetLanDeviceStatus(status))
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A device that does not answer still deserves a listing.
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The discovery window elapsed.
        }
    }

    /// <summary>Lists devices known to the Platform API only.</summary>
    public static async Task<int> ListHttpAsync(GoveeApp app, CancellationToken cancellationToken)
    {
        var client = app.CreatePlatformClient()
            ?? throw new GoveeException(app.Options.RequireApiKey());

        foreach (var info in await client.GetDevicesAsync(cancellationToken).ConfigureAwait(false))
        {
            await app.State.UpdateDeviceAsync(info.Sku, info.Device, d => d.SetHttpDeviceInfo(info)).ConfigureAwait(false);
        }

        foreach (var device in await app.State.GetDevicesAsync().ConfigureAwait(false))
        {
            Console.WriteLine($"{device.Sku,-7} {device.Id} {device.Name()}");
        }

        return 0;
    }

    /// <summary>Probes the LAN and prints whatever answers, with its current state.</summary>
    public static async Task<int> LanDiscoAsync(GoveeApp app, CancellationToken cancellationToken)
    {
        var options = app.Options.ToDiscoOptions();
        if (options.IsEmpty)
        {
            throw new GoveeException("Discovery options are empty");
        }

        await using var client = LanClient.Start(app.LoggerFactory.CreateLogger<LanClient>(), options);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.DiscoveryTimeoutSeconds));

        var seen = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            await foreach (var lanDevice in client.Discovered.ReadAllAsync(deadline.Token).ConfigureAwait(false))
            {
                if (!seen.Add(lanDevice.Device))
                {
                    continue;
                }

                string status;
                try
                {
                    var value = await client.QueryStatusAsync(lanDevice, deadline.Token).ConfigureAwait(false);
                    status = value.On
                        ? $"{value.Brightness}% #{value.Color.R:x2}{value.Color.G:x2}{value.Color.B:x2} {value.ColorTemperatureKelvin}k"
                        : "off";
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    status = ex.Message;
                }

                var device = new Devices.Device(lanDevice.Sku, lanDevice.Device);
                Console.WriteLine($"{lanDevice.Ip,-15} {device.ComputedName(),-10} {device.Id} {status}");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The discovery window elapsed.
        }

        return 0;
    }

    /// <summary>Controls one device directly over the LAN, bypassing all state tracking.</summary>
    public static async Task<int> LanControlAsync(GoveeApp app, ArgParser parser, CancellationToken cancellationToken)
    {
        var ipText = parser.GetString("ip")
            ?? throw new GoveeException("lan-control requires --ip <address>");

        if (!IPAddress.TryParse(ipText, out var address))
        {
            throw new GoveeException($"--ip expects an IP address, got '{ipText}'");
        }

        await using var client = LanClient.Start(app.LoggerFactory.CreateLogger<LanClient>(), new DiscoOptions());
        var device = await client.ScanIpAsync(address, cancellationToken).ConfigureAwait(false);

        var positional = parser.Positional;
        var subCommand = positional.Count > 1 ? positional[1].ToLowerInvariant() : "";
        var argument = positional.Count > 2 ? positional[2] : null;

        switch (subCommand)
        {
            case "on":
                await client.SendTurnAsync(device, true, cancellationToken).ConfigureAwait(false);
                break;

            case "off":
                await client.SendTurnAsync(device, false, cancellationToken).ConfigureAwait(false);
                break;

            case "brightness":
                await client.SendBrightnessAsync(device, RequireInt(argument, "brightness"), cancellationToken)
                    .ConfigureAwait(false);
                break;

            case "temperature":
                await client.SendColorTemperatureAsync(device, RequireInt(argument, "temperature"), cancellationToken)
                    .ConfigureAwait(false);
                break;

            case "color":
                await client.SendColorRgbAsync(device, CssColor.Parse(argument ?? ""), cancellationToken)
                    .ConfigureAwait(false);
                break;

            case "scene":
                await LanSceneAsync(app, client, device, parser, argument, cancellationToken).ConfigureAwait(false);
                break;

            case "command":
                var bytes = positional.Skip(2).Select(ParseByte).ToArray();
                var encoded = PacketManager.RawToBase64(bytes);
                Console.WriteLine($"encoded: [{string.Join(", ", encoded)}]");
                await client.SendRealAsync(device, encoded, cancellationToken).ConfigureAwait(false);
                break;

            default:
                throw new GoveeException(
                    "lan-control needs one of: on, off, brightness <percent>, temperature <kelvin>, " +
                    "color <css-color>, scene [--list | <name>], command <byte>...");
        }

        return 0;
    }

    private static async Task LanSceneAsync(
        GoveeApp app,
        LanClient client,
        LanDevice device,
        ArgParser parser,
        string? sceneName,
        CancellationToken cancellationToken)
    {
        if (parser.HasFlag("list"))
        {
            foreach (var name in (await app.SceneCatalog.ListLanSceneNamesAsync(device.Sku, cancellationToken)
                         .ConfigureAwait(false)).Order(StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine(name);
            }
            return;
        }

        if (sceneName is null)
        {
            throw new GoveeException("scene requires a name, or --list");
        }

        var code = await app.SceneCatalog.FindSceneCodeAsync(device.Sku, sceneName, cancellationToken).ConfigureAwait(false)
            ?? throw new GoveeException($"scene {sceneName} not found");

        var commands = PacketManager.EncodeToBase64(PacketManager.GenericLight, code);
        Console.WriteLine($"Computed [{string.Join(", ", commands)}]");
        await client.SendRealAsync(device, commands, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Controls one device through the Platform API, and dumps its metadata or state.</summary>
    public static async Task<int> HttpControlAsync(GoveeApp app, ArgParser parser, CancellationToken cancellationToken)
    {
        var id = parser.GetString("id")
            ?? throw new GoveeException("http-control requires --id <device id>");

        var client = app.CreatePlatformClient()
            ?? throw new GoveeException(app.Options.RequireApiKey());

        var device = await client.GetDeviceByIdAsync(id, cancellationToken).ConfigureAwait(false);

        var positional = parser.Positional;
        var subCommand = positional.Count > 1 ? positional[1].ToLowerInvariant() : "";
        var argument = positional.Count > 2 ? positional[2] : null;

        switch (subCommand)
        {
            case "on":
            case "off":
                Dump(await client.SetPowerStateAsync(device, subCommand == "on", cancellationToken).ConfigureAwait(false));
                break;

            case "info":
                Console.WriteLine(Json.SerializePretty(device));
                break;

            case "status":
                Console.WriteLine(Json.SerializePretty(
                    await client.GetDeviceStateAsync(device, cancellationToken).ConfigureAwait(false)));
                break;

            case "brightness":
                Dump(await client.SetBrightnessAsync(device, RequireInt(argument, "brightness"), cancellationToken)
                    .ConfigureAwait(false));
                break;

            case "temperature":
                Dump(await client.SetColorTemperatureAsync(device, RequireInt(argument, "temperature"), cancellationToken)
                    .ConfigureAwait(false));
                break;

            case "color":
                Dump(await client.SetColorRgbAsync(device, CssColor.Parse(argument ?? ""), cancellationToken)
                    .ConfigureAwait(false));
                break;

            case "scene":
                if (parser.HasFlag("list"))
                {
                    foreach (var name in (await client.ListSceneNamesAsync(device, cancellationToken).ConfigureAwait(false))
                             .Order(StringComparer.OrdinalIgnoreCase))
                    {
                        Console.WriteLine(name);
                    }
                }
                else if (argument is not null)
                {
                    await client.SetSceneByNameAsync(device, argument, cancellationToken).ConfigureAwait(false);
                }
                break;

            default:
                // Music modes are reached through `scene`, which lists and
                // accepts them under a "Music: " prefix.
                throw new GoveeException(
                    "http-control needs one of: on, off, info, status, brightness <percent>, " +
                    "temperature <kelvin>, color <css-color>, scene [--list | <name>]");
        }

        return 0;

        static void Dump(ControlDeviceResponseCapability result) => Console.WriteLine(Json.SerializePretty(result));
    }

    /// <summary>Inspects and triggers Tap-to-Run shortcuts.</summary>
    public static async Task<int> UndocAsync(GoveeApp app, ArgParser parser, CancellationToken cancellationToken)
    {
        var client = app.CreateUndocClient()
            ?? throw new GoveeException(app.Options.RequireEmail());

        var positional = parser.Positional;
        var subCommand = positional.Count > 1 ? positional[1].ToLowerInvariant() : "";

        switch (subCommand)
        {
            case "dump-one-click":
                var token = await client.LoginCommunityAsync(cancellationToken).ConfigureAwait(false);
                var shortcuts = await client.GetSavedOneClickShortcutsAsync(token, cancellationToken).ConfigureAwait(false);
                Console.WriteLine(Json.SerializePretty(shortcuts));
                break;

            case "show-one-click":
                Console.WriteLine(Json.SerializePretty(
                    await client.ParseOneClicksAsync(cancellationToken).ConfigureAwait(false)));
                break;

            case "one-click":
                var name = positional.Count > 2 ? positional[2] : null;
                if (name is null)
                {
                    throw new GoveeException("one-click requires the shortcut name");
                }

                var items = await client.ParseOneClicksAsync(cancellationToken).ConfigureAwait(false);
                var item = items.FirstOrDefault(i => i.Name == name)
                    ?? throw new GoveeException($"didn't find item {name}");

                var account = await client.LoginAccountAsync(cancellationToken).ConfigureAwait(false);
                var iotKey = await client.GetIotKeyAsync(account.Token, cancellationToken).ConfigureAwait(false);

                await using (var iot = IotClient.Create(
                    app.LoggerFactory.CreateLogger<IotClient>(), iotKey, account, app.Options.AmazonRootCa))
                {
                    await iot.StartAsync(cancellationToken).ConfigureAwait(false);
                    await iot.ActivateOneClickAsync(item, cancellationToken).ConfigureAwait(false);

                    // Give the publishes a moment to leave the socket before the
                    // client is torn down.
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                }
                break;

            default:
                throw new GoveeException("undoc needs one of: dump-one-click, show-one-click, one-click <name>");
        }

        return 0;
    }

    private static int RequireInt(string? value, string what)
    {
        if (value is null || !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new GoveeException($"{what} requires a numeric argument");
        }
        return parsed;
    }

    /// <summary>Accepts decimal or 0x-prefixed hex.</summary>
    private static byte ParseByte(string value)
    {
        var text = value.Trim();
        var isHex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);

        if (byte.TryParse(
                isHex ? text[2..] : text,
                isHex ? NumberStyles.HexNumber : NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsed))
        {
            return parsed;
        }

        throw new GoveeException($"'{value}' is not a byte value");
    }
}
