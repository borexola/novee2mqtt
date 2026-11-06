using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Novee2Mqtt.Ble;

/// <summary>
/// Choices a person made about Bluetooth, kept in a file of their own next to
/// the cache. They are not cached data, so purging the cache leaves them alone.
/// </summary>
public sealed class BleSettings
{
    private readonly ILogger _log;
    private readonly string _path;
    private readonly object _gate = new();
    private readonly HashSet<string> _keepConnected = new(StringComparer.OrdinalIgnoreCase);

    public BleSettings(ILogger<BleSettings> log, string path)
    {
        _log = log;
        _path = path;
        Load();
    }

    public bool KeepConnected(string deviceId)
    {
        lock (_gate)
        {
            return _keepConnected.Contains(deviceId);
        }
    }

    public void SetKeepConnected(string deviceId, bool keep)
    {
        lock (_gate)
        {
            var changed = keep ? _keepConnected.Add(deviceId) : _keepConnected.Remove(deviceId);
            if (changed)
            {
                Save();
            }
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(_path));
            _keepConnected.UnionWith(stored?.KeepConnected ?? []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A damaged file must not stop the bridge. The default is just "off".
            _log.LogWarning("Could not read {Path}, so no light will stay connected: {Message}", _path, ex.Message);
        }
    }

    private void Save()
    {
        try
        {
            // Written to one side first, so a crash cannot leave half a file.
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Stored { KeepConnected = [.. _keepConnected.Order(StringComparer.Ordinal)] }));
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning("Could not save {Path}: {Message}", _path, ex.Message);
        }
    }

    private sealed class Stored
    {
        [System.Text.Json.Serialization.JsonPropertyName("keep_connected")]
        public List<string> KeepConnected { get; set; } = [];
    }
}
