using Lineup.App.Web.Services;
using Lineup.App.Web.Services.Playback;
using Lineup.Core.VirtualOutputs.Runtime;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Lineup.App.Web.Components.Pages;

/// <summary>Coordinates bounded, independently owned live playback recovery.</summary>
public partial class Watch
{
    [Inject]
    private IServiceProvider Services { get; set; } = default!;

    private PlaybackReconnectPolicy? _reconnectPolicy;
    private CancellationTokenSource? _reconnectCancellation;
    private TaskCompletionSource<bool>? _reconnectReady;
    private CmafStartFailure? _reconnectStartupFailure;
    private VirtualDeviceId? _sessionWorkerId;
    private long? _sessionWorkerGeneration;
    private bool _hasStartedPlayback;
    private bool _checkingPlayback;
    private CmafPlayerPreferences? _reconnectPlayerPreferences;
    private string? _reconnectingSessionId;
    private TimeProvider Clock => Services.GetService<TimeProvider>() ?? TimeProvider.System;
    private PlaybackReconnectPolicy ReconnectPolicy => _reconnectPolicy ??= new(Clock);
    private bool IsReconnecting => _reconnectCancellation is not null;

    private VirtualOutputRuntimeSnapshot? GetWorkerStatus(VirtualDeviceId id) =>
        Services.GetService<IVirtualOutputRuntimeStatus>()?.GetSnapshots().SingleOrDefault(snapshot => snapshot.VirtualDeviceId == id);

    private CmafStartResponse OwnSession(CmafStartResponse session, VirtualDeviceDefinition player) =>
        session with
        {
            StopUrl = player.Hosting is null ? $"/api/stream/cmaf/stop/{Uri.EscapeDataString(session.SessionId)}" : session.StopUrl,
            WorkerId = player.Hosting is null ? null : player.Id,
            WorkerGeneration = player.Hosting is null ? null : session.WorkerGeneration ?? GetWorkerStatus(player.Id)?.Generation
        };

    private async Task<CmafPlaybackStatus?> GetPlaybackStatusAsync(CmafStartResponse session)
    {
        if (_sessionWorkerId is { } workerId)
        {
            if (GetWorkerFailureStatus(GetWorkerStatus(workerId)) is { } failure)
            {
                return failure;
            }
            if (session.StopUrl is { } stopUrl)
            {
                try
                {
                    return await JS.InvokeAsync<CmafPlaybackStatus>("getCmafSessionStatus", stopUrl.Replace("/media/stop/", "/media/status/", StringComparison.Ordinal));
                }
                catch (JSException exception)
                {
                    Logger.LogWarning(exception, "Unable to classify owned playback session {SessionId}", session.SessionId);
                    return GetWorkerFailureStatus(GetWorkerStatus(workerId));
                }
            }
            return null;
        }
        return Services.GetService<ManagementPlaybackService>()?.GetPlaybackStatus(session.SessionId);
    }

    private CmafPlaybackStatus? GetWorkerFailureStatus(VirtualOutputRuntimeSnapshot? worker) =>
        worker?.State is VirtualOutputRuntimeState.Failed or VirtualOutputRuntimeState.Disabled or VirtualOutputRuntimeState.RequiresConfiguration or VirtualOutputRuntimeState.Draining
            ? new(false, "worker_unavailable", false)
            : _sessionWorkerGeneration.HasValue &&
                (worker?.State == VirtualOutputRuntimeState.Starting ||
                worker?.State == VirtualOutputRuntimeState.Healthy && worker.Generation != _sessionWorkerGeneration)
                ? new(false, "worker_lost", true)
                : null;

    private async Task CheckPlaybackAsync()
    {
        if (!_hasStartedPlayback || IsReconnecting || _checkingPlayback || _session is not { } session)
        {
            return;
        }
        _checkingPlayback = true;
        try
        {
            var status = await GetPlaybackStatusAsync(session);
            if (_disposed || !ReferenceEquals(_session, session) || status is null || status.IsActive)
            {
                return;
            }
            if (!await TryReconnectAsync(session, status))
            {
                _errorMessage = Text["Watch_JsSessionUnavailable"];
                await StopStreamAsync();
            }
        }
        catch (JSException exception)
        {
            Logger.LogWarning(exception, "Unable to classify owned playback session {SessionId}", session.SessionId);
        }
        finally
        {
            _checkingPlayback = false;
        }
    }

