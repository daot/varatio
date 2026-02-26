using System.Diagnostics;
using System.Net.Mime;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VARatio.Api;

[ApiController]
[Route("VARatio")]
public class VARatioApiController : ControllerBase
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<VARatioApiController> _logger;
    private readonly IVarTimelineProvider _timelineProvider;
    private readonly ISessionManager _sessionManager;

    public VARatioApiController(
        ILibraryManager libraryManager,
        ILogger<VARatioApiController> logger,
        IVarTimelineProvider timelineProvider,
        ISessionManager sessionManager)
    {
        _libraryManager = libraryManager;
        _logger = logger;
        _timelineProvider = timelineProvider;
        _sessionManager = sessionManager;
    }

    /// <summary>
    /// Resolves a GUID to a library item. If it's not a direct item id, tries to find it from active sessions (e.g. MediaSourceId / PlaySessionId from the video URL).
    /// </summary>
    private Guid? ResolveToLibraryItemId(Guid guid)
    {
        var item = _libraryManager.GetItemById(guid);
        if (item != null && !string.IsNullOrEmpty(item.Path))
        {
            return guid;
        }

        var guidStr = guid.ToString("N");
        var guidStrDashes = guid.ToString();
        Guid? singleSessionItemId = null;
        var sessionsWithPlayback = 0;

        foreach (var session in _sessionManager.Sessions)
        {
            if (session.NowPlayingItem?.Id == null)
            {
                continue;
            }

            sessionsWithPlayback++;
            var libId = session.NowPlayingItem.Id;
            if (libId == guid)
            {
                return libId;
            }

            if (string.Equals(session.Id, guidStrDashes, StringComparison.OrdinalIgnoreCase)
                || string.Equals(session.Id, guidStr, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("VARatio: Resolved session id {Guid} to library item {ItemId}", guid, libId);
                return libId;
            }

            var mediaSourceId = session.PlayState?.MediaSourceId;
            if (string.Equals(mediaSourceId, guidStrDashes, StringComparison.OrdinalIgnoreCase)
                || string.Equals(mediaSourceId, guidStr, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("VARatio: Resolved MediaSourceId {Guid} to library item {ItemId}", guid, libId);
                return libId;
            }

            singleSessionItemId = libId;
        }

        // Fallback: if exactly one session has playback, use it (client may have sent a GUID we don't store)
        if (sessionsWithPlayback == 1 && singleSessionItemId != null)
        {
            _logger.LogDebug("VARatio: Resolved {Guid} to library item {ItemId} (single active session)", guid, singleSessionItemId);
            return singleSessionItemId;
        }

        return null;
    }

    [HttpGet("Data")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> GetData([FromQuery] Guid itemId)
    {
        var resolvedId = ResolveToLibraryItemId(itemId);
        if (resolvedId == null)
        {
            _logger.LogWarning("VARatio: GetData - item {ItemId} not found and could not resolve from sessions", itemId);
            return NotFound("Item not found");
        }

        var item = _libraryManager.GetItemById(resolvedId.Value);
        if (item == null || string.IsNullOrEmpty(item.Path))
        {
            _logger.LogWarning("VARatio: GetData - item {ItemId} not found", resolvedId);
            return NotFound("Item not found");
        }

        var varPath = Path.Combine(
            Path.GetDirectoryName(item.Path)!,
            Path.GetFileNameWithoutExtension(item.Path) + ".var");

        if (!System.IO.File.Exists(varPath))
        {
            _logger.LogInformation("VARatio: GetData - no .var file at {Path}", varPath);
            return NotFound("VARatio data not found for this item");
        }

        _logger.LogInformation("VARatio: GetData - serving .var file {Path}", varPath);
        var content = await System.IO.File.ReadAllTextAsync(varPath);
        return Content(content, MediaTypeNames.Text.Plain);
    }

    [HttpGet("Player.js")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> GetPlayerScript()
    {
        var assembly = typeof(VARatioApiController).Assembly;
        const string resourceName = "Jellyfin.Plugin.VARatio.Web.Player.js";

        await using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            _logger.LogError("VARatio: Player.js resource {Resource} not found in assembly", resourceName);
            return NotFound("Player script not found in assembly resources");
        }

        using var reader = new StreamReader(stream);
        var content = await reader.ReadToEndAsync();

        return Content(content, "application/javascript");
    }

    [HttpGet("Stream")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task Stream([FromQuery] Guid itemId, CancellationToken cancellationToken)
    {
        var resolvedId = ResolveToLibraryItemId(itemId);
        if (resolvedId == null)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            await Response.Body.FlushAsync(cancellationToken);
            return;
        }

        var item = _libraryManager.GetItemById(resolvedId.Value);
        if (item == null || string.IsNullOrEmpty(item.Path))
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            await Response.Body.FlushAsync(cancellationToken);
            return;
        }

        if (!_timelineProvider.TryGetTimeline(item.Path, out var timeline) || timeline is null)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            await Response.Body.FlushAsync(cancellationToken);
            return;
        }

        var filter = FfmpegCropFilterBuilder.BuildCropFilter(timeline);
        if (string.IsNullOrEmpty(filter))
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            await Response.Body.FlushAsync(cancellationToken);
            return;
        }

        var config = Plugin.Instance?.Configuration ?? new Config();
        var ffmpegPath = ResolveTool(config.FfmpegPath, "ffmpeg");

        var inputPath = item.Path;

        // We produce a fragmented MP4 stream with H.264 video and copied audio.
        var args =
            $"-nostdin -i \"{inputPath}\" " +
            $"-map 0:v:0 -map 0:a? " +
            $"-vf \"{filter}\" " +
            "-c:v libx264 -preset veryfast -crf 18 " +
            "-c:a copy " +
            "-movflags +frag_keyframe+empty_moov+default_base_moof " +
            "-f mp4 -";

        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = "video/mp4";
        Response.Headers.CacheControl = "no-store";

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        try
        {
            _logger.LogInformation("VARatio: Starting ffmpeg stream for {Path}", inputPath);
            process.Start();

            // Drain stderr in the background to avoid blocking if buffers fill.
            _ = Task.Run(async () =>
            {
                try
                {
                    var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
                    if (!string.IsNullOrWhiteSpace(stderr))
                    {
                        _logger.LogDebug("VARatio ffmpeg stderr for {Path}:{NewLine}{Err}",
                            inputPath, Environment.NewLine, stderr);
                    }
                }
                catch
                {
                    // Ignore logging errors.
                }
            }, cancellationToken);

            await using var output = process.StandardOutput.BaseStream;

            try
            {
                await output.CopyToAsync(Response.Body, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Client disconnected or request aborted.
            }
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                }
            }
            catch
            {
                // Ignore kill failures.
            }
        }
    }

    private static string ResolveTool(string configured, string fallback) =>
        string.IsNullOrWhiteSpace(configured) ? fallback : configured;
}
