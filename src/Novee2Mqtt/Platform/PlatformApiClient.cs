using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Novee2Mqtt.Core;
using Novee2Mqtt.Undocumented;
using Microsoft.Extensions.Logging;

namespace Novee2Mqtt.Platform;

/// <summary>
/// Client for the official Govee Platform API at
/// <see href="https://developer.govee.com/reference/get-you-devices"/>.
/// This is the authenticated-by-API-key surface; it is the only source for
/// scenes, segments and most sensor readings, but it is rate limited, so
/// results are cached aggressively.
/// </summary>
public sealed class PlatformApiClient(
    ILogger<PlatformApiClient> log,
    HttpClient httpClient,
    GoveeCache cache,
    SceneCatalog sceneCatalog,
    string apiKey)
{
    private const string Server = "https://openapi.api.govee.com";

    private static readonly TimeSpan OneWeek = TimeSpan.FromDays(7);
    private static readonly TimeSpan FiveMinutes = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Cache keys carry a fingerprint of the API key, so that switching to a
    /// different Govee account does not keep serving the previous account's
    /// device and scene lists until they expire.
    /// </summary>
    private readonly string _keyScope = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)))[..12];

    private readonly object _throttleLock = new();
    private DateTimeOffset _throttledUntil = DateTimeOffset.MinValue;

    private static string Endpoint(string path) => Server + path;

    /// <summary>
    /// When Govee has told us to back off, calls made before that time fail
    /// locally instead of spending another request on a 429.
    /// </summary>
    public DateTimeOffset? ThrottledUntil
    {
        get
        {
            lock (_throttleLock)
            {
                return _throttledUntil > DateTimeOffset.UtcNow ? _throttledUntil : null;
            }
        }
    }

    public Task<List<HttpDeviceInfo>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        var options = new CacheGetOptions(
            Topic: "http-api",
            Key: $"device-list-{_keyScope}",
            SoftTtl: TimeSpan.FromSeconds(900),
            HardTtl: OneWeek,
            NegativeTtl: TimeSpan.FromSeconds(60),
            AllowStale: true);

        return cache.GetAsync(options, async ct =>
        {
            var response = await GetAsync<GetDevicesResponse>(Endpoint("/router/api/v1/user/devices"), ct)
                .ConfigureAwait(false);
            return new CacheComputeResult<List<HttpDeviceInfo>>(response.Data);
        }, cancellationToken);
    }

    public async Task<HttpDeviceInfo> GetDeviceByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        var devices = await GetDevicesAsync(cancellationToken).ConfigureAwait(false);
        return devices.FirstOrDefault(d => d.Device == id)
            ?? throw new GoveeException($"device {id} not found");
    }

    public async Task<ControlDeviceResponseCapability> ControlDeviceAsync(
        HttpDeviceInfo device,
        DeviceCapability capability,
        JsonNode? value,
        CancellationToken cancellationToken = default)
    {
        var request = new
        {
            requestId = Guid.NewGuid().ToString(),
            payload = new
            {
                sku = device.Sku,
                device = device.Device,
                capability = new
                {
                    type = capability.Kind,
                    instance = capability.Instance,
                    value,
                },
            },
        };

        var response = await PostAsync<ControlDeviceResponse>(
            Endpoint("/router/api/v1/device/control"), request, cancellationToken).ConfigureAwait(false);

        log.LogInformation("control_device result: {Code} {Message}", response.Code, response.Message);
        return response.Capability;
    }

    public async Task<HttpDeviceState> GetDeviceStateAsync(HttpDeviceInfo device, CancellationToken cancellationToken = default)
    {
        var response = await PostAsync<GetDeviceStateResponse>(
            Endpoint("/router/api/v1/device/state"), DeviceIdRequest(device), cancellationToken).ConfigureAwait(false);

        return response.Payload;
    }

    private static object DeviceIdRequest(HttpDeviceInfo device) => new
    {
        requestId = Guid.NewGuid().ToString(),
        payload = new { sku = device.Sku, device = device.Device },
    };

    public Task<List<DeviceCapability>> GetDeviceScenesAsync(HttpDeviceInfo device, CancellationToken cancellationToken = default)
        => GetScenesAsync(device, "/router/api/v1/device/scenes", $"scene-list-{device.Sku}-{device.Device}-{_keyScope}", cancellationToken);

    public Task<List<DeviceCapability>> GetDeviceDiyScenesAsync(HttpDeviceInfo device, CancellationToken cancellationToken = default)
        => GetScenesAsync(device, "/router/api/v1/device/diy-scenes", $"scene-list-diy-{device.Sku}-{device.Device}-{_keyScope}", cancellationToken);

    private Task<List<DeviceCapability>> GetScenesAsync(
        HttpDeviceInfo device,
        string path,
        string cacheKey,
        CancellationToken cancellationToken)
    {
        if (!device.SupportsDynamicScenes())
        {
            return Task.FromResult(new List<DeviceCapability>());
        }

        var options = new CacheGetOptions(
            Topic: "http-api",
            Key: cacheKey,
            // Scenes change rarely and each refresh costs two requests per
            // device against a daily quota. "Purge Caches" forces a re-read.
            SoftTtl: TimeSpan.FromHours(6),
            HardTtl: OneWeek,
            NegativeTtl: FiveMinutes,
            AllowStale: true);

        return cache.GetAsync(options, async ct =>
        {
            var response = await PostAsync<GetDeviceScenesResponse>(
                Endpoint(path), DeviceIdRequest(device), ct).ConfigureAwait(false);

            return new CacheComputeResult<List<DeviceCapability>>(response.Payload.Capabilities);
        }, cancellationToken);
    }

    /// <summary>
    /// Collects every enum-valued scene capability we can find, from the device
    /// metadata, the scenes and DIY-scenes endpoints, and the app catalog.
    /// </summary>
    public async Task<List<DeviceCapability>> GetSceneCapsAsync(HttpDeviceInfo device, CancellationToken cancellationToken = default)
    {
        var sceneCaps = await GetDeviceScenesAsync(device, cancellationToken).ConfigureAwait(false);
        var diyCaps = await GetDeviceDiyScenesAsync(device, cancellationToken).ConfigureAwait(false);

        List<DeviceCapability> undocCaps;
        try
        {
            undocCaps = await sceneCatalog.SynthesizePlatformSceneListAsync(device.Sku, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning("SynthesizePlatformSceneList for {Sku}: {Message}", device.Sku, ex.Message);
            undocCaps = [];
        }

        var result = new List<DeviceCapability>();
        foreach (var (origin, caps) in new (string, List<DeviceCapability>)[]
                 {
                     ("device.capabilities", device.Capabilities),
                     ("scene_caps", sceneCaps),
                     ("diy_caps", diyCaps),
                     ("undoc_caps", undocCaps),
                 })
        {
            foreach (var cap in caps)
            {
                var isScene = cap.Kind == DeviceCapabilityKind.DynamicScene
                    || cap.Kind == DeviceCapabilityKind.DynamicSetting
                    || cap.Kind == DeviceCapabilityKind.Mode;

                if (!isScene)
                {
                    continue;
                }

                switch (cap.Parameters)
                {
                    case EnumParameters:
                        result.Add(cap);
                        break;
                    case null:
                        // Device has no scenes for this capability.
                        break;
                    default:
                        log.LogWarning(
                            "GetSceneCaps(sku={Sku} device={Id}): unexpected parameters in {Origin} for {Instance}; ignoring",
                            device.Sku, device.Device, origin, cap.Instance);
                        break;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Every effect name Home Assistant should offer: scenes, DIY scenes, and
    /// music modes prefixed with <c>Music:</c>. An empty entry is prepended so
    /// the list has a "no effect" option.
    /// </summary>
    public async Task<List<string>> ListSceneNamesAsync(HttpDeviceInfo device, CancellationToken cancellationToken = default)
    {
        var result = new List<string>();

        foreach (var cap in await GetSceneCapsAsync(device, cancellationToken).ConfigureAwait(false))
        {
            if (cap.Parameters is EnumParameters e)
            {
                result.AddRange(e.Options.Select(o => o.Name));
            }
        }

        if (device.CapabilityByInstance("musicMode")?.StructFieldByName("musicMode")?.FieldType is EnumParameters music)
        {
            result.AddRange(music.Options.Select(o => $"Music: {o.Name}"));
        }

        if (result.Count > 0)
        {
            result.Insert(0, "");
        }

        return SceneUtils.SortAndDedup(result);
    }

    public async Task<ControlDeviceResponseCapability> SetSceneByNameAsync(
        HttpDeviceInfo device,
        string scene,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(scene))
        {
            throw new GoveeException("Cannot set scene to no-scene");
        }

        if (scene.StartsWith("Music: ", StringComparison.Ordinal))
        {
            var musicMode = scene["Music: ".Length..];
            var cap = device.CapabilityByInstance("musicMode");
            var field = cap?.StructFieldByName("musicMode");
            if (cap is not null && field?.FieldType.EnumParameterByName(musicMode) is { } value)
            {
                var payload = new JsonObject
                {
                    ["musicMode"] = value,
                    ["sensitivity"] = 100,
                    ["autoColor"] = 1,
                };
                return await ControlDeviceAsync(device, cap, payload, cancellationToken).ConfigureAwait(false);
            }
        }

        foreach (var cap in await GetSceneCapsAsync(device, cancellationToken).ConfigureAwait(false))
        {
            if (cap.Parameters is not EnumParameters e)
            {
                continue;
            }

            foreach (var option in e.Options)
            {
                if (string.Equals(scene, option.Name, StringComparison.OrdinalIgnoreCase))
                {
                    return await ControlDeviceAsync(device, cap, option.Value?.DeepClone(), cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }

        throw new GoveeException($"Scene '{scene}' is not available for this device");
    }

    public async Task<ControlDeviceResponseCapability> SetTargetTemperatureAsync(
        HttpDeviceInfo device,
        string instanceName,
        TemperatureValue target,
        CancellationToken cancellationToken = default)
    {
        var cap = device.CapabilityByInstance(instanceName)
            ?? throw new GoveeException($"device has no {instanceName}");

        var constraints = TemperatureConstraints.Parse(cap).As(TemperatureUnits.Celsius);
        var min = constraints.Min.AsCelsius();
        var max = constraints.Max.AsCelsius();
        var celsius = target.AsCelsius();
        var clamped = Math.Clamp(celsius, min, max);

        if (Math.Abs(clamped - celsius) > double.Epsilon)
        {
            log.LogInformation(
                "SetTargetTemperature: constraining requested {Requested} to {Clamped} because min={Min} and max={Max}",
                celsius, clamped, min, max);
        }

        var value = new JsonObject
        {
            ["temperature"] = clamped,
            ["unit"] = "Celsius",
        };

        return await ControlDeviceAsync(device, cap, value, cancellationToken).ConfigureAwait(false);
    }

    public Task<ControlDeviceResponseCapability> SetWorkModeAsync(
        HttpDeviceInfo device,
        long workMode,
        long value,
        CancellationToken cancellationToken = default)
    {
        var cap = device.CapabilityByInstance("workMode")
            ?? throw new GoveeException("device has no workMode");

        return ControlDeviceAsync(device, cap, new JsonObject
        {
            ["workMode"] = workMode,
            ["modeValue"] = value,
        }, cancellationToken);
    }

    public Task<ControlDeviceResponseCapability> SetToggleStateAsync(
        HttpDeviceInfo device,
        string instance,
        bool on,
        CancellationToken cancellationToken = default)
    {
        var cap = device.CapabilityByInstance(instance)
            ?? throw new GoveeException($"device has no {instance}");

        var value = cap.EnumParameterByName(on ? "on" : "off")
            ?? throw new GoveeException($"{instance} has no on/off value");

        return ControlDeviceAsync(device, cap, JsonValue.Create(value), cancellationToken);
    }

    public Task<ControlDeviceResponseCapability> SetPowerStateAsync(HttpDeviceInfo device, bool on, CancellationToken cancellationToken = default)
        => SetToggleStateAsync(device, "powerSwitch", on, cancellationToken);

    public Task<ControlDeviceResponseCapability> SetBrightnessAsync(HttpDeviceInfo device, int percent, CancellationToken cancellationToken = default)
    {
        var cap = device.CapabilityByInstance("brightness")
            ?? throw new GoveeException("device has no brightness");

        if (cap.Parameters is not IntegerParameters integer)
        {
            throw new GoveeException("unexpected parameter type for brightness");
        }

        var value = Math.Clamp(percent, integer.Range.Min, integer.Range.Max);
        return ControlDeviceAsync(device, cap, JsonValue.Create(value), cancellationToken);
    }

    public Task<ControlDeviceResponseCapability> SetColorTemperatureAsync(HttpDeviceInfo device, int kelvin, CancellationToken cancellationToken = default)
    {
        var cap = device.CapabilityByInstance("colorTemperatureK")
            ?? throw new GoveeException("device has no colorTemperatureK");

        if (cap.Parameters is not IntegerParameters integer)
        {
            throw new GoveeException("unexpected parameter type for colorTemperatureK");
        }

        var value = Math.Clamp(kelvin, integer.Range.Min, integer.Range.Max);
        return ControlDeviceAsync(device, cap, JsonValue.Create(value), cancellationToken);
    }

    public Task<ControlDeviceResponseCapability> SetColorRgbAsync(HttpDeviceInfo device, DeviceColor color, CancellationToken cancellationToken = default)
    {
        var cap = device.CapabilityByInstance("colorRgb")
            ?? throw new GoveeException("device has no colorRgb");

        return ControlDeviceAsync(device, cap, JsonValue.Create(color.ToPacked()), cancellationToken);
    }

    public Task<ControlDeviceResponseCapability> SetSegmentRgbAsync(
        HttpDeviceInfo device,
        long segment,
        DeviceColor color,
        CancellationToken cancellationToken = default)
    {
        var cap = device.CapabilityByInstance("segmentedColorRgb")
            ?? throw new GoveeException("device has no segmentedColorRgb");

        return ControlDeviceAsync(device, cap, new JsonObject
        {
            ["segment"] = new JsonArray(segment),
            ["rgb"] = color.ToPacked(),
        }, cancellationToken);
    }

    public Task<ControlDeviceResponseCapability> SetSegmentBrightnessAsync(
        HttpDeviceInfo device,
        long segment,
        int percent,
        CancellationToken cancellationToken = default)
    {
        var cap = device.CapabilityByInstance("segmentedBrightness")
            ?? throw new GoveeException("device has no segmentedBrightness");

        var range = device.SupportsSegmentedBrightness()
            ?? throw new GoveeException("device doesn't support segmented brightness");

        var value = Math.Clamp(percent, range.Min, range.Max);

        return ControlDeviceAsync(device, cap, new JsonObject
        {
            ["segment"] = new JsonArray(segment),
            ["brightness"] = value,
        }, cancellationToken);
    }

    private Task<TResponse> GetAsync<TResponse>(string url, CancellationToken cancellationToken)
        => SendAsync<TResponse>(() => new HttpRequestMessage(HttpMethod.Get, url), cancellationToken);

    private Task<TResponse> PostAsync<TResponse>(string url, object body, CancellationToken cancellationToken)
        => SendAsync<TResponse>(() => new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body, options: Json.Options),
        }, cancellationToken);

    /// <summary>
    /// Sends a request, honouring a 429 by refusing further calls until the
    /// server's <c>Retry-After</c> has passed, and retrying a 5xx once.
    /// </summary>
    /// <param name="createRequest">
    /// Builds the request; a fresh one is needed for the retry since a request
    /// message cannot be sent twice.
    /// </param>
    private async Task<TResponse> SendAsync<TResponse>(Func<HttpRequestMessage> createRequest, CancellationToken cancellationToken)
    {
        if (ThrottledUntil is { } until)
        {
            throw new GoveeException($"Platform API is rate limited by Govee; retrying after {until:HH:mm:ss} UTC");
        }

        for (var attempt = 1; ; attempt++)
        {
            using var request = createRequest();
            request.Headers.TryAddWithoutValidation("Govee-API-Key", apiKey);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(60));

            try
            {
                using var response = await httpClient.SendAsync(request, cts.Token).ConfigureAwait(false);
                return await GoveeHttp.ReadJsonAsync<TResponse>(response, cts.Token).ConfigureAwait(false);
            }
            catch (HttpRequestFailedException ex) when (ex.IsRateLimited)
            {
                Throttle(ex.RetryAfter);
                throw;
            }
            catch (HttpRequestFailedException ex) when (ex.IsServerError && attempt == 1)
            {
                var pause = TimeSpan.FromMilliseconds(1000 + Random.Shared.Next(1000));
                log.LogWarning("Platform API returned {Status}; retrying once in {Pause}: {Message}",
                    (int)ex.Status, pause, ex.Message);
                await Task.Delay(pause, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private void Throttle(TimeSpan? retryAfter)
    {
        // Govee rarely sends Retry-After; a minute is long enough to let a
        // per-minute burst limit reset without hiding a daily-quota exhaustion
        // for long, since the next call will simply be told again.
        var pause = retryAfter ?? TimeSpan.FromMinutes(1);
        if (pause < TimeSpan.FromSeconds(10)) pause = TimeSpan.FromSeconds(10);
        if (pause > TimeSpan.FromHours(1)) pause = TimeSpan.FromHours(1);

        lock (_throttleLock)
        {
            _throttledUntil = DateTimeOffset.UtcNow + pause;
        }

        log.LogWarning("Platform API rate limit hit; pausing Platform API calls for {Pause}", pause);
    }
}

internal sealed class GetDevicesResponse
{
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("message")] public string Message { get; set; } = "";
    [JsonPropertyName("data")] public List<HttpDeviceInfo> Data { get; set; } = [];
}

internal sealed class GetDeviceStateResponse
{
    [JsonPropertyName("requestId")] public string RequestId { get; set; } = "";
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("msg")] public string Message { get; set; } = "";
    [JsonPropertyName("payload")] public HttpDeviceState Payload { get; set; } = new();
}

internal sealed class GetDeviceScenesResponse
{
    [JsonPropertyName("requestId")] public string RequestId { get; set; } = "";
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("msg")] public string Message { get; set; } = "";
    [JsonPropertyName("payload")] public GetDeviceScenesPayload Payload { get; set; } = new();
}

internal sealed class GetDeviceScenesPayload
{
    [JsonPropertyName("sku")] public string Sku { get; set; } = "";
    [JsonPropertyName("device")] public string Device { get; set; } = "";
    [JsonPropertyName("capabilities")] public List<DeviceCapability> Capabilities { get; set; } = [];
}

internal sealed class ControlDeviceResponse
{
    [JsonPropertyName("requestId")] public string RequestId { get; set; } = "";
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("msg")] public string Message { get; set; } = "";
    [JsonPropertyName("capability")] public ControlDeviceResponseCapability Capability { get; set; } = new();
}

public sealed class ControlDeviceResponseCapability
{
    [JsonPropertyName("type")] public DeviceCapabilityKind Kind { get; set; }
    [JsonPropertyName("instance")] public string Instance { get; set; } = "";
    [JsonPropertyName("value")] public JsonNode? Value { get; set; }
    [JsonPropertyName("state")] public JsonNode? State { get; set; }
}
