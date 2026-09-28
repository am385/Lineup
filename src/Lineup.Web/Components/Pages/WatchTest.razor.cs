using Lineup.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using System.Text.Json;

namespace Lineup.Web.Components.Pages;

/// <summary>
/// Provides deterministic browser CMAF codec compatibility tests.
/// </summary>
public partial class WatchTest : IAsyncDisposable
{
    [Inject]
    private IJSRuntime JS { get; set; } = default!;

    [Inject]
    private IBrowserDataStore BrowserData { get; set; } = default!;

    [Inject]
    private IStatusNotificationService Notifications { get; set; } = default!;

    [Inject]
    private ILogger<WatchTest> Logger { get; set; } = default!;

    private CmafProtocol _protocol = CmafProtocol.Dash;
    private WebPlayerQuality _quality = WebPlayerQuality.AppDefault;
    private CmafTestVideoCodec _videoCodec = CmafTestVideoCodec.H264;
    private CmafTestVideoProfile _videoProfile = CmafTestVideoProfile.Main;
    private CmafTestAudioCodec _audioCodec = CmafTestAudioCodec.Aac;
    private CmafTestChannelLayout _channelLayout = CmafTestChannelLayout.Stereo;
    private CmafTestSubtitleMode _subtitleMode;
    private CmafCompatibilityTestResponse? _session;
    private CmafTestPlayerResult? _playerResult;
    private CmafTestRuntimeError? _runtimeError;
    private CmafTestCapabilities? _capabilities;
    private CmafCompatibilityProfile? _storedProfile;
    private readonly List<CmafCapabilityResult> _runResults = [];
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private CancellationTokenSource? _runCancellation;
    private TaskCompletionSource<CmafTestPlayerResult>? _pendingPlayerResult;
    private CmafCapabilityTestCase? _currentCase;
    private DotNetObjectReference<WatchTest>? _dotNetReference;
    private string? _errorMessage;
    private bool _isStarting;
    private bool _isRunningAll;
    private bool _isRunningCase;
    private bool _needsPlayerInit;
    private bool _isDisposed;

    private bool CanEncodeSelectedAudio => CmafCompatibilityTestPlanner.IsEncoderAvailable(_audioCodec);
    private bool IsAutomatedTestBusy => _isRunningAll || _isRunningCase;
    private bool IsAnyTestBusy => _isStarting || IsAutomatedTestBusy;
    private IReadOnlyList<CmafTestChannelLayout> SupportedLayouts => CmafCompatibilityTestPlanner.GetSupportedLayouts(_audioCodec);

