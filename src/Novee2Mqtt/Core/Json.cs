using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Novee2Mqtt.Core;

/// <summary>
/// Shared serializer settings. Govee's APIs are inconsistent about casing and
/// about whether numbers arrive as numbers or strings, so everything funnels
/// through here.
/// </summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = Create(indented: false);
    public static readonly JsonSerializerOptions Pretty = Create(indented: true);

    private static JsonSerializerOptions Create(bool indented) => new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        WriteIndented = indented,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static T Deserialize<T>(string text)
    {
        try
        {
            var value = JsonSerializer.Deserialize<T>(text, Options);
            if (value is null)
            {
                throw new GoveeException($"parsing {typeof(T).Name}: got null. Input: {Truncate(text)}");
            }
            return value;
        }
        catch (JsonException ex)
        {
            throw new GoveeException($"parsing {typeof(T).Name}: {ex.Message}. Input: {Truncate(text)}", ex);
        }
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static string SerializePretty<T>(T value) => JsonSerializer.Serialize(value, Pretty);

    /// <summary>
    /// Equivalent of serde_json's <c>Value::pointer</c>: walks a JSON pointer
    /// such as <c>/value/workMode</c>, returning null if any step is missing.
    /// </summary>
    public static JsonNode? Pointer(this JsonNode? node, string pointer)
    {
        if (node is null || pointer.Length == 0)
        {
            return node;
        }

        var current = node;
        foreach (var rawSegment in pointer.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var segment = rawSegment.Replace("~1", "/").Replace("~0", "~");
            switch (current)
            {
                case JsonObject obj when obj.TryGetPropertyValue(segment, out var next):
                    current = next;
                    break;
                case JsonArray arr when int.TryParse(segment, out var index) && index >= 0 && index < arr.Count:
                    current = arr[index];
                    break;
                default:
                    return null;
            }
        }
        return current;
    }

    public static long? AsInt64(this JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }
        if (value.TryGetValue<long>(out var l)) return l;
        if (value.TryGetValue<double>(out var d) && d == Math.Floor(d)) return (long)d;
        if (value.TryGetValue<string>(out var s) && long.TryParse(s, out var parsed)) return parsed;
        return null;
    }

    public static double? AsDouble(this JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }
        if (value.TryGetValue<double>(out var d)) return d;
        if (value.TryGetValue<string>(out var s) && double.TryParse(s, out var parsed)) return parsed;
        return null;
    }

    public static string? AsString(this JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;

    /// <summary>
    /// Structural equality for JSON nodes. Used to match a work mode's value
    /// against the value reported in device state.
    /// </summary>
    public static bool JsonEquals(JsonNode? a, JsonNode? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }
        return a.ToJsonString() == b.ToJsonString();
    }

    private static string Truncate(string text) => text.Length > 2048 ? text[..2048] + "..." : text;
}

/// <summary>Anything that went wrong talking to Govee, a broker, or a device.</summary>
public class GoveeException : Exception
{
    public GoveeException(string message) : base(message) { }
    public GoveeException(string message, Exception inner) : base(message, inner) { }
}
