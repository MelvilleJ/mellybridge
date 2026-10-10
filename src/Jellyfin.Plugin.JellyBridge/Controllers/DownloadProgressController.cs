using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Jellyfin.Plugin.JellyBridge.BridgeModels;
using Jellyfin.Plugin.JellyBridge.Services;
using Jellyfin.Plugin.JellyBridge.Utils;

namespace Jellyfin.Plugin.JellyBridge.Controllers
{
    [ApiController]
    [Route("JellyBridge")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public class DownloadProgressController : ControllerBase
    {
        private readonly DebugLogger<DownloadProgressController> _logger;
        private readonly DownloadMonitorService _downloadMonitor;

        public DownloadProgressController(ILoggerFactory loggerFactory, DownloadMonitorService downloadMonitor)
        {
            _logger = new DebugLogger<DownloadProgressController>(loggerFactory.CreateLogger<DownloadProgressController>());
            _downloadMonitor = downloadMonitor;
        }

        [HttpGet("DownloadProgress")]
        public IActionResult GetDownloadProgress()
        {
            return Ok(ToResponse(_downloadMonitor.Snapshot));
        }

        [HttpPost("DownloadProgress/Refresh")]
        public async Task<IActionResult> RefreshDownloadProgress()
        {
            _logger.LogDebug("Download progress refresh requested from plugin configuration page.");
            await _downloadMonitor.PollAsync(HttpContext.RequestAborted);
            return Ok(ToResponse(_downloadMonitor.Snapshot));
        }

        private static object ToResponse(DownloadProgressSnapshot snapshot) => new
        {
            enabled = snapshot.Enabled,
            updatedAt = snapshot.UpdatedAt,
            error = snapshot.Error,
            items = snapshot.Items.Select(item => new
            {
                requestId = item.RequestId,
                mediaType = item.MediaType.ToString().ToLowerInvariant(),
                tmdbId = item.TmdbId,
                title = item.Title,
                requestedBy = item.RequestedBy,
                is4k = item.Is4k,
                stage = item.Stage.ToString(),
                statusText = item.StatusText,
                percent = item.Percent,
                estimatedCompletion = item.EstimatedCompletion,
                downloadCount = item.DownloadCount
            })
        };
    }
}
