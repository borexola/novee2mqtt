using System.Net;
using Novee2Mqtt.Core;
using Novee2Mqtt.Lan;

namespace Novee2Mqtt.Cli;

/// <summary>
/// Configuration for every command. Each setting can come from a command-line
/// flag or an environment variable; the flag wins. The Home Assistant add-on
/// only sets environment variables.
/// </summary>
public sealed class GoveeOptions
{
    // Govee credentials
    public string? ApiKey { get; set; }
    public string? Email { get; set; }
    public string? Password { get; set; }

    /// <summary>PEM bundle used to validate the AWS IoT endpoint. Optional.</summary>
    public string? AmazonRootCa { get; set; }

    // LAN discovery
    public bool NoMulticast { get; set; }
    public bool BroadcastAll { get; set; }
    public bool GlobalBroadcast { get; set; }
    public List<IPAddress> Scan { get; } = [];
    public int DiscoTimeout { get; set; } = 3;

    // MQTT broker
    public string? MqttHost { get; set; }
    public int MqttPort { get; set; } = 1883;
    public string? MqttUsername { get; set; }
    public string? MqttPassword { get; set; }
    public string HassDiscoveryPrefix { get; set; } = "homeassistant";
    public TemperatureScale TemperatureScale { get; set; } = TemperatureScale.Celsius;

    // Serve
    public int HttpPort { get; set; } = 8056;

    public string? CacheDir { get; set; }

    public string RequireApiKey() => ApiKey
        ?? throw new GoveeException(
            "Please specify the api key either via the --api-key parameter or by setting $GOVEE_API_KEY");

    public string RequireEmail() => Email
        ?? throw new GoveeException(
            "Please specify the govee account email either via the --govee-email parameter or by setting $GOVEE_EMAIL");

    public string RequirePassword() => Password
        ?? throw new GoveeException(
            "Please specify the govee account password either via the --govee-password parameter or by setting $GOVEE_PASSWORD");

    public string RequireMqttHost() => MqttHost
        ?? throw new GoveeException(
            "Please specify the mqtt broker either via the --mqtt-host parameter or by setting $GOVEE_MQTT_HOST");

    public bool HasGoveeAccount => Email is not null && Password is not null;

    public DiscoOptions ToDiscoOptions()
    {
        var options = new DiscoOptions
        {
            EnableMulticast = !NoMulticast,
            BroadcastAllInterfaces = BroadcastAll,
            GlobalBroadcast = GlobalBroadcast,
            DiscoveryTimeoutSeconds = DiscoTimeout,
        };
        options.AdditionalAddresses.AddRange(Scan);

        return options.ApplyEnvironmentOverrides();
    }

    /// <summary>
    /// Applies environment variables for anything not already set on the command
    /// line. LAN settings are handled later by <see cref="DiscoOptions.ApplyEnvironmentOverrides"/>.
    /// </summary>
    public void ApplyEnvironment()
    {
        ApiKey ??= Env.Get("GOVEE_API_KEY");
        Email ??= Env.Get("GOVEE_EMAIL");
        Password ??= Env.Get("GOVEE_PASSWORD");
        MqttHost ??= Env.Get("GOVEE_MQTT_HOST");
        MqttUsername ??= Env.Get("GOVEE_MQTT_USER");
        MqttPassword ??= Env.Get("GOVEE_MQTT_PASSWORD");
        CacheDir ??= Env.Get("GOVEE_CACHE_DIR");

        if (Env.GetInt("GOVEE_MQTT_PORT") is { } port)
        {
            MqttPort = port;
        }

        if (Env.Get("GOVEE_HASS_DISCOVERY_PREFIX") is { } prefix)
        {
            HassDiscoveryPrefix = prefix;
        }

        if (Env.Get("GOVEE_TEMPERATURE_SCALE") is { } scale)
        {
            TemperatureScale = TemperatureExtensions.ParseScale(scale);
        }

        if (Env.GetInt("GOVEE_HTTP_PORT") is { } httpPort)
        {
            HttpPort = httpPort;
        }
    }
}

