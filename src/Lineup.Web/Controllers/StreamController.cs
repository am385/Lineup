using Lineup.Web.Services;
using Lineup.HDHomeRun.Device;
using Lineup.Core;
using Lineup.Core.Storage;
using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Net;

namespace Lineup.Web.Controllers;

/// <summary>
/// Proxies video streams from HDHomeRun devices to avoid mixed content issues.
/// The browser connects to this HTTPS endpoint which forwards the HTTP stream from the device.
/// Supports multiple output formats: direct proxy, HLS (disk), and fMP4 (memory).
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class StreamController : ControllerBase
{
    private static readonly TimeSpan TunerDiagnosticTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Fmp4StartupTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DefaultHlsInactivityTimeout = TimeSpan.FromMinutes(2);
    private const string StreamLimitError = "The maximum number of concurrent streams is already active.";
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IEpgRepository _epgRepository;
    private readonly IAppSettingsService _settingsService;
    private readonly ChannelLineupStore _channelLineupStore;
    private readonly IDeviceStateService _deviceState;
    private readonly IHdHomeRunProxyProfileProvider _proxyProfiles;
    private readonly IMpegTsTranscodeService _mpegTsTranscodeService;
    private readonly IMediaProbeService _mediaProbeService;
    private readonly IActiveStreamRegistry _activeStreamRegistry;
    private readonly IProtectedContentSlateService _protectedContentSlateService;
    private readonly ITunerStreamMultiplexer _tunerStreamMultiplexer;
    private readonly ITunerCapacityLeaseRegistry _tunerCapacityLeases;
    private readonly ILogger<StreamController> _logger;
    private readonly IHostApplicationLifetime? _applicationLifetime;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _hlsInactivityTimeout;
    private readonly SubtitleSidecarService _subtitleSidecars;
    private readonly ITransientDataStore _transientData;

    // Track active HLS streams by session ID
    private static readonly ConcurrentDictionary<string, HlsSession> _hlsSessions = new();

    // Track active fMP4 streams
    private static readonly ConcurrentDictionary<string, FMp4Session> _fmp4Sessions = new();
    /// <summary>
    /// Initializes the streaming API controller.
    /// </summary>
    /// <param name="httpClientFactory">Factory for direct HDHomeRun diagnostic requests.</param>
    /// <param name="epgRepository">Provides cached channel DRM metadata.</param>
    /// <param name="settingsService">Provides device and transcode settings.</param>
    /// <param name="channelLineupStore">Provides persisted channel availability choices.</param>
    /// <param name="deviceState">Provides physical device information for Watch streams.</param>
    /// <param name="proxyProfiles">Resolves optional device-scoped proxy streams.</param>
    /// <param name="mpegTsTranscodeService">Produces the transparent MPEG-TS stream.</param>
    /// <param name="mediaProbeService">Probes source codec and bitrate metadata.</param>
    /// <param name="activeStreamRegistry">Tracks active hosted streams.</param>
    /// <param name="protectedContentSlateService">Generates configured protected-content fallback streams.</param>
    /// <param name="tunerStreamMultiplexer">Shares tuner input among concurrent stream consumers.</param>
    /// <param name="tunerCapacityLeases">Tracks physical tuner capacity across shared sources.</param>
    /// <param name="logger">Logger used for stream lifecycle diagnostics.</param>
    /// <param name="applicationLifetime">Signals application shutdown for live HLS cleanup.</param>
    /// <param name="timeProvider">Provides time for HLS inactivity expiration.</param>
    /// <param name="hlsInactivityTimeout">Overrides the internal HLS inactivity timeout.</param>
    /// <param name="subtitleSidecars">Owns transient WebVTT sidecars.</param>
    /// <param name="transientData">Provides the configured HLS artifact root.</param>
    public StreamController(
        IHttpClientFactory httpClientFactory,
        IEpgRepository epgRepository,
        IAppSettingsService settingsService,
        ChannelLineupStore channelLineupStore,
        IDeviceStateService deviceState,
        IHdHomeRunProxyProfileProvider proxyProfiles,
        IMpegTsTranscodeService mpegTsTranscodeService,
        IMediaProbeService mediaProbeService,
        IActiveStreamRegistry activeStreamRegistry,
        IProtectedContentSlateService protectedContentSlateService,
        ITunerStreamMultiplexer tunerStreamMultiplexer,
        ITunerCapacityLeaseRegistry tunerCapacityLeases,
        ILogger<StreamController> logger,
        IHostApplicationLifetime? applicationLifetime = null,
        TimeProvider? timeProvider = null,
        TimeSpan? hlsInactivityTimeout = null,
        SubtitleSidecarService? subtitleSidecars = null,
        ITransientDataStore? transientData = null)
    {
        _httpClientFactory = httpClientFactory;
        _epgRepository = epgRepository;
        _settingsService = settingsService;
        _channelLineupStore = channelLineupStore;
        _deviceState = deviceState;
        _proxyProfiles = proxyProfiles;
        _mpegTsTranscodeService = mpegTsTranscodeService;
        _mediaProbeService = mediaProbeService;
        _activeStreamRegistry = activeStreamRegistry;
        _protectedContentSlateService = protectedContentSlateService;
        _tunerStreamMultiplexer = tunerStreamMultiplexer;
        _tunerCapacityLeases = tunerCapacityLeases;
        _logger = logger;
        _applicationLifetime = applicationLifetime;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _hlsInactivityTimeout = NormalizeHlsInactivityTimeout(hlsInactivityTimeout);
        _transientData = transientData ?? TransientDataStore.CreateDefault();
        _subtitleSidecars = subtitleSidecars ?? new SubtitleSidecarService(_transientData);
    }

    /// <summary>
    /// Proxies the live TV stream for a given channel.
    /// Copies video while applying the configured per-track audio transcode behavior.
    /// </summary>
    /// <param name="channel">The channel number (e.g., "2.1", "5.1")</param>
    /// <param name="virtualDeviceId">Optional virtual DeviceID for a device-scoped stream.</param>
    /// <param name="transcode">Optional HDHomeRun hardware transcode profile. Defaults to none.</param>
    /// <returns>MPEG-TS video stream</returns>
    [HttpGet("{channel}")]
    [HttpGet("/auto/v{channel}")]
    [HttpGet("/hdhomerun/{virtualDeviceId}/auto/v{channel}")]
    public async Task<IActionResult> Stream(string channel, [FromRoute] string? virtualDeviceId = null, [FromQuery] string transcode = "none")
    {
        var profile = string.IsNullOrWhiteSpace(virtualDeviceId)
            ? await _proxyProfiles.GetPrimaryProfileAsync(HttpContext.RequestAborted)
            : await _proxyProfiles.FindProfileAsync(virtualDeviceId, HttpContext.RequestAborted);
        if (profile == null)
        {
            return string.IsNullOrWhiteSpace(virtualDeviceId)
                ? StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "The primary HDHomeRun device is unavailable." })
                : NotFound(new { error = "The requested virtual HDHomeRun device was not found." });
        }

        var disabledResult = await HandleDisabledMpegTsAsync(channel);
        if (disabledResult != null)
        {
            return disabledResult;
        }

        if (!TryBuildStreamUri(profile.PhysicalBaseUri.Host, channel, transcode, out var streamUri))
        {
            return BadRequest(new { error = "transcode must be one of: none, mobile, heavy, internet720, internet480, internet360" });
        }

        await using var tunerCapacityLease = await _tunerCapacityLeases.TryAcquireAsync(profile.PhysicalBaseUri, streamUri, profile.TunerCount, HttpContext.RequestAborted);
        if (tunerCapacityLease == null)
        {
            return HdHomeRunStreamError.CreateNoTunerAvailableResult(Response);
        }

        var audioMode = _settingsService.Settings.AudioTranscodeMode;
        var ac4Target = _settingsService.Settings.Ac4TranscodeTarget;
        var sessionId = Guid.NewGuid().ToString("N")[..8];
        _logger.LogInformation("Proxying MPEG-TS stream for channel {Channel} with tuner profile {Transcode}, audio mode {AudioMode}, and AC-4 target {Ac4Target}", channel, transcode, audioMode, ac4Target);

        try
        {
            await using var probeLease = await _tunerStreamMultiplexer.SubscribeAsync(streamUri, HttpContext.RequestAborted);
            using var probeLeaseCancellation = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
            var probeLeaseTask = probeLease.CopyToAsync(System.IO.Stream.Null, probeLeaseCancellation.Token);
            MediaProbeResult source;
            Stream tunerInput;
            try
            {
                source = await _mediaProbeService.ProbeAsync(streamUri, HttpContext.RequestAborted);
                tunerInput = await _tunerStreamMultiplexer.SubscribeAsync(streamUri, HttpContext.RequestAborted);
            }
            finally
            {
                probeLeaseCancellation.Cancel();
                await ObserveInputPumpAsync(probeLeaseTask);
                await probeLease.DisposeAsync();
            }

            var activeStream = ActiveStreamPlanFactory.CreateMpegTs(sessionId, channel, DateTime.UtcNow, source, _settingsService.Settings) with
            {
                ClientAddress = FormatClientAddress(HttpContext.Connection.RemoteIpAddress, HttpContext.Connection.RemotePort)
            };
            if (!_activeStreamRegistry.TryRegister(activeStream, _settingsService.Settings.MaximumConcurrentStreams, HttpContext.Abort))
            {
                await tunerInput.DisposeAsync();
                return StatusCode(StatusCodes.Status429TooManyRequests, new { error = StreamLimitError });
            }

            Response.ContentType = "video/mp2t";
            Response.Headers.CacheControl = "no-cache, no-store";

            await _mpegTsTranscodeService.TranscodeAsync(streamUri, tunerInput, source, _settingsService.Settings, Response.Body, HttpContext.RequestAborted);
            return new EmptyResult();
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            _logger.LogDebug("Stream closed for channel {Channel} (client disconnected)", channel);
            return new EmptyResult();
        }
        catch (MpegTsTranscodeException ex)
        {
            _logger.LogError(ex, "Failed to transcode MPEG-TS stream for channel {Channel}", channel);
            if (!Response.HasStarted)
            {
                var tunerError = await GetTunerErrorAsync(streamUri, HttpContext.RequestAborted);
                if (await IsContentProtectedAsync(channel, tunerError))
                {
                    if (_settingsService.Settings.ProtectedContentMode == ProtectedContentMode.StreamSlate)
                    {
                        await tunerCapacityLease.DisposeAsync();
                        var startedAtUtc = DateTime.UtcNow;
                        var activeStream = ActiveStreamPlanFactory.CreateProtectedSlate(sessionId, channel, HostedStreamFormat.MpegTs, startedAtUtc) with
                        {
                            ClientAddress = FormatClientAddress(HttpContext.Connection.RemoteIpAddress, HttpContext.Connection.RemotePort)
                        };
                        if (!_activeStreamRegistry.TryRegister(activeStream, _settingsService.Settings.MaximumConcurrentStreams, HttpContext.Abort))
                        {
                            return StatusCode(StatusCodes.Status429TooManyRequests, new { error = StreamLimitError });
                        }

                        Response.ContentType = "video/mp2t";
                        Response.Headers.CacheControl = "no-cache, no-store";
                        try
                        {
                            await _protectedContentSlateService.StreamAsync(HostedStreamFormat.MpegTs, channel, Response.Body, HttpContext.RequestAborted);
                        }
                        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
                        {
                            _logger.LogDebug("Protected-content slate closed for channel {Channel}", channel);
                        }

                        return new EmptyResult();
                    }

                    return StatusCode(StatusCodes.Status403Forbidden, new { code = 811, error = tunerError });
                }

                return StatusCode(StatusCodes.Status502BadGateway, new { error = ex.Message });
            }

            HttpContext.Abort();
            return new EmptyResult();
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "MPEG-TS response stream closed unexpectedly for channel {Channel}", channel);
            if (Response.HasStarted)
            {
                HttpContext.Abort();
                return new EmptyResult();
            }

            return StatusCode(StatusCodes.Status502BadGateway, new { error = "The stream connection closed unexpectedly." });
        }
        finally
        {
            _activeStreamRegistry.Unregister(sessionId);
        }
    }

    private (Uri PhysicalBaseUri, int TunerCount) GetWatchDevice()
    {
        var physicalBaseUri = HdHomeRunProxyProfileResolver.GetPhysicalBaseUri(_settingsService.Settings.DeviceAddress);
        var tunerCount = Math.Max(1, _deviceState.DeviceInfo?.TunerCount ?? 1);
        return (physicalBaseUri, tunerCount);
    }

    private async Task<IActionResult?> HandleDisabledMpegTsAsync(string channel)
    {
        if (!await IsChannelDisabledAsync(channel))
        {
            return null;
        }

        if (_settingsService.Settings.DisabledChannelMode == DisabledChannelMode.ReturnError)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = $"Channel {channel} is disabled." });
        }

        var sessionId = Guid.NewGuid().ToString("N")[..8];
        var activeStream = ActiveStreamPlanFactory.CreateDisabledSlate(sessionId, channel, HostedStreamFormat.MpegTs, DateTime.UtcNow) with
        {
            ClientAddress = FormatClientAddress(HttpContext.Connection.RemoteIpAddress, HttpContext.Connection.RemotePort)
        };
        if (!_activeStreamRegistry.TryRegister(activeStream, _settingsService.Settings.MaximumConcurrentStreams, HttpContext.Abort))
        {
            return StatusCode(StatusCodes.Status429TooManyRequests, new { error = StreamLimitError });
        }

        Response.ContentType = "video/mp2t";
        Response.Headers.CacheControl = "no-cache, no-store";
        try
        {
            await _protectedContentSlateService.StreamAsync(
                HostedStreamFormat.MpegTs,
                channel,
                Response.Body,
                HttpContext.RequestAborted,
                ChannelSlateReason.DisabledChannel);
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
        }
        finally
        {
            _activeStreamRegistry.Unregister(sessionId);
        }

        return new EmptyResult();
    }

    private async Task<bool> HandleDisabledPipeStreamAsync(string channel, HostedStreamFormat format, string? clientId = null)
    {
        if (!await IsChannelDisabledAsync(channel))
        {
            return false;
        }

        if (_settingsService.Settings.DisabledChannelMode == DisabledChannelMode.ReturnError)
        {
            Response.StatusCode = StatusCodes.Status403Forbidden;
            await Response.WriteAsJsonAsync(new { error = $"Channel {channel} is disabled." }, HttpContext.RequestAborted);
            return true;
        }

        using var streamCancellation = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
        var sessionId = Guid.NewGuid().ToString("N")[..8];
        var activeStream = ActiveStreamPlanFactory.CreateDisabledSlate(sessionId, channel, format, DateTime.UtcNow) with
        {
            ClientAddress = FormatClientAddress(HttpContext.Connection.RemoteIpAddress, HttpContext.Connection.RemotePort),
            ClientId = clientId
        };
        if (!_activeStreamRegistry.TryRegister(activeStream, _settingsService.Settings.MaximumConcurrentStreams, streamCancellation.Cancel))
        {
            Response.StatusCode = StatusCodes.Status429TooManyRequests;
            await Response.WriteAsJsonAsync(new { error = StreamLimitError }, HttpContext.RequestAborted);
            return true;
        }

        Response.ContentType = format == HostedStreamFormat.FragmentedMp4 ? "video/mp4" : "video/mp2t";
        Response.Headers.CacheControl = "no-cache, no-store";
        try
        {
            await _protectedContentSlateService.StreamAsync(format, channel, Response.Body, streamCancellation.Token, ChannelSlateReason.DisabledChannel);
        }
        catch (OperationCanceledException) when (streamCancellation.IsCancellationRequested)
        {
            if (!HttpContext.RequestAborted.IsCancellationRequested)
            {
                await Response.CompleteAsync();
            }
        }
        finally
        {
            _activeStreamRegistry.Unregister(sessionId);
        }

        return true;
    }

    private async Task<bool> IsChannelDisabledAsync(string channel)
    {
        var snapshot = await _channelLineupStore.ReadAsync(HttpContext.RequestAborted);
        return snapshot?.IsChannelEnabled(channel) == false;
    }

    /// <summary>
    /// Builds an HDHomeRun stream URI with an optional validated hardware transcode profile.
    /// </summary>
    /// <param name="deviceAddress">The HDHomeRun hostname or IP address.</param>
    /// <param name="channel">The virtual channel identifier.</param>
    /// <param name="transcode">The optional HDHomeRun hardware transcode profile.</param>
    /// <param name="streamUri">The resulting stream URI when validation succeeds.</param>
    /// <returns><see langword="true"/> when the profile and URI are valid; otherwise, <see langword="false"/>.</returns>
    public static bool TryBuildStreamUri(string deviceAddress, string channel, string? transcode, out Uri streamUri)
    {
        var profile = string.IsNullOrWhiteSpace(transcode) ? "none" : transcode.ToLowerInvariant();
        if (profile is not ("none" or "mobile" or "heavy" or "internet720" or "internet480" or "internet360"))
        {
            streamUri = null!;
            return false;
        }

        UriBuilder builder;
        try
        {
            builder = new UriBuilder(Uri.UriSchemeHttp, deviceAddress, DeviceEndpoints.StreamingPort, $"/auto/v{Uri.EscapeDataString(channel)}");
        }
        catch (UriFormatException)
        {
            streamUri = null!;
            return false;
        }
        catch (ArgumentException)
        {
            streamUri = null!;
            return false;
        }

        if (profile != "none")
        {
            builder.Query = $"transcode={Uri.EscapeDataString(profile)}";
        }

        streamUri = builder.Uri;
        return true;
    }

    /// <summary>
    /// Test endpoint to verify the HDHomeRun device transcoding capability
    /// </summary>
    [HttpGet("test/{channel}")]
    public async Task<IActionResult> TestTranscode(string channel, [FromQuery] string transcode = "heavy")
    {
        if (await IsChannelDisabledAsync(channel))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                success = false,
                channel,
                message = $"Channel {channel} is disabled."
            });
        }

        if (!TryBuildStreamUri(_settingsService.Settings.DeviceAddress, channel, transcode, out var streamUri))
        {
            return BadRequest(new { success = false, message = "transcode must be one of: none, mobile, heavy, internet720, internet480, internet360" });
        }

        try
        {
            var httpClient = _httpClientFactory.CreateClient("StreamProxy");
            httpClient.Timeout = TimeSpan.FromSeconds(15);

            using var request = new HttpRequestMessage(HttpMethod.Get, streamUri);
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, HttpContext.RequestAborted);
            if (!response.IsSuccessStatusCode)
            {
                var tunerError = response.Headers.TryGetValues("X-HDHomeRun-Error", out var values)
                    ? values.FirstOrDefault()
                    : null;
                return Ok(new
                {
                    success = false,
                    channel,
                    transcode,
                    deviceUrl = streamUri.AbsoluteUri,
                    statusCode = (int)response.StatusCode,
                    message = tunerError ?? $"The tuner returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase})."
                });
            }

            // Read a small chunk to see if stream starts
            await using var stream = await response.Content.ReadAsStreamAsync();
            var buffer = new byte[1024];
            var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), HttpContext.RequestAborted);

            return Ok(new
            {
                success = true,
                channel,
                transcode,
                deviceUrl = streamUri.AbsoluteUri,
                statusCode = (int)response.StatusCode,
                contentType = response.Content.Headers.ContentType?.ToString(),
                bytesReceived = bytesRead,
                message = bytesRead > 0 ? "Stream is responding" : "No data received"
            });
        }
        catch (Exception ex)
        {
            return Ok(new { success = false, channel, transcode, deviceUrl = streamUri.AbsoluteUri, message = ex.Message });
        }
    }

    /// <summary>
    /// Streams video using fragmented MP4 (fMP4) directly to memory/pipe.
    /// No disk I/O required - FFmpeg outputs to stdout which is piped to the browser.
    /// Uses Media Source Extensions (MSE) compatible output.
    /// </summary>
    /// <param name="channel">The channel number (e.g., "2.1", "5.1")</param>
    /// <param name="clientId">Optional Watch player identifier used for explicit stop notifications.</param>
    /// <param name="quality">Optional per-session Watch player quality override.</param>
    /// <param name="audioTrack">Optional absolute source audio stream index.</param>
    /// <param name="subtitleTrack">Optional absolute source subtitle stream index; omitted means Off.</param>
    /// <param name="subtitlePresentation">Previously validated subtitle presentation used only when a retry probe returns no tracks.</param>
    /// <param name="embeddedCaptions">Whether a previously validated retry selection represents captions embedded in video.</param>
    /// <param name="audioOutput">Browser audio output layout.</param>
    /// <returns>Fragmented MP4 video stream</returns>
    [HttpGet("fmp4/{channel}")]
    public async Task StreamFmp4(
        string channel,
        [FromQuery] string? clientId = null,
        [FromQuery] WebPlayerQuality quality = WebPlayerQuality.AppDefault,
        [FromQuery] int? audioTrack = null,
        [FromQuery] int? subtitleTrack = null,
        [FromQuery] SubtitlePresentation? subtitlePresentation = null,
        [FromQuery] bool embeddedCaptions = false,
        [FromQuery] WatchAudioOutput audioOutput = WatchAudioOutput.Stereo)
    {
        if (await HandleDisabledPipeStreamAsync(channel, HostedStreamFormat.FragmentedMp4, clientId))
        {
            return;
        }

        var device = GetWatchDevice();
        TryBuildStreamUri(device.PhysicalBaseUri.Host, channel, "none", out var sourceUri);
        await using var tunerCapacityLease = await _tunerCapacityLeases.TryAcquireAsync(device.PhysicalBaseUri, sourceUri, device.TunerCount, HttpContext.RequestAborted);
        if (tunerCapacityLease == null)
        {
            Response.Headers["X-HDHomeRun-Error"] = HdHomeRunStreamError.NoTunerAvailable;
            Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        var streamUrl = sourceUri.AbsoluteUri;

        _logger.LogInformation("Starting fMP4 stream for channel {Channel}", channel);

        Process? ffmpegProcess = null;
        Process? captionProcess = null;
        Task? inputPumpTask = null;
        Task? captionInputPumpTask = null;
        Task<string>? captionErrorTask = null;
        Stream? tunerInput = null;
        Stream? captionInput = null;
        Stream? tunerLease = null;
        Task? tunerLeaseTask = null;
        CancellationTokenSource? tunerLeaseCancellation = null;
        FMp4Session? fmp4Session = null;
        Exception? tunerInputError = null;
        var ffmpegErrors = new ConcurrentQueue<string>();
        var sessionId = Guid.NewGuid().ToString("N")[..8];
        var outputVideoBitRate = WebVideoTranscodePlanner.GetMaximumBitRate(_settingsService.Settings, quality);
        string? subtitlePath = null;
        using var streamCancellation = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
        var streamCancellationToken = streamCancellation.Token;

        try
        {
            tunerLease = await _tunerStreamMultiplexer.SubscribeAsync(sourceUri, streamCancellationToken);
            tunerLeaseCancellation = CancellationTokenSource.CreateLinkedTokenSource(streamCancellationToken);
            tunerLeaseTask = tunerLease.CopyToAsync(System.IO.Stream.Null, tunerLeaseCancellation.Token);
            var source = await ProbeBestEffortAsync(sourceUri, streamCancellationToken);
            var sourceVideoCodec = source.Tracks.FirstOrDefault(track => track.Type == MediaTrackType.Video)?.Codec;
            WatchTrackSelection selection;
            try
            {
                selection = WatchStreamPlanner.SelectTracks(source, audioTrack, subtitleTrack, subtitlePresentation, embeddedCaptions);
            }
            catch (ArgumentException ex)
            {
                Response.StatusCode = StatusCodes.Status400BadRequest;
                await Response.WriteAsJsonAsync(new { error = ex.Message }, HttpContext.RequestAborted);
                return;
            }
            var copyVideo = selection.SubtitlePresentation != SubtitlePresentation.BurnIn &&
                quality == WebPlayerQuality.AppDefault &&
                string.Equals(sourceVideoCodec, "h264", StringComparison.OrdinalIgnoreCase);
            tunerInput = await _tunerStreamMultiplexer.SubscribeAsync(sourceUri, streamCancellationToken);
            tunerLeaseCancellation.Cancel();
            await ObserveInputPumpAsync(tunerLeaseTask);
            await tunerLease.DisposeAsync();
            tunerLease = null;

            if (selection.SubtitlePresentation == SubtitlePresentation.WebVtt)
            {
                subtitlePath = _subtitleSidecars.Create(sessionId);
            }
            var ffmpegArgs = WatchStreamPlanner.CreateArguments(_settingsService.Settings, source, selection, quality, subtitlePath, audioOutput);

            var startInfo = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in ffmpegArgs)
            {
                startInfo.ArgumentList.Add(argument);
            }

            ffmpegProcess = Process.Start(startInfo);

            if (ffmpegProcess == null)
            {
                _logger.LogError("Failed to start FFmpeg process for fMP4");
                Response.StatusCode = 500;
                return;
            }

            if (selection.Subtitle?.IsEmbeddedClosedCaptions == true)
            {
                captionInput = await _tunerStreamMultiplexer.SubscribeAsync(sourceUri, streamCancellationToken);
                var captionStartInfo = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    RedirectStandardInput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                foreach (var argument in WatchStreamPlanner.CreateEmbeddedCaptionArguments(subtitlePath!))
                {
                    captionStartInfo.ArgumentList.Add(argument);
                }

                captionProcess = Process.Start(captionStartInfo) ??
                    throw new InvalidOperationException("Failed to start FFmpeg embedded-caption extractor.");
                captionErrorTask = captionProcess.StandardError.ReadToEndAsync();
                captionInputPumpTask = TunerInputPump.PumpAsync(captionInput, sourceUri, captionProcess, _logger, streamCancellationToken);
                captionInput = null;
            }

            inputPumpTask = TunerInputPump.PumpAsync(tunerInput, sourceUri, ffmpegProcess, _logger, streamCancellationToken, error => Volatile.Write(ref tunerInputError, error));
            tunerInput = null;

            var session = new FMp4Session
            {
                SessionId = sessionId,
                Channel = channel,
                Process = ffmpegProcess,
                StartTime = DateTime.UtcNow
            };
            fmp4Session = session;
            _fmp4Sessions[sessionId] = session;
            var activeStream = ActiveStreamPlanFactory.CreateFragmentedMp4(sessionId, channel, session.StartTime, source, outputVideoBitRate, copyVideo, selection, audioOutput) with
            {
                ClientAddress = FormatClientAddress(HttpContext.Connection.RemoteIpAddress, HttpContext.Connection.RemotePort),
                ClientId = clientId
            };
            if (!_activeStreamRegistry.TryRegister(activeStream, _settingsService.Settings.MaximumConcurrentStreams, streamCancellation.Cancel))
            {
                Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await Response.WriteAsJsonAsync(new { error = StreamLimitError });
                return;
            }

            // Capture metadata from the same FFmpeg process that serves the browser so the tuner is opened only once.
            _ = Task.Run(async () =>
            {
                var sourceTracks = source.Tracks.ToDictionary(track => track.Index);
                var readingInputMetadata = true;
                try
                {
                    while (!ffmpegProcess.HasExited)
                    {
                        var line = await ffmpegProcess.StandardError.ReadLineAsync();
                        if (line == null)
                        {
                            // End of stream reached
                            break;
                        }
                        if (!string.IsNullOrEmpty(line))
                        {
                            ffmpegErrors.Enqueue(line);
                            while (ffmpegErrors.Count > 50)
                            {
                                ffmpegErrors.TryDequeue(out _);
                            }

                            if (line.StartsWith("Stream mapping:", StringComparison.Ordinal) || line.StartsWith("Output #0", StringComparison.Ordinal))
                            {
                                readingInputMetadata = false;
                            }
                            else if (readingInputMetadata && FfmpegInputMetadataParser.TryParseTrack(line, out var track))
                            {
                                sourceTracks[track.Index] = track;
                                var source = new MediaProbeResult(sourceTracks.Values.OrderBy(value => value.Index).ToArray(), null);
                                lock (session.LifecycleGate)
                                {
                                    if (session.IsActive &&
                                        _fmp4Sessions.TryGetValue(sessionId, out var current) &&
                                        ReferenceEquals(current, session))
                                    {
                                        _activeStreamRegistry.Register(ActiveStreamPlanFactory.CreateFragmentedMp4(sessionId, channel, session.StartTime, source, outputVideoBitRate, copyVideo, selection, audioOutput));
                                    }
                                }
                            }

                            _logger.LogDebug("FFmpeg fMP4 [{SessionId}]: {Line}", sessionId, line);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
                {
                    _logger.LogDebug(ex, "Stopped reading FFmpeg diagnostics for fMP4 session {SessionId}", sessionId);
                }
            });

            // Set response headers for fMP4 stream
            Response.ContentType = "video/mp4";
            Response.Headers.CacheControl = "no-cache, no-store";
            Response.Headers["X-Session-Id"] = sessionId;

            // Stream FFmpeg output directly to response
            var buffer = new byte[64 * 1024];
            var firstReadTask = ffmpegProcess.StandardOutput.BaseStream
                .ReadAsync(buffer, streamCancellationToken)
                .AsTask();
            var startupDelayTask = Task.Delay(Fmp4StartupTimeout, streamCancellationToken);
            if (await Task.WhenAny(firstReadTask, startupDelayTask) != firstReadTask)
            {
                streamCancellationToken.ThrowIfCancellationRequested();
                await StopProcessAsync(ffmpegProcess);
                await ObserveInputPumpAsync(inputPumpTask);
                inputPumpTask = null;
                await WriteFmp4StartupErrorAsync(channel, streamUrl, sessionId, session.StartTime, tunerCapacityLease, streamCancellationToken, ffmpegErrors, Volatile.Read(ref tunerInputError));
                return;
            }

            var bytesRead = await firstReadTask;
            if (bytesRead == 0)
            {
                await ffmpegProcess.WaitForExitAsync(streamCancellationToken);
                await ObserveInputPumpAsync(inputPumpTask);
                inputPumpTask = null;
                await WriteFmp4StartupErrorAsync(channel, streamUrl, sessionId, session.StartTime, tunerCapacityLease, streamCancellationToken, ffmpegErrors, Volatile.Read(ref tunerInputError));
                return;
            }

            do
            {
                await Response.Body.WriteAsync(buffer.AsMemory(0, bytesRead), HttpContext.RequestAborted);
                await Response.Body.FlushAsync(HttpContext.RequestAborted);
            }
            while ((bytesRead = await ffmpegProcess.StandardOutput.BaseStream.ReadAsync(buffer, streamCancellationToken)) > 0);
        }
        catch (OperationCanceledException) when (streamCancellation.IsCancellationRequested)
        {
            if (HttpContext.RequestAborted.IsCancellationRequested)
            {
                _logger.LogDebug("fMP4 stream closed for channel {Channel} (client disconnected)", channel);
            }
            else
            {
                _logger.LogDebug("fMP4 stream stopped for channel {Channel}", channel);
                await Response.CompleteAsync();
            }
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 2)
        {
            _logger.LogError("FFmpeg not found for fMP4 streaming");
            SetInternalServerErrorStatus(Response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in fMP4 stream for channel {Channel}", channel);
            SetInternalServerErrorStatus(Response);
        }
        finally
        {
            var unregisterActiveStream = false;
            if (fmp4Session != null)
            {
                lock (fmp4Session.LifecycleGate)
                {
                    fmp4Session.IsActive = false;
                    if (((ICollection<KeyValuePair<string, FMp4Session>>)_fmp4Sessions).Remove(new KeyValuePair<string, FMp4Session>(sessionId, fmp4Session)))
                    {
                        unregisterActiveStream = true;
                    }
                }
            }

            if (ffmpegProcess != null && !ffmpegProcess.HasExited)
            {
                try
                {
                    ffmpegProcess.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
                {
                    _logger.LogDebug(ex, "Unable to stop FFmpeg process for fMP4 session {SessionId}", sessionId);
                }
            }
            if (captionProcess != null && !captionProcess.HasExited)
            {
                try
                {
                    captionProcess.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }
            }
            if (inputPumpTask is not null)
            {
                await ObserveInputPumpAsync(inputPumpTask);
            }
            if (captionInputPumpTask is not null)
            {
                await ObserveInputPumpAsync(captionInputPumpTask);
            }
            if (tunerInput is not null)
            {
                await tunerInput.DisposeAsync();
            }
            if (captionInput is not null)
            {
                await captionInput.DisposeAsync();
            }
            tunerLeaseCancellation?.Cancel();
            if (tunerLeaseTask is not null)
            {
                await ObserveInputPumpAsync(tunerLeaseTask);
            }
            if (tunerLease is not null)
            {
                await tunerLease.DisposeAsync();
            }
            tunerLeaseCancellation?.Dispose();
            ffmpegProcess?.Dispose();
            if (captionErrorTask is not null)
            {
                var captionError = await captionErrorTask;
                if (!string.IsNullOrWhiteSpace(captionError) && !streamCancellation.IsCancellationRequested)
                {
                    _logger.LogWarning("Embedded-caption extractor for fMP4 session {SessionId} reported: {CaptionError}", sessionId, captionError.Trim());
                }
            }
            captionProcess?.Dispose();
            if (subtitlePath is not null)
            {
                _subtitleSidecars.Remove(sessionId);
            }
            if (unregisterActiveStream)
            {
                _activeStreamRegistry.Unregister(sessionId);
            }
        }
    }

    /// <summary>
    /// Sets an internal-server-error response status when the response headers remain mutable.
    /// </summary>
    /// <param name="response">The response whose status should be updated.</param>
    internal static void SetInternalServerErrorStatus(HttpResponse response)
    {
        if (!response.HasStarted)
        {
            response.StatusCode = StatusCodes.Status500InternalServerError;
        }
    }

    /// <summary>Returns the incrementally written WebVTT sidecar for an active Watch session.</summary>
    /// <param name="sessionId">The allowlisted active fMP4 session identifier.</param>
    /// <param name="offset">The byte offset returned by the preceding sidecar request.</param>
    [HttpGet("fmp4/{sessionId}/subtitles.vtt")]
    public IActionResult GetFmp4Subtitles(string sessionId, [FromQuery] long offset = 0)
    {
        if (!_fmp4Sessions.ContainsKey(sessionId))
        {
            return NotFound(new { error = "Subtitle session not found." });
        }

        SubtitleSidecarChunk? chunk;
        try
        {
            chunk = _subtitleSidecars.ReadFrom(sessionId, offset);
        }
        catch (ArgumentException)
        {
            return BadRequest(new { error = "Invalid subtitle session identifier or offset." });
        }

        if (chunk is null)
        {
            return NotFound(new { error = "Subtitle data is not available yet." });
        }

        Response.Headers.CacheControl = "no-cache, no-store";
        Response.Headers["X-Lineup-Subtitle-Offset"] = chunk.NextOffset.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return File(chunk.Data, "text/vtt");
    }

    /// <summary>Returns WebVTT for the active fMP4 stream owned by one Watch client.</summary>
    /// <param name="clientId">The per-component Watch client identifier.</param>
    /// <param name="offset">The byte offset returned by the preceding sidecar request.</param>
    [HttpGet("fmp4/client/{clientId}/subtitles.vtt")]
    public IActionResult GetFmp4ClientSubtitles(string clientId, [FromQuery] long offset = 0)
    {
        if (clientId.Length is < 1 or > 64 || clientId.Any(character => !char.IsAsciiLetterOrDigit(character)))
        {
            return BadRequest(new { error = "Invalid Watch client identifier." });
        }

        var sessionId = _activeStreamRegistry.GetActiveStreams()
            .Where(stream => string.Equals(stream.ClientId, clientId, StringComparison.Ordinal))
            .OrderByDescending(stream => stream.StartedAtUtc)
            .Select(stream => stream.SessionId)
            .FirstOrDefault();
        return sessionId is null ? NotFound(new { error = "Watch session not found." }) : GetFmp4Subtitles(sessionId, offset);
    }

    /// <summary>
    /// Starts an HLS stream session and returns the playlist URL.
    /// HLS has native browser support - no JavaScript player library needed.
    /// Note: Uses disk for segment storage. For memory-only, use /fmp4/{channel}.
    /// </summary>
    /// <param name="channel">The channel number (e.g., "2.1", "5.1")</param>
    /// <returns>HLS playlist information</returns>
    [HttpPost("hls/start/{channel}")]
    public async Task<IActionResult> StartHlsStream(string channel)
    {
        var disabled = await IsChannelDisabledAsync(channel);
        if (disabled && _settingsService.Settings.DisabledChannelMode == DisabledChannelMode.ReturnError)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = $"Channel {channel} is disabled." });
        }

        ITunerCapacityLease? capacityLease = null;
        string? streamUrl = null;
        if (!disabled)
        {
            var device = GetWatchDevice();
            TryBuildStreamUri(device.PhysicalBaseUri.Host, channel, "none", out var sourceUri);
            capacityLease = await _tunerCapacityLeases.TryAcquireAsync(device.PhysicalBaseUri, sourceUri, device.TunerCount, HttpContext.RequestAborted);
            if (capacityLease == null)
            {
                return HdHomeRunStreamError.CreateNoTunerAvailableResult(Response);
            }

            streamUrl = sourceUri.AbsoluteUri;
        }

        // Generate unique session ID
        var sessionId = Guid.NewGuid().ToString("N")[..8];
        var outputVideoBitRate = _settingsService.Settings.MaximumVideoBitRateMbps * 1_000_000L;
        var hlsDir = _transientData.CreateHlsSessionDirectory(sessionId);
        var stopRequested = 0;

        if (disabled)
        {
            var activeStream = ActiveStreamPlanFactory.CreateDisabledSlate(sessionId, channel, HostedStreamFormat.Hls, DateTime.UtcNow) with
            {
                ClientAddress = FormatClientAddress(HttpContext.Connection.RemoteIpAddress, HttpContext.Connection.RemotePort)
            };
            if (!_activeStreamRegistry.TryRegister(
                activeStream,
                _settingsService.Settings.MaximumConcurrentStreams,
                () =>
                {
                    Interlocked.Exchange(ref stopRequested, 1);
                    StopHlsSession(sessionId);
                }))
            {
                return StatusCode(StatusCodes.Status429TooManyRequests, new { error = StreamLimitError });
            }
        }

        _logger.LogInformation("Starting HLS stream for channel {Channel}, session {SessionId}, dir {Dir}", channel, sessionId, hlsDir);

        try
        {
            // FFmpeg HLS output command
            var playlistPath = _transientData.GetFilePath(hlsDir, "stream.m3u8");

            // FFmpeg HLS command optimized for live streaming:
            // -fflags +genpts : Generate presentation timestamps
            // -flags +cgop : Use closed GOP for better seeking
            // -sc_threshold 0 : Disable scene change detection for consistent segments
            // -force_key_frames : Force keyframes for segment alignment
            // -hls_segment_type mpegts : Use MPEG-TS segments (better compatibility)
            // -hls_playlist_type event : Event playlist (segments accumulate)
            // -hls_init_time 0 : Start outputting segments immediately
            var ffmpegArgs = "-analyzeduration 1000000 -probesize 1000000 " +
                "-fflags +genpts " +
                "-i pipe:0 " +
                WebVideoTranscodePlanner.CreateArguments(
                    new AppSettings { MaximumVideoBitRateMbps = _settingsService.Settings.MaximumVideoBitRateMbps }) + " " +
                "-c:a aac -b:a 128k -ac 2 -ar 44100 " +
                "-map 0:v:0? -map 0:a:0? " +
                "-f hls " +
                "-hls_time 4 " +
                "-hls_list_size 10 " +
                "-hls_segment_type mpegts " +
                "-hls_flags delete_segments+append_list+omit_endlist " +
                "-hls_allow_cache 0 " +
                "-hls_start_number_source epoch " +
                $"\"{playlistPath}\"";

            _logger.LogInformation("FFmpeg command: ffmpeg {Args}", ffmpegArgs);
            HttpContext.RequestAborted.ThrowIfCancellationRequested();
            if (Volatile.Read(ref stopRequested) != 0)
            {
                _activeStreamRegistry.Unregister(sessionId);
                _transientData.DeleteDirectory(hlsDir);
                return new EmptyResult();
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = ffmpegArgs,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var process = disabled
                ? _protectedContentSlateService.StartHls(channel, playlistPath, ChannelSlateReason.DisabledChannel)
                : Process.Start(startInfo);

            if (process == null)
            {
                if (capacityLease != null)
                {
                    await capacityLease.DisposeAsync();
                }
                _transientData.DeleteDirectory(hlsDir);
                _activeStreamRegistry.Unregister(sessionId);
                return StatusCode(500, new { error = "Failed to start FFmpeg process" });
            }

            if (!disabled)
            {
                _ = TunerInputPump.PumpAsync(_tunerStreamMultiplexer, new Uri(streamUrl!), process, _logger, HttpContext.RequestAborted);
            }

            var session = new HlsSession
            {
                SessionId = sessionId,
                Channel = channel,
                Process = process,
                HlsDirectory = hlsDir,
                PlaylistPath = playlistPath,
                StartTime = DateTime.UtcNow,
                CapacityLease = capacityLease
            };

            RegisterHlsSession(session);
            if (HttpContext.RequestAborted.IsCancellationRequested || Volatile.Read(ref stopRequested) != 0)
            {
                StopHlsSession(sessionId);
                return new EmptyResult();
            }

            if (!disabled)
            {
                var activeStream = ActiveStreamPlanFactory.CreateHls(sessionId, channel, session.StartTime, new MediaProbeResult([], null), outputVideoBitRate) with
                {
                    ClientAddress = FormatClientAddress(HttpContext.Connection.RemoteIpAddress, HttpContext.Connection.RemotePort)
                };
                if (!_activeStreamRegistry.TryRegister(
                    activeStream,
                    _settingsService.Settings.MaximumConcurrentStreams,
                    () =>
                    {
                        Interlocked.Exchange(ref stopRequested, 1);
                        StopHlsSession(sessionId);
                    }))
                {
                    StopHlsSession(sessionId);
                    return StatusCode(StatusCodes.Status429TooManyRequests, new { error = StreamLimitError });
                }
            }

            // Capture FFmpeg stderr for error reporting
            var ffmpegErrors = new ConcurrentQueue<string>();

            // Log FFmpeg output in background
            StartHlsErrorMonitor(process, session, ffmpegErrors, parseSourceMetadata: !disabled, outputVideoBitRate);
            var usingSyntheticSlate = disabled;

            // Wait for playlist AND at least 2 segments to be created (up to 20 seconds)
            // This ensures the browser has enough content to start playing
            var segmentCount = 0;
            for (int i = 0; i < 200; i++)
            {
                if (Volatile.Read(ref stopRequested) != 0)
                {
                    StopHlsSession(sessionId);
                    return new EmptyResult();
                }

                // Check if process died
                if (process.HasExited)
                {
                    var exitCode = process.ExitCode;
                    var tunerError = usingSyntheticSlate ? null : await GetTunerErrorAsync(new Uri(streamUrl!), HttpContext.RequestAborted);
                    if (!usingSyntheticSlate && await IsContentProtectedAsync(channel, tunerError))
                    {
                        if (_settingsService.Settings.ProtectedContentMode == ProtectedContentMode.StreamSlate)
                        {
                            capacityLease = null;
                            DeleteHlsFiles(hlsDir);
                            ffmpegErrors.Clear();
                            var tunerSession = session;
                            var slateProcess = _protectedContentSlateService.StartHls(channel, playlistPath);
                            var slateSession = new HlsSession
                            {
                                SessionId = sessionId,
                                Channel = channel,
                                Process = slateProcess,
                                HlsDirectory = hlsDir,
                                PlaylistPath = playlistPath,
                                StartTime = DateTime.UtcNow,
                                CapacityLease = null
                            };
                            process = slateProcess;
                            session = slateSession;
                            DeactivateHlsSession(tunerSession);
                            RegisterHlsSession(slateSession);
                            tunerSession.CapacityLease?.Dispose();
                            tunerSession.Process.Dispose();
                            _activeStreamRegistry.Register(ActiveStreamPlanFactory.CreateProtectedSlate(sessionId, channel, HostedStreamFormat.Hls, session.StartTime));
                            StartHlsErrorMonitor(process, session, ffmpegErrors, parseSourceMetadata: false, outputVideoBitRate);
                            usingSyntheticSlate = true;
                            continue;
                        }

                        StopHlsSession(sessionId);
                        return StatusCode(StatusCodes.Status403Forbidden, new { code = 811, error = tunerError });
                    }

                    StopHlsSession(sessionId);
                    _logger.LogError("FFmpeg exited with code {ExitCode}. Errors: {Errors}", exitCode, string.Join("\n", ffmpegErrors.TakeLast(10)));
                    return StatusCode(500, new { error = $"FFmpeg exited with code {exitCode}", ffmpegOutput = ffmpegErrors.TakeLast(10).ToList() });
                }

                // Check for playlist and count segments
                if (_transientData.FileExists(playlistPath))
                {
                    // Count .ts segment files
                    var tsFiles = _transientData.EnumerateFiles(hlsDir, "*.ts");
                    segmentCount = tsFiles.Count;


                    // Wait for at least 2 segments before returning
                    if (segmentCount >= 2)
                    {
                        _logger.LogInformation("HLS playlist ready with {SegmentCount} segments for session {SessionId}", segmentCount, sessionId);
                        break;
                    }
                }
                await Task.Delay(100);
            }

            if (!_transientData.FileExists(playlistPath) || segmentCount < 2)
            {
                StopHlsSession(sessionId);
                return StatusCode(500, new { error = $"FFmpeg failed to create enough HLS segments (got {segmentCount}, need 2)", ffmpegOutput = ffmpegErrors.TakeLast(10).ToList() });
            }

            Volatile.Write(ref session.IsStarting, 0);
            if (process.HasExited)
            {
                TryCleanupCompletedHlsSession(process, session);
                return StatusCode(500, new { error = "FFmpeg exited after creating the initial HLS segments." });
            }

            return Ok(new { sessionId, channel, playlistUrl = $"/api/stream/hls/{sessionId}/stream.m3u8", segmentCount, message = "HLS stream started" });
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            if (!StopHlsSession(sessionId))
            {
                _activeStreamRegistry.Unregister(sessionId);
                capacityLease?.Dispose();
                TryDeleteTransientDirectory(hlsDir);
            }
            return new EmptyResult();
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 2)
        {
            if (!StopHlsSession(sessionId))
            {
                _activeStreamRegistry.Unregister(sessionId);
                capacityLease?.Dispose();
                TryDeleteTransientDirectory(hlsDir);
            }
            return StatusCode(500, new { error = "FFmpeg not found. Please install FFmpeg and add it to your PATH." });
        }
        catch (Exception ex)
        {
            if (!StopHlsSession(sessionId))
            {
                _activeStreamRegistry.Unregister(sessionId);
                capacityLease?.Dispose();
            }
            _logger.LogError(ex, "Error starting HLS stream for channel {Channel}", channel);
            try
            {
                TryDeleteTransientDirectory(hlsDir);
            }
            catch (IOException cleanupException)
            {
                _logger.LogWarning(cleanupException, "Failed to clean HLS directory {Directory}", hlsDir);
            }
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Serves HLS playlist (.m3u8) files
    /// </summary>
    [HttpGet("hls/{sessionId}/{filename}")]
    public IActionResult GetHlsFile(string sessionId, string filename)
    {
        if (!_hlsSessions.TryGetValue(sessionId, out var session))
        {
            return NotFound(new { error = "Session not found" });
        }

        if (!TryResolveHlsFilePath(session.HlsDirectory, filename, out var filePath))
        {
            return BadRequest(new { error = "Invalid HLS filename" });
        }

        if (!_transientData.FileExists(filePath))
        {
            return NotFound(new { error = "File not found" });
        }

        lock (session.LifecycleGate)
        {
            if (!session.IsActive ||
                !_hlsSessions.TryGetValue(sessionId, out var current) ||
                !ReferenceEquals(current, session))
            {
                return NotFound(new { error = "Session not found" });
            }

            Interlocked.Exchange(ref session.LastAccessTimestamp, _timeProvider.GetTimestamp());
        }

        // Determine content type
        string contentType;
        if (filename.EndsWith(".m3u8", StringComparison.Ordinal))
        {
            contentType = "application/vnd.apple.mpegurl";
        }
        else if (filename.EndsWith(".ts", StringComparison.Ordinal))
        {
            contentType = "video/mp2t";
        }
        else
        {
            contentType = "application/octet-stream";
        }

        // Add CORS and caching headers for HLS
        Response.Headers.AccessControlAllowOrigin = "*";
        Response.Headers.CacheControl = "no-cache";

        return PhysicalFile(filePath, contentType);
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
    /// Resolves a generated HLS playlist or segment basename beneath its session directory.
    /// </summary>
    /// <param name="hlsDirectory">The HLS session directory.</param>
    /// <param name="filename">The requested generated basename.</param>
    /// <param name="filePath">The canonical contained path when validation succeeds.</param>
    /// <returns><see langword="true"/> when the requested name is a valid generated HLS file.</returns>
    [NonAction]
    public static bool TryResolveHlsFilePath(string hlsDirectory, string filename, out string filePath)
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

        var isPlaylist = string.Equals(filename, "stream.m3u8", StringComparison.Ordinal);
        var isSegment = filename.StartsWith("stream", StringComparison.Ordinal) &&
            filename.EndsWith(".ts", StringComparison.Ordinal) &&
            filename.AsSpan(6, filename.Length - 9).Length > 0 &&
            filename.AsSpan(6, filename.Length - 9).IndexOfAnyExceptInRange('0', '9') < 0;
        if (!isPlaylist && !isSegment)
        {
            return false;
        }

        var directoryPath = Path.GetFullPath(hlsDirectory);
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

    /// <summary>
    /// Stops an HLS stream session
    /// </summary>
    [HttpPost("hls/stop/{sessionId}")]
    public IActionResult StopHls(string sessionId)
    {
        if (StopHlsSession(sessionId))
        {
            return Ok(new { message = "Session stopped" });
        }
        return NotFound(new { error = "Session not found" });
    }

    /// <summary>
    /// Lists active HLS sessions
    /// </summary>
    [HttpGet("hls/sessions")]
    public IActionResult GetHlsSessions()
    {
        var sessions = _hlsSessions.Values.Select(s => new
        {
            s.SessionId,
            s.Channel,
            startTime = s.StartTime,
            durationMinutes = (DateTime.UtcNow - s.StartTime).TotalMinutes,
            isRunning = s.Process is { HasExited: false }
        }).ToList();

        return Ok(sessions);
    }

    private bool StopHlsSession(string sessionId)
    {
        if (!_hlsSessions.TryRemove(sessionId, out var session))
        {
            return false;
        }

        CleanupHlsSession(session, stopProcess: true);
        return true;
    }

    private bool StopHlsSession(HlsSession session)
    {
        if (!((ICollection<KeyValuePair<string, HlsSession>>)_hlsSessions).Remove(new KeyValuePair<string, HlsSession>(session.SessionId, session)))
        {
            return false;
        }

        CleanupHlsSession(session, stopProcess: true);
        return true;
    }

    private void CleanupHlsSession(HlsSession session, bool stopProcess)
    {
        _logger.LogInformation("Stopping HLS session {SessionId} for channel {Channel}", session.SessionId, session.Channel);
        DeactivateHlsSession(session);
        try
        {
            if (stopProcess && session.Process is { HasExited: false })
            {
                // Send 'q' to FFmpeg stdin to quit gracefully (closes connections properly)
                try
                {
                    // On Windows, we can't easily send 'q' so we use Ctrl+C equivalent
                    // GenerateConsoleCtrlEvent doesn't work for processes without a console
                    // So we'll kill it but give it a moment to clean up
                    session.Process.Kill(entireProcessTree: false);

                    // Wait briefly for graceful shutdown
                    session.Process.WaitForExit(2000);

                    if (!session.Process.HasExited)
                    {
                        session.Process.Kill(entireProcessTree: true);
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
                {
                    _logger.LogDebug(ex, "Graceful FFmpeg shutdown failed for HLS session {SessionId}; forcing termination", session.SessionId);

                    // Force kill if graceful shutdown fails
                    try
                    {
                        session.Process.Kill(entireProcessTree: true);
                    }
                    catch (Exception forceKillException) when (forceKillException is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
                    {
                        _logger.LogWarning(forceKillException, "Unable to force-stop FFmpeg process for HLS session {SessionId}", session.SessionId);
                    }
                }
            }
            session.Process?.Dispose();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            _logger.LogDebug(ex, "Unable to dispose FFmpeg process for HLS session {SessionId}", session.SessionId);
        }
        finally
        {
            try
            {
                session.CapacityLease?.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unable to release tuner capacity for HLS session {SessionId}", session.SessionId);
            }
        }

        // Clean up HLS files
        try
        {
            _transientData.DeleteDirectory(session.HlsDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Unable to delete HLS directory {HlsDirectory} for session {SessionId}", session.HlsDirectory, session.SessionId);
        }
        finally
        {
            _activeStreamRegistry.Unregister(session.SessionId);
        }
    }

    private void TryCleanupCompletedHlsSession(Process process, HlsSession session)
    {
        bool hasExited;
        try
        {
            hasExited = process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return;
        }

        if (hasExited &&
            Volatile.Read(ref session.IsStarting) == 0 &&
            ((ICollection<KeyValuePair<string, HlsSession>>)_hlsSessions).Remove(new KeyValuePair<string, HlsSession>(session.SessionId, session)))
        {
            CleanupHlsSession(session, stopProcess: false);
        }
    }

    private void InitializeHlsSessionLifetime(HlsSession session)
    {
        Interlocked.Exchange(ref session.LastAccessTimestamp, _timeProvider.GetTimestamp());
        session.ExpirationCancellation = new CancellationTokenSource();
        session.ShutdownRegistration = _applicationLifetime?.ApplicationStopping.Register(
            () => StopHlsSession(session));
        session.ExpirationTask = ExpireInactiveHlsSessionAsync(session, session.ExpirationCancellation.Token);
    }

    private void RegisterHlsSession(HlsSession session)
    {
        _hlsSessions[session.SessionId] = session;
        InitializeHlsSessionLifetime(session);
    }

    private static TimeSpan NormalizeHlsInactivityTimeout(TimeSpan? timeout)
    {
        var value = timeout ?? DefaultHlsInactivityTimeout;
        return value < TimeSpan.FromMilliseconds(10)
            ? TimeSpan.FromMilliseconds(10)
            : value > TimeSpan.FromHours(1)
                ? TimeSpan.FromHours(1)
                : value;
    }

    private void DeactivateHlsSession(HlsSession session)
    {
        lock (session.LifecycleGate)
        {
            session.IsActive = false;
            session.ExpirationCancellation?.Cancel();
            session.ShutdownRegistration?.Dispose();
        }
    }

    private async Task ExpireInactiveHlsSessionAsync(HlsSession session, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                var lastAccess = Interlocked.Read(ref session.LastAccessTimestamp);
                var remaining = _hlsInactivityTimeout - _timeProvider.GetElapsedTime(lastAccess);
                if (remaining > TimeSpan.Zero)
                {
                    await Task.Delay(remaining, _timeProvider, cancellationToken);
                    continue;
                }

                lock (session.LifecycleGate)
                {
                    lastAccess = Interlocked.Read(ref session.LastAccessTimestamp);
                    if (!session.IsActive || _timeProvider.GetElapsedTime(lastAccess) < _hlsInactivityTimeout)
                    {
                        continue;
                    }

                    if (!((ICollection<KeyValuePair<string, HlsSession>>)_hlsSessions).Remove(new KeyValuePair<string, HlsSession>(session.SessionId, session)))
                    {
                        return;
                    }
                }

                _logger.LogInformation("Expiring inactive HLS session {SessionId}", session.SessionId);
                CleanupHlsSession(session, stopProcess: true);
                return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task<MediaProbeResult> ProbeBestEffortAsync(Uri inputUri, CancellationToken cancellationToken)
    {
        try
        {
            return await _mediaProbeService.ProbeAsync(inputUri, cancellationToken);
        }
        catch (MpegTsTranscodeException ex)
        {
            _logger.LogWarning("Unable to probe media metadata for {InputUri}; streaming will continue with unknown source details: {ProbeError}", inputUri, ex.Message);
            return new MediaProbeResult([], null);
        }
    }

    private async Task<string?> GetTunerErrorAsync(Uri inputUri, CancellationToken cancellationToken)
    {
        using var diagnosticCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        diagnosticCancellation.CancelAfter(TunerDiagnosticTimeout);
        try
        {
            var httpClient = _httpClientFactory.CreateClient("StreamProxy");
            using var response = await httpClient.GetAsync(inputUri, HttpCompletionOption.ResponseHeadersRead, diagnosticCancellation.Token);
            return response.Headers.TryGetValues("X-HDHomeRun-Error", out var values) ? values.FirstOrDefault() : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("Timed out retrieving tuner error details for {InputUri}", inputUri);
            return null;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "Unable to retrieve tuner error details for {InputUri}", inputUri);
            return null;
        }
    }

    private static int? TryGetTunerErrorCode(string? tunerError)
    {
        if (string.IsNullOrWhiteSpace(tunerError))
        {
            return null;
        }

        var separatorIndex = tunerError.IndexOf(' ');
        var codeText = separatorIndex >= 0 ? tunerError[..separatorIndex] : tunerError;
        return int.TryParse(codeText, out var code) ? code : null;
    }

    private async Task WriteFmp4StartupErrorAsync(
        string channel,
        string streamUrl,
        string sessionId,
        DateTime startedAtUtc,
        ITunerCapacityLease tunerCapacityLease,
        CancellationToken cancellationToken,
        IReadOnlyCollection<string>? ffmpegErrors = null,
        Exception? tunerInputError = null)
    {
        var tunerError = tunerInputError?.Message ?? await GetTunerErrorAsync(new Uri(streamUrl), cancellationToken);
        if (await IsContentProtectedAsync(channel, tunerError))
        {
            if (_settingsService.Settings.ProtectedContentMode == ProtectedContentMode.StreamSlate)
            {
                await tunerCapacityLease.DisposeAsync();
                _activeStreamRegistry.Register(ActiveStreamPlanFactory.CreateProtectedSlate(sessionId, channel, HostedStreamFormat.FragmentedMp4, startedAtUtc));
                await _protectedContentSlateService.StreamAsync(HostedStreamFormat.FragmentedMp4, channel, Response.Body, cancellationToken);
                return;
            }

            Response.StatusCode = StatusCodes.Status403Forbidden;
            await Response.WriteAsJsonAsync(new { code = 811, error = tunerError }, HttpContext.RequestAborted);
            return;
        }

        var recentFfmpegErrors = ffmpegErrors?.TakeLast(10).ToArray() ?? [];
        _logger.LogWarning(
            "FFmpeg could not start fMP4 channel {Channel}; tuner reported {TunerError}. Recent FFmpeg output: {FfmpegErrors}",
            channel,
            tunerError ?? "no diagnostic error",
            recentFfmpegErrors.Length == 0 ? "none" : string.Join(Environment.NewLine, recentFfmpegErrors));
        var error = tunerError ?? recentFfmpegErrors.LastOrDefault() ?? "The tuner did not provide video data.";
        Response.StatusCode = StatusCodes.Status502BadGateway;
        await Response.WriteAsJsonAsync(new { code = TryGetTunerErrorCode(tunerError), error }, HttpContext.RequestAborted);
    }

    private async Task<bool> IsContentProtectedAsync(string channel, string? tunerError)
    {
        if (!string.IsNullOrWhiteSpace(tunerError))
        {
            return ProtectedContentDetector.IsProtected(tunerError, cachedDrm: false);
        }

        var cachedChannel = (await _epgRepository.GetChannelsAsync())
            .FirstOrDefault(candidate => string.Equals(candidate.GuideNumber, channel, StringComparison.Ordinal));
        return ProtectedContentDetector.IsProtected(tunerError, cachedChannel?.DRM == true);
    }

    private static async Task StopProcessAsync(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }

        await process.WaitForExitAsync();
    }

    private static async Task ObserveInputPumpAsync(Task inputPumpTask)
    {
        try
        {
            await inputPumpTask;
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
    }

    private void StartHlsErrorMonitor(Process process, HlsSession session, ConcurrentQueue<string> errors, bool parseSourceMetadata, long outputVideoBitRate)
    {
        _ = Task.Run(async () =>
        {
            var sourceTracks = new Dictionary<int, MediaTrackMetadata>();
            var readingInputMetadata = parseSourceMetadata;
            try
            {
                while (await process.StandardError.ReadLineAsync() is { } line)
                {
                    if (string.IsNullOrEmpty(line))
                    {
                        continue;
                    }

                    if (line.StartsWith("Stream mapping:", StringComparison.Ordinal) || line.StartsWith("Output #0", StringComparison.Ordinal))
                    {
                        readingInputMetadata = false;
                    }
                    else if (readingInputMetadata && FfmpegInputMetadataParser.TryParseTrack(line, out var track))
                    {
                        sourceTracks[track.Index] = track;
                        var source = new MediaProbeResult(sourceTracks.Values.OrderBy(value => value.Index).ToArray(), null);
                        lock (session.LifecycleGate)
                        {
                            if (session.IsActive &&
                                _hlsSessions.TryGetValue(session.SessionId, out var current) &&
                                ReferenceEquals(current, session))
                            {
                                _activeStreamRegistry.Register(ActiveStreamPlanFactory.CreateHls(session.SessionId, session.Channel, session.StartTime, source, outputVideoBitRate));
                            }
                        }
                    }

                    errors.Enqueue(line);
                    while (errors.Count > 50)
                    {
                        errors.TryDequeue(out _);
                    }
                    _logger.LogDebug("FFmpeg HLS [{SessionId}]: {Line}", session.SessionId, line);
                }
            }
            catch (InvalidOperationException)
            {
                // The session shutdown path can dispose redirected streams while the monitor is awaiting a line.
            }
            finally
            {
                TryCleanupCompletedHlsSession(process, session);
            }
        });
    }

    private void DeleteHlsFiles(string directory)
    {
        _transientData.DeleteFiles(directory);
    }

    private void TryDeleteTransientDirectory(string directory)
    {
        try
        {
            _transientData.DeleteDirectory(directory);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private class HlsSession
    {
        /// <summary>
        /// Gets or sets session id.
        /// </summary>
        public required string SessionId { get; init; }
        /// <summary>
        /// Gets or sets channel.
        /// </summary>
        public required string Channel { get; init; }
        /// <summary>
        /// Gets or sets process.
        /// </summary>
        public required Process Process { get; init; }
        /// <summary>
        /// Gets or sets hls directory.
        /// </summary>
        public required string HlsDirectory { get; init; }
        /// <summary>
        /// Gets or sets playlist path.
        /// </summary>
        public required string PlaylistPath { get; init; }
        /// <summary>
        /// Gets or sets start time.
        /// </summary>
        public DateTime StartTime { get; init; }
        /// <summary>
        /// Gets or sets the physical tuner capacity lease owned by this session.
        /// </summary>
        public ITunerCapacityLease? CapacityLease { get; init; }
        /// <summary>
        /// Tracks whether the starting request still owns process-exit cleanup.
        /// </summary>
        public int IsStarting = 1;

        /// <summary>Synchronizes session lifecycle changes.</summary>
        public readonly object LifecycleGate = new();

        /// <summary>Tracks whether the session remains active.</summary>
        public bool IsActive = true;

        /// <summary>Stores the timestamp of the most recent session access.</summary>
        public long LastAccessTimestamp;

        /// <summary>Gets or sets cancellation for session expiration.</summary>
        public CancellationTokenSource? ExpirationCancellation;

        /// <summary>Gets or sets the session expiration task.</summary>
        public Task? ExpirationTask;

        /// <summary>Gets or sets the application-shutdown registration.</summary>
        public IDisposable? ShutdownRegistration;
    }

    private class FMp4Session
    {
        /// <summary>
        /// Gets or sets session id.
        /// </summary>
        public required string SessionId { get; init; }
        /// <summary>
        /// Gets or sets channel.
        /// </summary>
        public required string Channel { get; init; }
        /// <summary>
        /// Gets or sets process.
        /// </summary>
        public required Process Process { get; init; }
        /// <summary>
        /// Gets or sets start time.
        /// </summary>
        public DateTime StartTime { get; init; }

        /// <summary>Synchronizes session lifecycle changes.</summary>
        public readonly object LifecycleGate = new();

        /// <summary>Tracks whether the session remains active.</summary>
        public bool IsActive = true;
    }
}
