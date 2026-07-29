using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Novee2Mqtt.Core;
using Novee2Mqtt.Platform;
using Novee2Mqtt.Undocumented;
using Microsoft.Extensions.Logging.Abstractions;

namespace Novee2Mqtt.Tests;

/// <summary>
/// Verifies the exact JSON sent to Govee's Platform API. These shapes are a wire
/// contract with a third party, so they are pinned rather than inferred.
/// </summary>
public class PlatformRequestTests : IDisposable
{
    private readonly string _cacheDir = Path.Combine(Path.GetTempPath(), "govee-req-" + Guid.NewGuid().ToString("N"));
    private readonly GoveeCache _cache;
    private readonly RecordingHandler _handler = new();
    private readonly HttpClient _httpClient;
    private readonly PlatformApiClient _client;

    public PlatformRequestTests()
    {
        Directory.CreateDirectory(_cacheDir);
        _cache = new GoveeCache(NullLogger<GoveeCache>.Instance, _cacheDir);
        _httpClient = new HttpClient(_handler);

        var catalog = new SceneCatalog(NullLogger<SceneCatalog>.Instance, _httpClient, _cache);
        _client = new PlatformApiClient(
            NullLogger<PlatformApiClient>.Instance, _httpClient, _cache, catalog, "test-api-key");
    }

    public void Dispose()
    {
        _cache.Dispose();
        _httpClient.Dispose();
        try
        {
            Directory.Delete(_cacheDir, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private static HttpDeviceInfo Device() => Json.Deserialize<HttpDeviceInfo>("""
        {
          "sku":"H6072","device":"AA:BB","deviceName":"Lamp","type":"devices.types.light",
          "capabilities":[
            {"type":"devices.capabilities.on_off","instance":"powerSwitch",
             "parameters":{"dataType":"ENUM","options":[{"name":"on","value":1},{"name":"off","value":0}]}},
            {"type":"devices.capabilities.range","instance":"brightness",
             "parameters":{"dataType":"INTEGER","range":{"min":1,"max":100,"precision":1}}},
            {"type":"devices.capabilities.color_setting","instance":"colorRgb",
             "parameters":{"dataType":"INTEGER","range":{"min":0,"max":16777215,"precision":1}}}
          ]
        }
        """);

    [Fact]
    public async Task ControlRequestUsesGoveesPayloadShape()
    {
        _handler.RespondWith("""{"requestId":"x","code":200,"msg":"ok","capability":{"type":"devices.capabilities.on_off","instance":"powerSwitch","value":1,"state":{}}}""");

        await _client.SetPowerStateAsync(Device(), on: true);

        var body = JsonNode.Parse(_handler.LastBody!)!;

        Assert.Equal("devices.capabilities.on_off", body["payload"]!["capability"]!["type"]!.GetValue<string>());
        Assert.Equal("powerSwitch", body["payload"]!["capability"]!["instance"]!.GetValue<string>());
        Assert.Equal(1, body["payload"]!["capability"]!["value"]!.GetValue<int>());
        Assert.Equal("H6072", body["payload"]!["sku"]!.GetValue<string>());
        Assert.Equal("AA:BB", body["payload"]!["device"]!.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(body["requestId"]!.GetValue<string>()));
    }

    [Fact]
    public async Task ApiKeyIsSentAsAHeader()
    {
        _handler.RespondWith("""{"requestId":"x","code":200,"msg":"ok","capability":{"type":"t","instance":"i","value":1,"state":{}}}""");

        await _client.SetPowerStateAsync(Device(), on: false);

        Assert.Equal("test-api-key", _handler.LastRequest!.Headers.GetValues("Govee-API-Key").Single());
    }

    [Fact]
    public async Task StateRequestIdentifiesTheDevice()
    {
        _handler.RespondWith("""{"requestId":"x","code":200,"msg":"ok","payload":{"sku":"H6072","device":"AA:BB","capabilities":[]}}""");

        await _client.GetDeviceStateAsync(Device());

        var body = JsonNode.Parse(_handler.LastBody!)!;
        Assert.Equal("H6072", body["payload"]!["sku"]!.GetValue<string>());
        Assert.Equal("AA:BB", body["payload"]!["device"]!.GetValue<string>());
    }

    [Fact]
    public async Task BrightnessIsClampedToTheDeclaredRange()
    {
        _handler.RespondWith("""{"requestId":"x","code":200,"msg":"ok","capability":{"type":"t","instance":"i","value":1,"state":{}}}""");

        await _client.SetBrightnessAsync(Device(), 250);

        var body = JsonNode.Parse(_handler.LastBody!)!;
        Assert.Equal(100, body["payload"]!["capability"]!["value"]!.GetValue<int>());
    }

    [Fact]
    public async Task ColorIsPackedIntoASingleInteger()
    {
        _handler.RespondWith("""{"requestId":"x","code":200,"msg":"ok","capability":{"type":"t","instance":"i","value":1,"state":{}}}""");

        await _client.SetColorRgbAsync(Device(), new DeviceColor(0x12, 0x34, 0x56));

        var body = JsonNode.Parse(_handler.LastBody!)!;
        Assert.Equal(0x123456, body["payload"]!["capability"]!["value"]!.GetValue<int>());
    }

    /// <summary>Govee reports failures in the body even on an HTTP 200.</summary>
    [Fact]
    public async Task EmbeddedFailureCodeIsTreatedAsAnError()
    {
        _handler.RespondWith("""{"requestId":"x","code":401,"msg":"unauthorized"}""");

        var ex = await Assert.ThrowsAsync<HttpRequestFailedException>(
            () => _client.SetPowerStateAsync(Device(), on: true));

        Assert.Contains("unauthorized", ex.Message);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private string _response = "{}";

        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        public void RespondWith(string json) => _response = json;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_response, Encoding.UTF8, "application/json"),
                RequestMessage = request,
            };
        }
    }
}
