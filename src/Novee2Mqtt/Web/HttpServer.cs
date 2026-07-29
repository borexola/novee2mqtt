using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Novee2Mqtt.Core;
using Novee2Mqtt.Devices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Novee2Mqtt.Web;

/// <summary>
/// A small REST API plus the bundled control panel. Useful on its own for
/// scripting, and the only view into the bridge when Home Assistant is not the
/// consumer.
/// </summary>
public static class HttpServer
{
    private static readonly DateTimeOffset StartedAt = DateTimeOffset.UtcNow;

    /// <summary>Matches the options configured on the host, for hand-rolled writes such as SSE.</summary>
    private static readonly JsonSerializerOptions ApiJson = new()
    {
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public sealed class DeviceItem
    {
        [JsonPropertyName("sku")] public required string Sku { get; init; }
        [JsonPropertyName("id")] public required string Id { get; init; }
        [JsonPropertyName("name")] public required string Name { get; init; }
        [JsonPropertyName("room")] public string? Room { get; init; }
        [JsonPropertyName("ip")] public string? Ip { get; init; }
        [JsonPropertyName("state")] public DeviceState? State { get; init; }
    }

    public static WebApplication Build(ServiceState state, GoveeCache cache, int port, ILoggerFactory loggerFactory)
    {
        // The .NET base images preset these, which makes Kestrel bind port 8080
        // and log an override warning. The --http-port flag is the only source
        // of truth here, so clear them regardless of which image we run in.
        Environment.SetEnvironmentVariable("ASPNETCORE_URLS", null);
        Environment.SetEnvironmentVariable("ASPNETCORE_HTTP_PORTS", null);

        var builder = WebApplication.CreateSlimBuilder();

        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Any, port));
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new PassThroughLoggerProvider(loggerFactory));

        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = null;
            options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        });

        var app = builder.Build();

        MapRoutes(app, state, cache);
        MapObservability(app, state);
        MapAssets(app);

        return app;
    }

    private static void MapAssets(WebApplication app)
    {
        // The UI ships alongside the binary; in the container that is /app/assets.
        var assetsPath = Path.Combine(AppContext.BaseDirectory, "assets");

        if (Directory.Exists(assetsPath))
        {
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(assetsPath),
                RequestPath = "/assets",
            });
        }

        app.MapGet("/", () => Results.Redirect("/assets/index.html"));
    }

    private static async Task<List<DeviceItem>> BuildItemsAsync(ServiceState state)
    {
        var devices = await state.GetDevicesAsync().ConfigureAwait(false);

        return devices
            .OrderBy(d => d.RoomName() ?? "", StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.Name(), StringComparer.OrdinalIgnoreCase)
            .Select(d => new DeviceItem
            {
                Sku = d.Sku,
                Id = d.Id,
                Name = d.Name(),
                Room = d.RoomName(),
                Ip = d.IpAddress?.ToString(),
                State = d.ComputeDeviceState(),
            })
            .ToList();
    }

    private static void MapRoutes(WebApplication app, ServiceState state, GoveeCache cache)
    {
        app.MapGet("/api/devices", async () => Results.Json(await BuildItemsAsync(state), ApiJson));

        app.MapGet("/api/device/{id}", async (string id) =>
        {
            var items = await BuildItemsAsync(state).ConfigureAwait(false);
            var item = items.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase));
            return item is null
                ? Respond(StatusCodes.Status404NotFound, $"device '{id}' not found")
                : Results.Json(item, ApiJson);
        });

        app.MapGet("/api/device/{id}/power/on", (string id) =>
            ControlAsync(state, id, (s, d, ct) => s.DevicePowerOnAsync(d, true, ct)));

        app.MapGet("/api/device/{id}/power/off", (string id) =>
            ControlAsync(state, id, (s, d, ct) => s.DevicePowerOnAsync(d, false, ct)));

        // Convenience for wall panels and scripts that do not track state themselves.
        app.MapGet("/api/device/{id}/toggle", async (string id) =>
        {
            var device = await state.ResolveDeviceAsync(id).ConfigureAwait(false);
            if (device is null)
            {
                return Respond(StatusCodes.Status404NotFound, $"device '{id}' not found");
            }

            var on = device.ComputeDeviceState()?.On ?? false;
            return await ControlAsync(state, id, (s, d, ct) => s.DevicePowerOnAsync(d, !on, ct)).ConfigureAwait(false);
        });

        app.MapGet("/api/device/{id}/brightness/{level:int}", (string id, int level) =>
            ControlAsync(state, id, (s, d, ct) =>
                s.DeviceSetBrightnessAsync(d, (byte)Math.Clamp(level, 0, 100), ct)));

        app.MapGet("/api/device/{id}/colortemp/{kelvin:int}", (string id, int kelvin) =>
            ControlAsync(state, id, (s, d, ct) => s.DeviceSetColorTemperatureAsync(d, kelvin, ct)));

        app.MapGet("/api/device/{id}/color/{color}", (string id, string color) =>
        {
            if (!CssColor.TryParse(color, out var parsed))
            {
                return Task.FromResult(Respond(StatusCodes.Status400BadRequest, $"error parsing color '{color}'"));
            }
            return ControlAsync(state, id, (s, d, ct) => s.DeviceSetColorRgbAsync(d, parsed, ct));
        });

        app.MapGet("/api/device/{id}/scene/{scene}", (string id, string scene) =>
            ControlAsync(state, id, (s, d, ct) => s.DeviceSetSceneAsync(d, scene, ct)));

        app.MapGet("/api/device/{id}/scenes", async (string id) =>
        {
            var device = await state.ResolveDeviceAsync(id).ConfigureAwait(false);
            if (device is null)
            {
                return Respond(StatusCodes.Status404NotFound, $"device '{id}' not found");
            }

            try
            {
                var scenes = await state.DeviceListScenesAsync(device).ConfigureAwait(false);
                return Results.Json(scenes, ApiJson);
            }
            catch (Exception ex)
            {
                return Respond(StatusCodes.Status500InternalServerError, ex.Message);
            }
        });

        MapRoomRoutes(app, state);

        app.MapGet("/api/oneclicks", async () =>
        {
            if (state.UndocClient is not { } undoc)
            {
                return Respond(StatusCodes.Status500InternalServerError, "Undoc API client is not available");
            }

            try
            {
                return Results.Json(await undoc.ParseOneClicksAsync().ConfigureAwait(false), ApiJson);
            }
            catch (Exception ex)
            {
                return Respond(StatusCodes.Status500InternalServerError, ex.Message);
            }
        });

        app.MapGet("/api/oneclick/activate/{name}", async (string name) =>
        {
            if (state.UndocClient is not { } undoc)
            {
                return Respond(StatusCodes.Status500InternalServerError, "Undoc API client is not available");
            }
            if (state.IotClient is not { } iot)
            {
                return Respond(StatusCodes.Status500InternalServerError, "AWS IoT client is not available");
            }

            try
            {
                var items = await undoc.ParseOneClicksAsync().ConfigureAwait(false);
                var item = items.FirstOrDefault(i => i.Name == name);
                if (item is null)
                {
                    return Respond(StatusCodes.Status404NotFound, $"didn't find item {name}");
                }

                await iot.ActivateOneClickAsync(item).ConfigureAwait(false);
                return Respond(StatusCodes.Status200OK, "ok");
            }
            catch (Exception ex)
            {
                return Respond(StatusCodes.Status500InternalServerError, ex.Message);
            }
        });

        app.MapPost("/api/purge-caches", () =>
        {
            cache.Purge();
            return Respond(StatusCodes.Status200OK, "ok");
        });
    }

    /// <summary>
    /// Room-level control. Govee's own rooms come from the account, so this
    /// mirrors the grouping the phone app shows.
    /// </summary>
    private static void MapRoomRoutes(WebApplication app, ServiceState state)
    {
        app.MapGet("/api/rooms", async () =>
        {
            var items = await BuildItemsAsync(state).ConfigureAwait(false);

            var rooms = items
                .GroupBy(i => i.Room ?? "", StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Key.Length > 0)
                .Select(g => new
                {
                    name = g.Key,
                    devices = g.Count(),
                    on = g.Count(i => i.State?.On ?? false),
                })
                .OrderBy(r => r.name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return Results.Json(rooms, ApiJson);
        });

        app.MapGet("/api/room/{room}/power/{power}", async (string room, string power) =>
        {
            if (power is not ("on" or "off"))
            {
                return Respond(StatusCodes.Status400BadRequest, "power must be 'on' or 'off'");
            }

            var devices = await state.GetDevicesAsync().ConfigureAwait(false);
            var members = devices
                .Where(d => string.Equals(d.RoomName() ?? "", room, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (members.Count == 0)
            {
                return Respond(StatusCodes.Status404NotFound, $"no devices in room '{room}'");
            }

            var on = power == "on";
            var failures = new List<string>();

            // Sequential: the transports below are per-device rate limited, and a
            // burst of parallel cloud calls is what trips Govee's throttling.
            foreach (var device in members)
            {
                try
                {
                    using var lease = await state.ResolveDeviceForControlAsync(device.Id).ConfigureAwait(false);
                    await state.DevicePowerOnAsync(lease.Device, on, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    failures.Add($"{device.Name()}: {ex.Message}");
                }
            }

            return failures.Count == 0
                ? Respond(StatusCodes.Status200OK, $"{members.Count} devices {power}")
                : Respond(StatusCodes.Status500InternalServerError, string.Join("; ", failures));
        });
    }

    /// <summary>
    /// Health, metrics and a change stream: the things needed to run this
    /// unattended next to Home Assistant rather than watching a log.
    /// </summary>
    private static void MapObservability(WebApplication app, ServiceState state)
    {
        app.MapGet("/api/health", async () =>
        {
            var items = await BuildItemsAsync(state).ConfigureAwait(false);

            // A broker that was configured but is not connected is the one
            // failure worth reporting as unhealthy; running without Home
            // Assistant at all is a supported mode.
            var mqtt = state.HassClient?.IsConnected;
            var healthy = mqtt is not false && items.Count > 0;

            // Spelled out rather than left as null, so "not configured" cannot be
            // mistaken for "broken" by whoever is reading the probe output.
            static string Describe(bool? connected)
                => connected is null ? "disabled" : connected.Value ? "connected" : "disconnected";

            var body = new
            {
                status = healthy ? "ok" : "degraded",
                version = VersionInfo.Version,
                uptime_seconds = (long)(DateTimeOffset.UtcNow - StartedAt).TotalSeconds,
                devices = items.Count,
                devices_online = items.Count(i => i.State?.Online ?? false),
                transports = new
                {
                    mqtt = Describe(mqtt),
                    iot = Describe(state.IotClient?.IsConnected),
                    lan = state.LanClient is null ? "disabled" : "enabled",
                    platform = state.PlatformClient is null ? "disabled" : "enabled",
                },
            };

            return Results.Json(body, ApiJson, statusCode: healthy ? 200 : 503);
        });

        app.MapGet("/metrics", async () =>
        {
            var items = await BuildItemsAsync(state).ConfigureAwait(false);
            return Results.Text(RenderMetrics(state, items), "text/plain; version=0.0.4");
        });

        // Server-sent events: the UI subscribes once and is pushed a new
        // snapshot only when something actually changed.
        app.MapGet("/api/events", async (HttpContext context) =>
        {
            context.Response.Headers.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers["X-Accel-Buffering"] = "no";

            var previous = "";
            var lastWrite = DateTimeOffset.UtcNow;

            try
            {
                while (!context.RequestAborted.IsCancellationRequested)
                {
                    var payload = JsonSerializer.Serialize(
                        await BuildItemsAsync(state).ConfigureAwait(false), ApiJson);

                    // A comment line keeps idle connections alive through the
                    // proxies people put in front of this (nginx, HA ingress).
                    var frame = payload != previous
                        ? $"data: {payload}\n\n"
                        : DateTimeOffset.UtcNow - lastWrite > TimeSpan.FromSeconds(15) ? ":\n\n" : null;

                    if (frame is not null)
                    {
                        previous = payload;
                        lastWrite = DateTimeOffset.UtcNow;
                        await context.Response.WriteAsync(frame, context.RequestAborted).ConfigureAwait(false);
                        await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
                    }

                    await Task.Delay(TimeSpan.FromSeconds(1), context.RequestAborted).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // The browser navigated away.
            }
        });
    }

    private static string RenderMetrics(ServiceState state, List<DeviceItem> items)
    {
        var sb = new StringBuilder();

        void Gauge(string name, string help, IEnumerable<(string Labels, double Value)> samples)
        {
            sb.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n');
            sb.Append("# TYPE ").Append(name).Append(" gauge\n");
            foreach (var (labels, value) in samples)
            {
                sb.Append(name);
                if (labels.Length > 0)
                {
                    sb.Append('{').Append(labels).Append('}');
                }
                sb.Append(' ').Append(value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
            }
        }

        static string Escape(string? value)
            => (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ");

        string Labels(DeviceItem item)
            => $"id=\"{Escape(item.Id)}\",sku=\"{Escape(item.Sku)}\",name=\"{Escape(item.Name)}\",room=\"{Escape(item.Room)}\"";

        Gauge("novee2mqtt_build_info", "Build information.",
            [($"version=\"{Escape(VersionInfo.Version)}\"", 1)]);

        Gauge("novee2mqtt_uptime_seconds", "Seconds since the bridge started.",
            [("", (DateTimeOffset.UtcNow - StartedAt).TotalSeconds)]);

        Gauge("novee2mqtt_devices_total", "Number of known devices.", [("", items.Count)]);

        Gauge("novee2mqtt_transport_connected", "Whether each transport is available.",
        [
            ("transport=\"mqtt\"", state.HassClient?.IsConnected == true ? 1 : 0),
            ("transport=\"iot\"", state.IotClient?.IsConnected == true ? 1 : 0),
            ("transport=\"lan\"", state.LanClient is not null ? 1 : 0),
            ("transport=\"platform\"", state.PlatformClient is not null ? 1 : 0),
        ]);

        Gauge("novee2mqtt_device_on", "Whether the device is powered on.",
            items.Where(i => i.State is not null).Select(i => (Labels(i), i.State!.On ? 1d : 0d)));

        Gauge("novee2mqtt_device_online", "Whether Govee considers the device reachable.",
            items.Where(i => i.State?.Online is not null).Select(i => (Labels(i), i.State!.Online! == true ? 1d : 0d)));

        Gauge("novee2mqtt_device_brightness_percent", "Reported brightness.",
            items.Where(i => i.State is not null).Select(i => (Labels(i), (double)i.State!.Brightness)));

        Gauge("novee2mqtt_device_kelvin", "Reported colour temperature, 0 when in colour mode.",
            items.Where(i => i.State is not null).Select(i => (Labels(i), (double)i.State!.Kelvin)));

        Gauge("novee2mqtt_device_state_age_seconds", "Age of the most recent reading.",
            items.Where(i => i.State is not null)
                 .Select(i => (Labels(i), (DateTimeOffset.UtcNow - i.State!.Updated).TotalSeconds)));

        Gauge("novee2mqtt_device_lan_reachable", "Whether the device answered LAN discovery.",
            items.Select(i => (Labels(i), i.Ip is null ? 0d : 1d)));

        return sb.ToString();
    }

    private static async Task<IResult> ControlAsync(
        ServiceState state,
        string id,
        Func<ServiceState, Device, CancellationToken, Task> action)
    {
        DeviceControlLease lease;
        try
        {
            lease = await state.ResolveDeviceForControlAsync(id).ConfigureAwait(false);
        }
        catch (GoveeException ex)
        {
            return Respond(StatusCodes.Status404NotFound, ex.Message);
        }

        try
        {
            await action(state, lease.Device, CancellationToken.None).ConfigureAwait(false);
            return Respond(StatusCodes.Status200OK, "ok");
        }
        catch (Exception ex)
        {
            return Respond(StatusCodes.Status500InternalServerError, ex.Message);
        }
        finally
        {
            lease.Dispose();
        }
    }

    private static IResult Respond(int code, string message)
        => Results.Json(new { code, msg = message }, ApiJson, statusCode: code);

    /// <summary>
    /// Lets the web host log through the same provider as the rest of the
    /// service, so container logs stay in one format.
    /// </summary>
    private sealed class PassThroughLoggerProvider(ILoggerFactory factory) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => factory.CreateLogger(categoryName);

        public void Dispose()
        {
        }
    }
}
