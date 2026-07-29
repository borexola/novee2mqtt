using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Novee2Mqtt.Core;
using Microsoft.Extensions.Logging;

namespace Novee2Mqtt.Undocumented;

/// <summary>
/// Client for the endpoints the Govee mobile app uses. These are undocumented
/// and unsupported by Govee; they are what provide room names, Tap-to-Run
/// shortcuts, and the AWS IoT credentials that give low-latency status updates.
/// </summary>
public sealed class UndocumentedApiClient
{
    private const string HalfDayKey = "account-info";

    private static readonly TimeSpan HalfDay = TimeSpan.FromHours(12);
    private static readonly TimeSpan OneDay = TimeSpan.FromDays(1);
    private static readonly TimeSpan FifteenMinutes = TimeSpan.FromMinutes(15);

    private readonly ILogger<UndocumentedApiClient> _log;
    private readonly HttpClient _httpClient;
    private readonly GoveeCache _cache;
    private readonly string _email;
    private readonly string _password;
    private readonly string _clientId;

    public UndocumentedApiClient(
        ILogger<UndocumentedApiClient> log,
        HttpClient httpClient,
        GoveeCache cache,
        string email,
        string password)
    {
        _log = log;
        _httpClient = httpClient;
        _cache = cache;
        _email = email;
        _password = password;
        _clientId = UuidV5.CreateSimple(UuidV5.NamespaceDns, email);
    }

    public string ClientId => _clientId;

    private void ApplyCommonHeaders(HttpRequestMessage request, string? bearerToken = null)
    {
        if (bearerToken is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {bearerToken}");
        }
        request.Headers.TryAddWithoutValidation("appVersion", SceneCatalog.AppVersion);
        request.Headers.TryAddWithoutValidation("clientId", _clientId);
        request.Headers.TryAddWithoutValidation("clientType", "1");
        request.Headers.TryAddWithoutValidation("iotVersion", "0");
        request.Headers.TryAddWithoutValidation("timestamp", SceneCatalog.MillisecondTimestamp());
        request.Headers.UserAgent.ParseAdd(SceneCatalog.UserAgent);
    }

    public void InvalidateAccountLogin() => _cache.Invalidate("undoc-api", HalfDayKey);

    public void InvalidateCommunityLogin() => _cache.Invalidate("undoc-api", "community-login");

