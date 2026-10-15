using System.Buffers;
using System.Text;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Protocol;

namespace Novee2Mqtt.Core;

/// <summary>
/// An MQTT client that stays connected. MQTTnet's plain client does not
/// reconnect on its own and both brokers we talk to drop idle connections, so
/// this supervises the link and replays the subscriptions afterwards.
/// </summary>
public sealed class MqttConnection(ILogger log, string name, MqttClientOptions options) : IAsyncDisposable
{
    private readonly IMqttClient _client = new MqttClientFactory().CreateMqttClient();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly OrderedDispatcher _dispatcher = new();
    private Task? _supervisor;

    /// <summary>Runs after every reconnect, to re-subscribe and re-announce.</summary>
    public Func<CancellationToken, Task>? OnReconnected { get; set; }

    public Func<string, string, CancellationToken, Task>? OnMessage { get; set; }

    /// <summary>
    /// Set for the Govee IoT link, whose topics embed the account and device
    /// identifiers and so act as credentials.
    /// </summary>
    public bool TopicsAreSensitive { get; init; }

    public bool IsConnected => _client.IsConnected;

    private string Show(string topic) => TopicsAreSensitive && !Env.LogSensitiveData ? "REDACTED" : topic;

    public CancellationToken ShutdownToken => _shutdown.Token;

    /// <summary>Connects for the first time. Throws if the broker is unreachable.</summary>
    public async Task ConnectAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        _client.ApplicationMessageReceivedAsync += args =>
        {
            var topic = args.ApplicationMessage.Topic;
            var payload = Encoding.UTF8.GetString(BuffersExtensions.ToArray(args.ApplicationMessage.Payload));
            log.LogTrace("{Name} {Topic} -> {Payload}", name, Show(topic), payload);

            // Handlers can block for seconds while talking to a device, so the
            // receive loop must not wait for them. Messages on the same topic
            // still run in the order they arrived: an ON followed by an OFF for
            // one light, or two IoT status packets, must not be applied swapped.
            if (OnMessage is { } handler)
            {
                _dispatcher.Enqueue(topic, async () =>
                {
                    try
                    {
                        await handler(topic, payload, _shutdown.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // Shutting down.
                    }
                    catch (Exception ex)
                    {
                        log.LogError("{Name} handling {Topic}: {Message}", name, Show(topic), ex.Message);
                    }
                });
            }

            return Task.CompletedTask;
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        try
        {
            var result = await _client.ConnectAsync(options, cts.Token).ConfigureAwait(false);
            log.LogInformation("Connected to {Name}: {ResultCode}", name, result.ResultCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GoveeException($"timeout connecting to {name}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new GoveeException($"connecting to {name}: {ex.Message}", ex);
        }
    }

    public void StartSupervisor() => _supervisor = Task.Run(() => SuperviseAsync(_shutdown.Token), CancellationToken.None);

    private async Task SuperviseAsync(CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromSeconds(5);
        var maxDelay = TimeSpan.FromSeconds(60);
        var wasConnected = true;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

                if (_client.IsConnected)
                {
                    if (!wasConnected)
                    {
                        log.LogInformation("{Name} reconnected", name);
                        if (OnReconnected is { } handler)
                        {
                            await handler(cancellationToken).ConfigureAwait(false);
                        }
                        wasConnected = true;
                    }
                    delay = TimeSpan.FromSeconds(5);
                    continue;
                }

                wasConnected = false;
                log.LogWarning("{Name} connection is down; reconnecting", name);
                await _client.ConnectAsync(options, cancellationToken).ConfigureAwait(false);
                delay = TimeSpan.FromSeconds(5);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                log.LogError("{Name} reconnect failed: {Message}", name, ex.Message);
                delay = delay * 2 > maxDelay ? maxDelay : delay * 2;
            }
        }
    }

    public async Task SubscribeAsync(IEnumerable<string> topicFilters, CancellationToken cancellationToken)
    {
        var builder = new MqttClientSubscribeOptionsBuilder();
        foreach (var filter in topicFilters)
        {
            builder.WithTopicFilter(f => f.WithTopic(filter).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce));
            log.LogTrace("{Name} subscribing to {Filter}", name, Show(filter));
        }

        await _client.SubscribeAsync(builder.Build(), cancellationToken).ConfigureAwait(false);
    }

    public Task PublishAsync(string topic, string payload, CancellationToken cancellationToken)
    {
        log.LogTrace("{Topic} -> {Payload}", Show(topic), payload);

        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(Encoding.UTF8.GetBytes(payload))
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
            .WithRetainFlag(false)
            .Build();

        return _client.PublishAsync(message, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);

        if (_supervisor is not null)
        {
            try
            {
                await _supervisor.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown.
            }
        }

        try
        {
            await _client.DisconnectAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best effort; the last will covers an ungraceful exit.
        }

        _client.Dispose();
        _shutdown.Dispose();
    }
}

/// <summary>
/// Runs work items in the background, serialized per key but concurrent across
/// keys. Each key holds the tail of its chain; a new item continues from that
/// tail, and the entry is dropped once the chain has drained.
/// </summary>
public sealed class OrderedDispatcher
{
    private readonly Dictionary<string, Task> _tails = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    /// <summary>Items waiting or running, across all keys. For tests and diagnostics.</summary>
    public int PendingKeys
    {
        get
        {
            lock (_lock)
            {
                return _tails.Count;
            }
        }
    }

    public void Enqueue(string key, Func<Task> work)
    {
        lock (_lock)
        {
            var previous = _tails.TryGetValue(key, out var tail) ? tail : Task.CompletedTask;

            // ContinueWith rather than await: the chain must advance even if an
            // earlier item faulted, and the work itself catches its own errors.
            // Not ExecuteSynchronously: the first item of a chain would otherwise
            // start on the caller's thread, inside this lock.
            var next = previous.ContinueWith(
                _ => work(),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default).Unwrap();

            _tails[key] = next;

            next.ContinueWith(
                finished =>
                {
                    lock (_lock)
                    {
                        if (_tails.TryGetValue(key, out var current) && ReferenceEquals(current, finished))
                        {
                            _tails.Remove(key);
                        }
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }
}
