namespace Novee2Mqtt.Core;

public static class SceneUtils
{
    /// <summary>
    /// Orders scene names case-insensitively and drops adjacent duplicates, so
    /// the same scene arriving from both the Platform API and the app catalog
    /// only appears once in the Home Assistant effect list.
    /// </summary>
    public static List<string> SortAndDedup(IEnumerable<string> scenes)
    {
        var sorted = scenes.OrderBy(s => s.ToLowerInvariant(), StringComparer.Ordinal).ToList();

        var result = new List<string>(sorted.Count);
        foreach (var scene in sorted)
        {
            if (result.Count == 0 || result[^1] != scene)
            {
                result.Add(scene);
            }
        }
        return result;
    }
}
