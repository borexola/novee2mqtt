using System.Globalization;
using System.Reflection;

namespace Novee2Mqtt.Core;

public static class Env
{
    public static string? Get(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public static int? GetInt(string name)
    {
        var raw = Get(name);
        if (raw is null)
        {
            return null;
        }
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            throw new GoveeException($"${name} is invalid: '{raw}' is not an integer");
        }
        return value;
    }

    public static bool? GetBool(string name)
    {
        var raw = Get(name);
        return raw is null ? null : ParseTruthy(raw);
    }

    /// <summary>
    /// When set, the account and device MQTT topics are logged verbatim instead
    /// of redacted. Those topics act as credentials, so this is for debugging only.
    /// </summary>
    public static bool LogSensitiveData
    {
        get
        {
            try
            {
                return GetBool("GOVEE_LOG_SENSITIVE_DATA") ?? false;
            }
            catch (GoveeException)
            {
                return false;
            }
        }
    }

    /// <summary>Accepts true/yes/on/1 and false/no/off/0.</summary>
    public static bool ParseTruthy(string text)
    {
        var value = text.Trim();
        if (value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || value.Equals("on", StringComparison.OrdinalIgnoreCase)
            || value == "1")
        {
            return true;
        }

        if (value.Equals("false", StringComparison.OrdinalIgnoreCase)
            || value.Equals("no", StringComparison.OrdinalIgnoreCase)
            || value.Equals("off", StringComparison.OrdinalIgnoreCase)
            || value == "0")
        {
            return false;
        }

        throw new GoveeException($"invalid value '{text}', expected true/yes/on/1 or false/no/off/0");
    }

    /// <summary>
    /// Loads KEY=VALUE lines from a .env file if one is present, without
    /// overwriting variables that are already set in the environment.
    /// </summary>
    public static string? LoadDotEnv(string? path = null)
    {
        path ??= Path.Combine(Directory.GetCurrentDirectory(), ".env");
        if (!File.Exists(path))
        {
            return null;
        }

        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith("export ", StringComparison.Ordinal))
            {
                line = line[7..].TrimStart();
            }

            var eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
            {
                value = value[1..^1];
            }

            if (Environment.GetEnvironmentVariable(key) is null)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }

        return path;
    }
}

public static class VersionInfo
{
    private static readonly Lazy<string> Cached = new(() =>
    {
        var ciTag = Env.Get("GOVEE_CI_TAG");
        if (!string.IsNullOrEmpty(ciTag))
        {
            return ciTag;
        }

        var informational = typeof(VersionInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (!string.IsNullOrEmpty(informational))
        {
            // Strip the "+<commit sha>" source-revision suffix the SDK appends.
            var plus = informational.IndexOf('+');
            return plus > 0 ? informational[..plus] : informational;
        }

        return typeof(VersionInfo).Assembly.GetName().Version?.ToString() ?? "0.0.0";
    });

    public static string Version => Cached.Value;
}

