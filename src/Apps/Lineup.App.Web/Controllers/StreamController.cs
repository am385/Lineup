using System.Net;
using System.Text.Json;
using Lineup.App.Web.Services;
using Lineup.App.Web.Services.Playback;
using Lineup.Plugin.SiliconDust.HDHomeRun.Device;
using Microsoft.AspNetCore.Mvc;

namespace Lineup.App.Web.Controllers;

/// <summary>Adapts management playback startup and artifacts to HTTP responses.</summary>
[ApiController]
[Route("api/[controller]")]
public class StreamController : ControllerBase
{
    private const string StreamLimitError = "The stream or startup limit has been reached.";
    private readonly ILineupSettingsService _settingsService;
    private readonly IActiveStreamRegistry _activeStreamRegistry;
    private readonly ITransientDataStore _transientData;
    private readonly IVirtualMpegTsOutputService? _virtualMpegTsOutputs;
    private readonly ILogger<StreamController> _logger;
    private readonly ManagementPlaybackService _playback;

    /// <summary>Initializes the HTTP playback adapter and its application-owned runtime.</summary>
    public StreamController(
        IHttpClientFactory httpClientFactory,
        ILineupGuideReader guideReader,
        ILineupSettingsService settingsService,
        StreamingTopologyStore topologyStore,
        IMediaProbeService mediaProbeService,
        IActiveStreamRegistry activeStreamRegistry,
        IProtectedContentSlateService protectedContentSlateService,
        ITunerStreamMultiplexer tunerStreamMultiplexer,
        ILogger<StreamController> logger,
        TimeProvider? timeProvider = null,
        TimeSpan? cmafInactivityTimeout = null,
        SubtitleSidecarService? subtitleSidecars = null,
        ITransientDataStore? transientData = null,
        ICmafCompatibilityTestService? compatibilityTestService = null,
        IMediaProcessFactory? mediaProcessFactory = null,
        IStreamingRouteResolver? streamRoutes = null,
        ICmafOutputCoordinator? cmafOutputs = null,
        IVirtualMpegTsOutputService? virtualMpegTsOutputs = null,
        TimeSpan? cmafStartupTimeout = null,
        ManagementPlaybackService? playback = null)
    {
        _settingsService = settingsService;
        _activeStreamRegistry = activeStreamRegistry;
        _logger = logger;
        _transientData = transientData ?? TransientDataStore.CreateDefault();
        _virtualMpegTsOutputs = virtualMpegTsOutputs;
        _playback = playback ?? new ManagementPlaybackService(
            httpClientFactory, guideReader, settingsService, topologyStore, mediaProbeService, activeStreamRegistry,
            protectedContentSlateService, tunerStreamMultiplexer, logger, timeProvider, cmafInactivityTimeout,
            transientData: _transientData, compatibilityTestService: compatibilityTestService, mediaProcessFactory: mediaProcessFactory,
            streamRoutes: streamRoutes, cmafOutputs: cmafOutputs, cmafStartupTimeout: cmafStartupTimeout);
    }

    /// <summary>Gets the application runtime used by transport regression fixtures.</summary>
    internal ManagementPlaybackService Playback => _playback;

