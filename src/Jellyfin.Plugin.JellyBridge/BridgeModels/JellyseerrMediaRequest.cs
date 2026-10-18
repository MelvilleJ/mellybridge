using System.Text.Json.Serialization;
using Jellyfin.Plugin.JellyBridge.JellyseerrModel;
using Jellyfin.Plugin.JellyBridge.JellyfinModels;

namespace Jellyfin.Plugin.JellyBridge.BridgeModels;

/// <summary>
/// Bridge model for Jellyseerr MediaRequest objects that customizes the generated MediaRequest model
/// to match the actual API response structure from the TypeScript MediaRequest entity.
/// Only overrides properties where the type or JSON property name changes.
/// </summary>
public class JellyseerrMediaRequest : MediaRequest
{
    // Properties that need type changes from base class

    /// <summary>
    /// Media type as enum - override to use JsonStringEnumConverter
    /// </summary>
    [JsonPropertyName("type")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public new JellyseerrModel.MediaType Type { get; set; }

    /// <summary>
    /// Media as Media object ignored in json output - override to use JellyseerrMedia
    /// </summary>
    //[JsonIgnore]
    //public new Media Media { get; set; } = new();
    [JsonPropertyName("media")]
    public new JellyseerrMedia Media { get; set; } = new();

    /// <summary>
    /// Requesting user - override to use the filtered user shape returned by the API
    /// </summary>
    [JsonPropertyName("requestedBy")]
    public new JellyseerrUser? RequestedBy { get; set; }

    // All other properties inherit from base class with correct types and JSON names

    /// <summary>
    /// Media status for the quality tier this request targets (4K requests track Status4k).
    /// </summary>
    [JsonIgnore]
    public JellyseerrModel.MediaStatus? TargetMediaStatus => Media == null ? null : (Is4k ? Media.Status4k : Media.Status);

    /// <summary>
    /// Active downloads for the quality tier this request targets.
    /// </summary>
    [JsonIgnore]
    public List<JellyseerrDownloadingItem> TargetDownloads =>
        (Is4k ? Media?.DownloadStatus4kList : Media?.DownloadStatusList)?.Where(d => d != null).ToList() ?? new();

    /// <summary>
    /// Season numbers this request asks for. Empty for movies.
    /// </summary>
    [JsonIgnore]
    public List<int> RequestedSeasonNumbers =>
        Seasons?.Where(s => s != null).Select(s => s.SeasonNumber).Distinct().ToList() ?? new();

    /// <summary>
    /// Availability of one season for the quality tier this request targets, or null if Jellyseerr has no record of it.
    /// </summary>
    public JellyseerrModel.MediaStatus? GetTargetSeasonStatus(int seasonNumber)
    {
        var season = Media?.Seasons?.FirstOrDefault(s => s != null && s.SeasonNumber == seasonNumber);
        return season == null ? null : (Is4k ? season.Status4k : season.Status);
    }

    public static bool IsReadyStatus(JellyseerrModel.MediaStatus? status) =>
        status is JellyseerrModel.MediaStatus.AVAILABLE or JellyseerrModel.MediaStatus.PARTIALLY_AVAILABLE;

    /// <summary>
    /// True once the requested media can be watched in Jellyfin. For series this means every requested season,
    /// so a request for season 3 is not resolved by season 1 already being in the library.
    /// Partially available seasons count so that airing seasons resolve.
    /// </summary>
    [JsonIgnore]
    public bool IsMediaReady
    {
        get
        {
            var seasonNumbers = RequestedSeasonNumbers;
            if (seasonNumbers.Count > 0 && Media?.Seasons?.Count > 0)
            {
                return seasonNumbers.All(n => IsReadyStatus(GetTargetSeasonStatus(n)));
            }

            return IsReadyStatus(TargetMediaStatus);
        }
    }

    /// <summary>
    /// True while the request is still waiting on approval, a release search, or a download.
    /// </summary>
    [JsonIgnore]
    public bool IsInProgress => !IsMediaReady
        && Status is not (JellyseerrModel.MediaRequestStatus.DECLINED or JellyseerrModel.MediaRequestStatus.FAILED or JellyseerrModel.MediaRequestStatus.COMPLETED)
        && TargetMediaStatus is not (JellyseerrModel.MediaStatus.BLACKLISTED or JellyseerrModel.MediaStatus.DELETED);
    
    /// <summary>
    /// Tests if this request matches an IJellyfinItem by comparing TMDB IDs and media types.
    /// </summary>
    /// <param name="jellyfinItem">The IJellyfinItem to compare against</param>
    /// <returns>True if the request matches the IJellyfinItem, false otherwise</returns>
    public bool EqualsItem(IJellyfinItem jellyfinItem)
    {
        if (jellyfinItem == null || Media == null)
            return false;
            
        // Compare media types first
        var jellyfinType = jellyfinItem switch
        {
            JellyfinMovie => JellyseerrModel.MediaType.MOVIE,
            JellyfinSeries => JellyseerrModel.MediaType.TV,
            _ => throw new NotSupportedException($"Unsupported item type: {jellyfinItem.GetType().Name}")
        };
        if (Type != jellyfinType)
            return false;
            
        // Get TMDB ID from Jellyfin item
        int? jellyfinTmdbId = null;
        // Try to get TMDB ID from provider IDs
        if (jellyfinItem.ProviderIds.TryGetValue("Tmdb", out var providerId) && !string.IsNullOrEmpty(providerId))
        {
            if (int.TryParse(providerId, out var id))
            {
                jellyfinTmdbId = id;
            }
        }
        if (!jellyfinTmdbId.HasValue)
            return false;
            
        // Compare TMDB IDs
        return Media.Id == jellyfinTmdbId.Value;
    }
}
