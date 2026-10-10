namespace Jellyfin.Plugin.JellyBridge.BridgeModels;

/// <summary>
/// Stage of an in-progress Jellyseerr request, from approval through download.
/// </summary>
public enum RequestStage
{
    PendingApproval,
    Searching,
    Queued,
    Downloading
}

/// <summary>
/// Progress of a single in-progress Jellyseerr request.
/// </summary>
public class RequestProgress
{
    public int RequestId { get; set; }
    public JellyseerrModel.MediaType MediaType { get; set; }
    public int TmdbId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string RequestedBy { get; set; } = string.Empty;
    public bool Is4k { get; set; }
    public RequestStage Stage { get; set; }
    public string StatusText { get; set; } = string.Empty;
    public double? Percent { get; set; }
    public DateTimeOffset? EstimatedCompletion { get; set; }
    public int DownloadCount { get; set; }

    /// <summary>
    /// One-line status shown at the top of the item overview in Jellyfin clients.
    /// </summary>
    public string OverviewLine => (Stage == RequestStage.Downloading || Stage == RequestStage.Queued ? "⬇ " : "⏳ ") + StatusText;
}

/// <summary>
/// Latest poll result of the download monitor.
/// </summary>
public class DownloadProgressSnapshot
{
    public bool Enabled { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? Error { get; set; }
    public List<RequestProgress> Items { get; set; } = new();
}
