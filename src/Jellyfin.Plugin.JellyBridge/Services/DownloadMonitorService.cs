using System.Text.RegularExpressions;
using Jellyfin.Plugin.JellyBridge.BridgeModels;
using Jellyfin.Plugin.JellyBridge.Configuration;
using Jellyfin.Plugin.JellyBridge.JellyfinModels;
using Jellyfin.Plugin.JellyBridge.JellyseerrModel;
using Jellyfin.Plugin.JellyBridge.Utils;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyBridge.Services;

/// <summary>
/// Polls Jellyseerr for request and download status, shows it on JellyBridge items,
/// and notifies requesters in their Jellyfin sessions once their media is available.
/// </summary>
public sealed class DownloadMonitorService : IHostedService, IDisposable
{
    private const string StateFileName = "download-monitor.json";
    private const int MinimumIntervalSeconds = 15;
    private const long NotificationTimeoutMs = 15000;
    private const int MaxSeasonsInTitle = 3;
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TitleCacheLifetime = TimeSpan.FromHours(1);
    private static readonly TimeSpan UndeliveredLifetime = TimeSpan.FromDays(7);
    private static readonly Regex StatusLinePattern = new(@"^(?:⬇|⏳) [^\n]*\n*", RegexOptions.Compiled);

    private readonly DebugLogger<DownloadMonitorService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ISessionManager _sessionManager;
    private readonly SemaphoreSlim _pollLock = new(1, 1);
    private readonly Dictionary<(MediaType type, int tmdbId), string> _titleCache = new();
    private DateTimeOffset _titleCacheCreated = DateTimeOffset.MinValue;
    // Status lines persist in the Jellyfin database across restarts, so assume they may exist until one cleanup pass has run.
    private bool _overviewsMayHaveStatus = true;
    private HashSet<int>? _lastInProgressIds;
    private CancellationTokenSource? _stopping;
    private Task? _loop;

    public DownloadMonitorService(ILogger<DownloadMonitorService> logger, IServiceScopeFactory scopeFactory, ISessionManager sessionManager)
    {
        _logger = new DebugLogger<DownloadMonitorService>(logger);
        _scopeFactory = scopeFactory;
        _sessionManager = sessionManager;
    }

    public DownloadProgressSnapshot Snapshot { get; private set; } = new();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stopping = new CancellationTokenSource();
        _loop = RunAsync(_stopping.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_stopping == null || _loop == null)
        {
            return;
        }