    /// <summary>
    /// Logs in to the app API, caching the result for as long as the returned
    /// token lifetime allows.
    /// </summary>
    public Task<LoginAccountResponse> LoginAccountAsync(CancellationToken cancellationToken = default)
    {
        var options = new CacheGetOptions(
            Topic: "undoc-api",
            Key: HalfDayKey,
            SoftTtl: HalfDay,
            HardTtl: HalfDay,
            NegativeTtl: FifteenMinutes,
            AllowStale: false);

        return _cache.GetAsync(options, async ct =>
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                "https://app2.govee.com/account/rest/account/v1/login");
            ApplyCommonHeaders(request);
            request.Content = JsonContent.Create(new
            {
                email = _email,
                password = _password,
                client = _clientId,
            }, options: Json.Options);

            using var response = await SendAsync(request, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            var body = await GoveeHttp.ReadJsonAsync<LoginResponseEnvelope>(response, ct).ConfigureAwait(false);

            var ttl = TimeSpan.FromSeconds(Math.Max(60, body.Client.TokenExpireCycle));
            return new CacheComputeResult<LoginAccountResponse>(body.Client, ttl);
        }, cancellationToken);
    }

    public async Task<DevicesResponse> GetDeviceListAsync(string token, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "https://app2.govee.com/device/rest/devices/v1/list");
        ApplyCommonHeaders(request, token);

        using var response = await SendAsync(request, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            InvalidateAccountLogin();
        }

        return await GoveeHttp.ReadJsonAsync<DevicesResponse>(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Fetches the AWS IoT endpoint and PKCS#12 client certificate for this account.</summary>
    public Task<IotKey> GetIotKeyAsync(string token, CancellationToken cancellationToken = default)
    {
        var options = new CacheGetOptions(
            Topic: "undoc-api",
            Key: "iot-key",
            SoftTtl: HalfDay,
            HardTtl: HalfDay,
            NegativeTtl: TimeSpan.FromSeconds(10),
            AllowStale: false);

        return _cache.GetAsync(options, async ct =>
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                "https://app2.govee.com/app/v1/account/iot/key");
            ApplyCommonHeaders(request, token);

            using var response = await SendAsync(request, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            var body = await GoveeHttp.ReadJsonAsync<IotKeyEnvelope>(response, ct).ConfigureAwait(false);
            return new CacheComputeResult<IotKey>(body.Data);
        }, cancellationToken);
    }

    /// <summary>Logs in to the community API, which is what serves Tap-to-Run shortcuts.</summary>
    public Task<string> LoginCommunityAsync(CancellationToken cancellationToken = default)
    {
        var options = new CacheGetOptions(
            Topic: "undoc-api",
            Key: "community-login",
            SoftTtl: OneDay,
            HardTtl: HalfDay,
            NegativeTtl: TimeSpan.FromSeconds(10),
            AllowStale: false);

        return _cache.GetAsync(options, async ct =>
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                "https://community-api.govee.com/os/v1/login");
            ApplyCommonHeaders(request);
            request.Content = JsonContent.Create(new { email = _email, password = _password }, options: Json.Options);

            using var response = await SendAsync(request, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
            var body = await GoveeHttp.ReadJsonAsync<CommunityLoginEnvelope>(response, ct).ConfigureAwait(false);

            var remaining = DateTimeOffset.FromUnixTimeMilliseconds(body.Data.ExpiredAt) - DateTimeOffset.UtcNow;
            var ttl = remaining > TimeSpan.Zero && remaining < OneDay ? remaining : OneDay;

            return new CacheComputeResult<string>(body.Data.Token, ttl);
        }, cancellationToken);
    }

    public Task<List<OneClickComponent>> GetSavedOneClickShortcutsAsync(string communityToken, CancellationToken cancellationToken = default)
    {
        var options = new CacheGetOptions(
            Topic: "undoc-api",
            Key: "one-click-shortcuts",
            SoftTtl: OneDay,
            HardTtl: TimeSpan.FromDays(7),
            NegativeTtl: TimeSpan.FromSeconds(1),
            AllowStale: true);

        return _cache.GetAsync(options, async ct =>
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                "https://app2.govee.com/bff-app/v1/exec-plat/home");
            ApplyCommonHeaders(request, communityToken);

            using var response = await SendAsync(request, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                InvalidateCommunityLogin();
            }

            var body = await GoveeHttp.ReadJsonAsync<OneClickResponse>(response, ct).ConfigureAwait(false);
            return new CacheComputeResult<List<OneClickComponent>>(body.Data.Components);
        }, cancellationToken);
    }

    /// <summary>
    /// Flattens the shortcut tree into name plus the IoT publishes that trigger
    /// it. Shortcuts with no IoT rules (BLE-only groups) are skipped.
    /// </summary>
    public async Task<List<ParsedOneClick>> ParseOneClicksAsync(CancellationToken cancellationToken = default)
    {
        var token = await LoginCommunityAsync(cancellationToken).ConfigureAwait(false);
        var components = await GetSavedOneClickShortcutsAsync(token, cancellationToken).ConfigureAwait(false);

        var result = new List<ParsedOneClick>();
        foreach (var group in components)
        {
            foreach (var oneClick in group.OneClicks)
            {
                if (oneClick.IotRules.Count == 0)
                {
                    continue;
                }

                var entries = new List<ParsedOneClickEntry>();
                foreach (var rule in oneClick.IotRules)
                {
                    if (rule.DeviceObj.Topic is not { } topic)
                    {
                        continue;
                    }

                    entries.Add(new ParsedOneClickEntry
                    {
                        Topic = topic,
                        Messages = rule.Rule.Where(r => r.IotMsg is not null).Select(r => r.IotMsg!).ToList(),
                    });
                }

                if (entries.Count == 0)
                {
                    continue;
                }

                result.Add(new ParsedOneClick
                {
                    Name = $"One-Click: {group.Name}: {oneClick.Name}",
                    Entries = entries,
                });
            }
        }

        _log.LogDebug("Parsed {Count} one-click shortcuts", result.Count);
        return result;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        return await _httpClient.SendAsync(request, cts.Token).ConfigureAwait(false);
    }

    private sealed class LoginResponseEnvelope
    {
        [System.Text.Json.Serialization.JsonPropertyName("client")]
        public LoginAccountResponse Client { get; set; } = new();
    }

    private sealed class IotKeyEnvelope
    {
        [System.Text.Json.Serialization.JsonPropertyName("data")]
        public IotKey Data { get; set; } = new();
    }

    private sealed class CommunityLoginEnvelope
    {
        [System.Text.Json.Serialization.JsonPropertyName("data")]
        public CommunityLoginData Data { get; set; } = new();
    }

    private sealed class CommunityLoginData
    {
        [System.Text.Json.Serialization.JsonPropertyName("token")]
        public string Token { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("expiredAt")]
        public long ExpiredAt { get; set; }
    }
}