    /// <summary>Reads browser-reported codec capabilities after the page is rendered.</summary>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            try
            {
                _capabilities = await JS.InvokeAsync<CmafTestCapabilities>("getCmafTestCapabilities");
                var stored = await BrowserData.ReadAsync<CmafCompatibilityProfile>(CmafCompatibilityProfile.StorageKey);
                if (CmafCompatibilityProfilePolicy.IsValid(stored) &&
                    string.Equals(stored!.BrowserIdentity, _capabilities.BrowserIdentity, StringComparison.Ordinal))
                {
                    _storedProfile = stored;
                    _runResults.AddRange(stored.Results);
                }
            }
            catch (Exception ex) when (ex is JSException or JsonException or InvalidOperationException)
            {
                _errorMessage = $"Unable to read browser codec capabilities: {ex.Message}";
                Logger.LogWarning(ex, "Unable to read Watch Test browser codec capabilities");
            }
            StateHasChanged();
        }

        if (!_needsPlayerInit || _session is null)
        {
            return;
        }

        _needsPlayerInit = false;
        _dotNetReference ??= DotNetObjectReference.Create(this);
        var initializingSessionId = _session.SessionId;
        var pendingPlayerResult = _pendingPlayerResult;
        try
        {
            var playerResult = await JS.InvokeAsync<CmafTestPlayerResult>(
                "initCmafTestPlayer",
                "cmafTestVideoPlayer",
                _session.ManifestUrl,
                _session.VideoCodec,
                _session.AudioCodec,
                _session.SubtitleUrl,
                _dotNetReference);
            if (!string.Equals(_session?.SessionId, initializingSessionId, StringComparison.Ordinal))
            {
                return;
            }

            _playerResult = playerResult;
            pendingPlayerResult?.TrySetResult(playerResult);
            if (!playerResult.Success)
            {
                _errorMessage = FormatPlayerError(playerResult);
            }
        }
        catch (JSException ex)
        {
            if (!string.Equals(_session?.SessionId, initializingSessionId, StringComparison.Ordinal))
            {
                return;
            }

            _errorMessage = ex.Message;
            Logger.LogWarning(ex, "Unable to initialize the Watch Test player");
            pendingPlayerResult?.TrySetResult(new(false, false, Error: ex.Message));
        }
        StateHasChanged();
    }

    private async Task StartTestAsync()
    {
        if (IsAnyTestBusy || !CanEncodeSelectedAudio)
        {
            return;
        }

        _isStarting = true;
        _errorMessage = null;
        _playerResult = null;
        _runtimeError = null;
        await StopSessionAsync();
        try
        {
            var startUrl = BuildStartUrl(new CmafCompatibilityTestRequest
            {
                Protocol = _protocol,
                Quality = _quality,
                VideoCodec = _videoCodec,
                VideoProfile = _videoProfile,
                AudioCodec = _audioCodec,
                ChannelLayout = _channelLayout,
                SubtitleMode = _subtitleMode
            });
            _session = await JS.InvokeAsync<CmafCompatibilityTestResponse>("startCmafSession", startUrl);
            _needsPlayerInit = true;
        }
        catch (JSException ex)
        {
            _errorMessage = ex.Message;
            Logger.LogWarning(ex, "Unable to start Watch Test presentation");
        }
        finally
        {
            _isStarting = false;
        }
    }

    private async Task StopTestAsync()
    {
        await StopSessionAsync();
        StateHasChanged();
    }

    private async Task RunAllAsync()
    {
        if (IsAnyTestBusy || _capabilities is null || _session is not null)
        {
            return;
        }

        _isRunningAll = true;
        _runResults.Clear();
        _runCancellation = new CancellationTokenSource();
        _errorMessage = null;
        try
        {
            foreach (var test in CmafCompatibilityTestCatalog.All)
            {
                _runCancellation.Token.ThrowIfCancellationRequested();
                _currentCase = test;
                UpsertResult(await ExecuteCaseAsync(test, _runCancellation.Token));
                StateHasChanged();
            }

            await SaveCompleteProfileAsync(_runCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            RestoreStoredResults();
            Notifications.ShowSuccess("Compatibility run cancelled. The previous completed profile was kept.");
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException or JsonException)
        {
            RestoreStoredResults();
            _errorMessage = ex.Message;
            Logger.LogWarning(ex, "Unable to complete the automated Watch Test suite");
        }
        finally
        {
            try
            {
                await StopSessionAsync();
            }
            catch (JSException ex)
            {
                Logger.LogWarning(ex, "Unable to stop the automated Watch Test session");
            }
            finally
            {
                _currentCase = null;
                _isRunningAll = false;
                _runCancellation.Dispose();
                _runCancellation = null;
                StateHasChanged();
            }
        }
    }

    private async Task RunSingleCaseAsync(CmafCapabilityTestCase test)
    {
        if (IsAnyTestBusy || _capabilities is null || _session is not null)
        {
            return;
        }

        _isRunningCase = true;
        _currentCase = test;
        _errorMessage = null;
        try
        {
            UpsertResult(await ExecuteCaseAsync(test, _lifetimeCancellation.Token));
            if (HasCompleteResultSet())
            {
                await SaveCompleteProfileAsync(_lifetimeCancellation.Token);
            }
            else
            {
                Notifications.ShowSuccess($"{test.Label} completed. The profile will be saved after every test has a result.");
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException or JsonException)
        {
            _errorMessage = ex.Message;
            Logger.LogWarning(ex, "Unable to complete Watch Test case {CaseId}", test.Id);
        }
        finally
        {
            try
            {
                await StopSessionAsync(clearPlayerResult: false);
            }
            catch (JSException ex)
            {
                Logger.LogWarning(ex, "Unable to stop Watch Test case {CaseId}", test.Id);
            }
            finally
            {
                _currentCase = null;
                _isRunningCase = false;
                if (!_isDisposed)
                {
                    StateHasChanged();
                }
            }
        }
    }

    private async Task<CmafCapabilityResult> ExecuteCaseAsync(CmafCapabilityTestCase test, CancellationToken cancellationToken)
    {
        if (test.IsUnavailable)
        {
            return new CmafCapabilityResult
            {
                CaseId = test.Id,
                Kind = test.Kind,
                Request = test.Request,
                Status = CmafCapabilityStatus.Unavailable,
                Error = "No deterministic encoder is available."
            };
        }

        var result = await RunCaseAsync(test, cancellationToken);
        var passed = IsCaseSuccessful(test, result, _session);
        return new CmafCapabilityResult
        {
            CaseId = test.Id,
            Kind = test.Kind,
            Request = test.Request,
            Status = passed ? CmafCapabilityStatus.Passed : CmafCapabilityStatus.Failed,
            VideoCodec = result.Video?.Codec,
            AudioCodec = result.Audio?.Codec,
            AudioChannels = result.Audio?.Channels,
            SubtitleCueVisible = result.SubtitleCueVisible,
            Error = passed ? null : FormatPlayerError(result)
        };
    }

    private void UpsertResult(CmafCapabilityResult result)
    {
        var index = _runResults.FindIndex(existing => string.Equals(existing.CaseId, result.CaseId, StringComparison.Ordinal));
        if (index >= 0)
        {
            _runResults[index] = result;
        }
        else
        {
            _runResults.Add(result);
        }
    }

    private CmafCapabilityResult? GetResult(string caseId) =>
        _runResults.FirstOrDefault(result => string.Equals(result.CaseId, caseId, StringComparison.Ordinal));

    private bool HasCompleteResultSet() =>
        _runResults.Count == CmafCompatibilityTestCatalog.All.Count &&
        CmafCompatibilityTestCatalog.All.All(test => GetResult(test.Id) is not null);

    private async Task SaveCompleteProfileAsync(CancellationToken cancellationToken)
    {
        if (_capabilities is null || !HasCompleteResultSet())
        {
            throw new InvalidOperationException("A browser compatibility profile cannot be saved until every catalog test has a result.");
        }

        var profile = new CmafCompatibilityProfile
        {
            BrowserIdentity = _capabilities.BrowserIdentity,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            Claims = _capabilities.ToClaims(),
            Results = CmafCompatibilityTestCatalog.All.Select(test => GetResult(test.Id)!).ToArray()
        };
        if (!CmafCompatibilityProfilePolicy.IsValid(profile))
        {
            throw new InvalidOperationException("The completed browser compatibility profile is invalid.");
        }

        await BrowserData.WriteAsync(CmafCompatibilityProfile.StorageKey, profile, cancellationToken);
        _storedProfile = profile;
        Notifications.ShowSuccess("Browser compatibility profile saved. Watch CMAF can now optimize streams automatically.");
    }

    private void RestoreStoredResults()
    {
        _runResults.Clear();
        if (_storedProfile is not null)
        {
            _runResults.AddRange(_storedProfile.Results);
        }
    }

    private void CancelRun() => _runCancellation?.Cancel();

    private async Task<CmafTestPlayerResult> RunCaseAsync(CmafCapabilityTestCase test, CancellationToken cancellationToken)
    {
        await StopSessionAsync();
        _protocol = test.Request.Protocol;
        _quality = test.Request.Quality;
        _videoCodec = test.Request.VideoCodec;
        _videoProfile = test.Request.VideoProfile;
        _audioCodec = test.Request.AudioCodec;
        _channelLayout = test.Request.ChannelLayout;
        _subtitleMode = test.Request.SubtitleMode;
        _runtimeError = null;
        _playerResult = null;
        var pendingPlayerResult = new TaskCompletionSource<CmafTestPlayerResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingPlayerResult = pendingPlayerResult;
        _session = await JS.InvokeAsync<CmafCompatibilityTestResponse>("startCmafSession", cancellationToken, BuildStartUrl(test.Request));
        _needsPlayerInit = true;
        await InvokeAsync(StateHasChanged);
        try
        {
            return await pendingPlayerResult.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            if (ReferenceEquals(_pendingPlayerResult, pendingPlayerResult))
            {
                _pendingPlayerResult = null;
            }
        }
    }

    private static string BuildStartUrl(CmafCompatibilityTestRequest request) =>
        "/api/stream/cmaf/test/start" +
        $"?protocol={Uri.EscapeDataString(request.Protocol.ToString())}" +
        $"&quality={Uri.EscapeDataString(request.Quality.ToString())}" +
        $"&videoCodec={Uri.EscapeDataString(request.VideoCodec.ToString())}" +
        $"&videoProfile={Uri.EscapeDataString(request.VideoProfile.ToString())}" +
        $"&audioCodec={Uri.EscapeDataString(request.AudioCodec.ToString())}" +
        $"&channelLayout={Uri.EscapeDataString(request.ChannelLayout.ToString())}" +
        $"&subtitleMode={Uri.EscapeDataString(request.SubtitleMode.ToString())}";

    private static bool IsCaseSuccessful(CmafCapabilityTestCase test, CmafTestPlayerResult result, CmafCompatibilityTestResponse? session)
    {
        if (!result.Success || !result.PlaybackStarted)
        {
            return false;
        }

        return test.Kind switch
        {
            CmafCapabilityKind.Video => string.Equals(
                result.Video?.Codec,
                session?.VideoCodec,
                StringComparison.OrdinalIgnoreCase),
            CmafCapabilityKind.Audio => string.Equals(
                result.Audio?.Codec,
                session?.AudioCodec,
                StringComparison.OrdinalIgnoreCase) &&
                result.Audio?.Channels == CmafCompatibilityTestPlanner.GetChannelCount(test.Request.ChannelLayout),
            CmafCapabilityKind.SubtitleSidecar => result.SubtitleCueVisible,
            _ => true
        };
    }

    private async Task StopSessionAsync(bool clearPlayerResult = true)
    {
        var sessionId = _session?.SessionId;
        try
        {
            await JS.InvokeVoidAsync("stopMediaPlayer", "cmafTestVideoPlayer");
        }
        finally
        {
            try
            {
                if (sessionId is not null)
                {
                    await JS.InvokeVoidAsync("stopCmafSession", sessionId);
                }
            }
            finally
            {
                _session = null;
                if (clearPlayerResult)
                {
                    _playerResult = null;
                }
                _needsPlayerInit = false;
            }
        }
    }

    /// <summary>Records a Shaka playback error without applying any fallback.</summary>
    [JSInvokable]
    public Task OnCmafTestPlayerError(CmafTestRuntimeError error)
    {
        _runtimeError = error;
        _errorMessage = FormatRuntimeError(error);
        if (_playerResult is not null)
        {
            _playerResult = _playerResult with { PlaybackStarted = false };
            _pendingPlayerResult?.TrySetResult(_playerResult);
        }
        StateHasChanged();
        return Task.CompletedTask;
    }

    private void ChangeAudioCodec(ChangeEventArgs args)
    {
        if (!Enum.TryParse<CmafTestAudioCodec>(args.Value?.ToString(), out var value))
        {
            return;
        }

        _audioCodec = value;
        var layouts = SupportedLayouts;
        if (layouts.Count > 0 && !layouts.Contains(_channelLayout))
        {
            _channelLayout = layouts[^1];
        }
    }

    private void ChangeVideoCodec(ChangeEventArgs args)
    {
        if (!Enum.TryParse<CmafTestVideoCodec>(args.Value?.ToString(), out var value))
        {
            return;
        }

        _videoCodec = value;
        if (value == CmafTestVideoCodec.H264)
        {
            _videoProfile = CmafTestVideoProfile.Main;
        }
    }

    private static string FormatLayout(CmafTestChannelLayout layout) =>
        layout switch
        {
            CmafTestChannelLayout.Mono => "Mono (1.0)",
            CmafTestChannelLayout.Stereo => "Stereo (2.0)",
            CmafTestChannelLayout.Surround3Point0 => "3.0",
            CmafTestChannelLayout.Quad => "Quad (4.0)",
            CmafTestChannelLayout.Surround5Point0 => "5.0",
            CmafTestChannelLayout.Surround5Point1 => "5.1",
            CmafTestChannelLayout.Surround7Point1 => "7.1",
            _ => layout.ToString()
        };

    private string SelectedChannelSequence => string.Join(" → ", CmafCompatibilityTestPlanner.GetChannelLabels(_channelLayout));

    private static string FormatClaim(bool? claim) =>
        claim switch
        {
            true => "Supported",
            false => "Not supported",
            null => "No claim"
        };

    private static string FormatAudio(CmafTestPlayerAudio? audio) =>
        audio is null
            ? "Not reported"
            : string.Join(" · ", new[] { audio.Label, audio.Codec, audio.Channels.HasValue ? $"{audio.Channels} ch" : null }.Where(value => !string.IsNullOrWhiteSpace(value)));

    private static string FormatVideo(CmafTestPlayerVideo? video) =>
        video is null
            ? "Not reported"
            : string.Join(" · ", new[] { video.Codec, video.Width.HasValue && video.Height.HasValue ? $"{video.Width}x{video.Height}" : null }.Where(value => !string.IsNullOrWhiteSpace(value)));

    private static string FormatPlayerError(CmafTestPlayerResult result)
    {
        var details = result.ErrorDetails.HasValue ? Environment.NewLine + result.ErrorDetails.Value.GetRawText() : string.Empty;
        return $"{result.Error ?? "Playback failed."}{(result.ErrorCode.HasValue ? $" (Shaka {result.ErrorCode})" : string.Empty)}{details}";
    }

    private static string FormatRuntimeError(CmafTestRuntimeError error)
    {
        var details = error.Details.HasValue ? Environment.NewLine + error.Details.Value.GetRawText() : string.Empty;
        return $"{error.Message}{(error.ErrorCode.HasValue ? $" (Shaka {error.ErrorCode})" : string.Empty)}{details}";
    }

    /// <summary>Stops the synthetic session and browser player when navigation disposes the page.</summary>
    public async ValueTask DisposeAsync()
    {
        _isDisposed = true;
        _lifetimeCancellation.Cancel();
        _runCancellation?.Cancel();
        _pendingPlayerResult?.TrySetCanceled(_lifetimeCancellation.Token);
        try
        {
            await StopSessionAsync();
        }
        catch (Exception ex) when (ex is JSDisconnectedException or InvalidOperationException)
        {
            Logger.LogDebug(ex, "Watch Test browser session was already disconnected during disposal");
        }
        _lifetimeCancellation.Dispose();
        _dotNetReference?.Dispose();
    }

    /// <summary>Describes browser-reported support before a playback test.</summary>
    public sealed record CmafTestCapabilities(
        bool ShakaSupported,
        bool MediaSourceAvailable,
        string? NativeHls,
        bool? H264,
        bool? Hevc,
        bool? Aac,
        bool? Ac3,
        bool? Eac3,
        bool? Ac4,
        string BrowserIdentity = "unknown",
        bool? HevcMain10 = null)
    {
        /// <summary>Converts browser declarations into the persisted diagnostic contract.</summary>
        public CmafBrowserClaims ToClaims() =>
            new()
            {
                ShakaSupported = ShakaSupported,
                MediaSourceAvailable = MediaSourceAvailable,
                NativeHls = NativeHls,
                H264 = H264,
                Hevc = Hevc,
                HevcMain10 = HevcMain10,
                Aac = Aac,
                Ac3 = Ac3,
                Eac3 = Eac3,
                Ac4 = Ac4
            };
    }

    /// <summary>Describes one active audio rendition reported by Shaka.</summary>
    public sealed record CmafTestPlayerAudio(string? Label, string? Codec, int? Channels);

    /// <summary>Describes one active video rendition reported by Shaka.</summary>
    public sealed record CmafTestPlayerVideo(string? Codec, int? Width, int? Height);

    /// <summary>Describes an asynchronous Shaka playback error.</summary>
    public sealed record CmafTestRuntimeError(string Message, int? ErrorCode = null, JsonElement? Details = null);

    /// <summary>Describes the exact no-fallback Shaka playback result.</summary>
    public sealed record CmafTestPlayerResult(
        bool Success,
        bool PlaybackStarted,
        string? ManifestUrl = null,
        bool? VideoSupported = null,
        bool? AudioSupported = null,
        CmafTestPlayerAudio? Audio = null,
        CmafTestPlayerVideo? Video = null,
        string? Error = null,
        int? ErrorCode = null,
        JsonElement? ErrorDetails = null,
        bool SubtitleCueVisible = false);
}
