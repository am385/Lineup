using ProviderWebPlayer = Lineup.Plugin.WebPlayer;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text.Json;

namespace Lineup.App.Web.Services.Playback;

/// <summary>
/// Owns management playback producers, readiness, viewer leases, and media resource cleanup independently of HTTP requests.
/// </summary>
public sealed class ManagementPlaybackService : IAsyncDisposable
{
    private static readonly TimeSpan DefaultCmafStartupTimeout = TimeSpan.FromSeconds(24);
    private static readonly TimeSpan TunerDiagnosticTimeout = TimeSpan.FromSeconds(5);
    private const string StreamLimitError = "The stream or startup limit has been reached.";
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILineupGuideReader _guideReader;
    private readonly ILineupSettingsService _settingsService;
    private readonly StreamingTopologyStore _topologyStore;
    private readonly IMediaProbeService _mediaProbeService;
    private readonly IActiveStreamRegistry _activeStreamRegistry;
    private readonly IProtectedContentSlateService _protectedContentSlateService;
    private readonly ICmafCompatibilityTestService _compatibilityTestService;
    private readonly ITunerStreamMultiplexer _tunerStreamMultiplexer;
    private readonly ILogger _logger;
    private readonly ITransientDataStore _transientData;
    private readonly IMediaProcessFactory _mediaProcessFactory;
    private readonly IMediaWorkAdmission? _mediaWork;
    private readonly IStreamingRouteResolver? _streamRoutes;
    private readonly ICmafOutputCoordinator _cmafOutputs;
    private readonly TimeSpan _cmafStartupTimeout;
    private readonly CmafMediaTasks _preflightTasks = new();
    private readonly bool _ownsCmafOutputs;

