using System.Net;
using System.Text.Json.Nodes;

namespace Novee2Mqtt.Core;

/// <summary>Thrown when a Govee endpoint reports failure, carrying the HTTP status for retry decisions.</summary>
public sealed class HttpRequestFailedException(HttpStatusCode status, string message, TimeSpan? retryAfter = null)
    : GoveeException(message)
{
    public HttpStatusCode Status { get; } = status;

    /// <summary>The server's <c>Retry-After</c>, when it sent one.</summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;

    public bool IsRateLimited => Status == HttpStatusCode.TooManyRequests;

    public bool IsServerError => (int)Status >= 500;
}

public static class GoveeHttp
{
    /// <summary>
    /// Reads a Govee JSON response. Govee signals errors both through the HTTP
    /// status and through an embedded <c>status</c>/<c>code</c> field that can be
    /// non-200 on an HTTP 200 response, so both are checked.
    /// </summary>
    public static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var url = response.RequestMessage?.RequestUri?.ToString() ?? "<unknown url>";
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestFailedException(
                response.StatusCode,
                $"request {url} status {(int)response.StatusCode}: {response.ReasonPhrase}. Response body: {Truncate(body)}",
                RetryAfter(response));
        }

        if (TryGetEmbeddedStatus(body, out var embeddedStatus, out var embeddedMessage) && embeddedStatus != 200)
        {
            var status = Enum.IsDefined(typeof(HttpStatusCode), embeddedStatus)
                ? (HttpStatusCode)embeddedStatus
                : HttpStatusCode.InternalServerError;

            throw new HttpRequestFailedException(
                status,
                $"Request to {url} failed with code {embeddedStatus} {embeddedMessage}. Full response: {Truncate(body)}",
                RetryAfter(response));
        }

        return Json.Deserialize<T>(body);
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
        {
            return delta;
        }
        if (header?.Date is { } date)
        {
            return date - DateTimeOffset.UtcNow;
        }
        return null;
    }

    private static bool TryGetEmbeddedStatus(string body, out int status, out string message)
    {
        status = 0;
        message = "";

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(body);
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }

        if (node is not JsonObject obj)
        {
            return false;
        }

        var statusNode = obj["status"] ?? obj["code"];
        if (statusNode.AsInt64() is not { } value)
        {
            return false;
        }

        status = (int)value;
        message = (obj["message"] ?? obj["msg"]).AsString() ?? "";
        return true;
    }

    private static string Truncate(string text) => text.Length > 4096 ? text[..4096] + "..." : text;
}
