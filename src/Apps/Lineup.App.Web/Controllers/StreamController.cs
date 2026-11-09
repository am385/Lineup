using Lineup.Plugin.SiliconDust.HDHomeRun.Device;
using ProviderWebPlayer = Lineup.Plugin.WebPlayer;
using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Lineup.App.Web.Services;

namespace Lineup.App.Web.Controllers;

/// <summary>
/// Hosts the management UI's synthetic Web Player compatibility test endpoints.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class StreamController : ControllerBase
{
    private static readonly TimeSpan DefaultCmafStartupTimeout = TimeSpan.FromSeconds(24);
    private static readonly TimeSpan TunerDiagnosticTimeout = TimeSpan.FromSeconds(5);
    private const string StreamLimitError = "The maximum number of concurrent streams is already active.";
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILineupGuideReader _guideReader;
    private readonly ILineupSettingsService _settingsService;
    private readonly StreamingTopologyStore _topologyStore;
    private readonly IMediaProbeService _mediaProbeService;
    private readonly IActiveStreamRegistry _activeStreamRegistry;
    private readonly IProtectedContentSlateService _protectedContentSlateService;
    private readonly ICmafCompatibilityTestService _compatibilityTestService;
    private readonly ITunerStreamMultiplexer _tunerStreamMultiplexer;
    private readonly ILogger<StreamController> _logger;
    private readonly SubtitleSidecarService _subtitleSidecars;
    private readonly ITransientDataStore _transientData;
    private readonly IMediaProcessFactory _mediaProcessFactory;
    private readonly IStreamingRouteResolver? _streamRoutes;
    private readonly ICmafOutputCoordinator _cmafOutputs;
    private readonly IVirtualMpegTsOutputService? _virtualMpegTsOutputs;
    private readonly TimeSpan _cmafStartupTimeout;

    /// <summary>
    /// Initializes the streaming API controller.
    /// </summary>
    /// <param name="httpClientFactory">Factory for direct HDHomeRun diagnostic requests.</param>
    /// <param name="guideReader">Provides cached channel DRM metadata.</param>
    /// <param name="settingsService">Provides device and transcode settings.</param>
    /// <param name="topologyStore">Provides persisted physical channel availability choices.</param>
    /// <param name="mediaProbeService">Probes source codec and bitrate metadata.</param>
    /// <param name="activeStreamRegistry">Tracks active hosted streams.</param>
    /// <param name="protectedContentSlateService">Generates configured protected-content fallback streams.</param>
    /// <param name="tunerStreamMultiplexer">Shares tuner input among concurrent stream consumers.</param>
    /// <param name="logger">Logger used for stream lifecycle diagnostics.</param>
    /// <param name="timeProvider">Provides time for CMAF inactivity expiration.</param>
    /// <param name="cmafInactivityTimeout">Overrides the internal CMAF inactivity timeout.</param>
    /// <param name="subtitleSidecars">Owns transient WebVTT sidecars.</param>
    /// <param name="transientData">Provides the configured CMAF artifact root.</param>
    /// <param name="compatibilityTestService">Generates synthetic single-rendition CMAF compatibility tests.</param>
    /// <param name="mediaProcessFactory">Starts FFmpeg without shell interpretation.</param>
    /// <param name="streamRoutes">Resolves configured topology routes.</param>
    /// <param name="cmafOutputs">Owns shared CMAF outputs and viewer aliases.</param>
    /// <param name="virtualMpegTsOutputs">Opens management-hosted virtual MPEG-TS streams.</param>
    /// <param name="cmafStartupTimeout">Overrides the complete management-hosted CMAF startup deadline.</param>
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
        TimeSpan? cmafStartupTimeout = null)
    {
        _httpClientFactory = httpClientFactory;
        _guideReader = guideReader;
        _settingsService = settingsService;
        _topologyStore = topologyStore;
        _mediaProbeService = mediaProbeService;
        _activeStreamRegistry = activeStreamRegistry;
        _protectedContentSlateService = protectedContentSlateService;
        _tunerStreamMultiplexer = tunerStreamMultiplexer;
        _logger = logger;
        _transientData = transientData ?? TransientDataStore.CreateDefault();
        _subtitleSidecars = subtitleSidecars ?? new SubtitleSidecarService(_transientData);
        _compatibilityTestService = compatibilityTestService ?? new CmafCompatibilityTestService();
        _mediaProcessFactory = mediaProcessFactory ?? new MediaProcessFactory();
        _streamRoutes = streamRoutes;
        _cmafOutputs = cmafOutputs ?? new CmafOutputCoordinator(timeProvider, cmafInactivityTimeout);
        _virtualMpegTsOutputs = virtualMpegTsOutputs;
        _cmafStartupTimeout = cmafStartupTimeout ?? DefaultCmafStartupTimeout;
        if (_cmafStartupTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(cmafStartupTimeout), _cmafStartupTimeout, "The CMAF startup timeout must be greater than zero.");
        }
    }

    /// <summary>Streams one management-hosted virtual HDHomeRun channel as MPEG-TS.</summary>
    [HttpGet("/auto/v{channel}")]
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
            if (!_activeStreamRegistry.TryRegister(activeStream, _settingsService.Settings.MaximumConcurrentStreams, HttpContext.Abort))
            {
                return StatusCode(StatusCodes.Status429TooManyRequests, new { error = StreamLimitError });
            }
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

    /// <summary>Starts a management-hosted Web Player CMAF presentation.</summary>
    [HttpPost("cmaf/start/{channel}")]
    public async Task<IActionResult> StartCmafStream(string channel, [FromBody] CmafStreamRequest request)
    {
        if (request.CompatibilityProfile is not null && !CmafCompatibilityProfilePolicy.IsValid(request.CompatibilityProfile))
        {
            return BadRequest(new { error = "The browser compatibility profile is incomplete, stale, oversized, or invalid." });
        }
        try
        {
            request = CmafStreamPlanner.ApplyOverrides(request);
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { error = exception.Message });
        }
        using var startupCancellation = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
        startupCancellation.CancelAfter(_cmafStartupTimeout);
        try
        {
            return await StartCmafStreamCore(channel, request, startupCancellation.Token);
        }
        catch (OperationCanceledException) when (startupCancellation.IsCancellationRequested && !HttpContext.RequestAborted.IsCancellationRequested)
        {
            return CreateCmafStartupTimeoutResult();
        }
    }

    /// <summary>
    /// Starts an exact single-rendition synthetic CMAF browser compatibility test.
    /// </summary>
    /// <param name="request">The codec, layout, quality, and protocol selection.</param>
    /// <returns>The transient test session and exact manifest URL.</returns>
    [HttpPost("watch-test/start")]
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

        var sessionId = StreamSessionId.New().ToString();
        var directory = _transientData.CreateSessionDirectory(WebPlayerTransientWorkspaces.Cmaf, sessionId);
        Process? process = null;
        CmafSession? session = null;
        var ownershipTransferred = false;
        try
        {
            var manifestPath = _transientData.GetFilePath(directory, CmafStreamPlanner.DashManifestName);
            var subtitlePath = request.SubtitleMode == CmafTestSubtitleMode.WebVttSidecar
                ? _transientData.GetFilePath(directory, "captions-0.vtt")
                : null;
            if (subtitlePath is not null)
            {
                await System.IO.File.WriteAllTextAsync(
                    subtitlePath,
                    CmafCompatibilityTestPlanner.CreateChannelSubtitleWebVtt(request.ChannelLayout),
                    HttpContext.RequestAborted);
            }
            process = _compatibilityTestService.Start(_settingsService.Settings.Streaming.VirtualDefaults, request, manifestPath);
            session = new CmafSession
            {
                SessionId = sessionId,
                Channel = "Watch Test",
                Process = process,
                CmafDirectory = directory,
                PlaylistPath = manifestPath,
                StartTime = DateTime.UtcNow
            };
            var errors = new ConcurrentQueue<string>();
            var outputVideoBitRate = WebVideoTranscodePlanner.GetMaximumBitRate(
                _settingsService.Settings.Streaming.VirtualDefaults,
                request.Quality);
            var errorMonitorTask = StartCmafErrorMonitor(process, session, errors, parseSourceMetadata: false, outputVideoBitRate);

            for (var attempt = 0; attempt < 200; attempt++)
            {
                if (process.HasExited)
                {
                    await errorMonitorTask;
                    return StatusCode(StatusCodes.Status502BadGateway, new { error = errors.LastOrDefault() ?? "FFmpeg exited before the Watch Test presentation was ready." });
                }

                var hlsPath = _transientData.GetFilePath(directory, CmafStreamPlanner.HlsManifestName);
                if (_transientData.FileExists(manifestPath) && _transientData.FileExists(hlsPath) && _transientData.EnumerateFiles(directory, "*.m4s").Count >= 2)
                {
                    var baseUrl = $"/api/stream/watch-test/{sessionId}";
                    var manifestUrl = request.Protocol == CmafProtocol.Dash
                        ? $"{baseUrl}/{CmafStreamPlanner.DashManifestName}"
                        : $"{baseUrl}/{CmafStreamPlanner.HlsManifestName}";
                    var (width, height) = CmafCompatibilityTestPlanner.GetResolution(request.Quality);
                    (string VideoCodec, string AudioCodec) codecs;
                    try
                    {
                        using var manifestStream = _transientData.OpenRead(manifestPath) ?? throw new IOException("Watch Test manifest could not be opened.");
                        using var manifestReader = new StreamReader(manifestStream);
                        codecs = CmafCompatibilityTestPlanner.ParseManifestCodecs(await manifestReader.ReadToEndAsync(HttpContext.RequestAborted));
                    }
                    catch (System.Xml.XmlException)
                    {
                        await Task.Delay(100, HttpContext.RequestAborted);
                        continue;
                    }
                    session = await RegisterCmafSessionAsync(session, HttpContext.RequestAborted);
                    Volatile.Write(ref session.IsStarting, 0);
                    if (!session.IsActive || process.HasExited)
                    {
                        await errorMonitorTask;
                        return StatusCode(StatusCodes.Status502BadGateway, new { error = errors.LastOrDefault() ?? "FFmpeg exited before the Watch Test presentation was ready." });
                    }
                    var response = new CmafCompatibilityTestResponse(
                        sessionId,
                        manifestUrl,
                        codecs.VideoCodec,
                        codecs.AudioCodec,
                        CmafCompatibilityTestPlanner.GetChannelCount(request.ChannelLayout),
                        width,
                        height,
                        subtitlePath is null ? null : $"{baseUrl}/captions-0.vtt");
                    ownershipTransferred = true;
                    return Ok(response);
                }

                await Task.Delay(100, HttpContext.RequestAborted);
            }

            return StatusCode(StatusCodes.Status504GatewayTimeout, new { error = "FFmpeg did not create the Watch Test manifests and fragments in time." });
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            return new EmptyResult();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            _logger.LogError(ex, "Error starting Watch Test CMAF session {SessionId}", sessionId);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = ex.Message });
        }
        finally
        {
            if (!ownershipTransferred)
            {
                if (session is not null)
                {
                    StopCmafSession(session);
                }
                else
                {
                    CleanupIncompleteCmafOutput(process, null, null, directory);
                }
            }
        }
    }

    private async Task<bool> IsChannelDisabledAsync(string channel, CancellationToken cancellationToken)
    {
        return await _topologyStore.GetEnablementAsync(channel, cancellationToken) == PhysicalChannelEnablementState.Disabled;
    }

    private async Task<IActionResult> StartCmafStreamCore(string channel, CmafStreamRequest request, CancellationToken startupToken)
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

        var disabled = await IsChannelDisabledAsync(channel, startupToken);
        if (disabled && _settingsService.Settings.DisabledChannelMode == DisabledChannelMode.ReturnError)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = $"Channel {channel} is disabled." });
        }

        ResolvedStreamingRoute? topologyRoute = null;
        ResolvedStreamingCandidate? selectedCandidate = null;
        Uri? sourceUri = null;
        var sourceSettings = CreateSourceDeinterlaceSettingsSnapshot(_settingsService.Settings);
        var virtualProfile = _settingsService.Settings.Streaming.VirtualDefaults;
        var protectedContent = false;
        MediaProbeResult rawSource = new([], null);
        MediaProbeResult source = rawSource;
        if (!disabled)
        {
            if (_streamRoutes is null)
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "The streaming topology runtime is unavailable." });
            }
            try
            {
                topologyRoute = await _streamRoutes.ResolveVirtualAsync(ResolveWebPlayerVirtualDeviceId(), channel, startupToken)
                    ?? throw new InvalidOperationException("The streaming topology did not resolve a route.");
                virtualProfile = topologyRoute.VirtualProfile;
                var protection = ProtectedContentDetector.Detect(topologyRoute);
                if (protection.IsProtected)
                {
                    if (_settingsService.Settings.ProtectedContentMode == ProtectedContentMode.ReturnError)
                    {
                        return CreateProtectedContentResult(protection);
                    }
                    protectedContent = true;
                    rawSource = ChannelSlatePlanner.CreateMedia();
                    source = rawSource;
                }
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = ex.Message });
            }
            try
            {
                if (!protectedContent)
                {
                    await using var selected = await _tunerStreamMultiplexer.SubscribeFirstAvailableAsync(topologyRoute, startupToken);
                    selectedCandidate = selected.Candidate;
                    sourceUri = selectedCandidate.DiagnosticUri;
                    using var selectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(startupToken);
                    var selectionPump = selected.Stream.CopyToAsync(System.IO.Stream.Null, selectionCancellation.Token);
                    try
                    {
                        rawSource = await ProbeBestEffortAsync(selectedCandidate, startupToken);
                    }
                    finally
                    {
                        selectionCancellation.Cancel();
                        _ = ObserveInputPumpAsync(selectionPump);
                    }
                }
            }
            catch (StreamCapacityExceededException ex)
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = ex.Message });
            }
            catch (StreamAcquisitionException exception)
            {
                return CreateSourceAcquisitionResult(channel, exception);
            }
            if (!protectedContent)
            {
                if (!MediaProbeResultPolicy.HasAudio(rawSource) &&
                    (rawSource.AudioDiscovery == AudioDiscoveryStatus.ExpectedMissing || MediaProbeResultPolicy.ExpectsAudio(selectedCandidate!.SourceMetadata)))
                {
                    _logger.LogWarning("CMAF source audio discovery is incomplete for channel {Channel}, request {RequestId}.", channel, HttpContext.TraceIdentifier);
                    return StatusCode(StatusCodes.Status502BadGateway, new
                    {
                        code = "media_audio_missing",
                        error = "The source advertises audio, but bounded probing did not discover it.",
                        stage = "probing"
                    });
                }
                source = GetEffectiveSource(rawSource, selectedCandidate!.PhysicalProfile);
            }
        }

        WebPlayerTrackSelection selection;
        IReadOnlyList<CmafAudioRendition> audioRenditions;
        ProviderWebPlayer.WebPlayerPlaybackPlan? providerPlan = null;
        try
        {
            selection = CmafStreamPlanner.SelectTracks(source, request);
            request = CmafStreamPlanner.ApplyTopologyProfile(request, virtualProfile, source);
            fallbackAudio = CmafStreamPlanner.ResolveFallbackAudio(request);
            if (topologyRoute is not null && !protectedContent)
            {
                providerPlan = ProviderWebPlayer.WebPlayerPlaybackPlanner.Create(
                    _settingsService.Settings.Streaming,
                    topologyRoute.VirtualDevice.Id,
                    selectedCandidate!.SourceChannelId.PhysicalDeviceId,
                    source.Tracks.Select(ToWebPlayerTrack).ToArray(),
                    ToWebPlayerRequest(request));
            }
            _ = CmafStreamPlanner.CreateAudioPlan(selection.Audio, request.PreferredAudio, fallbackAudio);
            audioRenditions = disabled || protectedContent
                ? []
                : CmafStreamPlanner.CreatePresentationAudioRenditions(source, selection.Audio, request, virtualProfile);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return UnprocessableEntity(new { error = ex.Message });
        }

        var sessionId = StreamSessionId.New().ToString();
        var directory = _transientData.CreateSessionDirectory(WebPlayerTransientWorkspaces.Cmaf, sessionId);
        var manifestPath = _transientData.GetFilePath(directory, CmafStreamPlanner.DashManifestName);
        var selectableSubtitles = disabled || protectedContent ? [] : CmafStreamPlanner.GetSelectableSubtitles(source);
        var subtitlePaths = selectableSubtitles.ToDictionary(
            subtitle => subtitle.Index,
            subtitle => _transientData.GetFilePath(directory, $"captions-{subtitle.Index}.vtt"));
        var viewerStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedAt = DateTime.UtcNow;
        var outputVideoBitRate = WebVideoTranscodePlanner.GetMaximumBitRate(virtualProfile, request.Quality);
        var activeStream = disabled
            ? ActiveStreamPlanFactory.CreateDisabledSlate(sessionId, channel, HostedStreamFormat.Cmaf, startedAt)
            : protectedContent
                ? ActiveStreamPlanFactory.CreateProtectedSlate(sessionId, channel, HostedStreamFormat.Cmaf, startedAt)
                : ActiveStreamPlanFactory.CreateCmaf(sessionId, channel, startedAt, rawSource, source, selection, request, _settingsService.Settings, virtualProfile);
        activeStream = activeStream with
        {
            ClientAddress = FormatClientAddress(HttpContext.Connection.RemoteIpAddress, HttpContext.Connection.RemotePort),
            ClientId = request.ClientId,
            Flow = disabled
                ? CreateSyntheticFlow(GetWebPlayerVirtualDeviceId(), "Disabled channel slate")
                : protectedContent
                    ? CreateSyntheticFlow(GetWebPlayerVirtualDeviceId(), "Protected content slate")
                    : CreatePhysicalFlow(selectedCandidate!, topologyRoute!.VirtualDevice.Id)
        };
        if (!_activeStreamRegistry.TryRegister(activeStream, _settingsService.Settings.MaximumConcurrentStreams, () =>
            {
                viewerStopped.TrySetResult();
                StopCmafSession(sessionId);
            }))
        {
            TryDeleteTransientDirectory(directory);
            return StatusCode(StatusCodes.Status429TooManyRequests, new { error = StreamLimitError });
        }

        var transformKey = providerPlan is null
            ? new StreamTransformKey("web-player-cmaf", $"{(protectedContent ? "protected" : "disabled")}:{channel}")
            : new StreamTransformKey(
                providerPlan.TransformKey.Stage,
                $"{selectedCandidate!.SourceChannelId.PhysicalDeviceId}:{selectedCandidate.SourceChannelId.ProviderKey}|{CmafStreamPlanner.CreatePlanKey(virtualProfile, source, selection, request)}");
        var pendingOutput = new PendingCmafOutput(this, directory);
        ICmafOutputLease outputLease;
        try
        {
            outputLease = await _cmafOutputs.AcquireAsync(
                transformKey,
                new StreamSessionId(Guid.ParseExact(sessionId, "N")),
                _ => ValueTask.FromResult(new CmafOutputRegistration(
                    pendingOutput,
                    pendingOutput,
                    released => _activeStreamRegistry.Unregister(released.ToString()))),
                TimeSpan.FromSeconds(_settingsService.Settings.Streaming.KeepWarmPeriodSeconds),
                startupToken);
        }
        catch (OperationCanceledException) when (startupToken.IsCancellationRequested)
        {
            await pendingOutput.DisposeAsync();
            _activeStreamRegistry.Unregister(sessionId);
            TryDeleteTransientDirectory(directory);
            return HttpContext.RequestAborted.IsCancellationRequested
                ? new EmptyResult()
                : CreateCmafStartupTimeoutResult();
        }
        catch
        {
            await pendingOutput.DisposeAsync();
            _activeStreamRegistry.Unregister(sessionId);
            TryDeleteTransientDirectory(directory);
            throw;
        }
        activeStream = activeStream with
        {
            Flow = activeStream.Flow! with { ReusableOutputId = outputLease.OutputId }
        };
        _activeStreamRegistry.TryUpdate(sessionId, _ => activeStream);
        var sharedOutput = (PendingCmafOutput)outputLease.State;
        var ownsProducer = ReferenceEquals(sharedOutput, pendingOutput);
        if (!ownsProducer)
        {
            await pendingOutput.DisposeAsync();
            TryDeleteTransientDirectory(directory);
        }
        else
        {
            _ = pendingOutput.RunAsync(StartProducerAsync, _cmafStartupTimeout, outputLease.OutputId);
        }
        try
        {
            await Task.WhenAny(sharedOutput.Response, viewerStopped.Task).WaitAsync(startupToken);
            if (viewerStopped.Task.IsCompleted)
            {
                await outputLease.DisposeAsync();
                return new EmptyResult();
            }
            var result = await sharedOutput.Response;
            if (result is not OkObjectResult)
            {
                await sharedOutput.Stopped;
            }
            return result is OkObjectResult { Value: CmafStreamResponse response } ? Ok(RebaseCmafResponse(response, sessionId)) : result;
        }
        catch (OperationCanceledException) when (startupToken.IsCancellationRequested && ownsProducer)
        {
            await outputLease.DisposeAsync();
            return HttpContext.RequestAborted.IsCancellationRequested ? new EmptyResult() : CreateCmafStartupTimeoutResult();
        }
        catch
        {
            await outputLease.DisposeAsync();
            throw;
        }

        async Task<IActionResult> StartProducerAsync(CancellationToken producerToken)
        {
            Process? process = null;
            Task? inputPumpTask = null;
            Process? captionProcess = null;
            Task<string>? captionErrorTask = null;
            try
            {
                var ffmpegArguments = disabled
                    ? null
                    : CmafStreamPlanner.CreateArguments(virtualProfile, source, selection, request, manifestPath, subtitlePaths);
                producerToken.ThrowIfCancellationRequested();

                if (disabled || protectedContent)
                {
                    process = _protectedContentSlateService.StartCmaf(
                        channel,
                        manifestPath,
                        disabled ? ChannelSlateReason.DisabledChannel : ChannelSlateReason.ContentProtected);
                }
                else
                {
                    process = _mediaProcessFactory.Start(new MediaProcessRequest
                    {
                        FileName = "ffmpeg",
                        Arguments = ffmpegArguments!,
                        WorkingDirectory = directory,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    });
                }

                if (process == null)
                {
                    throw new InvalidOperationException("Failed to start FFmpeg process.");
                }
                pendingOutput.Attach(process, directory);
                if (!disabled)
                {
                    inputPumpTask = PumpEffectiveSourceAsync(selectedCandidate!, rawSource, sourceSettings, process, pendingOutput.LifetimeToken);
                }
                var embeddedCaptions = selectableSubtitles.FirstOrDefault(subtitle => subtitle.IsEmbeddedClosedCaptions);
                if (!disabled && embeddedCaptions is not null)
                {
                    captionProcess = _mediaProcessFactory.Start(new MediaProcessRequest
                    {
                        FileName = "ffmpeg",
                        Arguments = WebPlayerTrackPlanner.CreateEmbeddedCaptionArguments(subtitlePaths[embeddedCaptions.Index]),
                        RedirectStandardInput = true,
                        RedirectStandardError = true
                    });
                    pendingOutput.Attach(process, directory, captionProcess);
                    captionErrorTask = MediaProcessDiagnostics.ReadTailAsync(captionProcess.StandardError);
                    pendingOutput.Attach(process, directory, captionProcess, captionErrorTask);
                    _ = PumpSourceAsync(selectedCandidate!, captionProcess, pendingOutput.LifetimeToken);
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
                        virtualProfile.Deinterlace)
                        .Any(plan => plan.CopySource)
                            ? CmafStreamPlanner.CreateHevcCodecString(source.Tracks.FirstOrDefault(track => track.Type == MediaTrackType.Video))
                            : null,
                    AudioRenditions = audioRenditions,
                    CaptionProcess = captionProcess,
                    CaptionErrorTask = captionErrorTask,
                    TransformKey = transformKey,
                    OutputId = outputLease.OutputId
                };
                var errors = new ConcurrentQueue<string>();
                var errorMonitorTask = StartCmafErrorMonitor(process, session, errors, parseSourceMetadata: false, outputVideoBitRate);
                var usingSlate = disabled || protectedContent;
                var sourceAudioFallbackApplied = false;

                for (var attempt = 0; attempt < 200; attempt++)
                {
                    producerToken.ThrowIfCancellationRequested();
                    if (!usingSlate && inputPumpTask?.IsFaulted == true)
                    {
                        await EnsureInputPumpSucceededAsync(inputPumpTask);
                    }
                    if (process.HasExited)
                    {
                        await errorMonitorTask.WaitAsync(producerToken);
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
                            var retryProcess = _mediaProcessFactory.Start(new MediaProcessRequest
                            {
                                FileName = "ffmpeg",
                                Arguments = CmafStreamPlanner.CreateArguments(virtualProfile, source, selection, retryRequest, manifestPath, subtitlePaths),
                                WorkingDirectory = directory,
                                RedirectStandardInput = true,
                                RedirectStandardOutput = true,
                                RedirectStandardError = true
                            });

                            var sourceSession = session;
                            process = retryProcess;
                            pendingOutput.Attach(process, directory, captionProcess, captionErrorTask);
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
                                AudioRenditions = CmafStreamPlanner.CreatePresentationAudioRenditions(source, selection.Audio, retryRequest, virtualProfile),
                                CapacityLease = sourceSession.CapacityLease,
                                CaptionProcess = sourceSession.CaptionProcess,
                                CaptionErrorTask = sourceSession.CaptionErrorTask,
                                TransformKey = sourceSession.TransformKey,
                                OutputId = outputLease.OutputId
                            };
                            sourceSession.CapacityLease = null;
                            sourceSession.CaptionProcess = null;
                            sourceSession.CaptionErrorTask = null;
                            sourceSession.IsActive = false;
                            sourceSession.Process.Dispose();
                            inputPumpTask = PumpEffectiveSourceAsync(selectedCandidate!, rawSource, sourceSettings, process, pendingOutput.LifetimeToken);
                            UpdateCmafViewerPlans(outputLease.OutputId, ActiveStreamPlanFactory.CreateCmaf(
                                sessionId,
                                channel,
                                startedAt,
                                rawSource,
                                source,
                                selection,
                                retryRequest,
                                _settingsService.Settings,
                                virtualProfile) with
                            {
                                Flow = CreatePhysicalFlow(selectedCandidate!, topologyRoute!.VirtualDevice.Id) with
                                {
                                    ReusableOutputId = outputLease.OutputId
                                }
                            });
                            errors.Clear();
                            errorMonitorTask = StartCmafErrorMonitor(process, session, errors, parseSourceMetadata: false, outputVideoBitRate);
                            continue;
                        }

                        var tunerError = usingSlate ? null : await GetTunerErrorAsync(sourceUri!, producerToken);
                        var protection = usingSlate
                            ? new ProtectedContentDetection(IsProtected: false, IsInferred: false, Message: null)
                            : await DetectContentProtectionAsync(channel, tunerError, producerToken);
                        if (protection.IsProtected && _settingsService.Settings.ProtectedContentMode == ProtectedContentMode.StreamSlate)
                        {
                            DeleteCmafFiles(directory);
                            var tunerSession = session;
                            StopCaptionProcess(tunerSession);
                            var slateProcess = _protectedContentSlateService.StartCmaf(channel, manifestPath);
                            pendingOutput.Attach(slateProcess, directory);
                            session = new CmafSession
                            {
                                SessionId = sessionId,
                                Channel = channel,
                                Process = slateProcess,
                                CmafDirectory = directory,
                                PlaylistPath = manifestPath,
                                StartTime = DateTime.UtcNow,
                                TransformKey = transformKey,
                                OutputId = outputLease.OutputId
                            };
                            process = slateProcess;
                            tunerSession.IsActive = false;
                            tunerSession.CapacityLease?.Dispose();
                            tunerSession.Process.Dispose();
                            UpdateCmafViewerPlans(outputLease.OutputId, ActiveStreamPlanFactory.CreateProtectedSlate(sessionId, channel, HostedStreamFormat.Cmaf, session.StartTime) with
                            {
                                Flow = CreateSyntheticFlow(GetWebPlayerVirtualDeviceId(), "Protected content slate") with
                                {
                                    ReusableOutputId = outputLease.OutputId
                                }
                            });
                            errors.Clear();
                            errorMonitorTask = StartCmafErrorMonitor(process, session, errors, parseSourceMetadata: false, outputVideoBitRate);
                            usingSlate = true;
                            continue;
                        }

                        if (protection.IsProtected)
                        {
                            return CreateProtectedContentResult(protection);
                        }
                        var failure = new MediaProcessException(process.ExitCode, string.Join("\n", errors));
                        MediaProcessDiagnostics.LogFailure(_logger, failure, "readiness", sessionId, streamSessionId: outputLease.OutputId.ToString());
                        return StatusCode(StatusCodes.Status502BadGateway, new
                        {
                            code = "media_process_failed",
                            error = "The media processor exited before the presentation was ready.",
                            stage = "readiness"
                        });
                    }

                    if (!usingSlate && inputPumpTask?.IsCompleted == true)
                    {
                        await EnsureInputPumpSucceededAsync(inputPumpTask);
                        if (!process.HasExited)
                        {
                            throw new MediaInputException("The upstream media stream ended before the presentation was ready.");
                        }
                        continue;
                    }

                    var hlsPath = _transientData.GetFilePath(directory, CmafStreamPlanner.HlsManifestName);
                    int[] representationFragmentCounts = usingSlate
                        ? []
                        : GetRepresentationFragmentCounts(directory, session.AudioRenditions.Count + 1);
                    var fragmentsReady = usingSlate
                        ? _transientData.EnumerateFiles(directory, "*.m4s").Count >= 2
                        : representationFragmentCounts.All(count => count >= 2);
                    fragmentsReady &= Enumerable.Range(0, usingSlate ? 1 : session.AudioRenditions.Count + 1)
                        .All(index => _transientData.FileExists(_transientData.GetFilePath(directory, $"init-{index}.mp4")));
                    if (_transientData.FileExists(manifestPath) && _transientData.FileExists(hlsPath) && fragmentsReady)
                    {
                        Volatile.Write(ref session.IsStarting, 0);
                        var baseUrl = $"/api/stream/cmaf/{sessionId}";
                        var hlsUrl = $"{baseUrl}/{CmafStreamPlanner.HlsManifestName}";
                        var packagedFallback = session.AudioRenditions.FirstOrDefault(rendition => !rendition.Plan.CopySource)?.Plan;
                        var response = new CmafStreamResponse(sessionId, hlsUrl, $"{baseUrl}/{CmafStreamPlanner.DashManifestName}")
                        {
                            SourceAudioFallbackApplied = sourceAudioFallbackApplied,
                            FallbackAudioTitle = packagedFallback?.Title,
                            FallbackAudioCodec = packagedFallback?.Codec,
                            HasSourceVideoRendition = CmafStreamPlanner.CreateVideoRenditions(
                                source.Tracks.FirstOrDefault(track => track.Type == MediaTrackType.Video),
                                request,
                                selection.SubtitlePresentation == SubtitlePresentation.BurnIn,
                                virtualProfile.Deinterlace).Any(plan => plan.CopySource),
                            SourceVideoCodec = session.SourceVideoCodec,
                            Subtitles = selectableSubtitles.Select(subtitle => new CmafSubtitleRendition(
                                subtitle.Index,
                                subtitle.Title ?? subtitle.Language ?? $"Subtitle {subtitle.Index}",
                                subtitle.Language,
                                $"{baseUrl}/captions-{subtitle.Index}.vtt",
                                subtitle.IsEmbeddedClosedCaptions)).ToArray()
                        };
                        pendingOutput.Complete(session);
                        return Ok(response);
                    }
                    await Task.Delay(100, producerToken);
                }

                int[] timedOutFragmentCounts = usingSlate
                    ? []
                    : GetRepresentationFragmentCounts(directory, session.AudioRenditions.Count + 1);
                _logger.LogWarning(
                    "CMAF readiness timed out for channel {Channel}, session {SessionId}; expected {RepresentationCount} representations with fragment counts [{FragmentCounts}]. FFmpeg diagnostics: {FfmpegDiagnostics}",
                    channel,
                    sessionId,
                    session.AudioRenditions.Count + 1,
                    string.Join(", ", timedOutFragmentCounts),
                    string.Join(" | ", errors.TakeLast(10)));
                return StatusCode(StatusCodes.Status504GatewayTimeout, new { error = "FFmpeg did not create the shared CMAF manifests and fragments in time." });
            }
            catch (OperationCanceledException) when (producerToken.IsCancellationRequested)
            {
                return pendingOutput.LifetimeToken.IsCancellationRequested ? new EmptyResult() : CreateCmafStartupTimeoutResult();
            }
            catch (MediaInputException exception)
            {
                MediaProcessDiagnostics.LogFailure(_logger, exception, "source", sessionId, streamSessionId: outputLease.OutputId.ToString());
                return StatusCode(StatusCodes.Status502BadGateway, new { code = "media_source_failed", error = exception.Message, stage = "source" });
            }
            catch (MediaProcessException exception)
            {
                MediaProcessDiagnostics.LogFailure(_logger, exception, "process", sessionId, streamSessionId: outputLease.OutputId.ToString());
                return StatusCode(StatusCodes.Status502BadGateway, new
                {
                    code = "media_process_failed",
                    error = "The media processor could not start the presentation.",
                    stage = "process"
                });
            }
            catch (Exception ex) when (ex is MediaProcessException or InvalidOperationException or IOException or StreamCapacityExceededException)
            {
                _logger.LogError(ex, "Error starting CMAF stream for channel {Channel}", channel);
                if (ex is StreamAcquisitionException acquisitionException)
                {
                    return CreateSourceAcquisitionResult(channel, acquisitionException);
                }
                var statusCode = ex is StreamCapacityExceededException ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status500InternalServerError;
                return StatusCode(statusCode, new { error = ex.Message });
            }
        }
    }

    private void UpdateCmafViewerPlans(StreamSessionId outputId, ActiveStreamSnapshot plan)
    {
        foreach (var viewer in _cmafOutputs.GetViewers().Where(viewer => viewer.OutputId == outputId))
        {
            _activeStreamRegistry.TryUpdate(viewer.ViewerSessionId.ToString(), current => plan with
            {
                SessionId = current.SessionId,
                StartedAtUtc = current.StartedAtUtc,
                ClientAddress = current.ClientAddress,
                ClientId = current.ClientId
            });
        }
    }

    private IActionResult CreateCmafStartupTimeoutResult() =>
        StatusCode(
            StatusCodes.Status504GatewayTimeout,
            new
            {
                error = $"CMAF startup did not complete within {_cmafStartupTimeout.TotalSeconds:0} seconds.",
                code = "cmaf_startup_timeout",
                stage = "startup"
            });

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
    [HttpGet("watch-test/{sessionId}/{filename}")]
    [HttpGet("cmaf/{sessionId}/{filename}")]
    public IActionResult GetCmafFile(string sessionId, string filename, [FromQuery] long offset = 0)
    {
        if (!TryGetCmafSession(sessionId, out var session))
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
            return File(Array.Empty<byte>(), "text/vtt; charset=utf-8");
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

    private static ActiveStreamFlowContext CreatePhysicalFlow(ResolvedStreamingCandidate candidate, VirtualDeviceId virtualDeviceId, string? producerNodeId = null) =>
        new()
        {
            PhysicalDeviceId = candidate.SourceChannelId.PhysicalDeviceId,
            SourceChannelId = candidate.SourceChannelId,
            VirtualDeviceId = virtualDeviceId,
            ProducerNodeId = producerNodeId
        };

    private static ActiveStreamFlowContext CreateSyntheticFlow(VirtualDeviceId? virtualDeviceId, string source) =>
        new()
        {
            VirtualDeviceId = virtualDeviceId,
            SyntheticSource = source
        };

    private VirtualDeviceId? GetWebPlayerVirtualDeviceId() =>
        _settingsService.Settings.Streaming.VirtualDevices
            .SingleOrDefault(device => string.Equals(device.Adapter.Kind, WebPlayerVirtualTunerDriver.AdapterKind, StringComparison.OrdinalIgnoreCase))
            ?.Id;

    private int[] GetRepresentationFragmentCounts(string directory, int representationCount) =>
        Enumerable.Range(0, representationCount)
            .Select(index => _transientData.EnumerateFiles(directory, $"chunk-{index}-*.m4s").Take(2).Count())
            .ToArray();

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

    /// <summary>
    /// Stops a shared CMAF stream session.
    /// </summary>
    [HttpPost("watch-test/stop/{sessionId}")]
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
    /// Lists active shared CMAF sessions.
    /// </summary>
    [HttpGet("watch-test/sessions")]
    [HttpGet("cmaf/sessions")]
    public IActionResult GetCmafSessions()
    {
        var sessions = _cmafOutputs.GetViewers()
            .Select(viewer => (viewer, session: GetOutputSession(viewer.State)))
            .Where(item => item.session is not null)
            .Select(item =>
        {
            var (viewer, session) = item;
            return new
            {
                SessionId = viewer.ViewerSessionId.ToString(),
                session!.Channel,
                startTime = session.StartTime,
                durationMinutes = (DateTime.UtcNow - session.StartTime).TotalMinutes,
                isRunning = session.Process is { HasExited: false }
            };
        }).ToList();

        return Ok(sessions);
    }

    private bool StopCmafSession(string sessionId)
    {
        return TryParseSessionId(sessionId, out var parsed) &&
            _cmafOutputs.ReleaseViewerAsync(parsed).AsTask().GetAwaiter().GetResult();
    }

    private bool StopCmafSession(CmafSession session)
    {
        if (session.OutputId is not StreamSessionId outputId)
        {
            CleanupCmafSession(session, stopProcess: true);
            return false;
        }
        _cmafOutputs.ReleaseOutputAsync(outputId).AsTask().GetAwaiter().GetResult();
        return true;
    }

    private void CleanupCmafSession(CmafSession session, bool stopProcess)
    {
        _logger.LogInformation("Stopping CMAF session {SessionId} for channel {Channel}", session.SessionId, session.Channel);
        session.IsActive = false;
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
    }

    private void CleanupIncompleteCmafOutput(Process? process, Process? captionProcess, Task<string>? captionErrorTask, string? directory)
    {
        if (process is { HasExited: false })
        {
            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }
            catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
            {
                _logger.LogDebug(exception, "Unable to stop an incomplete CMAF process");
            }
        }
        process?.Dispose();
        StopCaptionProcess(captionProcess, captionErrorTask, "starting");
        if (directory is not null)
        {
            TryDeleteTransientDirectory(directory);
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

        if (hasExited && Volatile.Read(ref session.IsStarting) == 0)
        {
            if (session.OutputId is StreamSessionId outputId)
            {
                _cmafOutputs.ReleaseOutputAsync(outputId).AsTask().GetAwaiter().GetResult();
            }
            else
            {
                CleanupCmafSession(session, stopProcess: false);
            }
        }
    }

    private async Task<CmafSession> RegisterCmafSessionAsync(CmafSession session, CancellationToken cancellationToken)
    {
        var viewerId = new StreamSessionId(Guid.ParseExact(session.SessionId, "N"));
        var transformKey = session.TransformKey ?? new StreamTransformKey("cmaf-exclusive", session.SessionId);
        var lease = await _cmafOutputs.AcquireAsync(
            transformKey,
            viewerId,
            _ => ValueTask.FromResult(new CmafOutputRegistration(
                new CmafSessionOwner(this, session),
                session,
                released => _activeStreamRegistry.Unregister(released.ToString()))),
            cancellationToken);
        var output = (CmafSession)lease.State;
        output.OutputId = lease.OutputId;
        if (!ReferenceEquals(output, session))
        {
            CleanupCmafSession(session, stopProcess: true);
        }
        return output;
    }

    private bool TryGetCmafSession(string sessionId, out CmafSession session)
    {
        if (TryParseSessionId(sessionId, out var parsed) &&
            _cmafOutputs.TryGetViewer(parsed, out var viewer) &&
            GetOutputSession(viewer?.State) is CmafSession found)
        {
            session = found;
            return true;
        }
        session = null!;
        return false;
    }

    private static bool TryParseSessionId(string value, out StreamSessionId sessionId)
    {
        var valid = Guid.TryParseExact(value, "N", out var parsed);
        sessionId = new StreamSessionId(parsed);
        return valid;
    }

    private static CmafSession? GetOutputSession(object? state) => state switch
    {
        CmafSession session => session,
        PendingCmafOutput pending => pending.Session,
        _ => null
    };

    private static CmafStreamResponse RebaseCmafResponse(CmafStreamResponse response, string sessionId)
    {
        var baseUrl = $"/api/stream/cmaf/{sessionId}";
        return response with
        {
            SessionId = sessionId,
            HlsManifestUrl = $"{baseUrl}/{CmafStreamPlanner.HlsManifestName}",
            DashManifestUrl = $"{baseUrl}/{CmafStreamPlanner.DashManifestName}",
            Subtitles = response.Subtitles.Select(subtitle => subtitle with
            {
                Url = $"{baseUrl}/captions-{subtitle.SourceIndex}.vtt"
            }).ToArray()
        };
    }

    private MediaProbeResult GetEffectiveSource(MediaProbeResult source, PhysicalTranscodeProfile physicalProfile) =>
        _tunerStreamMultiplexer is IEffectiveTunerStreamMultiplexer effective
            ? effective.GetEffectiveSource(source, physicalProfile)
            : source;

    private ValueTask<Stream> SubscribeEffectiveSourceAsync(
        ResolvedStreamingCandidate candidate,
        MediaProbeResult source,
        LineupSettings settings,
        CancellationToken cancellationToken) =>
        _tunerStreamMultiplexer is IEffectiveTunerStreamMultiplexer effective
            ? effective.SubscribeEffectiveAsync(
                candidate.Acquisition,
                candidate.OpenAsync,
                candidate.DiagnosticUri.AbsoluteUri,
                source,
                candidate.PhysicalProfile,
                settings,
                cancellationToken)
            : _tunerStreamMultiplexer.SubscribeAsync(candidate.Acquisition, candidate.OpenAsync, cancellationToken);

    private ValueTask<Stream> SubscribeMpegTsOutputAsync(
        ResolvedStreamingRoute route,
        ResolvedStreamingCandidate candidate,
        MediaProbeResult source,
        LineupSettings settings,
        CancellationToken cancellationToken) =>
        _tunerStreamMultiplexer is IEffectiveTunerStreamMultiplexer effective
            ? effective.SubscribeMpegTsAsync(
                candidate.Acquisition,
                candidate.OpenAsync,
                candidate.DiagnosticUri.AbsoluteUri,
                source,
                candidate.PhysicalProfile,
                route.VirtualProfile,
                settings,
                TimeSpan.FromSeconds(settings.Streaming.KeepWarmPeriodSeconds),
                cancellationToken)
            : _tunerStreamMultiplexer.SubscribeAsync(candidate.Acquisition, candidate.OpenAsync, cancellationToken);

    private static async Task EnsureInputPumpSucceededAsync(Task inputPumpTask)
    {
        try
        {
            await inputPumpTask;
        }
        catch (Exception exception) when (exception is IOException or MediaProcessException)
        {
            throw new MediaInputException("The upstream media stream failed before the presentation was ready.", exception);
        }
    }

    private async Task PumpEffectiveSourceAsync(
        ResolvedStreamingCandidate candidate,
        MediaProbeResult source,
        LineupSettings settings,
        Process process,
        CancellationToken cancellationToken)
    {
        await using var input = await SubscribeEffectiveSourceAsync(candidate, source, settings, cancellationToken);
        await MediaStreamPump.PumpAsync(input, process, _logger, candidate.DiagnosticUri.AbsoluteUri, cancellationToken);
    }

    private async Task PumpSourceAsync(ResolvedStreamingCandidate candidate, Process process, CancellationToken cancellationToken)
    {
        await using var input = await _tunerStreamMultiplexer.SubscribeAsync(candidate.Acquisition, candidate.OpenAsync, cancellationToken);
        await MediaStreamPump.PumpAsync(input, process, _logger, candidate.DiagnosticUri.AbsoluteUri, cancellationToken);
    }

    private static ProviderWebPlayer.WebPlayerMediaTrack ToWebPlayerTrack(MediaTrackMetadata track) =>
        new()
        {
            Index = track.Index,
            Type = track.Type switch
            {
                MediaTrackType.Video => ProviderWebPlayer.WebPlayerMediaType.Video,
                MediaTrackType.Audio => ProviderWebPlayer.WebPlayerMediaType.Audio,
                _ => ProviderWebPlayer.WebPlayerMediaType.Subtitle
            },
            Codec = track.Codec,
            Channels = track.Channels,
            IsDefault = track.IsDefault,
            SubtitleMode = track.SubtitlePresentation switch
            {
                SubtitlePresentation.WebVtt => ProviderWebPlayer.WebPlayerSubtitleMode.WebVtt,
                SubtitlePresentation.BurnIn => ProviderWebPlayer.WebPlayerSubtitleMode.BurnIn,
                _ => ProviderWebPlayer.WebPlayerSubtitleMode.Unsupported
            }
        };

    private static ProviderWebPlayer.WebPlayerPlaybackRequest ToWebPlayerRequest(CmafStreamRequest request) =>
        new()
        {
            AudioTrackIndex = request.AudioTrack,
            SubtitleTrackIndex = request.SubtitleTrack,
            MaximumHeight = request.Quality switch
            {
                WebPlayerQuality.High => 1080,
                WebPlayerQuality.Medium => 720,
                WebPlayerQuality.Low => 480,
                _ => null
            }
        };

    private VirtualDeviceId ResolveWebPlayerVirtualDeviceId() =>
        _settingsService.Settings.Streaming.VirtualDevices.Single(device =>
            device.Enabled &&
            device.Hosting is null &&
            string.Equals(device.Adapter.Kind, WebPlayerVirtualTunerDriver.AdapterKind, StringComparison.OrdinalIgnoreCase)).Id;

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

    private static LineupSettings CreateSourceDeinterlaceSettingsSnapshot(LineupSettings settings) =>
        new()
        {
            Streaming = settings.Streaming
        };

    private async Task<MediaProbeResult> ProbeBestEffortAsync(ResolvedStreamingCandidate candidate, CancellationToken cancellationToken)
    {
        try
        {
            return await _mediaProbeService.ProbeAsync(
                candidate.Acquisition,
                candidate.OpenAsync,
                candidate.DiagnosticUri.AbsoluteUri,
                candidate.SourceMetadata,
                cancellationToken);
        }
        catch (MpegTsTranscodeException ex)
        {
            _logger.LogWarning(
                "Unable to probe media metadata for {InputUri}; streaming will continue with unknown source details: {ProbeError}",
                candidate.DiagnosticUri,
                ex.Message);
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

    private async Task<ProtectedContentDetection> DetectContentProtectionAsync(string channel, string? tunerError, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(tunerError))
        {
            return ProtectedContentDetector.Detect(tunerError, cachedDrm: false);
        }

        var cachedChannel = (await _guideReader.GetChannelsAsync(cancellationToken: cancellationToken))
            .FirstOrDefault(candidate => string.Equals(candidate.GuideNumber, channel, StringComparison.Ordinal));
        return ProtectedContentDetector.Detect(tunerError, cachedChannel?.DRM == true);
    }

    private ObjectResult CreateProtectedContentResult(ProtectedContentDetection protection) =>
        StatusCode(StatusCodes.Status422UnprocessableEntity, new
        {
            code = "protected_content",
            error = protection.Message ?? "The requested channel is protected and cannot be streamed.",
            retryable = false,
            inferred = protection.IsInferred,
            source = protection.IsInferred ? "channel-lineup" : "tuner"
        });

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
                        if (session.IsActive)
                        {
                            _activeStreamRegistry.Register(ActiveStreamPlanFactory.CreateCmaf(session.SessionId, session.Channel, session.StartTime, source, outputVideoBitRate));
                        }
                    }

                    MediaProcessDiagnostics.AppendLine(errors, line);
                    _logger.LogDebug("FFmpeg CMAF [{SessionId}]: {Line}", session.SessionId, MediaProcessDiagnostics.GetTail(line));
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
        public IDisposable? CapacityLease { get; set; }
        /// <summary>Gets the provider-neutral transform identity selected for this output.</summary>
        public StreamTransformKey? TransformKey { get; init; }
        /// <summary>Gets or sets the reusable output identity assigned by the coordinator.</summary>
        public StreamSessionId? OutputId { get; set; }
        /// <summary>Gets the optional embedded-caption extractor process.</summary>
        public Process? CaptionProcess { get; set; }
        /// <summary>Gets the task draining embedded-caption extractor diagnostics.</summary>
        public Task<string>? CaptionErrorTask { get; set; }
        /// <summary>
        /// Tracks whether the starting request still owns process-exit cleanup.
        /// </summary>
        public int IsStarting = 1;

        /// <summary>Tracks whether the session remains active.</summary>
        public bool IsActive = true;
    }

    private sealed class CmafSessionOwner(StreamController controller, CmafSession session) : IAsyncDisposable
    {
        private StreamController? _controller = controller;

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _controller, null)?.CleanupCmafSession(session, stopProcess: true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class PendingCmafOutput : IAsyncDisposable
    {
        private readonly TaskCompletionSource<IActionResult> _response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _gate = new();
        private readonly CancellationTokenSource _lifetimeCancellation = new();
        private readonly CancellationToken _lifetimeToken;
        private StreamController? _controller;
        private Process? _process;
        private Process? _captionProcess;
        private Task<string>? _captionErrorTask;
        private string? _directory;
        private bool _starting;
        private bool _resourcesDisposed;

        /// <summary>Owns pending startup artifacts independently of the initiating viewer.</summary>
        public PendingCmafOutput(StreamController controller, string directory)
        {
            _controller = controller;
            _directory = directory;
            _lifetimeToken = _lifetimeCancellation.Token;
        }

        /// <summary>Gets the producer's terminal startup result shared by all viewers.</summary>
        public Task<IActionResult> Response => _response.Task;

        /// <summary>Gets completion after the producer's processes and output artifacts have been released.</summary>
        public Task Stopped => _stopped.Task;

        /// <summary>Gets cancellation when the coordinator releases the producer.</summary>
        public CancellationToken LifetimeToken => _lifetimeToken;

        /// <summary>Gets the completed media session once readiness succeeds.</summary>
        public CmafSession? Session { get; private set; }

        /// <summary>Runs bounded readiness and propagates its terminal result without using an individual viewer's lifetime.</summary>
        public async Task RunAsync(Func<CancellationToken, Task<IActionResult>> start, TimeSpan timeout, StreamSessionId outputId)
        {
            StreamController owner;
            lock (_gate)
            {
                if (_controller is not { } controller)
                {
                    return;
                }
                owner = controller;
                _starting = true;
            }
            try
            {
                using var startupCancellation = CancellationTokenSource.CreateLinkedTokenSource(LifetimeToken);
                startupCancellation.CancelAfter(timeout);
                _response.TrySetResult(await start(startupCancellation.Token));
            }
            catch (Exception exception)
            {
                owner._logger.LogError(exception, "Shared CMAF producer startup failed for output {OutputId}", outputId);
                _response.TrySetException(exception);
                _ = _response.Task.Exception;
            }
            finally
            {
                bool disposed;
                lock (_gate)
                {
                    _starting = false;
                    disposed = _controller is null;
                }
                if (disposed)
                {
                    DisposeResources(owner);
                }
                else if (Session is null)
                {
                    await owner._cmafOutputs.ReleaseOutputAsync(outputId);
                }
            }
        }

        /// <summary>Transfers current process and artifact ownership, including replacements made during startup.</summary>
        public void Attach(Process process, string directory, Process? captionProcess = null, Task<string>? captionErrorTask = null)
        {
            lock (_gate)
            {
                _process = process;
                _directory = directory;
                _captionProcess = captionProcess;
                _captionErrorTask = captionErrorTask;
            }
        }

        /// <summary>Transfers pending resources into a ready media session.</summary>
        public void Complete(CmafSession session)
        {
            lock (_gate)
            {
                LifetimeToken.ThrowIfCancellationRequested();
                Session = session;
                _process = null;
                _captionProcess = null;
                _captionErrorTask = null;
                _directory = null;
            }
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            StreamController owner;
            lock (_gate)
            {
                if (_controller is not { } controller)
                {
                    return ValueTask.CompletedTask;
                }
                owner = controller;
                _controller = null;
                _lifetimeCancellation.Cancel();
                _response.TrySetCanceled();
                if (_starting)
                {
                    // Startup retains cleanup ownership until any in-flight process replacement finishes.
                    return new ValueTask(_stopped.Task);
                }
            }
            DisposeResources(owner);
            return ValueTask.CompletedTask;
        }

        private void DisposeResources(StreamController owner)
        {
            CmafSession? session;
            Process? process;
            Process? captionProcess;
            Task<string>? captionErrorTask;
            string? directory;
            lock (_gate)
            {
                if (_resourcesDisposed)
                {
                    return;
                }
                _resourcesDisposed = true;
                session = Session;
                process = _process;
                captionProcess = _captionProcess;
                captionErrorTask = _captionErrorTask;
                directory = _directory;
                _process = null;
                _captionProcess = null;
                _captionErrorTask = null;
                _directory = null;
            }
            try
            {
                if (session is not null)
                {
                    owner.CleanupCmafSession(session, stopProcess: true);
                }
                else
                {
                    owner.CleanupIncompleteCmafOutput(process, captionProcess, captionErrorTask, directory);
                }
            }
            catch (Exception exception)
            {
                owner._logger.LogError(exception, "Unable to clean up shared CMAF producer resources");
                _stopped.TrySetException(exception);
                throw;
            }
            finally
            {
                _lifetimeCancellation.Dispose();
                _stopped.TrySetResult();
            }
        }
    }

}