/// <summary>
/// Minimal command-line parser: a subcommand, then <c>--flag</c>,
/// <c>--name value</c> or <c>--name=value</c> in any order.
/// </summary>
public sealed class ArgParser(IReadOnlyList<string> args)
{
    private readonly Dictionary<string, List<string>> _values = new(StringComparer.Ordinal);
    private readonly HashSet<string> _flags = new(StringComparer.Ordinal);
    private readonly List<string> _positional = [];
    private bool _parsed;

    /// <summary>Options that take no value, so a following token is positional.</summary>
    public HashSet<string> BooleanOptions { get; } = new(StringComparer.Ordinal)
    {
        "no-multicast", "broadcast-all", "global-broadcast", "skip-lan", "list", "help",
    };

    public IReadOnlyList<string> Positional
    {
        get
        {
            EnsureParsed();
            return _positional;
        }
    }

    private void EnsureParsed()
    {
        if (_parsed)
        {
            return;
        }
        _parsed = true;

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];

            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                _positional.Add(arg);
                continue;
            }

            var body = arg[2..];
            var eq = body.IndexOf('=');

            if (eq >= 0)
            {
                Add(body[..eq], body[(eq + 1)..]);
                continue;
            }

            if (BooleanOptions.Contains(body))
            {
                _flags.Add(body);
                continue;
            }

            if (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                Add(body, args[++i]);
                continue;
            }

            _flags.Add(body);
        }

        void Add(string name, string value)
        {
            if (!_values.TryGetValue(name, out var list))
            {
                list = [];
                _values[name] = list;
            }
            list.Add(value);
        }
    }

    public bool HasFlag(string name)
    {
        EnsureParsed();
        return _flags.Contains(name) || (_values.TryGetValue(name, out var v) && v.Count > 0 && Env.ParseTruthy(v[0]));
    }

    public string? GetString(string name)
    {
        EnsureParsed();
        return _values.TryGetValue(name, out var list) && list.Count > 0 ? list[^1] : null;
    }

    public IReadOnlyList<string> GetAll(string name)
    {
        EnsureParsed();
        return _values.TryGetValue(name, out var list) ? list : [];
    }

    public int? GetInt(string name)
    {
        var raw = GetString(name);
        if (raw is null)
        {
            return null;
        }
        if (!int.TryParse(raw, out var value))
        {
            throw new GoveeException($"--{name} expects an integer, got '{raw}'");
        }
        return value;
    }

    /// <summary>Builds options from the parsed flags, then layers the environment underneath.</summary>
    public GoveeOptions ToOptions()
    {
        var options = new GoveeOptions
        {
            ApiKey = GetString("api-key"),
            Email = GetString("govee-email"),
            Password = GetString("govee-password"),
            AmazonRootCa = GetString("amazon-root-ca"),
            NoMulticast = HasFlag("no-multicast"),
            BroadcastAll = HasFlag("broadcast-all"),
            GlobalBroadcast = HasFlag("global-broadcast"),
            MqttHost = GetString("mqtt-host"),
            MqttUsername = GetString("mqtt-username"),
            MqttPassword = GetString("mqtt-password"),
        };

        foreach (var entry in GetAll("scan"))
        {
            foreach (var part in entry.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!IPAddress.TryParse(part, out var address))
                {
                    throw new GoveeException($"--scan expects IP addresses, got '{part}'");
                }
                options.Scan.Add(address);
            }
        }

        if (GetInt("disco-timeout") is { } discoTimeout) options.DiscoTimeout = discoTimeout;
        if (GetInt("mqtt-port") is { } mqttPort) options.MqttPort = mqttPort;
        if (GetInt("http-port") is { } httpPort) options.HttpPort = httpPort;
        if (GetString("hass-discovery-prefix") is { } prefix) options.HassDiscoveryPrefix = prefix;
        if (GetString("temperature-scale") is { } scale) options.TemperatureScale = TemperatureExtensions.ParseScale(scale);

        options.ApplyEnvironment();

        // Default to the copy shipped next to the binary.
        options.AmazonRootCa ??= Path.Combine(AppContext.BaseDirectory, "AmazonRootCA1.pem");

        return options;
    }
}
