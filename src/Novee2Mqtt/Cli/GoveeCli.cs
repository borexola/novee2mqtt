using System.Runtime.InteropServices;
using Novee2Mqtt.Core;

namespace Novee2Mqtt.Cli;

public static class GoveeCli
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (Env.LoadDotEnv() is { } dotEnvPath)
        {
            Console.Error.WriteLine($"Loading environment overrides from {dotEnvPath}");
        }

        var parser = new ArgParser(args);

        var command = parser.Positional.Count > 0 ? parser.Positional[0].ToLowerInvariant() : "";

        if (command is "version" || parser.HasFlag("version"))
        {
            Console.WriteLine(VersionInfo.Version);
            return 0;
        }

        if (command is "help" || parser.HasFlag("help"))
        {
            PrintUsage();
            return 0;
        }

        if (command is "")
        {
            PrintUsage();
            return 2;
        }

        GoveeOptions options;
        try
        {
            options = parser.ToOptions();
        }
        catch (GoveeException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        // Checked before the app is constructed: a probe should not open the
        // cache or contact Govee, it only asks the running instance how it is.
        if (command is "health")
        {
            return await HealthAsync(options.HttpPort).ConfigureAwait(false);
        }

        using var app = GoveeApp.Create(options);
        using var shutdown = new CancellationTokenSource();

        // docker stop and the Home Assistant supervisor send SIGTERM. Cancelling
        // the default handling gives serve time to publish its offline state and
        // close connections before the process exits.
        void RequestShutdown(PosixSignalContext context)
        {
            context.Cancel = true;
            shutdown.Cancel();
        }

        using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, RequestShutdown);
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, RequestShutdown);

        try
        {
            return command switch
            {
                "serve" => await ServeCommand.RunAsync(app, shutdown.Token).ConfigureAwait(false),
                "list" => await DiagnosticCommands.ListAsync(app, parser, shutdown.Token).ConfigureAwait(false),
                "list-http" => await DiagnosticCommands.ListHttpAsync(app, shutdown.Token).ConfigureAwait(false),
                "lan-disco" => await DiagnosticCommands.LanDiscoAsync(app, shutdown.Token).ConfigureAwait(false),
                "lan-control" => await DiagnosticCommands.LanControlAsync(app, parser, shutdown.Token).ConfigureAwait(false),
                "http-control" => await DiagnosticCommands.HttpControlAsync(app, parser, shutdown.Token).ConfigureAwait(false),
                "undoc" => await DiagnosticCommands.UndocAsync(app, parser, shutdown.Token).ConfigureAwait(false),
                _ => Unknown(command),
            };
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (GoveeException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex}");
            return 1;
        }
    }

    /// <summary>
    /// Probes the local instance and mirrors its verdict in the exit code, so
    /// Docker's HEALTHCHECK works in an image that has no shell or curl.
    /// </summary>
    private static async Task<int> HealthAsync(int port)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        try
        {
            var response = await client.GetAsync($"http://127.0.0.1:{port}/api/health").ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            Console.WriteLine(body);
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"health check failed: {ex.Message}");
            return 1;
        }
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown command '{command}'");
        PrintUsage();
        return 2;
    }

    private static void PrintUsage()
    {
        Console.WriteLine($"""
            novee2mqtt {VersionInfo.Version} - a bridge between Govee devices and Home Assistant via MQTT

            USAGE:
              novee2mqtt <command> [options]

            COMMANDS:
              serve                      Run the bridge: discover devices, publish to MQTT, serve the web UI
              list                       List devices from every configured source
              list-http                  List devices known to the Govee Platform API
              lan-disco                  Probe the LAN and report devices that answer
              lan-control --ip <addr>    Control one device over the LAN
                  on | off | brightness <percent> | temperature <kelvin> | color <css-color>
                  scene [--list | <name>] | command <byte>...
              http-control --id <id>     Control one device through the Platform API
                  on | off | info | status | brightness <percent> | temperature <kelvin>
                  color <css-color> | scene [--list | <name>]   (music modes are "Music: <mode>" scenes)
              undoc                      Inspect Tap-to-Run shortcuts
                  dump-one-click | show-one-click | one-click <name>
              health                     Probe a running instance; exit 0 when healthy

            GOVEE CREDENTIALS:
              --api-key <key>            $GOVEE_API_KEY      Needed for scenes, segments and sensors
              --govee-email <email>      $GOVEE_EMAIL        Needed for room names, IoT and Tap-to-Run
              --govee-password <pass>    $GOVEE_PASSWORD

            MQTT:
              --mqtt-host <host>         $GOVEE_MQTT_HOST    The broker Home Assistant uses
              --mqtt-port <port>         $GOVEE_MQTT_PORT    Default 1883
              --mqtt-username <user>     $GOVEE_MQTT_USER
              --mqtt-password <pass>     $GOVEE_MQTT_PASSWORD
              --hass-discovery-prefix    $GOVEE_HASS_DISCOVERY_PREFIX  Default "homeassistant"
              --temperature-scale <C|F>  $GOVEE_TEMPERATURE_SCALE      Default C

            LAN DISCOVERY:
              --no-multicast             $GOVEE_LAN_NO_MULTICAST
              --broadcast-all            $GOVEE_LAN_BROADCAST_ALL
              --global-broadcast         $GOVEE_LAN_BROADCAST_GLOBAL
              --scan <ip[,ip...]>        $GOVEE_LAN_SCAN
              --disco-timeout <seconds>  $GOVEE_LAN_DISCO_TIMEOUT      Default 3

            OTHER:
              --http-port <port>         $GOVEE_HTTP_PORT    Web UI port, default 8056
              --amazon-root-ca <path>    PEM used to validate the AWS IoT endpoint
                                         $GOVEE_CACHE_DIR    Where to keep the on-disk cache
                                         $GOVEE_LOG          trace | debug | info | warn | error
            """);
    }
}
