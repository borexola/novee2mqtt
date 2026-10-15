using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Novee2Mqtt.Cli;
using Novee2Mqtt.Core;

namespace Novee2Mqtt.Tests;

public class GoveeCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "govee-cache-" + Guid.NewGuid().ToString("N"));
    private GoveeCache _cache;

    public GoveeCacheTests()
    {
        Directory.CreateDirectory(_dir);
        _cache = new GoveeCache(NullLogger<GoveeCache>.Instance, _dir);
    }

    public void Dispose()
    {
        _cache.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private static CacheGetOptions Options(TimeSpan soft, bool allowStale = true, TimeSpan? negative = null, TimeSpan? hard = null)
        => new("test", "key", soft, hard ?? TimeSpan.FromDays(1), negative ?? TimeSpan.FromHours(1), allowStale);

    [Fact]
    public async Task ValueIsServedFromCacheUntilTheSoftTtlPasses()
    {
        var computed = 0;
        Task<CacheComputeResult<string>> Compute(CancellationToken _) => Task.FromResult(new CacheComputeResult<string>($"v{++computed}"));

        Assert.Equal("v1", await _cache.GetAsync(Options(TimeSpan.FromHours(1)), Compute));
        Assert.Equal("v1", await _cache.GetAsync(Options(TimeSpan.FromHours(1)), Compute));
        Assert.Equal(1, computed);

        // The expiry is fixed when the entry is written, so the fresh entry is
        // still served; invalidating it forces a recompute, and an entry
        // written with a zero soft TTL is expired by the time it is next read.
        _cache.Invalidate("test", "key");
        Assert.Equal("v2", await _cache.GetAsync(Options(TimeSpan.Zero), Compute));
        Assert.Equal("v3", await _cache.GetAsync(Options(TimeSpan.Zero), Compute));
        Assert.Equal(3, computed);
    }

    [Fact]
    public async Task StaleValueStandsInForAFailedRefresh()
    {
        await _cache.GetAsync(Options(TimeSpan.Zero), _ => Task.FromResult(new CacheComputeResult<string>("good")));

        var result = await _cache.GetAsync<string>(Options(TimeSpan.Zero, allowStale: true),
            _ => throw new GoveeException("govee is down"));

        Assert.Equal("good", result);
    }

    [Fact]
    public async Task FailureIsRememberedForTheNegativeTtl()
    {
        var attempts = 0;
        Task<CacheComputeResult<string>> Fail(CancellationToken _)
        {
            attempts++;
            throw new GoveeException("login rejected");
        }

        var options = Options(TimeSpan.Zero, allowStale: false, negative: TimeSpan.FromHours(1));

        await Assert.ThrowsAsync<GoveeException>(() => _cache.GetAsync(options, Fail));
        var remembered = await Assert.ThrowsAsync<GoveeException>(() => _cache.GetAsync(options, Fail));

        Assert.Equal("login rejected", remembered.Message);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task HardExpiredRowsAreSweptWhenTheCacheOpens()
    {
        // A negative hard TTL writes a row that is already past its hard expiry.
        await _cache.GetAsync(Options(TimeSpan.FromHours(1), hard: TimeSpan.FromSeconds(-10)),
            _ => Task.FromResult(new CacheComputeResult<string>("old")));
        Assert.Equal(1, CountRows());

        _cache.Dispose();
        _cache = new GoveeCache(NullLogger<GoveeCache>.Instance, _dir);

        Assert.Equal(0, CountRows());
    }

    private int CountRows()
    {
        using var connection = new SqliteConnection($"Data Source={_cache.Path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM cache";
        return Convert.ToInt32(command.ExecuteScalar());
    }
}

public class OrderedDispatcherTests
{
    [Fact]
    public async Task ItemsWithTheSameKeyRunInOrder()
    {
        var dispatcher = new OrderedDispatcher();
        var order = new List<int>();
        var done = new TaskCompletionSource();

        for (var i = 0; i < 20; i++)
        {
            var n = i;
            dispatcher.Enqueue("topic", async () =>
            {
                // Earlier items take longer, so parallel execution would reorder them.
                await Task.Delay(n < 5 ? 20 - n * 4 : 0);
                lock (order)
                {
                    order.Add(n);
                }
                if (n == 19)
                {
                    done.SetResult();
                }
            });
        }

        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(Enumerable.Range(0, 20), order);
    }

    [Fact]
    public async Task DifferentKeysDoNotBlockEachOther()
    {
        var dispatcher = new OrderedDispatcher();
        var release = new TaskCompletionSource();
        var other = new TaskCompletionSource();

        dispatcher.Enqueue("a", () => release.Task);
        dispatcher.Enqueue("b", () =>
        {
            other.SetResult();
            return Task.CompletedTask;
        });

        await other.Task.WaitAsync(TimeSpan.FromSeconds(5));
        release.SetResult();
    }

    [Fact]
    public async Task AFaultedItemDoesNotStallTheChain()
    {
        var dispatcher = new OrderedDispatcher();
        var second = new TaskCompletionSource();

        dispatcher.Enqueue("a", () => throw new InvalidOperationException("boom"));
        dispatcher.Enqueue("a", () =>
        {
            second.SetResult();
            return Task.CompletedTask;
        });

        await second.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task DrainedKeysAreForgotten()
    {
        var dispatcher = new OrderedDispatcher();
        var done = new TaskCompletionSource();

        dispatcher.Enqueue("a", () =>
        {
            done.SetResult();
            return Task.CompletedTask;
        });

        await done.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // The cleanup continuation runs just after the item; give it a moment.
        for (var i = 0; i < 50 && dispatcher.PendingKeys > 0; i++)
        {
            await Task.Delay(10);
        }
        Assert.Equal(0, dispatcher.PendingKeys);
    }
}

public class ArgParserTests
{
    [Fact]
    public void ParsesPositionalsFlagsAndValues()
    {
        var parser = new ArgParser(["serve", "--mqtt-host", "broker", "--no-multicast", "extra", "--http-port=9000"]);

        Assert.Equal(["serve", "extra"], parser.Positional);
        Assert.Equal("broker", parser.GetString("mqtt-host"));
        Assert.True(parser.HasFlag("no-multicast"));
        Assert.Equal(9000, parser.GetInt("http-port"));
    }

    [Fact]
    public void BooleanOptionsDoNotSwallowTheFollowingToken()
    {
        var parser = new ArgParser(["lan-control", "scene", "--list", "Sunset"]);

        Assert.Equal(["lan-control", "scene", "Sunset"], parser.Positional);
        Assert.True(parser.HasFlag("list"));
    }

    [Fact]
    public void RepeatedOptionsAccumulate()
    {
        var parser = new ArgParser(["list", "--scan", "10.0.0.1", "--scan", "10.0.0.2,10.0.0.3"]);

        Assert.Equal(["10.0.0.1", "10.0.0.2,10.0.0.3"], parser.GetAll("scan"));
        Assert.Equal(3, parser.ToOptions().Scan.Count);
    }

    [Fact]
    public void RejectsNonIntegerValues()
    {
        var parser = new ArgParser(["serve", "--http-port", "lots"]);

        Assert.Throws<GoveeException>(() => parser.GetInt("http-port"));
    }

    [Fact]
    public void FlagsBeatEnvironmentVariables()
    {
        var previous = Environment.GetEnvironmentVariable("GOVEE_MQTT_PORT");
        Environment.SetEnvironmentVariable("GOVEE_MQTT_PORT", "1999");
        try
        {
            Assert.Equal(1999, new ArgParser(["serve"]).ToOptions().MqttPort);
            Assert.Equal(2000, new ArgParser(["serve", "--mqtt-port", "2000"]).ToOptions().MqttPort);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GOVEE_MQTT_PORT", previous);
        }
    }
}

public class DotEnvTests
{
    [Fact]
    public void LoadsValuesWithoutOverridingTheEnvironment()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var quoted = $"NOVEE_TEST_QUOTED_{suffix}";
        var exported = $"NOVEE_TEST_EXPORTED_{suffix}";
        var preset = $"NOVEE_TEST_PRESET_{suffix}";

        var path = Path.Combine(Path.GetTempPath(), $"novee-{suffix}.env");
        File.WriteAllLines(path,
        [
            "# a comment",
            "",
            $"{quoted}=\"hello world\"",
            $"export {exported}=bare",
            $"{preset}=from-file",
            "not a pair",
        ]);

        Environment.SetEnvironmentVariable(preset, "from-env");
        try
        {
            Assert.Equal(path, Env.LoadDotEnv(path));

            Assert.Equal("hello world", Environment.GetEnvironmentVariable(quoted));
            Assert.Equal("bare", Environment.GetEnvironmentVariable(exported));
            Assert.Equal("from-env", Environment.GetEnvironmentVariable(preset));
        }
        finally
        {
            File.Delete(path);
            foreach (var name in new[] { quoted, exported, preset })
            {
                Environment.SetEnvironmentVariable(name, null);
            }
        }
    }

    [Fact]
    public void MissingFileIsNotAnError()
    {
        Assert.Null(Env.LoadDotEnv(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), ".env")));
    }
}

public class GoveeHttpTests
{
    [Fact]
    public async Task RateLimitResponseCarriesRetryAfter()
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("""{"code":429,"msg":"slow down"}"""),
        };
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30));

        var ex = await Assert.ThrowsAsync<HttpRequestFailedException>(
            () => GoveeHttp.ReadJsonAsync<JsonNode>(response, CancellationToken.None));

        Assert.True(ex.IsRateLimited);
        Assert.Equal(TimeSpan.FromSeconds(30), ex.RetryAfter);
    }

    /// <summary>Govee reports some failures as HTTP 200 with the code in the body.</summary>
    [Fact]
    public async Task EmbeddedServerErrorIsClassifiedAsOne()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"status":500,"message":"internal"}"""),
        };

        var ex = await Assert.ThrowsAsync<HttpRequestFailedException>(
            () => GoveeHttp.ReadJsonAsync<JsonNode>(response, CancellationToken.None));

        Assert.True(ex.IsServerError);
        Assert.False(ex.IsRateLimited);
    }
}
