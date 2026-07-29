using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Novee2Mqtt.Core;
using Novee2Mqtt.Platform;
using Microsoft.Extensions.Logging;

namespace Novee2Mqtt.Undocumented;

/// <summary>
/// The per-SKU light effect library published by the Govee app backend. This
/// endpoint needs no authentication, so it is available even when only LAN
/// control is configured — it is what makes scene-by-name work over the LAN.
/// </summary>
public sealed class SceneCatalog(ILogger<SceneCatalog> log, HttpClient httpClient, GoveeCache cache)
{
    public const string AppVersion = "6.2.0";

    public static string UserAgent =>
        $"GoveeHome/{AppVersion} (com.ihoment.GoVeeSensor; build:2; iOS 16.5.0) Alamofire/5.6.4";

    public static string MillisecondTimestamp() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();

    public async Task<List<LightEffectCategory>> GetScenesForDeviceAsync(string sku, CancellationToken cancellationToken = default)
    {
        var options = new CacheGetOptions(
            Topic: "undoc-api",
            Key: $"scenes-{sku}",
            SoftTtl: TimeSpan.FromDays(1),
            HardTtl: TimeSpan.FromDays(7),
            NegativeTtl: TimeSpan.FromSeconds(1),
            AllowStale: true);

        return await cache.GetAsync(options, async ct =>
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://app2.govee.com/appsku/v1/light-effect-libraries?sku={Uri.EscapeDataString(sku)}");
            request.Headers.TryAddWithoutValidation("AppVersion", AppVersion);
            request.Headers.UserAgent.ParseAdd(UserAgent);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));

            using var response = await httpClient.SendAsync(request, timeout.Token).ConfigureAwait(false);
            var body = await GoveeHttp.ReadJsonAsync<LightEffectLibraryResponse>(response, timeout.Token).ConfigureAwait(false);
            return new CacheComputeResult<List<LightEffectCategory>>(body.Data.Categories);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds a synthetic <c>dynamic_scene</c> capability from the app catalog.
    /// Works around Govee's Platform API omitting most scenes for many devices.
    /// </summary>
    public async Task<List<DeviceCapability>> SynthesizePlatformSceneListAsync(string sku, CancellationToken cancellationToken = default)
    {
        var catalog = await GetScenesForDeviceAsync(sku, cancellationToken).ConfigureAwait(false);

        var options = new List<EnumOption>();
        foreach (var category in catalog)
        {
            foreach (var scene in category.Scenes)
            {
                var first = scene.LightEffects.FirstOrDefault();
                if (first is null)
                {
                    continue;
                }

                options.Add(new EnumOption
                {
                    Name = scene.SceneName,
                    Value = new JsonObject
                    {
                        ["paramId"] = first.SceneParamId,
                        ["id"] = scene.SceneId,
                    },
                });
            }
        }

        return
        [
            new DeviceCapability
            {
                Kind = DeviceCapabilityKind.DynamicScene,
                Instance = "lightScene",
                Parameters = new EnumParameters { Options = options },
            },
        ];
    }

    /// <summary>
    /// Finds the scene code and effect blob for a named scene, for devices that
    /// are driven over the LAN rather than the Platform API.
    /// </summary>
    public async Task<Ble.SetSceneCode?> FindSceneCodeAsync(string sku, string sceneName, CancellationToken cancellationToken = default)
    {
        foreach (var category in await GetScenesForDeviceAsync(sku, cancellationToken).ConfigureAwait(false))
        {
            foreach (var scene in category.Scenes)
            {
                if (!string.Equals(scene.SceneName, sceneName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var effect in scene.LightEffects)
                {
                    if (effect.SceneCode != 0)
                    {
                        return new Ble.SetSceneCode((ushort)effect.SceneCode, effect.SceneParam);
                    }
                }
            }
        }

        log.LogDebug("No LAN scene code found for {Sku} scene {Scene}", sku, sceneName);
        return null;
    }

    /// <summary>Scene names that can be activated over the LAN, i.e. those with a non-zero scene code.</summary>
    public async Task<List<string>> ListLanSceneNamesAsync(string sku, CancellationToken cancellationToken = default)
    {
        var names = new List<string>();
        foreach (var category in await GetScenesForDeviceAsync(sku, cancellationToken).ConfigureAwait(false))
        {
            foreach (var scene in category.Scenes)
            {
                if (scene.LightEffects.Any(e => e.SceneCode != 0))
                {
                    names.Add(scene.SceneName);
                }
            }
        }
        return names;
    }
}
