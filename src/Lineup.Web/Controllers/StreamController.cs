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
/// Supports direct proxy and shared CMAF output.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class StreamController : ControllerBase
{
    private static readonly TimeSpan TunerDiagnosticTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultCmafInactivityTimeout = TimeSpan.FromMinutes(2);
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
    private readonly ICmafCompatibilityTestService _compatibilityTestService;
    private readonly ITunerStreamMultiplexer _tunerStreamMultiplexer;
    private readonly ITunerCapacityLeaseRegistry _tunerCapacityLeases;
    private readonly ILogger<StreamController> _logger;
    private readonly IHostApplicationLifetime? _applicationLifetime;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _cmafInactivityTimeout;
    private readonly SubtitleSidecarService _subtitleSidecars;
    private readonly ITransientDataStore _transientData;

    // Track active CMAF streams by session ID
    private static readonly ConcurrentDictionary<string, CmafSession> _cmafSessions = new();

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
    /// <param name="applicationLifetime">Signals application shutdown for live CMAF cleanup.</param>
    /// <param name="timeProvider">Provides time for CMAF inactivity expiration.</param>
    /// <param name="cmafInactivityTimeout">Overrides the internal CMAF inactivity timeout.</param>
    /// <param name="subtitleSidecars">Owns transient WebVTT sidecars.</param>
    /// <param name="transientData">Provides the configured CMAF artifact root.</param>
    /// <param name="compatibilityTestService">Generates synthetic single-rendition CMAF compatibility tests.</param>
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
        TimeSpan? cmafInactivityTimeout = null,
        SubtitleSidecarService? subtitleSidecars = null,
        ITransientDataStore? transientData = null,
        ICmafCompatibilityTestService? compatibilityTestService = null)
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
        _cmafInactivityTimeout = NormalizeCmafInactivityTimeout(cmafInactivityTimeout);
        _transientData = transientData ?? TransientDataStore.CreateDefault();
        _subtitleSidecars = subtitleSidecars ?? new SubtitleSidecarService(_transientData);
        _compatibilityTestService = compatibilityTestService ?? new CmafCompatibilityTestService();
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
                var sourceSettings = CreateSourceDeinterlaceSettingsSnapshot(_settingsService.Settings);
                var rawSource = await _mediaProbeService.ProbeAsync(streamUri, HttpContext.RequestAborted);
                tunerInput = await SubscribeEffectiveSourceAsync(streamUri, rawSource, sourceSettings, HttpContext.RequestAborted);
                source = GetEffectiveSource(rawSource, sourceSettings);
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
    /// Starts the canonical shared CMAF DASH/HLS presentation.
    /// </summary>
    /// <param name="channel">The virtual channel.</param>
    /// <param name="request">The typed presentation request.</param>
    /// <returns>The session and both manifest URLs.</returns>
    [HttpPost("cmaf/start/{channel}")]
    public Task<IActionResult> StartCmafStream(string channel, [FromQuery] CmafStreamRequest request) => StartCmafStreamCore(channel, request);

    /// <summary>
    /// Starts a capability-aware shared CMAF presentation from a typed JSON request.
    /// </summary>
    /// <param name="channel">The virtual channel.</param>
    /// <param name="request">The presentation preferences and optional completed browser profile.</param>
    /// <returns>The session and both manifest URLs.</returns>
    [HttpPost("cmaf/start-v2/{channel}")]
    public Task<IActionResult> StartCmafStreamV2(string channel, [FromBody] CmafStreamRequest request)
    {
        if (request.CompatibilityProfile is not null && !CmafCompatibilityProfilePolicy.IsValid(request.CompatibilityProfile))
        {
            return Task.FromResult<IActionResult>(BadRequest(new { error = "The browser compatibility profile is incomplete, stale, oversized, or invalid." }));
        }
        try
        {
            request = CmafStreamPlanner.ApplyOverrides(request);
        }
        catch (ArgumentException ex)
        {
            return Task.FromResult<IActionResult>(BadRequest(new { error = ex.Message }));
        }
        return StartCmafStreamCore(channel, request);
    }

    /// <summary>
    /// Starts a compatibility HLS request backed by the shared CMAF presentation.
    /// </summary>
    /// <param name="channel">The virtual channel.</param>
    /// <returns>The session and both manifest URLs.</returns>
    [HttpPost("hls/start/{channel}")]
    public Task<IActionResult> StartHlsStream(string channel) => StartCmafStreamCore(channel, new CmafStreamRequest { PreferredAudio = CmafPreferredAudio.Fallback });

    /// <summary>
    /// Starts an exact single-rendition synthetic CMAF browser compatibility test.
    /// </summary>
    /// <param name="request">The codec, layout, quality, and protocol selection.</param>
    /// <returns>The transient test session and exact manifest URL.</returns>
    [HttpPost("cmaf/test/start")]
    public async Task<IActionResult> StartCmafCompatibilityTest([FromQuery] CmafCompatibilityTestRequest request)
    {
        try
        {
            CmafCompatibilityTestPlanner.ValidateRequest(request);
        }
        catch (NotSupportedException ex)
        {
            return UnprocessableEntity(new { error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }

        var sessionId = Guid.NewGuid().ToString("N")[..8];
        var directory = _transientData.CreateCmafSessionDirectory(sessionId);
        var manifestPath = _transientData.GetFilePath(directory, CmafStreamPlanner.DashManifestName);
        var subtitlePath = request.SubtitleMode == CmafTestSubtitleMode.WebVttSidecar
            ? _transientData.GetFilePath(directory, "captions-0.vtt")
            : null;
        Process? process = null;
        try
        {
            if (subtitlePath is not null)
            {
                await System.IO.File.WriteAllTextAsync(
                    subtitlePath,
                    CmafCompatibilityTestPlanner.CreateChannelSubtitleWebVtt(request.ChannelLayout),
                    HttpContext.RequestAborted);
            }
            process = _compatibilityTestService.Start(_settingsService.Settings, request, manifestPath);
            var session = new CmafSession
            {
                SessionId = sessionId,
                Channel = "Watch Test",
                Process = process,
                CmafDirectory = directory,
                PlaylistPath = manifestPath,
                StartTime = DateTime.UtcNow
            };
            RegisterCmafSession(session);
            var errors = new ConcurrentQueue<string>();
            var outputVideoBitRate = WebVideoTranscodePlanner.GetMaximumBitRate(_settingsService.Settings, request.Quality);
            var errorMonitorTask = StartCmafErrorMonitor(process, session, errors, parseSourceMetadata: false, outputVideoBitRate);

            for (var attempt = 0; attempt < 200; attempt++)
            {
                if (process.HasExited)
                {
                    await errorMonitorTask;
                    StopCmafSession(sessionId);
                    return StatusCode(StatusCodes.Status502BadGateway, new { error = errors.LastOrDefault() ?? "FFmpeg exited before the Watch Test presentation was ready." });
                }

                var hlsPath = _transientData.GetFilePath(directory, CmafStreamPlanner.HlsManifestName);
                if (_transientData.FileExists(manifestPath) && _transientData.FileExists(hlsPath) && _transientData.EnumerateFiles(directory, "*.m4s").Count >= 2)
                {
                    Volatile.Write(ref session.IsStarting, 0);
                    var baseUrl = $"/api/stream/cmaf/{sessionId}";
                    var manifestUrl = request.Protocol == CmafProtocol.Dash
                        ? $"{baseUrl}/{CmafStreamPlanner.DashManifestName}"
                        : $"{baseUrl}/{CmafStreamPlanner.HlsManifestName}";
                    var (width, height) = CmafCompatibilityTestPlanner.GetResolution(request.Quality);
                    using var manifestStream = _transientData.OpenRead(manifestPath) ?? throw new IOException("Watch Test manifest could not be opened.");
                    using var manifestReader = new StreamReader(manifestStream);
                    (string VideoCodec, string AudioCodec) codecs;
                    try
                    {
                        codecs = CmafCompatibilityTestPlanner.ParseManifestCodecs(await manifestReader.ReadToEndAsync(HttpContext.RequestAborted));
                    }
                    catch (System.Xml.XmlException)
                    {
                        await Task.Delay(100, HttpContext.RequestAborted);
                        continue;
                    }
                    return Ok(new CmafCompatibilityTestResponse(
                        sessionId,
                        manifestUrl,
                        codecs.VideoCodec,
                        codecs.AudioCodec,
                        CmafCompatibilityTestPlanner.GetChannelCount(request.ChannelLayout),
                        width,
                        height,
                        subtitlePath is null ? null : $"{baseUrl}/captions-0.vtt"));
                }

                await Task.Delay(100, HttpContext.RequestAborted);
            }

            StopCmafSession(sessionId);
            return StatusCode(StatusCodes.Status504GatewayTimeout, new { error = "FFmpeg did not create the Watch Test manifests and fragments in time." });
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            if (!StopCmafSession(sessionId))
            {
                TryDeleteTransientDirectory(directory);
                process?.Dispose();
            }
            return new EmptyResult();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            if (!StopCmafSession(sessionId))
            {
                TryDeleteTransientDirectory(directory);
                process?.Dispose();
            }
            _logger.LogError(ex, "Error starting Watch Test CMAF session {SessionId}", sessionId);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = ex.Message });
        }
    }

    private async Task<IActionResult> StartCmafStreamCore(string channel, CmafStreamRequest request)
    {
        CmafFallbackAudio fallbackAudio;
        try
        {
            fallbackAudio = CmafStreamPlanner.ResolveFallbackAudio(request);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }

        var disabled = await IsChannelDisabledAsync(channel);
        if (disabled && _settingsService.Settings.DisabledChannelMode == DisabledChannelMode.ReturnError)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = $"Channel {channel} is disabled." });
        }

        ITunerCapacityLease? capacityLease = null;
        Uri? sourceUri = null;
        var sourceSettings = CreateSourceDeinterlaceSettingsSnapshot(_settingsService.Settings);
        MediaProbeResult rawSource = new([], null);
        MediaProbeResult source = rawSource;
        if (!disabled)
        {
            var device = GetWatchDevice();
            TryBuildStreamUri(device.PhysicalBaseUri.Host, channel, "none", out sourceUri);
            capacityLease = await _tunerCapacityLeases.TryAcquireAsync(device.PhysicalBaseUri, sourceUri, device.TunerCount, HttpContext.RequestAborted);
            if (capacityLease == null)
            {
                return HdHomeRunStreamError.CreateNoTunerAvailableResult(Response);
            }
            rawSource = await ProbeBestEffortAsync(sourceUri, HttpContext.RequestAborted);
            source = GetEffectiveSource(rawSource, sourceSettings);
        }

        WebPlayerTrackSelection selection;
        IReadOnlyList<CmafAudioRendition> audioRenditions;
        try
        {
            selection = CmafStreamPlanner.SelectTracks(source, request);
            _ = CmafStreamPlanner.CreateAudioPlan(selection.Audio, request.PreferredAudio, fallbackAudio);
            audioRenditions = disabled
                ? []
                : CmafStreamPlanner.CreatePresentationAudioRenditions(
                    source,
                    selection.Audio,
                    request.PreferredAudio,
                    fallbackAudio,
                    request.CompatibilityProfile,
                    request.Overrides.Enabled ? request.Overrides.FallbackAudio : null,
                    request.Overrides.Enabled && request.Overrides.Audio.HasValue);
        }
        catch (ArgumentException ex)
        {
            capacityLease?.Dispose();
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            capacityLease?.Dispose();
            return UnprocessableEntity(new { error = ex.Message });
        }

        var sessionId = Guid.NewGuid().ToString("N")[..8];
        var directory = _transientData.CreateCmafSessionDirectory(sessionId);
        var manifestPath = _transientData.GetFilePath(directory, CmafStreamPlanner.DashManifestName);
        var selectableSubtitles = disabled ? [] : CmafStreamPlanner.GetSelectableSubtitles(source);
        var subtitlePaths = selectableSubtitles.ToDictionary(
            subtitle => subtitle.Index,
            subtitle => _transientData.GetFilePath(directory, $"captions-{subtitle.Index}.vtt"));
        var stopRequested = 0;
        var startedAt = DateTime.UtcNow;
        var outputVideoBitRate = WebVideoTranscodePlanner.GetMaximumBitRate(_settingsService.Settings, request.Quality);
        var activeStream = disabled
            ? ActiveStreamPlanFactory.CreateDisabledSlate(sessionId, channel, HostedStreamFormat.Cmaf, startedAt)
            : ActiveStreamPlanFactory.CreateCmaf(sessionId, channel, startedAt, source, selection, request, _settingsService.Settings);
        activeStream = activeStream with
        {
            ClientAddress = FormatClientAddress(HttpContext.Connection.RemoteIpAddress, HttpContext.Connection.RemotePort),
            ClientId = request.ClientId
        };
        if (!_activeStreamRegistry.TryRegister(activeStream, _settingsService.Settings.MaximumConcurrentStreams, () =>
            {
                Interlocked.Exchange(ref stopRequested, 1);
                StopCmafSession(sessionId);
            }))
        {
            capacityLease?.Dispose();
            TryDeleteTransientDirectory(directory);
            return StatusCode(StatusCodes.Status429TooManyRequests, new { error = StreamLimitError });
        }

        Process? process = null;
        Process? captionProcess = null;
        Task<string>? captionErrorTask = null;
        try
        {
            var ffmpegArguments = disabled ? null : CmafStreamPlanner.CreateArguments(_settingsService.Settings, source, selection, request, manifestPath, subtitlePaths);
            if (Volatile.Read(ref stopRequested) != 0)
            {
                _activeStreamRegistry.Unregister(sessionId);
                capacityLease?.Dispose();
                TryDeleteTransientDirectory(directory);
                return new EmptyResult();
            }

            if (disabled)
            {
                process = _protectedContentSlateService.StartCmaf(channel, manifestPath, ChannelSlateReason.DisabledChannel);
            }
            else
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    WorkingDirectory = directory,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                foreach (var argument in ffmpegArguments!)
                {
                    startInfo.ArgumentList.Add(argument);
                }
                process = Process.Start(startInfo);
            }

            if (process == null)
            {
                throw new InvalidOperationException("Failed to start FFmpeg process.");
            }
            if (!disabled)
            {
                _ = PumpEffectiveSourceAsync(sourceUri!, rawSource, sourceSettings, process, HttpContext.RequestAborted);
            }
            var embeddedCaptions = selectableSubtitles.FirstOrDefault(subtitle => subtitle.IsEmbeddedClosedCaptions);
            if (!disabled && embeddedCaptions is not null)
            {
                var captionStartInfo = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    RedirectStandardInput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                foreach (var argument in WebPlayerTrackPlanner.CreateEmbeddedCaptionArguments(subtitlePaths[embeddedCaptions.Index]))
                {
                    captionStartInfo.ArgumentList.Add(argument);
                }

                captionProcess = Process.Start(captionStartInfo) ??
                    throw new InvalidOperationException("Failed to start FFmpeg embedded-caption extractor.");
                captionErrorTask = captionProcess.StandardError.ReadToEndAsync();
                _ = TunerInputPump.PumpAsync(_tunerStreamMultiplexer, sourceUri!, captionProcess, _logger, HttpContext.RequestAborted);
            }

            var session = new CmafSession
            {
                SessionId = sessionId,
                Channel = channel,
                Process = process,
                CmafDirectory = directory,
                PlaylistPath = manifestPath,
                StartTime = startedAt,
                SourceVideoCodec = CmafStreamPlanner.CreateVideoRenditions(
                    source.Tracks.FirstOrDefault(track => track.Type == MediaTrackType.Video),
                    request,
                    selection.SubtitlePresentation == SubtitlePresentation.BurnIn,
                    _settingsService.Settings.WebPlayerDeinterlaceMode)
                    .Any(plan => plan.CopySource)
                        ? CmafStreamPlanner.CreateHevcCodecString(source.Tracks.FirstOrDefault(track => track.Type == MediaTrackType.Video))
                        : null,
                AudioRenditions = audioRenditions,
                CapacityLease = capacityLease,
                CaptionProcess = captionProcess,
                CaptionErrorTask = captionErrorTask
            };
            RegisterCmafSession(session);
            var errors = new ConcurrentQueue<string>();
            var errorMonitorTask = StartCmafErrorMonitor(process, session, errors, parseSourceMetadata: false, outputVideoBitRate);
            var usingSlate = disabled;
            var sourceAudioFallbackApplied = false;

            for (var attempt = 0; attempt < 200; attempt++)
            {
                if (Volatile.Read(ref stopRequested) != 0)
                {
                    StopCmafSession(sessionId);
                    return new EmptyResult();
                }
                if (process.HasExited)
                {
                    await errorMonitorTask;
                    if (!usingSlate && CmafStreamPlanner.ShouldRetryWithFallback(
                        request,
                        source.Tracks.Where(track => track.Type == MediaTrackType.Audio),
                        errors))
                    {
                        _logger.LogInformation(
                            "CMAF source audio codec {Codec} is not supported by the MP4 muxer for session {SessionId}; retrying with {FallbackAudio}",
                            selection.Audio?.Codec,
                            sessionId,
                            fallbackAudio);
                        var retryRequest = request with { PreferredAudio = CmafPreferredAudio.Fallback };
                        var retryStartInfo = new ProcessStartInfo
                        {
                            FileName = "ffmpeg",
                            WorkingDirectory = directory,
                            RedirectStandardInput = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        foreach (var argument in CmafStreamPlanner.CreateArguments(_settingsService.Settings, source, selection, retryRequest, manifestPath, subtitlePaths))
                        {
                            retryStartInfo.ArgumentList.Add(argument);
                        }

                        var retryProcess = Process.Start(retryStartInfo);
                        if (retryProcess is null)
                        {
                            throw new InvalidOperationException("Failed to restart FFmpeg with CMAF AAC audio.");
                        }

                        var sourceSession = session;
                        process = retryProcess;
                        request = retryRequest;
                        sourceAudioFallbackApplied = true;
                        session = new CmafSession
                        {
                            SessionId = sessionId,
                            Channel = channel,
                            Process = process,
                            CmafDirectory = directory,
                            PlaylistPath = manifestPath,
                            StartTime = startedAt,
                            SourceVideoCodec = sourceSession.SourceVideoCodec,
                            AudioRenditions = CmafStreamPlanner.CreatePresentationAudioRenditions(
                                source,
                                selection.Audio,
                                retryRequest.PreferredAudio,
                                fallbackAudio,
                                retryRequest.CompatibilityProfile,
                                retryRequest.Overrides.Enabled ? retryRequest.Overrides.FallbackAudio : null,
                                retryRequest.Overrides.Enabled && retryRequest.Overrides.Audio.HasValue),
                            CapacityLease = sourceSession.CapacityLease,
                            CaptionProcess = sourceSession.CaptionProcess,
                            CaptionErrorTask = sourceSession.CaptionErrorTask
                        };
                        sourceSession.CapacityLease = null;
                        sourceSession.CaptionProcess = null;
                        sourceSession.CaptionErrorTask = null;
                        DeactivateCmafSession(sourceSession);
                        RegisterCmafSession(session);
                        sourceSession.Process.Dispose();
                        _ = PumpEffectiveSourceAsync(sourceUri!, rawSource, sourceSettings, process, HttpContext.RequestAborted);
                        _activeStreamRegistry.Register(ActiveStreamPlanFactory.CreateCmaf(
                            sessionId,
                            channel,
                            startedAt,
                            source,
                            selection,
                            retryRequest,
                            _settingsService.Settings) with
                        {
                            ClientAddress = FormatClientAddress(HttpContext.Connection.RemoteIpAddress, HttpContext.Connection.RemotePort),
                            ClientId = retryRequest.ClientId
                        });
                        errors.Clear();
                        errorMonitorTask = StartCmafErrorMonitor(process, session, errors, parseSourceMetadata: false, outputVideoBitRate);
                        continue;
                    }

                    var tunerError = usingSlate ? null : await GetTunerErrorAsync(sourceUri!, HttpContext.RequestAborted);
                    if (!usingSlate && await IsContentProtectedAsync(channel, tunerError) && _settingsService.Settings.ProtectedContentMode == ProtectedContentMode.StreamSlate)
                    {
                        capacityLease = null;
                        DeleteCmafFiles(directory);
                        var tunerSession = session;
                        StopCaptionProcess(tunerSession);
                        var slateProcess = _protectedContentSlateService.StartCmaf(channel, manifestPath);
                        session = new CmafSession
                        {
                            SessionId = sessionId,
                            Channel = channel,
                            Process = slateProcess,
                            CmafDirectory = directory,
                            PlaylistPath = manifestPath,
                            StartTime = DateTime.UtcNow
                        };
                        process = slateProcess;
                        DeactivateCmafSession(tunerSession);
                        RegisterCmafSession(session);
                        tunerSession.CapacityLease?.Dispose();
                        tunerSession.Process.Dispose();
                        _activeStreamRegistry.Register(ActiveStreamPlanFactory.CreateProtectedSlate(sessionId, channel, HostedStreamFormat.Cmaf, session.StartTime));
                        errors.Clear();
                        errorMonitorTask = StartCmafErrorMonitor(process, session, errors, parseSourceMetadata: false, outputVideoBitRate);
                        usingSlate = true;
                        continue;
                    }

                    StopCmafSession(sessionId);
                    if (!usingSlate && await IsContentProtectedAsync(channel, tunerError))
                    {
                        return StatusCode(StatusCodes.Status403Forbidden, new { code = 811, error = tunerError });
                    }
                    return StatusCode(StatusCodes.Status502BadGateway, new { error = errors.LastOrDefault() ?? "FFmpeg exited before the CMAF presentation was ready." });
                }

                var hlsPath = _transientData.GetFilePath(directory, CmafStreamPlanner.HlsManifestName);
                if (_transientData.FileExists(manifestPath) && _transientData.FileExists(hlsPath) && _transientData.EnumerateFiles(directory, "*.m4s").Count >= 2)
                {
                    Volatile.Write(ref session.IsStarting, 0);
                    var baseUrl = $"/api/stream/cmaf/{sessionId}";
                    var hlsUrl = $"{baseUrl}/{CmafStreamPlanner.HlsManifestName}";
                    var packagedFallback = audioRenditions.FirstOrDefault(rendition => !rendition.Plan.CopySource)?.Plan;
                    return Ok(new CmafStreamResponse(sessionId, hlsUrl, $"/api/stream/hls/{sessionId}/{CmafStreamPlanner.HlsManifestName}", $"{baseUrl}/{CmafStreamPlanner.DashManifestName}")
                    {
                        SourceAudioFallbackApplied = sourceAudioFallbackApplied,
                        FallbackAudioTitle = packagedFallback?.Title,
                        FallbackAudioCodec = packagedFallback?.Codec,
                        HasSourceVideoRendition = CmafStreamPlanner.CreateVideoRenditions(
                            source.Tracks.FirstOrDefault(track => track.Type == MediaTrackType.Video),
                            request,
                            selection.SubtitlePresentation == SubtitlePresentation.BurnIn,
                            _settingsService.Settings.WebPlayerDeinterlaceMode).Any(plan => plan.CopySource),
                        SourceVideoCodec = session.SourceVideoCodec,
                        Subtitles = selectableSubtitles.Select(subtitle => new CmafSubtitleRendition(
                            subtitle.Index,
                            subtitle.Title ?? subtitle.Language ?? $"Subtitle {subtitle.Index}",
                            subtitle.Language,
                            $"{baseUrl}/captions-{subtitle.Index}.vtt",
                            subtitle.IsEmbeddedClosedCaptions)).ToArray()
                    });
                }
                await Task.Delay(100, HttpContext.RequestAborted);
            }

            StopCmafSession(sessionId);
            return StatusCode(StatusCodes.Status504GatewayTimeout, new { error = "FFmpeg did not create the shared CMAF manifests and fragments in time." });
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            if (!StopCmafSession(sessionId))
            {
                _activeStreamRegistry.Unregister(sessionId);
                capacityLease?.Dispose();
                TryDeleteTransientDirectory(directory);
            }
            return new EmptyResult();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            if (!StopCmafSession(sessionId))
            {
                _activeStreamRegistry.Unregister(sessionId);
                capacityLease?.Dispose();
                TryDeleteTransientDirectory(directory);
                process?.Dispose();
                StopCaptionProcess(captionProcess, captionErrorTask, sessionId);
            }
            _logger.LogError(ex, "Error starting CMAF stream for channel {Channel}", channel);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = ex.Message });
        }
    }
    /// <summary>
    /// Serves contained shared CMAF manifests, fragments, and subtitle artifacts.
    /// </summary>
    [HttpGet("hls/{sessionId}/{filename}")]
    [HttpGet("cmaf/{sessionId}/{filename}")]
    public IActionResult GetCmafFile(string sessionId, string filename, [FromQuery] long offset = 0)
    {
        if (!_cmafSessions.TryGetValue(sessionId, out var session))
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

        lock (session.LifecycleGate)
        {
            if (!session.IsActive ||
                !_cmafSessions.TryGetValue(sessionId, out var current) ||
                !ReferenceEquals(current, session))
            {
                return NotFound(new { error = "Session not found" });
            }

            Interlocked.Exchange(ref session.LastAccessTimestamp, _timeProvider.GetTimestamp());
        }

        if (!_transientData.FileExists(filePath))
        {
            if (!isSubtitle)
            {
                return NotFound(new { error = "File not found" });
            }

            Response.Headers.CacheControl = "no-cache, no-store";
            Response.Headers["X-Lineup-Subtitle-Offset"] = offset.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return File(Array.Empty<byte>(), "text/vtt; charset=utf-8");
        }

        if (isSubtitle)
        {
            using var stream = _transientData.OpenRead(filePath);
            if (stream is null)
            {
                Response.Headers.CacheControl = "no-cache, no-store";
                Response.Headers["X-Lineup-Subtitle-Offset"] = offset.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return File(Array.Empty<byte>(), "text/vtt; charset=utf-8");
            }
            var effectiveOffset = Math.Min(offset, stream.Length);
            stream.Position = effectiveOffset;
            var data = new byte[(int)Math.Min(stream.Length - effectiveOffset, 64 * 1024)];
            stream.ReadExactly(data);
            Response.Headers.CacheControl = "no-cache, no-store";
            Response.Headers["X-Lineup-Subtitle-Offset"] = (effectiveOffset + data.Length).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return File(data, "text/vtt; charset=utf-8");
        }

        if ((session.SourceVideoCodec is not null || session.AudioRenditions.Count > 0) &&
            (string.Equals(extension, ".mpd", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(filename, CmafStreamPlanner.HlsManifestName, StringComparison.Ordinal)))
        {
            using var stream = _transientData.OpenRead(filePath);
            if (stream is null)
            {
                return NotFound(new { error = "File not found" });
            }

            using var reader = new StreamReader(stream);
            var manifest = CmafStreamPlanner.RewriteManifestAudioLabels(reader.ReadToEnd(), session.AudioRenditions);
            if (session.SourceVideoCodec is { } sourceVideoCodec)
            {
                manifest = CmafStreamPlanner.RewriteManifestCodecs(manifest, sourceVideoCodec);
            }
            Response.Headers.CacheControl = "no-cache, no-store";
            return Content(
                manifest,
                string.Equals(extension, ".mpd", StringComparison.OrdinalIgnoreCase) ? "application/dash+xml" : "application/vnd.apple.mpegurl");
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
    /// Resolves an approved generated CMAF or legacy HLS basename beneath its session directory.
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

        var isLegacyPlaylist = string.Equals(filename, "stream.m3u8", StringComparison.Ordinal);
        var isLegacySegment = IsNumberedFile(filename, "stream", ".ts");
        var isManifest = filename is CmafStreamPlanner.DashManifestName or CmafStreamPlanner.HlsManifestName;
        var isMediaPlaylist = IsNumberedFile(filename, "media_", ".m3u8");
        var isInitialization = IsNumberedFile(filename, "init-", ".mp4");
        var isFragment = IsCmafFragment(filename);
        var isSubtitle = IsNumberedFile(filename, "captions-", ".vtt");
        if (!isLegacyPlaylist && !isLegacySegment && !isManifest && !isMediaPlaylist && !isInitialization && !isFragment && !isSubtitle)
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

    /// <summary>
    /// Stops a shared CMAF stream session.
    /// </summary>
    [HttpPost("hls/stop/{sessionId}")]
    [HttpPost("cmaf/stop/{sessionId}")]
    public IActionResult StopCmaf(string sessionId)
    {
        if (StopCmafSession(sessionId))
        {
            return Ok(new { message = "Session stopped" });
        }
        return NotFound(new { error = "Session not found" });
    }

    /// <summary>
    /// Lists active shared CMAF sessions through the legacy HLS compatibility route.
    /// </summary>
    [HttpGet("cmaf/sessions")]
    [HttpGet("hls/sessions")]
    public IActionResult GetCmafSessions()
    {
        var sessions = _cmafSessions.Values.Select(s => new
        {
            s.SessionId,
            s.Channel,
            startTime = s.StartTime,
            durationMinutes = (DateTime.UtcNow - s.StartTime).TotalMinutes,
            isRunning = s.Process is { HasExited: false }
        }).ToList();

        return Ok(sessions);
    }

    private bool StopCmafSession(string sessionId)
    {
        if (!_cmafSessions.TryRemove(sessionId, out var session))
        {
            return false;
        }

        CleanupCmafSession(session, stopProcess: true);
        return true;
    }

    private bool StopCmafSession(CmafSession session)
    {
        if (!((ICollection<KeyValuePair<string, CmafSession>>)_cmafSessions).Remove(new KeyValuePair<string, CmafSession>(session.SessionId, session)))
        {
            return false;
        }

        CleanupCmafSession(session, stopProcess: true);
        return true;
    }

    private void CleanupCmafSession(CmafSession session, bool stopProcess)
    {
        _logger.LogInformation("Stopping CMAF session {SessionId} for channel {Channel}", session.SessionId, session.Channel);
        DeactivateCmafSession(session);
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
                    _logger.LogDebug(ex, "Graceful FFmpeg shutdown failed for CMAF session {SessionId}; forcing termination", session.SessionId);

                    // Force kill if graceful shutdown fails
                    try
                    {
                        session.Process.Kill(entireProcessTree: true);
                    }
                    catch (Exception forceKillException) when (forceKillException is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
                    {
                        _logger.LogWarning(forceKillException, "Unable to force-stop FFmpeg process for CMAF session {SessionId}", session.SessionId);
                    }
                }
            }
            session.Process?.Dispose();
            StopCaptionProcess(session);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            _logger.LogDebug(ex, "Unable to dispose FFmpeg process for CMAF session {SessionId}", session.SessionId);
        }
        finally
        {
            try
            {
                session.CapacityLease?.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unable to release tuner capacity for CMAF session {SessionId}", session.SessionId);
            }
        }

        // Clean up CMAF files
        try
        {
            _transientData.DeleteDirectory(session.CmafDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Unable to delete CMAF directory {CmafDirectory} for session {SessionId}", session.CmafDirectory, session.SessionId);
        }
        finally
        {
            _activeStreamRegistry.Unregister(session.SessionId);
        }
    }

    private void StopCaptionProcess(CmafSession session) =>
        StopCaptionProcess(session.CaptionProcess, session.CaptionErrorTask, session.SessionId);

    private void StopCaptionProcess(Process? process, Task<string>? errorTask, string sessionId)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(2000);
            }
            if (errorTask?.IsCompletedSuccessfully == true && !string.IsNullOrWhiteSpace(errorTask.Result))
            {
                _logger.LogDebug("Embedded-caption extractor for CMAF session {SessionId} reported: {CaptionError}", sessionId, errorTask.Result.Trim());
            }
            process.Dispose();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            _logger.LogDebug(ex, "Unable to stop embedded-caption extractor for CMAF session {SessionId}", sessionId);
        }
    }

    private void TryCleanupCompletedCmafSession(Process process, CmafSession session)
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
            ((ICollection<KeyValuePair<string, CmafSession>>)_cmafSessions).Remove(new KeyValuePair<string, CmafSession>(session.SessionId, session)))
        {
            CleanupCmafSession(session, stopProcess: false);
        }
    }

    private void InitializeCmafSessionLifetime(CmafSession session)
    {
        Interlocked.Exchange(ref session.LastAccessTimestamp, _timeProvider.GetTimestamp());
        session.ExpirationCancellation = new CancellationTokenSource();
        session.ShutdownRegistration = _applicationLifetime?.ApplicationStopping.Register(
            () => StopCmafSession(session));
        session.ExpirationTask = ExpireInactiveCmafSessionAsync(session, session.ExpirationCancellation.Token);
    }

    private void RegisterCmafSession(CmafSession session)
    {
        _cmafSessions[session.SessionId] = session;
        InitializeCmafSessionLifetime(session);
    }

    private static TimeSpan NormalizeCmafInactivityTimeout(TimeSpan? timeout)
    {
        var value = timeout ?? DefaultCmafInactivityTimeout;
        return value < TimeSpan.FromMilliseconds(10)
            ? TimeSpan.FromMilliseconds(10)
            : value > TimeSpan.FromHours(1)
                ? TimeSpan.FromHours(1)
                : value;
    }

    private void DeactivateCmafSession(CmafSession session)
    {
        lock (session.LifecycleGate)
        {
            session.IsActive = false;
            session.ExpirationCancellation?.Cancel();
            session.ShutdownRegistration?.Dispose();
        }
    }

    private async Task ExpireInactiveCmafSessionAsync(CmafSession session, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                var lastAccess = Interlocked.Read(ref session.LastAccessTimestamp);
                var remaining = _cmafInactivityTimeout - _timeProvider.GetElapsedTime(lastAccess);
                if (remaining > TimeSpan.Zero)
                {
                    await Task.Delay(remaining, _timeProvider, cancellationToken);
                    continue;
                }

                lock (session.LifecycleGate)
                {
                    lastAccess = Interlocked.Read(ref session.LastAccessTimestamp);
                    if (!session.IsActive || _timeProvider.GetElapsedTime(lastAccess) < _cmafInactivityTimeout)
                    {
                        continue;
                    }

                    if (!((ICollection<KeyValuePair<string, CmafSession>>)_cmafSessions).Remove(new KeyValuePair<string, CmafSession>(session.SessionId, session)))
                    {
                        return;
                    }
                }

                _logger.LogInformation("Expiring inactive CMAF session {SessionId}", session.SessionId);
                CleanupCmafSession(session, stopProcess: true);
                return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private MediaProbeResult GetEffectiveSource(MediaProbeResult source, AppSettings settings) =>
        _tunerStreamMultiplexer is IEffectiveTunerStreamMultiplexer effective
            ? effective.GetEffectiveSource(source, settings)
            : source;

    private ValueTask<Stream> SubscribeEffectiveSourceAsync(Uri sourceUri, MediaProbeResult source, AppSettings settings, CancellationToken cancellationToken) =>
        _tunerStreamMultiplexer is IEffectiveTunerStreamMultiplexer effective
            ? effective.SubscribeEffectiveAsync(sourceUri, source, settings, cancellationToken)
            : _tunerStreamMultiplexer.SubscribeAsync(sourceUri, cancellationToken);

    private async Task PumpEffectiveSourceAsync(Uri sourceUri, MediaProbeResult source, AppSettings settings, Process process, CancellationToken cancellationToken)
    {
        await using var input = await SubscribeEffectiveSourceAsync(sourceUri, source, settings, cancellationToken);
        await TunerInputPump.PumpAsync(input, sourceUri, process, _logger, cancellationToken);
    }

    private static AppSettings CreateSourceDeinterlaceSettingsSnapshot(AppSettings settings) =>
        new()
        {
            SourceDeinterlaceMode = settings.SourceDeinterlaceMode,
            WebVideoPreset = settings.WebVideoPreset,
            WebVideoQuality = settings.WebVideoQuality,
            MaximumVideoBitRateMbps = settings.MaximumVideoBitRateMbps
        };

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

    private Task StartCmafErrorMonitor(Process process, CmafSession session, ConcurrentQueue<string> errors, bool parseSourceMetadata, long outputVideoBitRate)
    {
        return Task.Run(async () =>
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
                                _cmafSessions.TryGetValue(session.SessionId, out var current) &&
                                ReferenceEquals(current, session))
                            {
                                _activeStreamRegistry.Register(ActiveStreamPlanFactory.CreateCmaf(session.SessionId, session.Channel, session.StartTime, source, outputVideoBitRate));
                            }
                        }
                    }

                    errors.Enqueue(line);
                    while (errors.Count > 50)
                    {
                        errors.TryDequeue(out _);
                    }
                    _logger.LogDebug("FFmpeg CMAF [{SessionId}]: {Line}", session.SessionId, line);
                }
            }
            catch (InvalidOperationException)
            {
                // The session shutdown path can dispose redirected streams while the monitor is awaiting a line.
            }
            finally
            {
                TryCleanupCompletedCmafSession(process, session);
            }
        });
    }

    private void DeleteCmafFiles(string directory)
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

    private class CmafSession
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
        /// Gets or sets the CMAF artifact directory.
        /// </summary>
        public required string CmafDirectory { get; init; }
        /// <summary>
        /// Gets or sets playlist path.
        /// </summary>
        public required string PlaylistPath { get; init; }
        /// <summary>
        /// Gets or sets start time.
        /// </summary>
        public DateTime StartTime { get; init; }
        /// <summary>Gets the browser codec string inserted into manifests for copied HEVC video.</summary>
        public string? SourceVideoCodec { get; init; }
        /// <summary>Gets the audio renditions whose labels are inserted into CMAF manifests.</summary>
        public IReadOnlyList<CmafAudioRendition> AudioRenditions { get; init; } = [];
        /// <summary>
        /// Gets or sets the physical tuner capacity lease owned by this session.
        /// </summary>
        public ITunerCapacityLease? CapacityLease { get; set; }
        /// <summary>Gets the optional embedded-caption extractor process.</summary>
        public Process? CaptionProcess { get; set; }
        /// <summary>Gets the task draining embedded-caption extractor diagnostics.</summary>
        public Task<string>? CaptionErrorTask { get; set; }
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

}