    /// <summary>
    /// Initializes the application-owned management playback runtime.
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
    /// <param name="transientData">Provides the configured CMAF artifact root.</param>
    /// <param name="compatibilityTestService">Generates synthetic single-rendition CMAF compatibility tests.</param>
    /// <param name="mediaProcessFactory">Starts FFmpeg without shell interpretation.</param>
    /// <param name="streamRoutes">Resolves configured topology routes.</param>
    /// <param name="cmafOutputs">Owns shared CMAF outputs and viewer aliases.</param>
    /// <param name="cmafStartupTimeout">Overrides the complete management-hosted CMAF startup deadline.</param>
    /// <param name="mediaWork">Admits deployment-wide media ownership and actual workspace allocation.</param>
    public ManagementPlaybackService(
        IHttpClientFactory httpClientFactory,
        ILineupGuideReader guideReader,
        ILineupSettingsService settingsService,
        StreamingTopologyStore topologyStore,
        IMediaProbeService mediaProbeService,
        IActiveStreamRegistry activeStreamRegistry,
        IProtectedContentSlateService protectedContentSlateService,
        ITunerStreamMultiplexer tunerStreamMultiplexer,
        ILogger logger,
        TimeProvider? timeProvider = null,
        TimeSpan? cmafInactivityTimeout = null,
        ITransientDataStore? transientData = null,
        ICmafCompatibilityTestService? compatibilityTestService = null,
        IMediaProcessFactory? mediaProcessFactory = null,
        IStreamingRouteResolver? streamRoutes = null,
        ICmafOutputCoordinator? cmafOutputs = null,
        TimeSpan? cmafStartupTimeout = null,
        IMediaWorkAdmission? mediaWork = null)
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
        _compatibilityTestService = compatibilityTestService ?? new CmafCompatibilityTestService();
        _mediaProcessFactory = mediaProcessFactory ?? new MediaProcessFactory();
        _mediaWork = mediaWork;
        _streamRoutes = streamRoutes;
        _cmafOutputs = cmafOutputs ?? new CmafOutputCoordinator(timeProvider, cmafInactivityTimeout, mediaWork);
        _ownsCmafOutputs = cmafOutputs is null;
        _cmafStartupTimeout = cmafStartupTimeout ?? DefaultCmafStartupTimeout;
        if (_cmafStartupTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(cmafStartupTimeout), _cmafStartupTimeout, "The CMAF startup timeout must be greater than zero.");
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_ownsCmafOutputs)
        {
            await _cmafOutputs.DisposeAsync();
        }
        await _preflightTasks.DrainAsync();
    }

    /// <summary>Starts a management-hosted Web Player CMAF presentation.</summary>
    public async Task<ManagementPlaybackStartResult> StartAsync(string channel, CmafStreamRequest request, string? clientAddress = null, string? requestId = null, CancellationToken cancellationToken = default)
    {
        if (request.CompatibilityProfile is not null && !CmafCompatibilityProfilePolicy.IsValid(request.CompatibilityProfile))
        {
            return Failure(ManagementPlaybackFailureKind.InvalidRequest, "The browser compatibility profile is incomplete, stale, oversized, or invalid.");
        }
        try
        {
            request = CmafStreamPlanner.ApplyOverrides(request);
        }
        catch (ArgumentException exception)
        {
            return Failure(ManagementPlaybackFailureKind.InvalidRequest, exception.Message);
        }
        using var admissionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var startupCancellation = CancellationTokenSource.CreateLinkedTokenSource(admissionCancellation.Token);
        startupCancellation.CancelAfter(_cmafStartupTimeout);
        try
        {
            return await StartCmafStreamCore(channel, request, clientAddress, requestId, admissionCancellation.Cancel, cancellationToken, startupCancellation.Token);
        }
        catch (OperationCanceledException) when (startupCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return admissionCancellation.IsCancellationRequested ? new ManagementPlaybackStartResult { IsCanceled = true } : CreateCmafStartupTimeoutResult();
        }
    }

    /// <summary>
    /// Starts an exact single-rendition synthetic CMAF browser compatibility test.
    /// </summary>
    /// <param name="request">The codec, layout, quality, and protocol selection.</param>
    /// <param name="cancellationToken">Cancels only this compatibility startup.</param>
    /// <returns>The typed presentation or terminal startup failure.</returns>
    public async Task<ManagementPlaybackStartResult> StartCompatibilityAsync(CmafCompatibilityTestRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            CmafCompatibilityTestPlanner.ValidateRequest(request);
        }
        catch (NotSupportedException ex)
        {
            return Failure(ManagementPlaybackFailureKind.Unsupported, ex.Message);
        }
        catch (ArgumentException ex)
        {
            return Failure(ManagementPlaybackFailureKind.InvalidRequest, ex.Message);
        }

        var sessionId = StreamSessionId.New().ToString();
        var directory = _transientData.CreateSessionDirectory(WebPlayerTransientWorkspaces.Cmaf, sessionId);
        Process? process = null;
        CmafSession? session = null;
        PendingCmafOutput? pending = null;
        ICmafOutputLease? outputLease = null;
        var ownershipTransferred = false;
        try
        {
            outputLease = await _cmafOutputs.AcquireAsync(
                new StreamTransformKey("web-player-compatibility", sessionId),
                new StreamSessionId(Guid.ParseExact(sessionId, "N")),
                _ =>
                {
                    _mediaWork?.CheckWorkspace(directory);
                    pending = new PendingCmafOutput(this, directory);
                    return ValueTask.FromResult(new CmafOutputRegistration(pending, pending));
                }, cancellationToken);
            var manifestPath = _transientData.GetFilePath(directory, CmafStreamPlanner.DashManifestName);
            var subtitlePath = request.SubtitleMode == CmafTestSubtitleMode.WebVttSidecar
                ? _transientData.GetFilePath(directory, "captions-0.vtt")
                : null;
            if (subtitlePath is not null)
            {
                await System.IO.File.WriteAllTextAsync(
                    subtitlePath,
                    CmafCompatibilityTestPlanner.CreateChannelSubtitleWebVtt(request.ChannelLayout),
                    cancellationToken);
            }
            process = _compatibilityTestService.Start(_settingsService.Settings.Streaming.VirtualDefaults, request, manifestPath);
            session = new CmafSession
            {
                SessionId = sessionId,
                Channel = "Watch Test",
                IsCompatibilityTest = true,
                Process = process,
                CmafDirectory = directory,
                PlaylistPath = manifestPath,
                StartTime = DateTime.UtcNow
            };
            session.OutputId = outputLease.OutputId;
            pending!.Complete(session);
            var errors = new ConcurrentQueue<string>();
            var outputVideoBitRate = WebVideoTranscodePlanner.GetMaximumBitRate(
                _settingsService.Settings.Streaming.VirtualDefaults,
                request.Quality);
            var errorMonitorTask = StartCmafErrorMonitor(process, session, errors, parseSourceMetadata: false, outputVideoBitRate);
            session.Tasks.Track(errorMonitorTask, _logger, "compatibility diagnostics");

            for (var attempt = 0; attempt < 200; attempt++)
            {
                if (process.HasExited)
                {
                    await errorMonitorTask;
                    return Failure(ManagementPlaybackFailureKind.MediaFailure, errors.LastOrDefault() ?? "FFmpeg exited before the Watch Test presentation was ready.");
                }

                var hlsPath = _transientData.GetFilePath(directory, CmafStreamPlanner.HlsManifestName);
                if (_transientData.FileExists(manifestPath) && _transientData.FileExists(hlsPath) && _transientData.EnumerateFiles(directory, "*.m4s").Count >= 2)
                {
                    var (width, height) = CmafCompatibilityTestPlanner.GetResolution(request.Quality);
                    (string VideoCodec, string AudioCodec) codecs;
                    try
                    {
                        using var manifestStream = _transientData.OpenRead(manifestPath) ?? throw new IOException("Watch Test manifest could not be opened.");
                        using var manifestReader = new StreamReader(manifestStream);
                        codecs = CmafCompatibilityTestPlanner.ParseManifestCodecs(await manifestReader.ReadToEndAsync(cancellationToken));
                    }
                    catch (System.Xml.XmlException)
                    {
                        await Task.Delay(100, cancellationToken);
                        continue;
                    }
                    Volatile.Write(ref session.IsStarting, 0);
                    if (!session.IsActive || process.HasExited)
                    {
                        await errorMonitorTask;
                        return Failure(ManagementPlaybackFailureKind.MediaFailure, errors.LastOrDefault() ?? "FFmpeg exited before the Watch Test presentation was ready.");
                    }
                    var response = new ManagementCompatibilityPresentation(
                        sessionId,
                        request.Protocol,
                        codecs.VideoCodec,
                        codecs.AudioCodec,
                        CmafCompatibilityTestPlanner.GetChannelCount(request.ChannelLayout),
                        width,
                        height,
                        subtitlePath is not null);
                    ownershipTransferred = true;
                    return new ManagementPlaybackStartResult { Compatibility = response };
                }

                await Task.Delay(100, cancellationToken);
            }

            return Failure(ManagementPlaybackFailureKind.Timeout, "FFmpeg did not create the Watch Test manifests and fragments in time.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new ManagementPlaybackStartResult { IsCanceled = true };
        }
        catch (MediaResourceLimitException ex)
        {
            _logger.LogWarning(ex, "Watch Test media admission rejected for session {SessionId}", sessionId);
            return Failure(ManagementPlaybackFailureKind.AdmissionLimit, ex.Message, ex.Code, "admission");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            _logger.LogError(ex, "Error starting Watch Test CMAF session {SessionId}", sessionId);
            return Failure(ManagementPlaybackFailureKind.InternalError, ex.Message);
        }
        finally
        {
            if (!ownershipTransferred)
            {
                if (outputLease is not null)
                {
                    await _cmafOutputs.ReleaseOutputAsync(outputLease.OutputId);
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

    private async Task<ManagementPlaybackStartResult> StartCmafStreamCore(
        string channel,
        CmafStreamRequest request,
        string? clientAddress,
        string? requestId,
        Action cancelStartup,
        CancellationToken cancellationToken,
        CancellationToken startupToken)
    {
        using var startup = _activeStreamRegistry.TryBeginStartup(_settingsService.Settings.MaximumConcurrentStreams, cancelStartup);
        if (startup is null)
        {
            return Failure(ManagementPlaybackFailureKind.AdmissionLimit, StreamLimitError);
        }
        CmafFallbackAudio fallbackAudio;
        try
        {
            fallbackAudio = CmafStreamPlanner.ResolveFallbackAudio(request);
        }
        catch (ArgumentException ex)
        {
            return Failure(ManagementPlaybackFailureKind.InvalidRequest, ex.Message);
        }

        var disabled = await IsChannelDisabledAsync(channel, startupToken);
        if (disabled && _settingsService.Settings.DisabledChannelMode == DisabledChannelMode.ReturnError)
        {
            return Failure(ManagementPlaybackFailureKind.Disabled, $"Channel {channel} is disabled.");
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
                return Failure(ManagementPlaybackFailureKind.Unavailable, "The streaming topology runtime is unavailable.");
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
                }
            }
            catch (KeyNotFoundException ex)
            {
                return Failure(ManagementPlaybackFailureKind.NotFound, ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return Failure(ManagementPlaybackFailureKind.Unavailable, ex.Message);
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
                        _preflightTasks.Track(selectionPump, _logger, "source selection");
                    }
                }
            }
            catch (MediaResourceLimitException ex)
            {
                _logger.LogWarning(ex, "CMAF source admission rejected for channel {Channel}", channel);
                return Failure(ManagementPlaybackFailureKind.AdmissionLimit, ex.Message, ex.Code, "admission");
            }
            catch (StreamCapacityExceededException ex)
            {
                return Failure(ManagementPlaybackFailureKind.Unavailable, ex.Message);
            }
            catch (StreamAcquisitionException exception) when (
                exception.Category == PhysicalStreamFailureCategory.ProtectedContent && _settingsService.Settings.ProtectedContentMode == ProtectedContentMode.StreamSlate)
            {
                startupToken.ThrowIfCancellationRequested();
                protectedContent = true;
            }
            catch (StreamAcquisitionException exception)
            {
                return CreateSourceAcquisitionResult(exception);
            }
            if (protectedContent)
            {
                rawSource = ChannelSlatePlanner.CreateMedia();
                source = rawSource;
            }
            else
            {
                if (!MediaProbeResultPolicy.HasAudio(rawSource) &&
                    (rawSource.AudioDiscovery == AudioDiscoveryStatus.ExpectedMissing || MediaProbeResultPolicy.ExpectsAudio(selectedCandidate!.SourceMetadata)))
                {
                    _logger.LogWarning("CMAF source audio discovery is incomplete for channel {Channel}, request {RequestId}.", channel, requestId);
                    return Failure(ManagementPlaybackFailureKind.MediaFailure, "The source advertises audio, but bounded probing did not discover it.", "media_audio_missing", "probing");
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
            return Failure(ManagementPlaybackFailureKind.InvalidRequest, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Failure(ManagementPlaybackFailureKind.Unsupported, ex.Message);
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
                : ActiveStreamPlanFactory.CreateCmaf(sessionId, channel, startedAt, rawSource, source, selection, request, virtualProfile);
        activeStream = activeStream with
        {
            ClientAddress = clientAddress,
            ClientId = request.ClientId,
            Flow = disabled
                ? CreateSyntheticFlow(GetWebPlayerVirtualDeviceId(), "Disabled channel slate")
                : protectedContent
                    ? CreateSyntheticFlow(GetWebPlayerVirtualDeviceId(), "Protected content slate")
                    : CreatePhysicalFlow(selectedCandidate!, topologyRoute!.VirtualDevice.Id)
        };
        if (!startup.TryRegister(activeStream, () =>
            {
                viewerStopped.TrySetResult();
                _preflightTasks.Track(Task.Run(() => StopCmafSessionAsync(sessionId)), _logger, "viewer stop");
            }))
        {
            TryDeleteTransientDirectory(directory);
            return Failure(ManagementPlaybackFailureKind.AdmissionLimit, StreamLimitError);
        }

        var sourceIdentity = selectedCandidate is null ? null : JsonSerializer.Serialize(new { Source = selectedCandidate.Acquisition.SourceIdentity, Normalization = selectedCandidate.PhysicalProfile });
        var planKey = CmafStreamPlanner.CreatePlanKey(virtualProfile, source, selection, request);
        var transformKey = providerPlan is null
            ? new StreamTransformKey("web-player-cmaf", $"{(protectedContent ? "protected" : "disabled")}:{channel}|{planKey}")
            : new StreamTransformKey(
                providerPlan.TransformKey.Stage,
                $"{sourceIdentity}|{planKey}");
        var pendingOutput = new PendingCmafOutput(this, directory);
        ICmafOutputLease outputLease;
        try
        {
            outputLease = await _cmafOutputs.AcquireAsync(
                transformKey,
                new StreamSessionId(Guid.ParseExact(sessionId, "N")),
                _ =>
                {
                    _mediaWork?.CheckWorkspace(directory);
                    return ValueTask.FromResult(new CmafOutputRegistration(
                    pendingOutput,
                    pendingOutput,
                    released => _activeStreamRegistry.Unregister(released.ToString())));
                },
                TimeSpan.FromSeconds(_settingsService.Settings.Streaming.KeepWarmPeriodSeconds),
                startupToken);
        }
        catch (OperationCanceledException) when (startupToken.IsCancellationRequested)
        {
            await pendingOutput.DisposeAsync();
            _activeStreamRegistry.Unregister(sessionId);
            TryDeleteTransientDirectory(directory);
            return cancellationToken.IsCancellationRequested
                ? new ManagementPlaybackStartResult { IsCanceled = true }
                : CreateCmafStartupTimeoutResult();
        }
        catch (MediaResourceLimitException ex)
        {
            await pendingOutput.DisposeAsync();
            _activeStreamRegistry.Unregister(sessionId);
            _logger.LogWarning(ex, "CMAF media admission rejected for session {SessionId}", sessionId);
            return Failure(ManagementPlaybackFailureKind.AdmissionLimit, ex.Message, ex.Code, "admission");
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
            pendingOutput.Start(StartProducerAsync, _cmafStartupTimeout, outputLease.OutputId);
        }
        try
        {
            await Task.WhenAny(sharedOutput.Response, viewerStopped.Task).WaitAsync(startupToken);
            if (viewerStopped.Task.IsCompleted)
            {
                await outputLease.DisposeAsync();
                return new ManagementPlaybackStartResult { IsCanceled = true };
            }
            var result = await sharedOutput.Response;
            if (result.Presentation is null)
            {
                await sharedOutput.Stopped;
            }
            return result.Presentation is { } presentation ? result with { Presentation = presentation with { SessionId = sessionId } } : result;
        }
        catch (OperationCanceledException) when (startupToken.IsCancellationRequested && ownsProducer)
        {
            await outputLease.DisposeAsync();
            return cancellationToken.IsCancellationRequested ? new ManagementPlaybackStartResult { IsCanceled = true } : CreateCmafStartupTimeoutResult();
        }
        catch
        {
            await outputLease.DisposeAsync();
            throw;
        }

        async Task<ManagementPlaybackStartResult> StartProducerAsync(CancellationToken producerToken)
        {
            Process? process = null;
            Task? inputPumpTask = null;
            Process? captionProcess = null;
            Task<string>? captionErrorTask = null;
            CancellationTokenSource? inputCancellation = null;
            CancellationTokenSource? captionCancellation = null;
            try
            {
                var usingSlate = disabled || protectedContent;
                var ffmpegArguments = usingSlate
                    ? null
                    : CmafStreamPlanner.CreateArguments(virtualProfile, source, selection, request, manifestPath, subtitlePaths);
                producerToken.ThrowIfCancellationRequested();

                if (usingSlate)
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
                if (!usingSlate)
                {
                    inputCancellation = pendingOutput.Tasks.CreateCancellation(pendingOutput.LifetimeToken);
                    inputPumpTask = PumpEffectiveSourceAsync(selectedCandidate!, rawSource, sourceSettings, process, inputCancellation.Token, pendingOutput.Tasks);
                    pendingOutput.Tasks.Track(inputPumpTask, _logger, "media input");
                }
                var embeddedCaptions = selectableSubtitles.FirstOrDefault(subtitle => subtitle.IsEmbeddedClosedCaptions);
                if (!usingSlate && embeddedCaptions is not null)
                {
                    captionProcess = _mediaProcessFactory.Start(new MediaProcessRequest
                    {
                        FileName = "ffmpeg",
                        Arguments = WebPlayerTrackPlanner.CreateEmbeddedCaptionArguments(subtitlePaths[embeddedCaptions.Index]),
                        RedirectStandardInput = true,
                        RedirectStandardError = true
                    });
                    pendingOutput.Attach(process, directory, captionProcess);
                    captionCancellation = pendingOutput.Tasks.CreateCancellation(pendingOutput.LifetimeToken);
                    captionErrorTask = MediaProcessDiagnostics.ReadTailAsync(captionProcess.StandardError, captionCancellation.Token);
                    pendingOutput.Attach(process, directory, captionProcess, captionErrorTask);
                    pendingOutput.Tasks.Track(captionErrorTask, _logger, "caption diagnostics");
                    pendingOutput.Tasks.Track(PumpSourceAsync(selectedCandidate!, captionProcess, captionCancellation.Token), _logger, "caption input");
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
                    Tasks = pendingOutput.Tasks,
                    TransformKey = transformKey,
                    OutputId = outputLease.OutputId
                };
                var errors = new ConcurrentQueue<string>();
                var errorMonitorTask = StartCmafErrorMonitor(process, session, errors, parseSourceMetadata: false, outputVideoBitRate);
                pendingOutput.Tasks.Track(errorMonitorTask, _logger, "media diagnostics");
                var sourceAudioFallbackApplied = false;

                for (var attempt = 0; attempt < 200; attempt++)
                {
                    producerToken.ThrowIfCancellationRequested();
                    if (inputPumpTask?.IsFaulted == true)
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
                                Tasks = pendingOutput.Tasks,
                                TransformKey = sourceSession.TransformKey,
                                OutputId = outputLease.OutputId
                            };
                            sourceSession.CapacityLease = null;
                            sourceSession.CaptionProcess = null;
                            sourceSession.CaptionErrorTask = null;
                            sourceSession.IsActive = false;
                            inputCancellation!.Cancel();
                            await ObserveInputPumpAsync(inputPumpTask!);
                            sourceSession.Process.Dispose();
                            inputCancellation = pendingOutput.Tasks.CreateCancellation(pendingOutput.LifetimeToken);
                            inputPumpTask = PumpEffectiveSourceAsync(selectedCandidate!, rawSource, sourceSettings, process, inputCancellation.Token, pendingOutput.Tasks);
                            pendingOutput.Tasks.Track(inputPumpTask, _logger, "fallback media input");
                            UpdateCmafViewerPlans(outputLease.OutputId, ActiveStreamPlanFactory.CreateCmaf(
                                sessionId,
                                channel,
                                startedAt,
                                rawSource,
                                source,
                                selection,
                                retryRequest,
                                virtualProfile) with
                            {
                                Flow = CreatePhysicalFlow(selectedCandidate!, topologyRoute!.VirtualDevice.Id) with
                                {
                                    ReusableOutputId = outputLease.OutputId
                                }
                            });
                            errors.Clear();
                            errorMonitorTask = StartCmafErrorMonitor(process, session, errors, parseSourceMetadata: false, outputVideoBitRate);
                            pendingOutput.Tasks.Track(errorMonitorTask, _logger, "fallback media diagnostics");
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
                            tunerSession.IsActive = false;
                            inputCancellation!.Cancel();
                            await ObserveInputPumpAsync(inputPumpTask!);
                            inputCancellation = null;
                            captionCancellation?.Cancel();
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
                                Tasks = pendingOutput.Tasks,
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
                            pendingOutput.Tasks.Track(errorMonitorTask, _logger, "slate diagnostics");
                            usingSlate = true;
                            continue;
                        }

                        if (protection.IsProtected)
                        {
                            return CreateProtectedContentResult(protection);
                        }
                        var failure = new MediaProcessException(process.ExitCode, string.Join("\n", errors));
                        MediaProcessDiagnostics.LogFailure(_logger, failure, "readiness", sessionId, streamSessionId: outputLease.OutputId.ToString());
                        return Failure(ManagementPlaybackFailureKind.MediaFailure, "The media processor exited before the presentation was ready.", "media_process_failed", "readiness");
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
                        var packagedFallback = session.AudioRenditions.FirstOrDefault(rendition => !rendition.Plan.CopySource)?.Plan;
                        var response = new ManagementPlaybackPresentation(sessionId)
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
                            Subtitles = selectableSubtitles.Select(subtitle => new ManagementPlaybackSubtitle(
                                subtitle.Index,
                                subtitle.Title ?? subtitle.Language ?? $"Subtitle {subtitle.Index}",
                                subtitle.Language,
                                subtitle.IsEmbeddedClosedCaptions)).ToArray()
                        };
                        pendingOutput.Complete(session);
                        return new ManagementPlaybackStartResult { Presentation = response };
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
                return Failure(ManagementPlaybackFailureKind.Timeout, "FFmpeg did not create the shared CMAF manifests and fragments in time.");
            }
            catch (OperationCanceledException) when (producerToken.IsCancellationRequested)
            {
                return pendingOutput.LifetimeToken.IsCancellationRequested ? new ManagementPlaybackStartResult { IsCanceled = true } : CreateCmafStartupTimeoutResult();
            }
            catch (MediaInputException exception)
            {
                MediaProcessDiagnostics.LogFailure(_logger, exception, "source", sessionId, streamSessionId: outputLease.OutputId.ToString());
                return Failure(ManagementPlaybackFailureKind.MediaFailure, exception.Message, "media_source_failed", "source");
            }
            catch (MediaProcessException exception)
            {
                MediaProcessDiagnostics.LogFailure(_logger, exception, "process", sessionId, streamSessionId: outputLease.OutputId.ToString());
                return Failure(ManagementPlaybackFailureKind.MediaFailure, "The media processor could not start the presentation.", "media_process_failed", "process");
            }
            catch (MediaResourceLimitException exception)
            {
                _logger.LogWarning(exception, "CMAF media processing admission rejected for session {SessionId}", sessionId);
                return Failure(ManagementPlaybackFailureKind.AdmissionLimit, exception.Message, exception.Code, "admission");
            }
            catch (Exception ex) when (ex is MediaProcessException or InvalidOperationException or IOException or StreamCapacityExceededException)
            {
                _logger.LogError(ex, "Error starting CMAF stream for channel {Channel}", channel);
                if (ex is StreamAcquisitionException acquisitionException)
                {
                    return CreateSourceAcquisitionResult(acquisitionException);
                }
                var kind = ex is StreamCapacityExceededException ? ManagementPlaybackFailureKind.Unavailable : ManagementPlaybackFailureKind.InternalError;
                return Failure(kind, ex.Message);
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

    private ManagementPlaybackStartResult CreateCmafStartupTimeoutResult() =>
        Failure(ManagementPlaybackFailureKind.Timeout, $"CMAF startup did not complete within {_cmafStartupTimeout.TotalSeconds:0} seconds.", "cmaf_startup_timeout", "startup");

    private static ManagementPlaybackStartResult Failure(ManagementPlaybackFailureKind kind, string error, string? code = null, string? stage = null) =>
        new() { Failure = new ManagementPlaybackFailure(kind, error, code, stage) };

    private static ManagementPlaybackStartResult CreateSourceAcquisitionResult(StreamAcquisitionException exception) =>
        new() { Failure = new ManagementPlaybackFailure(ManagementPlaybackFailureKind.SourceAcquisition, exception.Message) { Acquisition = exception } };

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

    /// <summary>Releases one viewer alias and its final producer when no viewer remains.</summary>
    public bool Stop(string sessionId) => StopCmafSession(sessionId);

    /// <summary>Reads classified viewer status without extending media ownership.</summary>
    public CmafPlaybackStatus GetPlaybackStatus(string sessionId) =>
        TryParseSessionId(sessionId, out var id) ? _cmafOutputs.GetPlaybackStatus(id) : new(false, "session_unavailable", false);

    /// <summary>Gets immutable artifact metadata and renews the viewer's inactivity lease.</summary>
    public bool TryGetArtifacts(string sessionId, out ManagementPlaybackArtifacts artifacts)
    {
        if (TryGetCmafSession(sessionId, out var session))
        {
            artifacts = new(session.CmafDirectory, session.IsCompatibilityTest, session.SourceVideoCodec, session.AudioRenditions);
            return true;
        }
        artifacts = null!;
        return false;
    }

    /// <summary>Gets current viewer diagnostics without exposing process ownership.</summary>
    public IReadOnlyList<ManagementPlaybackViewer> GetViewers() =>
        _cmafOutputs.GetViewers()
            .Select(viewer => (viewer, session: GetOutputSession(viewer.State)))
            .Where(item => item.session is not null)
            .Select(item => new ManagementPlaybackViewer(
                item.viewer.ViewerSessionId.ToString(), item.session!.Channel, item.session.StartTime,
                (DateTime.UtcNow - item.session.StartTime).TotalMinutes, item.session.Process is { HasExited: false }))
            .ToArray();

    private bool StopCmafSession(string sessionId) => Task.Run(() => StopCmafSessionAsync(sessionId)).GetAwaiter().GetResult();

    private async Task<bool> StopCmafSessionAsync(string sessionId) =>
        TryParseSessionId(sessionId, out var parsed) && await _cmafOutputs.ReleaseViewerAsync(parsed);

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
                try
                {
                    session.Process.Kill(entireProcessTree: false);
                    session.Process.WaitForExit(2000);

                    if (!session.Process.HasExited)
                    {
                        session.Process.Kill(entireProcessTree: true);
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
                {
                    _logger.LogDebug(ex, "Graceful FFmpeg shutdown failed for CMAF session {SessionId}; forcing termination", session.SessionId);

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
            session.Process?.WaitForExit();
            if (session.Process is not null)
            {
                MediaProcessAdmission.WaitForReleaseAsync(session.Process).GetAwaiter().GetResult();
            }
            session.Process?.Dispose();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            _logger.LogDebug(ex, "Unable to dispose FFmpeg process for CMAF session {SessionId}", session.SessionId);
        }
        finally
        {
            StopCaptionProcess(session);
            session.Tasks.DrainAsync().GetAwaiter().GetResult();
            try
            {
                session.CapacityLease?.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unable to release tuner capacity for CMAF session {SessionId}", session.SessionId);
            }
        }

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
        if (process is { HasExited: true })
        {
            MediaProcessAdmission.WaitForReleaseAsync(process).GetAwaiter().GetResult();
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
                process.WaitForExit();
            }
            if (errorTask?.IsCompletedSuccessfully == true && !string.IsNullOrWhiteSpace(errorTask.Result))
            {
                _logger.LogDebug("Embedded-caption extractor for CMAF session {SessionId} reported: {CaptionError}", sessionId, errorTask.Result.Trim());
            }
            MediaProcessAdmission.WaitForReleaseAsync(process).GetAwaiter().GetResult();
            process.Dispose();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            _logger.LogDebug(ex, "Unable to stop embedded-caption extractor for CMAF session {SessionId}", sessionId);
        }
    }

    private void TryCleanupCompletedCmafSession(Process process, CmafSession session)
    {
        if (!session.IsActive)
        {
            return;
        }
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
                _cmafOutputs.ReleaseOutputAsync(outputId, session.Tasks.SourceTerminationCode).AsTask().GetAwaiter().GetResult();
            }
            else
            {
                CleanupCmafSession(session, stopProcess: false);
            }
        }
    }

    /// <summary>Transfers an already-created synthetic or test producer into coordinator ownership.</summary>
    internal async Task<CmafSession> RegisterCmafSessionAsync(CmafSession session, CancellationToken cancellationToken)
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
        CancellationToken cancellationToken,
        CmafMediaTasks tasks)
    {
        tasks.SourceTerminationCode = null;
        await using var input = await SubscribeEffectiveSourceAsync(candidate, source, settings, cancellationToken);
        await MediaStreamPump.PumpAsync(input, process, _logger, candidate.DiagnosticUri.AbsoluteUri, cancellationToken, code => tasks.SourceTerminationCode = code);
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

    private async Task<ProtectedContentDetection> DetectContentProtectionAsync(string channel, string? tunerError, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(tunerError))
        {
            return ProtectedContentDetector.Detect(tunerError, cachedDrm: false);
        }

        var cachedChannel = (await _guideReader.GetChannelDirectoryAsync(cancellationToken))
            .FirstOrDefault(candidate => string.Equals(candidate.GuideNumber, channel, StringComparison.Ordinal));
        return ProtectedContentDetector.Detect(tunerError, cachedChannel?.DRM == true);
    }

    private static ManagementPlaybackStartResult CreateProtectedContentResult(ProtectedContentDetection protection) =>
        new()
        {
            Failure = new ManagementPlaybackFailure(ManagementPlaybackFailureKind.ProtectedContent, protection.Message ?? "The requested channel is protected and cannot be streamed.")
            {
                Protection = protection
            }
        };

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
        catch (MediaProcessException)
        {
        }
    }

    private Task StartCmafErrorMonitor(Process process, CmafSession session, ConcurrentQueue<string> errors, bool parseSourceMetadata, long outputVideoBitRate)
    {
        process.Exited += (_, _) =>
        {
            try
            {
                TryCleanupCompletedCmafSession(process, session);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Unable to release exited management playback producer {SessionId}", session.SessionId);
            }
        };
        process.EnableRaisingEvents = true;
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

    /// <summary>Owns one producer process and its associated media resources.</summary>
    internal sealed class CmafSession
    {
        /// <summary>
        /// Gets or sets session id.
        /// </summary>
        public required string SessionId { get; init; }
        /// <summary>
        /// Gets or sets channel.
        /// </summary>
        public required string Channel { get; init; }
        /// <summary>Gets whether this session owns synthetic Watch Test output rather than normal plugin media.</summary>
        public bool IsCompatibilityTest { get; init; }
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
        /// <summary>Gets the owned input, caption, and diagnostic tasks that must settle during cleanup.</summary>
        public CmafMediaTasks Tasks { get; init; } = new();
        /// <summary>
        /// Tracks whether the starting request still owns process-exit cleanup.
        /// </summary>
        public int IsStarting = 1;

        /// <summary>Tracks whether the session remains active.</summary>
        public bool IsActive = true;
    }

    private sealed class CmafSessionOwner(ManagementPlaybackService service, CmafSession session) : IAsyncDisposable
    {
        private ManagementPlaybackService? _service = service;

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _service, null)?.CleanupCmafSession(session, stopProcess: true);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Observes media task failures immediately and joins every owned task before artifact cleanup finishes.</summary>
    internal sealed class CmafMediaTasks
    {
        /// <summary>Records only source read termination, never intentional cancellation or a closed processor pipe.</summary>
        public volatile string? SourceTerminationCode;

        private readonly Lock _gate = new();
        private readonly List<Task> _observations = [];
        private readonly List<CancellationTokenSource> _cancellations = [];
        private Task? _drainTask;

        /// <summary>Creates a producer-owned cancellation source retained until all media tasks have settled.</summary>
        public CancellationTokenSource CreateCancellation(CancellationToken lifetimeToken)
        {
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
            lock (_gate)
            {
                _cancellations.Add(cancellation);
            }
            return cancellation;
        }

        /// <summary>Retains and observes one task without changing the original task's failure semantics.</summary>
        public void Track(Task task, ILogger logger, string purpose)
        {
            lock (_gate)
            {
                _observations.RemoveAll(observation => observation.IsCompleted);
                _observations.Add(ObserveAsync(task, logger, purpose));
            }
        }

        /// <summary>Waits for the terminal state of all owned tasks and releases their cancellation resources.</summary>
        public Task DrainAsync()
        {
            lock (_gate)
            {
                return _drainTask ??= DrainCoreAsync(_observations.ToArray(), _cancellations.ToArray());
            }
        }

        private static async Task DrainCoreAsync(Task[] observations, CancellationTokenSource[] cancellations)
        {
            try
            {
                foreach (var cancellation in cancellations)
                {
                    cancellation.Cancel();
                }
                await Task.WhenAll(observations).ConfigureAwait(false);
            }
            finally
            {
                foreach (var cancellation in cancellations)
                {
                    cancellation.Dispose();
                }
            }
        }

        private static async Task ObserveAsync(Task task, ILogger logger, string purpose)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Owned management playback {Purpose} task failed", purpose);
            }
        }
    }

    /// <summary>Owns startup independently from any viewer cancellation token.</summary>
    internal sealed class PendingCmafOutput : IAsyncDisposable
    {
        private readonly TaskCompletionSource<ManagementPlaybackStartResult> _response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Lock _gate = new();
        private readonly CancellationTokenSource _lifetimeCancellation = new();
        private readonly CancellationToken _lifetimeToken;
        private ManagementPlaybackService? _service;
        private Process? _process;
        private Process? _captionProcess;
        private Task<string>? _captionErrorTask;
        private string? _directory;
        private bool _starting;
        private bool _resourcesDisposed;

        /// <summary>Owns pending startup artifacts independently of the initiating viewer.</summary>
        public PendingCmafOutput(ManagementPlaybackService service, string directory)
        {
            _service = service;
            _directory = directory;
            _lifetimeToken = _lifetimeCancellation.Token;
        }

        /// <summary>Gets the producer's terminal startup result shared by all viewers.</summary>
        public Task<ManagementPlaybackStartResult> Response => _response.Task;

        /// <summary>Gets completion after the producer's processes and output artifacts have been released.</summary>
        public Task Stopped => _stopped.Task;

        /// <summary>Gets cancellation when the coordinator releases the producer.</summary>
        public CancellationToken LifetimeToken => _lifetimeToken;

        /// <summary>Gets the completed media session once readiness succeeds.</summary>
        public CmafSession? Session { get; private set; }

        /// <summary>Gets tasks owned by this producer before and after readiness.</summary>
        public CmafMediaTasks Tasks { get; } = new();

        /// <summary>Gets the producer-owned startup task, retained until its terminal state.</summary>
        public Task StartupCompletion { get; private set; } = Task.CompletedTask;

        /// <summary>Starts readiness without transferring ownership to an individual viewer.</summary>
        public void Start(Func<CancellationToken, Task<ManagementPlaybackStartResult>> start, TimeSpan timeout, StreamSessionId outputId) =>
            StartupCompletion = RunAsync(start, timeout, outputId);

        /// <summary>Runs bounded readiness and propagates its terminal result without using an individual viewer's lifetime.</summary>
        private async Task RunAsync(Func<CancellationToken, Task<ManagementPlaybackStartResult>> start, TimeSpan timeout, StreamSessionId outputId)
        {
            using var operation = Lineup.Core.Extensibility.Diagnostics.LineupTelemetry.Begin("media_readiness");
            ManagementPlaybackService owner;
            lock (_gate)
            {
                if (_service is not { } service)
                {
                    return;
                }
                owner = service;
                _starting = true;
            }
            try
            {
                using var startupCancellation = CancellationTokenSource.CreateLinkedTokenSource(LifetimeToken);
                startupCancellation.CancelAfter(timeout);
                var result = await start(startupCancellation.Token);
                if (result.Failure is null)
                {
                    operation.Succeed();
                }
                _response.TrySetResult(result);
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
                    disposed = _service is null;
                }
                try
                {
                    if (disposed)
                    {
                        DisposeResources(owner);
                    }
                    else if (Session is null)
                    {
                        await owner._cmafOutputs.ReleaseOutputAsync(outputId);
                    }
                }
                catch (Exception exception)
                {
                    owner._logger.LogError(exception, "Unable to complete shared CMAF producer startup cleanup for output {OutputId}", outputId);
                    _stopped.TrySetException(exception);
                    _ = _stopped.Task.Exception;
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
            ManagementPlaybackService owner;
            lock (_gate)
            {
                if (_service is not { } service)
                {
                    return ValueTask.CompletedTask;
                }
                owner = service;
                _service = null;
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

        private void DisposeResources(ManagementPlaybackService owner)
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
                    owner.CleanupIncompleteCmafOutput(process, captionProcess, captionErrorTask, null);
                    Tasks.DrainAsync().GetAwaiter().GetResult();
                    if (directory is not null)
                    {
                        owner.TryDeleteTransientDirectory(directory);
                    }
                }
            }
            catch (Exception exception)
            {
                owner._logger.LogError(exception, "Unable to clean up shared CMAF producer resources");
                _stopped.TrySetException(exception);
                _ = _stopped.Task.Exception;
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
