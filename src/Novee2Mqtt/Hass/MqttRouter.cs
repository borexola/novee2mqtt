namespace Novee2Mqtt.Hass;

/// <summary>An inbound MQTT message matched against a route pattern.</summary>
public sealed class RouteContext
{
    public required string Topic { get; init; }
    public required string Payload { get; init; }
    public required IReadOnlyDictionary<string, string> Parameters { get; init; }

    public string Param(string name)
        => Parameters.TryGetValue(name, out var value)
            ? value
            : throw new Core.GoveeException($"route parameter '{name}' is missing from topic {Topic}");
}

/// <summary>
/// Routes MQTT topics to handlers using <c>:name</c> path parameters, e.g.
/// <c>gv2mqtt/light/:id/command</c>. Patterns are also what we subscribe with,
/// after rewriting each parameter to a single-level <c>+</c> wildcard.
/// </summary>
public sealed class MqttRouter
{
    private readonly List<Route> _routes = [];

    public void Add(string pattern, Func<RouteContext, CancellationToken, Task> handler)
        => _routes.Add(new Route(pattern.Split('/'), handler));

    /// <summary>Topic filters to subscribe to, one per registered route.</summary>
    public IEnumerable<string> SubscriptionFilters => _routes
        .Select(r => string.Join('/', r.Segments.Select(s => s.StartsWith(':') ? "+" : s)))
        .Distinct(StringComparer.Ordinal);

    /// <returns>False if no route matched, so the caller can log it.</returns>
    public async Task<bool> DispatchAsync(string topic, string payload, CancellationToken cancellationToken)
    {
        var segments = topic.Split('/');

        foreach (var route in _routes)
        {
            if (!route.TryMatch(segments, out var parameters))
            {
                continue;
            }

            var context = new RouteContext
            {
                Topic = topic,
                Payload = payload,
                Parameters = parameters!,
            };

            await route.Handler(context, cancellationToken).ConfigureAwait(false);
            return true;
        }

        return false;
    }

    private sealed record Route(string[] Segments, Func<RouteContext, CancellationToken, Task> Handler)
    {
        public bool TryMatch(string[] topicSegments, out Dictionary<string, string>? parameters)
        {
            parameters = null;

            if (topicSegments.Length != Segments.Length)
            {
                return false;
            }

            Dictionary<string, string>? captured = null;

            for (var i = 0; i < Segments.Length; i++)
            {
                var pattern = Segments[i];
                if (pattern.StartsWith(':'))
                {
                    captured ??= new Dictionary<string, string>(StringComparer.Ordinal);
                    captured[pattern[1..]] = topicSegments[i];
                }
                else if (!string.Equals(pattern, topicSegments[i], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            parameters = captured ?? new Dictionary<string, string>(StringComparer.Ordinal);
            return true;
        }
    }
}