        _stopping.Cancel();
        try
        {
            await _loop.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose()
    {
        _stopping?.Dispose();
        _pollLock.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(StartupDelay, cancellationToken).ConfigureAwait(false);
        while (!cancellationToken.IsCancellationRequested)
        {
            await PollAsync(cancellationToken).ConfigureAwait(false);

            var interval = Math.Max(MinimumIntervalSeconds, Plugin.GetConfigOrDefault<int>(nameof(PluginConfiguration.DownloadMonitorIntervalSeconds)));
            await Task.Delay(TimeSpan.FromSeconds(interval), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs one poll. Concurrent calls (timer and the config page refresh button) wait for the running poll instead of overlapping.
    /// </summary>
    public async Task PollAsync(CancellationToken cancellationToken)
    {
        await _pollLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var showProgress = Plugin.GetConfigOrDefault<bool>(nameof(PluginConfiguration.ShowRequestProgress));
            var notify = Plugin.GetConfigOrDefault<bool>(nameof(PluginConfiguration.NotifyOnAvailable));
            if (!showProgress && _overviewsMayHaveStatus)
            {
                await ClearOverviewsAsync(cancellationToken).ConfigureAwait(false);
            }

            if (!showProgress && !notify)
            {
                Snapshot = new DownloadProgressSnapshot { Enabled = false, UpdatedAt = DateTimeOffset.UtcNow };
                return;
            }

            if (string.IsNullOrEmpty(Plugin.GetConfiguration().ApiKey))
            {
                Snapshot = new DownloadProgressSnapshot { Enabled = true, UpdatedAt = DateTimeOffset.UtcNow, Error = "Jellyseerr API key is not configured." };
                return;
            }

            using var scope = _scopeFactory.CreateScope();
            var apiService = scope.ServiceProvider.GetRequiredService<ApiService>();
            var libraryManager = scope.ServiceProvider.GetRequiredService<JellyfinILibraryManager>();
            var metadataService = scope.ServiceProvider.GetRequiredService<MetadataService>();

            // ApiService returns empty defaults on failure, and an empty request list would look like every request finished.
            if (await apiService.CallEndpointAsync(JellyseerrEndpoint.Status) is not SystemStatus { Version.Length: > 0 })
            {
                Snapshot = new DownloadProgressSnapshot { Enabled = true, UpdatedAt = DateTimeOffset.UtcNow, Error = "Failed to connect to Jellyseerr." };
                return;
            }

            var requests = (await apiService.CallEndpointAsync(JellyseerrEndpoint.ReadRequests) as List<JellyseerrMediaRequest> ?? new())
                .Where(r => r?.Media != null && r.Media.TmdbId > 0)
                .ToList();

            var jellyfinItems = new List<IJellyfinItem>();
            jellyfinItems.AddRange(libraryManager.GetExistingItems<JellyfinMovie>());
            jellyfinItems.AddRange(libraryManager.GetExistingItems<JellyfinSeries>());

            var titles = await ResolveTitlesAsync(requests, jellyfinItems, metadataService).ConfigureAwait(false);
            var progress = requests
                .Where(r => r.IsInProgress)
                .Select(r => BuildProgress(r, titles))
                .OrderBy(p => p.Stage)
                .ThenBy(p => p.Title)
                .ToList();

            Snapshot = new DownloadProgressSnapshot { Enabled = true, UpdatedAt = DateTimeOffset.UtcNow, Items = progress };
            _logger.LogDebug("Download monitor found {Count} in-progress requests", progress.Count);

            var inProgressIds = progress.Select(p => p.RequestId).ToHashSet();
            var requestFinished = _lastInProgressIds?.Any(id => !inProgressIds.Contains(id)) == true;
            _lastInProgressIds = inProgressIds;

            if (showProgress)
            {
                await UpdateOverviewsAsync(jellyfinItems, progress, cancellationToken).ConfigureAwait(false);
                _overviewsMayHaveStatus = true;
            }

            if (notify)
            {
                await ProcessNotificationsAsync(requests, titles, libraryManager, cancellationToken).ConfigureAwait(false);
            }

            if (requestFinished)
            {
                _ = SyncRequestStatusAsync();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Download monitor poll failed");
            Snapshot = new DownloadProgressSnapshot { Enabled = true, UpdatedAt = DateTimeOffset.UtcNow, Error = ex.Message };
        }
        finally
        {
            _pollLock.Release();
        }
    }

    #region Progress

    private static RequestProgress BuildProgress(JellyseerrMediaRequest request, Dictionary<(MediaType type, int tmdbId), string> titles)
    {
        var downloads = request.TargetDownloads;
        var progress = new RequestProgress
        {
            RequestId = request.Id,
            MediaType = request.Media.MediaType,
            TmdbId = request.Media.TmdbId,
            Title = titles.GetValueOrDefault((request.Media.MediaType, request.Media.TmdbId))
                ?? downloads.FirstOrDefault()?.Title
                ?? $"TMDB {request.Media.TmdbId}",
            RequestedBy = new[] { request.RequestedBy?.JellyfinUsername, request.RequestedBy?.Username, request.RequestedBy?.DisplayName }
                .FirstOrDefault(name => !string.IsNullOrEmpty(name)) ?? string.Empty,
            Is4k = request.Is4k,
            DownloadCount = downloads.Count
        };

        if (request.Status == MediaRequestStatus.PENDING)
        {
            progress.Stage = RequestStage.PendingApproval;
            progress.StatusText = "Requested, waiting for approval";
            return progress;
        }

        if (downloads.Count == 0)
        {
            progress.Stage = RequestStage.Searching;
            progress.StatusText = "Approved, searching for a release";
            return progress;
        }

        var size = downloads.Sum(d => d.Size);
        var sizeLeft = downloads.Sum(d => d.SizeLeft);
        progress.Percent = size > 0 ? Math.Clamp((size - sizeLeft) * 100.0 / size, 0, 100) : null;
        progress.EstimatedCompletion = downloads
            .Select(d => d.EstimatedCompletionTime)
            .Where(t => t > DateTimeOffset.UtcNow)
            .Max();

        var percentText = progress.Percent.HasValue ? $" {progress.Percent.Value:0}%" : string.Empty;
        var filesText = downloads.Count > 1 ? $", {downloads.Count} files" : string.Empty;
        if (downloads.Any(d => string.Equals(d.Status, "downloading", StringComparison.OrdinalIgnoreCase)))
        {
            progress.Stage = RequestStage.Downloading;
            var etaText = progress.EstimatedCompletion.HasValue
                ? $" (about {FormatDuration(progress.EstimatedCompletion.Value - DateTimeOffset.UtcNow)} left{filesText})"
                : (filesText.Length > 0 ? $" ({filesText.TrimStart(',', ' ')})" : string.Empty);
            progress.StatusText = $"Downloading{percentText}{etaText}";
        }
        else
        {
            progress.Stage = RequestStage.Queued;
            var state = downloads.First().Status;
            progress.StatusText = $"Download {(string.IsNullOrEmpty(state) ? "queued" : state.ToLowerInvariant())}{percentText}{filesText}";
        }

        return progress;
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalDays >= 1)
        {
            return $"{(int)duration.TotalDays}d {duration.Hours}h";
        }

        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        }

        return duration.TotalMinutes >= 1 ? $"{(int)duration.TotalMinutes}m" : "under a minute";
    }

    private async Task<Dictionary<(MediaType type, int tmdbId), string>> ResolveTitlesAsync(
        List<JellyseerrMediaRequest> requests,
        List<IJellyfinItem> jellyfinItems,
        MetadataService metadataService)
    {
        if (DateTimeOffset.UtcNow - _titleCacheCreated > TitleCacheLifetime)
        {
            _titleCache.Clear();
            _titleCacheCreated = DateTimeOffset.UtcNow;
        }

        foreach (var item in jellyfinItems)
        {
            var tmdbId = item.GetTmdbId();
            if (tmdbId.HasValue && !string.IsNullOrEmpty(item.Name))
            {
                _titleCache.TryAdd((IJellyseerrItem.GetMediaType(item), tmdbId.Value), item.Name);
            }
        }

        var hasMissingTitles = requests.Any(r => !_titleCache.ContainsKey((r.Media.MediaType, r.Media.TmdbId)));
        if (hasMissingTitles)
        {
            // Requested items hidden by .ignore are no longer in Jellyfin, but their discover metadata still holds the title.
            var (movies, shows) = await metadataService.ReadMetadataAsync().ConfigureAwait(false);
            foreach (var movie in movies.Where(m => !string.IsNullOrEmpty(m.MediaName)))
            {
                _titleCache.TryAdd((MediaType.MOVIE, movie.Id), movie.MediaName);
            }

            foreach (var show in shows.Where(s => !string.IsNullOrEmpty(s.MediaName)))
            {
                _titleCache.TryAdd((MediaType.TV, show.Id), show.MediaName);
            }
        }

        return _titleCache;
    }

    /// <summary>
    /// Hides finished requests from the discover library now instead of at the next scheduled sync.
    /// </summary>
    private async Task SyncRequestStatusAsync()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var syncService = scope.ServiceProvider.GetRequiredService<SyncService>();
            var refreshService = scope.ServiceProvider.GetRequiredService<RefreshService>();
            await Plugin.ExecuteWithLockAsync(async () =>
            {
                var result = await syncService.SyncRequestStatus().ConfigureAwait(false);
                await refreshService.ApplyRefreshAsync(result).ConfigureAwait(false);
                return result.Success;
            }, _logger, "Sync Request Status").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update discover library after a request finished");
        }
    }

