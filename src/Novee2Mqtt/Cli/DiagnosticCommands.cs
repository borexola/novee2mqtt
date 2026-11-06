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

    /// <summary>
    /// Scans over Bluetooth and prints every Govee device in range, alongside
    /// which configured device it would be matched to.
    /// </summary>
    public static async Task<int> BleScanAsync(GoveeApp app, ArgParser parser, CancellationToken cancellationToken)
    {
        var options = app.Options.ToBleOptions();
        await using var ble = await BleClient.StartAsync(app.LoggerFactory.CreateLogger<BleClient>(), options, cancellationToken)
            .ConfigureAwait(false);

        var seconds = parser.GetInt("seconds") ?? options.ScanSeconds;
        Console.Error.WriteLine($"Scanning on {ble.AdapterName} for {seconds} seconds...");
        var found = await ble.ScanAsync(TimeSpan.FromSeconds(seconds), cancellationToken).ConfigureAwait(false);

        // Known device ids, so the scan can say which ones it accounts for.
        var known = new Dictionary<string, Devices.Device>(StringComparer.OrdinalIgnoreCase);
        if (!parser.HasFlag("skip-cloud"))
        {
            try
            {
                if (app.CreatePlatformClient() is { } platform)
                {
                    foreach (var info in await platform.GetDevicesAsync(cancellationToken).ConfigureAwait(false))
                    {
                        var device = new Devices.Device(info.Sku, info.Device);
                        device.SetHttpDeviceInfo(info);
                        known[info.Device] = device;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine($"Platform API device list unavailable: {ex.Message}");
            }
        }

        if (found.Count == 0)
        {
            Console.WriteLine("No Govee devices seen. Other devices in range: " +
                              (ble.Seen.Count == 0 ? "none" : ble.Seen.Count.ToString()));
        }

        foreach (var result in found)
        {
            var match = known.Values.FirstOrDefault(d => ble.Resolve(d.Sku, d.Id)?.Address == result.Address);
            var profile = BleLightProfile.Resolve(result.Sku ?? "");
            var encrypted = result.Encrypted switch { true => "yes", false => "no", null => "?" };
            Console.WriteLine(
                $"{result.Address} {result.Name,-22} rssi={result.Rssi?.ToString() ?? "?",4} encrypted={encrypted,-3} " +
                $"colour_mode=0x{(byte)profile.ColorMode:x2} brightness={(profile.BrightnessAsPercent ? "percent" : "byte"),-7} " +
                (match is null ? "(not in your Govee account, or no credentials)" : $"-> {match.Name()} ({match.Id})"));
        }

        foreach (var device in known.Values)
        {
            if (ble.Resolve(device.Sku, device.Id) is null)
            {
                Console.WriteLine($"not seen: {device.Name()} ({device.Id}), expected at {BleClient.ExpectedAddress(device.Id) ?? "?"}");
            }
        }

        return 0;
    }

    /// <summary>Controls one device directly over Bluetooth, bypassing all state tracking.</summary>
    public static async Task<int> BleControlAsync(GoveeApp app, ArgParser parser, CancellationToken cancellationToken)
    {
        var addressText = parser.GetString("address")
            ?? throw new GoveeException("ble-control requires --address <bluetooth address>");
        var address = BleNaming.NormalizeAddress(addressText)
            ?? throw new GoveeException($"--address expects a Bluetooth address such as AA:BB:CC:DD:EE:FF, got '{addressText}'");
        var sku = parser.GetString("sku")?.ToUpperInvariant()
            ?? throw new GoveeException("ble-control requires --sku <model>, for example --sku H6008");

        if (parser.GetString("color-mode") is { } mode)
        {
            var current = BleLightProfile.Resolve(sku);
            BleLightProfile.Overrides[sku] = current with { ColorMode = BleLightProfile.ParseColorMode(mode) };
        }
        if (parser.GetString("brightness-scale") is { } scale)
        {
            var current = BleLightProfile.Resolve(sku);
            BleLightProfile.Overrides[sku] = current with { BrightnessAsPercent = !scale.Equals("byte", StringComparison.OrdinalIgnoreCase) };
        }

        var options = app.Options.ToBleOptions();
        await using var ble = await BleClient.StartAsync(app.LoggerFactory.CreateLogger<BleClient>(), options, cancellationToken)
            .ConfigureAwait(false);

        var device = new BleDevice(address, sku, $"{sku} at {address}");
        var profile = BleLightProfile.Resolve(sku);
        Console.Error.WriteLine($"Using colour mode 0x{(byte)profile.ColorMode:x2}, brightness as {(profile.BrightnessAsPercent ? "percent" : "byte")}");

        var positional = parser.Positional;
        var subCommand = positional.Count > 1 ? positional[1].ToLowerInvariant() : "";
        var argument = positional.Count > 2 ? positional[2] : null;

        try
        {
            switch (subCommand)
            {
                case "on":
                    await ble.SendTurnAsync(device, true, cancellationToken).ConfigureAwait(false);
                    break;

                case "off":
                    await ble.SendTurnAsync(device, false, cancellationToken).ConfigureAwait(false);
                    break;

                case "brightness":
                    await ble.SendBrightnessAsync(device, RequireInt(argument, "brightness"), cancellationToken).ConfigureAwait(false);
                    break;

                case "temperature":
                    await ble.SendColorTemperatureAsync(device, RequireInt(argument, "temperature"), cancellationToken).ConfigureAwait(false);
                    break;

                case "color":
                    await ble.SendColorRgbAsync(device, CssColor.Parse(argument ?? ""), cancellationToken).ConfigureAwait(false);
                    break;

                case "status":
                    break;

                case "command":
                    var bytes = positional.Skip(2).Select(ParseByte).ToArray();
                    var frame = PacketManager.Frame(bytes);
                    Console.WriteLine($"frame: {Convert.ToHexString(frame)}");
                    await ble.SendFramesAsync(device, [frame], cancellationToken).ConfigureAwait(false);
                    break;

                default:
                    throw new GoveeException(
                        "ble-control needs one of: on, off, brightness <percent>, temperature <kelvin>, " +
                        "color <css-color>, status, command <byte>...");
            }

            var replies = await ble.QueryStatusAsync(device, cancellationToken).ConfigureAwait(false);
            var status = LanDeviceStatus.Empty;
            foreach (var reply in replies)
            {
                Console.WriteLine($"reply: {Convert.ToHexString(reply)}");
                status = LightFrames.ApplyReply(profile, status, reply) ?? status;
            }

            Console.WriteLine(replies.Count == 0
                ? "The device answered no status query. A write it ignored looks the same, so check the light itself."
                : status.On
                    ? $"on {status.Brightness}% {status.Color} {(status.ColorTemperatureKelvin > 0 ? status.ColorTemperatureKelvin + "k" : "")}"
                    : "off");
        }
        finally
        {
            await ble.DisconnectAsync(device).ConfigureAwait(false);
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
