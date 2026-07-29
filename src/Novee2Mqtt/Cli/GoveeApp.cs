using Novee2Mqtt.Core;
using Novee2Mqtt.Devices;
using Novee2Mqtt.Hass;
using Novee2Mqtt.Platform;
using Novee2Mqtt.Undocumented;
using Microsoft.Extensions.Logging;

namespace Novee2Mqtt.Cli;

/// <summary>
/// Wires up the shared services every command needs, and creates the API
/// clients that the supplied credentials allow.
/// </summary>
public sealed class GoveeApp : IDisposable
{
    private GoveeApp(GoveeOptions options, ILoggerFactory loggerFactory, HttpClient httpClient, GoveeCache cache)
    {
        Options = options;
        LoggerFactory = loggerFactory;
        HttpClient = httpClient;
        Cache = cache;
        SceneCatalog = new SceneCatalog(loggerFactory.CreateLogger<SceneCatalog>(), httpClient, cache);
        State = new ServiceState(loggerFactory.CreateLogger<ServiceState>(), SceneCatalog)
        {
            HassDiscoveryPrefix = options.HassDiscoveryPrefix,
            TemperatureScale = options.TemperatureScale,
        };
    }

    public GoveeOptions Options { get; }
    public ILoggerFactory LoggerFactory { get; }
    public HttpClient HttpClient { get; }
    public GoveeCache Cache { get; }
    public SceneCatalog SceneCatalog { get; }
    public ServiceState State { get; }

    public static GoveeApp Create(GoveeOptions options)
    {
        var loggerFactory = CreateLoggerFactory();

        var httpClient = new HttpClient
        {
            // Individual calls apply their own, shorter, deadlines.
            Timeout = TimeSpan.FromSeconds(120),
        };

        var cache = new GoveeCache(loggerFactory.CreateLogger<GoveeCache>(), options.CacheDir);

        return new GoveeApp(options, loggerFactory, httpClient, cache);
    }

    private static ILoggerFactory CreateLoggerFactory()
    {
        var level = ResolveLogLevel();

        return Microsoft.Extensions.Logging.LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(level);
            builder.AddConsole(console => console.FormatterName = CompactConsoleFormatter.FormatterName);
            builder.AddConsoleFormatter<CompactConsoleFormatter, Microsoft.Extensions.Logging.Console.ConsoleFormatterOptions>();

            // Kestrel's request logging is noise for a service like this.
            builder.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
            builder.AddFilter("Microsoft.Hosting", LogLevel.Warning);
        });
    }

    /// <summary>
    /// Reads GOVEE_LOG, accepting either a bare level or a <c>target=level</c>
    /// list of which the last level wins.
    /// </summary>
    private static LogLevel ResolveLogLevel()
    {
        var raw = Env.Get("GOVEE_LOG");
        if (raw is null)
        {
            return LogLevel.Information;
        }

        LogLevel? resolved = null;
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var token = part.Contains('=') ? part[(part.IndexOf('=') + 1)..] : part;
            resolved = token.Trim().ToLowerInvariant() switch
            {
                "trace" => LogLevel.Trace,
                "debug" => LogLevel.Debug,
                "info" => LogLevel.Information,
                "warn" or "warning" => LogLevel.Warning,
                "error" => LogLevel.Error,
                "off" or "none" => LogLevel.None,
                _ => resolved,
            };
        }

        return resolved ?? LogLevel.Information;
    }

    public PlatformApiClient? CreatePlatformClient()
    {
        if (Options.ApiKey is not { } apiKey)
        {
            return null;
        }

        return new PlatformApiClient(
            LoggerFactory.CreateLogger<PlatformApiClient>(), HttpClient, Cache, SceneCatalog, apiKey);
    }

    public UndocumentedApiClient? CreateUndocClient()
    {
        if (!Options.HasGoveeAccount)
        {
            return null;
        }

        return new UndocumentedApiClient(
            LoggerFactory.CreateLogger<UndocumentedApiClient>(),
            HttpClient,
            Cache,
            Options.RequireEmail(),
            Options.RequirePassword());
    }

    public EntityEnumerator CreateEntityEnumerator()
        => new(LoggerFactory.CreateLogger<EntityEnumerator>(), State);

    public void Dispose()
    {
        Cache.Dispose();
        HttpClient.Dispose();
        // Disposing the factory flushes the console logger's queue, so nothing
        // is lost when the container stops.
        LoggerFactory.Dispose();
    }
}

/// <summary>
/// One line per event: <c>2026-07-27T21:20:26 INF HassClient message</c>.
/// The stock formatters interleave the event id and full category namespace,
/// which is noise in `docker logs` and the add-on log viewer.
/// </summary>
internal sealed class CompactConsoleFormatter()
    : Microsoft.Extensions.Logging.Console.ConsoleFormatter(FormatterName)
{
    public const string FormatterName = "govee-compact";

    public override void Write<TState>(
        in Microsoft.Extensions.Logging.Abstractions.LogEntry<TState> logEntry,
        IExternalScopeProvider? scopeProvider,
        TextWriter textWriter)
    {
        var message = logEntry.Formatter(logEntry.State, logEntry.Exception);
        if (string.IsNullOrEmpty(message) && logEntry.Exception is null)
        {
            return;
        }

        var level = logEntry.LogLevel switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "???",
        };

        // Just the class name; the namespaces carry no information here.
        var category = logEntry.Category;
        var lastDot = category.LastIndexOf('.');
        if (lastDot >= 0)
        {
            category = category[(lastDot + 1)..];
        }

        textWriter.Write(DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
        textWriter.Write(' ');
        textWriter.Write(level);
        textWriter.Write(' ');
        textWriter.Write(category);
        textWriter.Write(' ');
        textWriter.WriteLine(message);

        if (logEntry.Exception is { } exception)
        {
            textWriter.WriteLine(exception.ToString());
        }
    }
}