    #endregion

    #region Item Overviews

    private async Task ClearOverviewsAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var libraryManager = scope.ServiceProvider.GetRequiredService<JellyfinILibraryManager>();
        var jellyfinItems = new List<IJellyfinItem>();
        jellyfinItems.AddRange(libraryManager.GetExistingItems<JellyfinMovie>());
        jellyfinItems.AddRange(libraryManager.GetExistingItems<JellyfinSeries>());
        await UpdateOverviewsAsync(jellyfinItems, new List<RequestProgress>(), cancellationToken).ConfigureAwait(false);
        _overviewsMayHaveStatus = false;
    }

    private async Task UpdateOverviewsAsync(List<IJellyfinItem> jellyfinItems, List<RequestProgress> progress, CancellationToken cancellationToken)
    {
        var statusLines = progress
            .GroupBy(p => (p.MediaType, p.TmdbId))
            .ToDictionary(g => g.Key, g => g.First().OverviewLine);

        var updated = 0;
        foreach (var item in jellyfinItems.Where(i => FolderUtils.IsPathInSyncDirectory(i.Path)))
        {
            BaseItem? baseItem = item switch
            {
                JellyfinMovie movie => movie.Inner,
                JellyfinSeries series => series.Inner,
                _ => null
            };
            var tmdbId = item.GetTmdbId();
            if (baseItem == null || !tmdbId.HasValue)
            {
                continue;
            }

            var currentOverview = baseItem.Overview ?? string.Empty;
            var originalOverview = StatusLinePattern.Replace(currentOverview, string.Empty, 1);
            var newOverview = originalOverview;
            if (statusLines.TryGetValue((IJellyseerrItem.GetMediaType(item), tmdbId.Value), out var statusLine))
            {
                newOverview = string.IsNullOrEmpty(originalOverview) ? statusLine : $"{statusLine}\n\n{originalOverview}";
            }

            if (newOverview == currentOverview)
            {
                continue;
            }

            try
            {
                baseItem.Overview = newOverview;
                await baseItem.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
                updated++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to update download status on '{ItemName}'", item.Name);
            }
        }

        if (updated > 0)
        {
            _logger.LogDebug("Updated download status on {Count} JellyBridge items", updated);
        }
    }

    #endregion

    #region Notifications

    private async Task ProcessNotificationsAsync(
        List<JellyseerrMediaRequest> requests,
        Dictionary<(MediaType type, int tmdbId), string> titles,
        JellyfinILibraryManager libraryManager,
        CancellationToken cancellationToken)
    {
        var statePath = Path.Combine(Plugin.Instance.DataFolderPath, StateFileName);
        var state = await LoadStateAsync(statePath).ConfigureAwait(false);
        var inProgressIds = requests.Where(r => r.IsInProgress).Select(r => r.Id).ToList();

        // First run: start watching current requests without notifying for everything that finished in the past.
        if (state == null)
        {
            await SaveStateAsync(statePath, new DownloadMonitorState { Watching = inProgressIds }).ConfigureAwait(false);
            return;
        }

        var watching = state.Watching.ToHashSet();
        foreach (var request in requests.Where(r => r.IsMediaReady && watching.Contains(r.Id)))
        {
            if (!Guid.TryParse(request.RequestedBy?.JellyfinUserId, out var userId))
            {
                _logger.LogDebug("Request {RequestId} is available but its requester has no Jellyfin account", request.Id);
                continue;
            }

            state.Undelivered.Add(new PendingNotification
            {
                RequestId = request.Id,
                UserId = userId,
                Title = GetAvailableTitle(request, titles, libraryManager) + GetSeasonSuffix(request),
                ReadyAt = DateTimeOffset.UtcNow
            });
        }

        state.Watching = inProgressIds;
        state.Undelivered = await DeliverAsync(state.Undelivered, cancellationToken).ConfigureAwait(false);
        await SaveStateAsync(statePath, state).ConfigureAwait(false);
    }

    private static string GetAvailableTitle(
        JellyseerrMediaRequest request,
        Dictionary<(MediaType type, int tmdbId), string> titles,
        JellyfinILibraryManager libraryManager)
    {
        var jellyfinMediaId = request.Is4k ? request.Media.JellyfinMediaId4k : request.Media.JellyfinMediaId;
        if (Guid.TryParse(jellyfinMediaId, out var itemId))
        {
            var name = libraryManager.Inner.GetItemById(itemId)?.Name;
            if (!string.IsNullOrEmpty(name))
            {
                return name;
            }
        }

        return titles.GetValueOrDefault((request.Media.MediaType, request.Media.TmdbId)) ?? "Your request";
    }

    private static string GetSeasonSuffix(JellyseerrMediaRequest request)
    {
        var seasons = request.RequestedSeasonNumbers.OrderBy(n => n).ToList();
        return seasons.Count switch
        {
            0 or > MaxSeasonsInTitle => string.Empty,
            1 => $" Season {seasons[0]}",
            _ => $" Seasons {string.Join(", ", seasons)}"
        };
    }

    /// <summary>
    /// Sends each notification to all of the user's open sessions. Users with no open session keep theirs until they come back online or it expires.
    /// </summary>
    private async Task<List<PendingNotification>> DeliverAsync(List<PendingNotification> notifications, CancellationToken cancellationToken)
    {
        var remaining = new List<PendingNotification>();
        foreach (var notification in notifications)
        {
            var sessions = _sessionManager.Sessions.Where(s => s.UserId == notification.UserId).ToList();
            if (sessions.Count == 0)
            {
                if (DateTimeOffset.UtcNow - notification.ReadyAt < UndeliveredLifetime)
                {
                    remaining.Add(notification);
                }
                continue;
            }

            var message = new MessageCommand
            {
                Header = "Ready to watch",
                Text = $"{notification.Title} has finished downloading and is ready to watch.",
                TimeoutMs = NotificationTimeoutMs
            };

            var delivered = false;
            foreach (var session in sessions)
            {
                try
                {
                    await _sessionManager.SendMessageCommand(null!, session.Id, message, cancellationToken).ConfigureAwait(false);
                    delivered = true;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug("Could not send notification to session {SessionId} ({Client}): {Message}", session.Id, session.Client, ex.Message);
                }
            }

            if (delivered)
            {
                _logger.LogInformation("Notified user {UserId} that '{Title}' is available", notification.UserId, notification.Title);
            }
            else
            {
                remaining.Add(notification);
            }
        }

        return remaining;
    }

    private async Task<DownloadMonitorState?> LoadStateAsync(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            return JellyBridgeJsonSerializer.Deserialize<DownloadMonitorState>(json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read download monitor state, starting fresh");
            return null;
        }
    }

    private static async Task SaveStateAsync(string path, DownloadMonitorState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JellyBridgeJsonSerializer.Serialize(state)).ConfigureAwait(false);
    }

    private sealed class DownloadMonitorState
    {
        public List<int> Watching { get; set; } = new();
        public List<PendingNotification> Undelivered { get; set; } = new();
    }

    private sealed class PendingNotification
    {
        public int RequestId { get; set; }
        public Guid UserId { get; set; }
        public string Title { get; set; } = string.Empty;
        public DateTimeOffset ReadyAt { get; set; }
    }

    #endregion
}
