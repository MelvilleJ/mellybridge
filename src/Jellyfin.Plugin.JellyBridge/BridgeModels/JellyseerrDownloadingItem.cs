using System.Text.Json.Serialization;
using Jellyfin.Plugin.JellyBridge.JellyseerrModel;

namespace Jellyfin.Plugin.JellyBridge.BridgeModels;

public class JellyseerrDownloadingItem : DownloadingItem
{
    /// <summary>
    /// Media type as enum - override to use JsonStringEnumConverter
    /// </summary>
    [JsonPropertyName("mediaType")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public new JellyseerrModel.MediaType MediaType { get; set; }

    /// <summary>
    /// Size in bytes as reported by Radarr/Sonarr. Override: int overflows for downloads over 2 GB.
    /// </summary>
    [JsonPropertyName("size")]
    public new long Size { get; set; }

    /// <summary>
    /// Remaining bytes as reported by Radarr/Sonarr. Override: int overflows for downloads over 2 GB.
    /// </summary>
    [JsonPropertyName("sizeLeft")]
    public new long SizeLeft { get; set; }

    /// <summary>
    /// Estimated completion time. Override: queued downloads may report null, which fails the non-nullable base property.
    /// </summary>
    [JsonPropertyName("estimatedCompletionTime")]
    public new DateTimeOffset? EstimatedCompletionTime { get; set; }
}
