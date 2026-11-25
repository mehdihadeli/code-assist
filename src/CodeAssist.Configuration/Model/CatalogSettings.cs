using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace CodeAssist.Configuration;

public enum CatalogRefresh
{
    /// <summary>Only the 'models refresh' command fetches a new catalog.</summary>
    [JsonStringEnumMemberName("manual")] Manual,
    /// <summary>Refresh in the background when the cached copy is older than a day.</summary>
    [JsonStringEnumMemberName("daily")] Daily,
    /// <summary>Never touch the network; use only the cached catalog (or settings overrides).</summary>
    [JsonStringEnumMemberName("never")] Never,
}

public sealed class CatalogSettings
{
    [Description("Catalog feed in the models.dev api.json shape. HTTPS only.")]
    public string Url { get; set; } = "https://models.dev/api.json";

    [Description("When the cached catalog is refreshed.")]
    public CatalogRefresh Refresh { get; set; } = CatalogRefresh.Manual;

    [Range(1, 300), Description("Seconds to wait for the feed.")]
    public int TimeoutSeconds { get; set; } = 20;
}
