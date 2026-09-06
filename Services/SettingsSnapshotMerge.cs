using System.Text.Json.Nodes;
namespace XrayUI.Services;
public static class SettingsSnapshotMerge
{
    public static JsonObject Apply(JsonObject current, JsonObject baseline, JsonObject edited)
    {
        var merged = (JsonObject)current.DeepClone();
        foreach (var pair in edited)
            if (!JsonNode.DeepEquals(baseline[pair.Key], pair.Value))
                merged[pair.Key] = pair.Value?.DeepClone();
        return merged;
    }
}