    private async Task<bool> TryReconnectAsync(CmafStartResponse session, CmafPlaybackStatus? status = null)
    {
        if (_disposed || !ReferenceEquals(_session, session) || IsReconnecting)
        {
            return true;
        }
        if (!_hasStartedPlayback || _selectedChannelNumber is not { } channel)
        {
            return false;
        }
        if (status is null)
        {
            status = await GetPlaybackStatusAsync(session);
        }
        if (_disposed || !ReferenceEquals(_session, session) || IsReconnecting)
        {
            return true;
        }
        if (status is not { IsActive: false, Retryable: true } || status.Code is not ("source_eof" or "source_failure" or "worker_lost"))
        {
            return false;
        }

        ReconnectPolicy.MarkFailed();
        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        _reconnectCancellation = cancellation;
        _reconnectingSessionId = session.SessionId;
        _isPlayerLoading = true;
        _errorMessage = null;
        StateHasChanged();
        var deadline = EnforceReconnectDeadlineAsync(cancellation, token);
        var terminalFailure = false;
        try
        {
            _reconnectPlayerPreferences = await JS.InvokeAsync<CmafPlayerPreferences?>("getCmafPlayerPreferences", "cmafVideoPlayer", session.SessionId);
            token.ThrowIfCancellationRequested();
            await StopStreamAsync(preserveLoadingIndicator: true, preserveReconnect: true);
            while (!token.IsCancellationRequested && ReconnectPolicy.TryNextAttempt(out var delay))
            {
                await Task.Delay(delay, Clock, token);
                var player = GetWebPlayer();
                if (player is null || player.Hosting is not null &&
                    GetWorkerStatus(player.Id)?.State is VirtualOutputRuntimeState.Failed or VirtualOutputRuntimeState.Disabled or VirtualOutputRuntimeState.RequiresConfiguration or VirtualOutputRuntimeState.Draining)
                {
                    _errorMessage = Text["Watch_WebPlayerNotEnabled"];
                    terminalFailure = true;
                    break;
                }
                _reconnectStartupFailure = null;
                var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _reconnectReady = ready;
                await TuneChannelAsync(channel, _selectedChannel, _runtimeFallbackAudioOverride, _runtimeVideoOverride, _runtimeAudioOverride, reconnect: true);
                token.ThrowIfCancellationRequested();
                StateHasChanged();
                if (_session is not null && await ready.Task.WaitAsync(token))
                {
                    return true;
                }
                if (_reconnectStartupFailure?.CanRetry != true)
                {
                    terminalFailure = true;
                    break;
                }
                await StopStreamAsync(preserveLoadingIndicator: true, preserveReconnect: true);
            }
            if (!token.IsCancellationRequested)
            {
                if (!terminalFailure)
                {
                    _errorMessage = Text["Watch_ReconnectExhausted"];
                }
                await StopStreamAsync();
                StateHasChanged();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (JSException exception)
        {
            Logger.LogWarning(exception, "Unable to recover owned playback for channel {Channel}", channel);
            _errorMessage = Text["Watch_UnableToStartPlayback", exception.Message];
            await StopStreamAsync();
            StateHasChanged();
        }
        finally
        {
            if (ReferenceEquals(_reconnectCancellation, cancellation))
            {
                _reconnectCancellation = null;
                _reconnectingSessionId = null;
                _reconnectReady = null;
            }
            await cancellation.CancelAsync();
            await deadline;
            if (!_disposed)
            {
                StateHasChanged();
            }
        }
        return true;
    }

    private async Task EnforceReconnectDeadlineAsync(CancellationTokenSource cancellation, CancellationToken token)
    {
        try
        {
            var remaining = ReconnectPolicy.Remaining;
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining, Clock, token);
            }
            await InvokeAsync(async () =>
            {
                if (ReferenceEquals(_reconnectCancellation, cancellation))
                {
                    _errorMessage = Text["Watch_ReconnectExhausted"];
                    await StopStreamAsync();
                    StateHasChanged();
                }
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    private void CancelReconnect()
    {
        var cancellation = _reconnectCancellation;
        _reconnectCancellation = null;
        _reconnectingSessionId = null;
        cancellation?.Cancel();
        _reconnectReady?.TrySetCanceled();
        _reconnectReady = null;
        _reconnectPolicy?.Reset();
        _hasStartedPlayback = false;
        _reconnectPlayerPreferences = null;
    }

    /// <summary>Describes a structured failure from an automatic fresh-session attempt.</summary>
    public sealed record CmafStartFailure(string Error, string? Code, bool Retryable)
    {
        /// <summary>Allows only explicit transient source failures, never arbitrary HTTP or configuration errors.</summary>
        internal bool CanRetry => Retryable && Code is
            "source_no_signal" or "source_no_video_data" or "source_capacity_exceeded" or "source_acquisition_timeout" or
            "source_unavailable" or "media_source_failed" or "codec_fallback" or "worker_lost";
    }

    /// <summary>Separates an owned fresh session from an explicitly classified startup failure.</summary>
    public sealed record CmafStartAttempt(CmafStartResponse? Session, CmafStartFailure? Failure);

    /// <summary>Retains per-viewer browser selections across a fresh media element and presentation.</summary>
    public sealed record CmafPlayerPreferences(double Volume, bool Muted, string? AudioLabel, string? AudioLanguage, string? SubtitleLabel, string? SubtitleLanguage);
}
