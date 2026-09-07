using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace XrayUI.Models
{
    /// <summary>
    /// User-facing notes shipped as changelog.json with each GitHub release.
    /// Entries are matched by version; translations share the same entry.
    /// </summary>
    internal sealed class ChangelogFeed
    {
        [JsonPropertyName("versions")] public List<ChangelogVersion>? Versions { get; set; }
    }

    internal sealed class ChangelogVersion
    {
        [JsonPropertyName("version")] public string? Version { get; set; }
        [JsonPropertyName("zh")]      public List<string>? Zh { get; set; }
        [JsonPropertyName("en")]      public List<string>? En { get; set; }
        [JsonPropertyName("ru")]      public List<string>? Ru { get; set; }
    }
}