    /// <summary>Streams one management-hosted virtual HDHomeRun channel as MPEG-TS.</summary>
    [HttpGet("/auto/v{channel}")]
    [Lineup.Core.VirtualOutputs.Hosting.PluginConnectionEndpoint]
    [HttpGet("/hdhomerun/{virtualDeviceId}/auto/v{channel}")]
    public async Task<IActionResult> Stream(string channel, string? virtualDeviceId = null, [FromQuery] string transcode = "none")
    {
        if (!string.Equals(transcode, "none", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { error = "Hardware transcode query profiles are not supported by configured topology streams." });
        }
        if (_virtualMpegTsOutputs is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "The streaming topology runtime is unavailable." });
        }

        using var startup = _activeStreamRegistry.TryBeginStartup(_settingsService.Settings.MaximumConcurrentStreams, HttpContext.Abort);
        if (startup is null)
        {
            return StatusCode(StatusCodes.Status429TooManyRequests, new { error = StreamLimitError });
        }
        try
        {
            await using var output = await _virtualMpegTsOutputs.OpenAsync(
                ResolveHdHomeRunVirtualDeviceId(virtualDeviceId),
                channel,
                HttpContext.RequestAborted);
            var sessionId = StreamSessionId.New().ToString();
            var activeStream = ActiveStreamPlanFactory.CreateMpegTs(
                sessionId,
                channel,
                DateTime.UtcNow,
                output.RawSource,
                output.Source,
                output.Profile) with
            {
                ClientAddress = FormatClientAddress(HttpContext.Connection.RemoteIpAddress, HttpContext.Connection.RemotePort),
                Flow = output.Flow
            };
            if (!startup.TryRegister(activeStream, HttpContext.Abort))
            {
                return StatusCode(StatusCodes.Status429TooManyRequests, new { error = StreamLimitError });
            }
            startup.Dispose();
            try
            {
                Response.ContentType = "video/mp2t";
                Response.Headers.CacheControl = "no-cache, no-store";
                await output.Stream.CopyToAsync(Response.Body, HttpContext.RequestAborted);
                return new EmptyResult();
            }
            finally
            {
                _activeStreamRegistry.Unregister(sessionId);
            }
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            return new EmptyResult();
        }
        catch (KeyNotFoundException exception)
        {
            return NotFound(new { error = exception.Message });
        }
        catch (StreamAcquisitionException exception)
        {
            return CreateSourceAcquisitionResult(channel, exception);
        }
        catch (InvalidOperationException exception)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = exception.Message });
        }
    }

    /// <summary>Starts a management-hosted Web Player presentation.</summary>
    [HttpPost("cmaf/start/{channel}")]
    [Lineup.Core.VirtualOutputs.Hosting.PluginConnectionEndpoint]
    public async Task<IActionResult> StartCmafStream(string channel, [FromBody] CmafStreamRequest request) =>
        MapStartupResult(await _playback.StartAsync(
            channel,
            request,
            FormatClientAddress(HttpContext.Connection.RemoteIpAddress, HttpContext.Connection.RemotePort),
            HttpContext.TraceIdentifier,
            HttpContext.RequestAborted), channel);

    /// <summary>Starts an exact single-rendition synthetic compatibility presentation.</summary>
    [HttpPost("watch-test/start")]
    public async Task<IActionResult> StartCmafCompatibilityTest([FromQuery] CmafCompatibilityTestRequest request) =>
        MapStartupResult(await _playback.StartCompatibilityAsync(request, HttpContext.RequestAborted));

    /// <summary>Stops one viewer without stopping a shared producer used by other viewers.</summary>
    [HttpPost("watch-test/stop/{sessionId}")]
    [HttpPost("cmaf/stop/{sessionId}")]
    public IActionResult StopCmaf(string sessionId) =>
        _playback.Stop(sessionId) ? Ok(new { message = "Session stopped" }) : NotFound(new { error = "Session not found" });

    /// <summary>Lists active viewer aliases independently of shared producers.</summary>
    [HttpGet("watch-test/sessions")]
    [HttpGet("cmaf/sessions")]
    public IActionResult GetCmafSessions() => Ok(_playback.GetViewers().Select(viewer => new
    {
        viewer.SessionId,
        viewer.Channel,
        startTime = viewer.StartTime,
        durationMinutes = viewer.DurationMinutes,
        isRunning = viewer.IsRunning
    }).ToList());

    private IActionResult MapStartupResult(ManagementPlaybackStartResult result, string channel = "")
    {
        if (result.IsCanceled)
        {
            return new EmptyResult();
        }
        if (result.Presentation is { } presentation)
        {
            var baseUrl = $"/api/stream/cmaf/{presentation.SessionId}";
            return Ok(new CmafStreamResponse(presentation.SessionId, $"{baseUrl}/{CmafStreamPlanner.HlsManifestName}", $"{baseUrl}/{CmafStreamPlanner.DashManifestName}")
            {
                SourceAudioFallbackApplied = presentation.SourceAudioFallbackApplied,
                FallbackAudioTitle = presentation.FallbackAudioTitle,
                FallbackAudioCodec = presentation.FallbackAudioCodec,
                HasSourceVideoRendition = presentation.HasSourceVideoRendition,
                SourceVideoCodec = presentation.SourceVideoCodec,
                Subtitles = presentation.Subtitles.Select(subtitle => new CmafSubtitleRendition(
                    subtitle.SourceIndex, subtitle.Title, subtitle.Language, $"{baseUrl}/captions-{subtitle.SourceIndex}.vtt", subtitle.IsEmbeddedClosedCaptions)).ToArray()
            });
        }
        if (result.Compatibility is { } compatibility)
        {
            var baseUrl = $"/api/stream/watch-test/{compatibility.SessionId}";
            var manifest = compatibility.Protocol == CmafProtocol.Dash ? CmafStreamPlanner.DashManifestName : CmafStreamPlanner.HlsManifestName;
            return Ok(new CmafCompatibilityTestResponse(
                compatibility.SessionId, $"{baseUrl}/{manifest}", compatibility.VideoCodec, compatibility.AudioCodec,
                compatibility.ChannelCount, compatibility.Width, compatibility.Height, compatibility.HasSubtitles ? $"{baseUrl}/captions-0.vtt" : null));
        }
        var failure = result.Failure ?? throw new InvalidOperationException("Playback startup returned no terminal result.");
        if (failure.Acquisition is { } acquisition)
        {
            return CreateSourceAcquisitionResult(channel, acquisition);
        }
        if (failure.Protection is { } protection)
        {
            return StatusCode(StatusCodes.Status422UnprocessableEntity, new
            {
                code = "protected_content",
                error = failure.Error,
                retryable = false,
                inferred = protection.IsInferred,
                source = protection.IsInferred ? "channel-lineup" : "tuner"
            });
        }
        if (failure.Kind == ManagementPlaybackFailureKind.Unsupported)
        {
            return UnprocessableEntity(new { error = failure.Error });
        }
        var status = failure.Kind switch
        {
            ManagementPlaybackFailureKind.InvalidRequest => StatusCodes.Status400BadRequest,
            ManagementPlaybackFailureKind.Disabled => StatusCodes.Status403Forbidden,
            ManagementPlaybackFailureKind.NotFound => StatusCodes.Status404NotFound,
            ManagementPlaybackFailureKind.Unavailable => StatusCodes.Status503ServiceUnavailable,
            ManagementPlaybackFailureKind.AdmissionLimit => StatusCodes.Status429TooManyRequests,
            ManagementPlaybackFailureKind.MediaFailure => StatusCodes.Status502BadGateway,
            ManagementPlaybackFailureKind.Timeout => StatusCodes.Status504GatewayTimeout,
            _ => StatusCodes.Status500InternalServerError
        };
        return failure.Code is null
            ? StatusCode(status, new { error = failure.Error })
            : StatusCode(status, new { code = failure.Code, error = failure.Error, stage = failure.Stage });
    }

    private IActionResult CreateSourceAcquisitionResult(string channel, StreamAcquisitionException exception)
    {
        var failure = StreamAcquisitionHttpMapper.Map(exception);
        _logger.LogWarning(
            exception,
            "Source acquisition failed for channel {Channel} with code {FailureCode}; retryable: {Retryable}; candidates: {Candidates}",
            channel,
            failure.Code,
            exception.Retryable,
            StreamAcquisitionHttpMapper.FormatCandidates(exception));
        return StatusCode(failure.StatusCode, new { code = failure.Code, error = failure.Error });
    }

    /// <summary>
    /// Serves contained shared CMAF manifests, fragments, and subtitle artifacts.
    /// Opens artifacts before creating responses so cleanup cannot invalidate a deferred file open.
    /// The response executor owns binary streams; text artifacts are read and disposed within this action.
    /// </summary>
    [Lineup.Core.VirtualOutputs.Hosting.PluginConnectionEndpoint]
    [HttpGet("cmaf/{sessionId}/{filename}")]
    public IActionResult GetCmafFile(string sessionId, string filename, [FromQuery] long offset = 0)
    {
        if (!_playback.TryGetArtifacts(sessionId, out var session))
        {
            return NotFound(new { error = "Session not found" });
        }

        if (!TryResolveCmafFilePath(session.CmafDirectory, filename, out var filePath))
        {
            return BadRequest(new { error = "Invalid CMAF filename" });
        }

        var extension = Path.GetExtension(filename);
        var isSubtitle = string.Equals(extension, ".vtt", StringComparison.OrdinalIgnoreCase);
        if (isSubtitle && offset < 0)
        {
            return BadRequest(new { error = "Invalid subtitle offset." });
        }

        var stream = _transientData.OpenRead(filePath);
        if (stream is null)
        {
            if (!isSubtitle)
            {
                return NotFound(new { error = "File not found" });
            }

            Response.Headers.CacheControl = "no-cache, no-store";
            Response.Headers["X-Lineup-Subtitle-Offset"] = offset.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return File([], "text/vtt; charset=utf-8");
        }

        if (isSubtitle)
        {
            using (stream)
            {
                var effectiveOffset = Math.Min(offset, stream.Length);
                stream.Position = effectiveOffset;
                var data = new byte[(int)Math.Min(stream.Length - effectiveOffset, 64 * 1024)];
                stream.ReadExactly(data);
                Response.Headers.CacheControl = "no-cache, no-store";
                Response.Headers["X-Lineup-Subtitle-Offset"] = (effectiveOffset + data.Length).ToString(System.Globalization.CultureInfo.InvariantCulture);
                return File(data, "text/vtt; charset=utf-8");
            }
        }

        if (string.Equals(extension, ".mpd", StringComparison.OrdinalIgnoreCase))
        {
            using (stream)
            {
                using var reader = new StreamReader(stream);
                var manifest = reader.ReadToEnd();
                if (!CmafStreamPlanner.IsDashManifestComplete(manifest))
                {
                    return StatusCode(425, new { code = "media_manifest_not_ready", error = "The media manifest is being published.", stage = "delivery" });
                }
                manifest = CmafStreamPlanner.RewriteManifestAudioLabels(manifest, session.AudioRenditions);
                if (session.SourceVideoCodec is { } sourceVideoCodec)
                {
                    manifest = CmafStreamPlanner.RewriteManifestCodecs(manifest, sourceVideoCodec);
                }
                Response.Headers.CacheControl = "no-cache, no-store";
                return Content(manifest, "application/dash+xml");
            }
        }

        if ((session.SourceVideoCodec is not null || session.AudioRenditions.Count > 0) &&
            string.Equals(filename, CmafStreamPlanner.HlsManifestName, StringComparison.Ordinal))
        {
            using (stream)
            {
                using var reader = new StreamReader(stream);
                var manifest = CmafStreamPlanner.RewriteManifestAudioLabels(reader.ReadToEnd(), session.AudioRenditions);
                if (session.SourceVideoCodec is { } sourceVideoCodec)
                {
                    manifest = CmafStreamPlanner.RewriteManifestCodecs(manifest, sourceVideoCodec);
                }
                Response.Headers.CacheControl = "no-cache, no-store";
                return Content(manifest, "application/vnd.apple.mpegurl");
            }
        }

        if (IsNumberedFile(filename, "media_", ".m3u8"))
        {
            using (stream)
            {
                var dashPath = _transientData.GetFilePath(session.CmafDirectory, CmafStreamPlanner.DashManifestName);
                using var dashStream = _transientData.OpenRead(dashPath);
                if (dashStream is null)
                {
                    return NotFound(new { error = "DASH timeline not found" });
                }

                using var playlistReader = new StreamReader(stream);
                using var dashReader = new StreamReader(dashStream);
                var dashManifest = dashReader.ReadToEnd();
                if (!CmafStreamPlanner.IsDashManifestComplete(dashManifest))
                {
                    return StatusCode(425, new { code = "media_manifest_not_ready", error = "The media manifest is being published.", stage = "delivery" });
                }
                var playlist = CmafStreamPlanner.RewriteHlsProgramDateTimes(playlistReader.ReadToEnd(), dashManifest);
                Response.Headers.CacheControl = "no-cache, no-store";
                return Content(playlist, "application/vnd.apple.mpegurl");
            }
        }

        var contentType = extension switch
        {
            ".mpd" => "application/dash+xml",
            ".m3u8" => "application/vnd.apple.mpegurl",
            ".mp4" => "video/mp4",
            ".m4s" => "video/iso.segment",
            ".ts" => "video/mp2t",
            ".vtt" => "text/vtt; charset=utf-8",
            _ => "application/octet-stream"
        };

        Response.Headers.AccessControlAllowOrigin = "*";
        Response.Headers.CacheControl = extension is ".mp4" or ".m4s"
            ? "public, max-age=31536000, immutable"
            : "no-cache, no-store";

        var transferred = false;
        try
        {
            var result = File(stream, contentType);
            if (stream is FileStream fileStream)
            {
                result.LastModified = System.IO.File.GetLastWriteTimeUtc(fileStream.SafeFileHandle);
            }
            transferred = true;
            return result;
        }
        finally
        {
            if (!transferred)
            {
                stream.Dispose();
            }
        }
    }

    /// <summary>Serves synthetic Watch Test artifacts without consuming plugin connection capacity.</summary>
    [HttpGet("watch-test/{sessionId}/{filename}")]
    public IActionResult GetCompatibilityFile(string sessionId, string filename, [FromQuery] long offset = 0)
    {
        if (!_playback.TryGetArtifacts(sessionId, out var session) || !session.IsCompatibilityTest)
        {
            return NotFound(new { error = "Session not found" });
        }
        return GetCmafFile(sessionId, filename, offset);
    }

    /// <summary>
    /// Formats a remote client endpoint for active-stream diagnostics.
    /// </summary>
    /// <param name="address">The remote client IP address.</param>
    /// <param name="port">The remote client port.</param>
    /// <returns>The endpoint text, or <see langword="null"/> when the address is unavailable.</returns>
    [NonAction]
    public static string? FormatClientAddress(IPAddress? address, int port)
    {
        return address is null ? null : new IPEndPoint(address, port).ToString();
    }

    /// <summary>
    /// Resolves an approved generated CMAF basename beneath its session directory.
    /// </summary>
    /// <param name="cmafDirectory">The CMAF session directory.</param>
    /// <param name="filename">The requested generated basename.</param>
    /// <param name="filePath">The canonical contained path when validation succeeds.</param>
    /// <returns><see langword="true"/> when the requested name is a valid generated CMAF artifact.</returns>
    [NonAction]
    public static bool TryResolveCmafFilePath(string cmafDirectory, string filename, out string filePath)
    {
        filePath = string.Empty;
        if (string.IsNullOrWhiteSpace(filename) ||
            Path.IsPathRooted(filename) ||
            filename.Contains('/') ||
            filename.Contains('\\'))
        {
            return false;
        }

        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(filename);
        }
        catch (UriFormatException)
        {
            return false;
        }

        if (!string.Equals(decoded, filename, StringComparison.Ordinal) &&
            (decoded.Contains('/') || decoded.Contains('\\') || Path.IsPathRooted(decoded)))
        {
            return false;
        }

        var isManifest = filename is CmafStreamPlanner.DashManifestName or CmafStreamPlanner.HlsManifestName;
        var isMediaPlaylist = IsNumberedFile(filename, "media_", ".m3u8");
        var isInitialization = IsNumberedFile(filename, "init-", ".mp4");
        var isFragment = IsCmafFragment(filename);
        var isSubtitle = IsNumberedFile(filename, "captions-", ".vtt");
        if (!isManifest && !isMediaPlaylist && !isInitialization && !isFragment && !isSubtitle)
        {
            return false;
        }

        var directoryPath = Path.GetFullPath(cmafDirectory);
        var candidatePath = Path.GetFullPath(Path.Combine(directoryPath, filename));
        var directoryPrefix = Path.TrimEndingDirectorySeparator(directoryPath) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!candidatePath.StartsWith(directoryPrefix, comparison))
        {
            return false;
        }

        filePath = candidatePath;
        return true;
    }

    private static bool IsNumberedFile(string fileName, string prefix, string suffix)
    {
        if (!fileName.StartsWith(prefix, StringComparison.Ordinal) || !fileName.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }

        var digits = fileName.AsSpan(prefix.Length, fileName.Length - prefix.Length - suffix.Length);
        return !digits.IsEmpty && digits.IndexOfAnyExceptInRange('0', '9') < 0;
    }

    private static bool IsCmafFragment(string fileName)
    {
        if (!fileName.StartsWith("chunk-", StringComparison.Ordinal) || !fileName.EndsWith(".m4s", StringComparison.Ordinal))
        {
            return false;
        }

        var components = fileName.AsSpan(6, fileName.Length - 10).ToString().Split('-', StringSplitOptions.RemoveEmptyEntries);
        return components.Length == 2 && components.All(component => component.All(char.IsAsciiDigit));
    }

    private VirtualDeviceId ResolveHdHomeRunVirtualDeviceId(string? virtualDeviceId)
    {
        var devices = _settingsService.Settings.Streaming.VirtualDevices
            .Where(device =>
                device.Enabled &&
                device.Hosting is null &&
                string.Equals(device.Adapter.Kind, HdHomeRunVirtualTunerDriver.AdapterKind, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (string.IsNullOrWhiteSpace(virtualDeviceId))
        {
            return devices.FirstOrDefault()?.Id
                ?? throw new KeyNotFoundException("No enabled virtual HDHomeRun device is configured.");
        }

        var matched = devices.SingleOrDefault(device =>
        {
            if (Guid.TryParse(virtualDeviceId, out var parsed) && device.Id.Value == parsed)
            {
                return true;
            }

            var configured = device.Adapter.Configuration.Deserialize<HdHomeRunVirtualTunerConfiguration>(
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            var externalId = string.IsNullOrWhiteSpace(configured?.DeviceId)
                ? HdHomeRunProxyIdentity.CreateDeviceId(device.Id.Value.ToString("N"))
                : configured.DeviceId;
            return externalId.Equals(virtualDeviceId, StringComparison.OrdinalIgnoreCase);
        });
        return matched?.Id
            ?? throw new KeyNotFoundException("The requested configured virtual HDHomeRun device was not found.");
    }

}
