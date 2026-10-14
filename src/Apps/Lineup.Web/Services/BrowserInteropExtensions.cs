using Microsoft.JSInterop;

namespace Lineup.Web.Services;

/// <summary>
/// Provides typed entry points for Lineup browser behavior.
/// </summary>
internal static class BrowserInteropExtensions
{
    /// <summary>
    /// Applies and persists the selected browser theme.
    /// </summary>
    public static ValueTask SetThemeAsync(this IJSRuntime js, string theme) =>
        js.InvokeVoidAsync("setTheme", theme);

    /// <summary>
    /// Starts monitoring the current Lineup process for a restart.
    /// </summary>
    public static ValueTask BeginLineupRestartWatchAsync(this IJSRuntime js, string destination) =>
        js.InvokeVoidAsync("beginLineupRestartWatch", destination);

    /// <summary>
    /// Starts monitoring the current Lineup process for a factory reset.
    /// </summary>
    public static ValueTask BeginLineupFactoryResetWatchAsync(this IJSRuntime js) =>
        js.InvokeVoidAsync("beginLineupFactoryResetWatch");

    /// <summary>
    /// Downloads text content as a browser file.
    /// </summary>
    public static ValueTask DownloadTextFileAsync(this IJSRuntime js, string fileName, string contentType, string content) =>
        js.InvokeVoidAsync("downloadTextFile", fileName, contentType, content);

    /// <summary>
    /// Copies text to the browser clipboard.
    /// </summary>
    public static ValueTask CopyTextAsync(this IJSRuntime js, string content) =>
        js.InvokeVoidAsync("copyTextToClipboard", content);

    /// <summary>
    /// Gets the available guide viewport width.
    /// </summary>
    public static ValueTask<int> GetGuideViewportWidthAsync(this IJSRuntime js) =>
        js.InvokeAsync<int>("getGuideViewportWidth");

    /// <summary>
    /// Starts observing guide viewport size changes.
    /// </summary>
    public static ValueTask SetupGuideResizeObserverAsync<T>(this IJSRuntime js, DotNetObjectReference<T> callbackReference)
        where T : class =>
        js.InvokeVoidAsync("setupGuideResizeObserver", callbackReference);

    /// <summary>
    /// Stops observing guide viewport size changes.
    /// </summary>
    public static ValueTask DisposeGuideResizeObserverAsync(this IJSRuntime js) =>
        js.InvokeVoidAsync("disposeGuideResizeObserver");

    /// <summary>
    /// Gets browser CMAF compatibility capabilities.
    /// </summary>
    public static ValueTask<TResult> GetCmafTestCapabilitiesAsync<TResult>(this IJSRuntime js) =>
        js.InvokeAsync<TResult>("getCmafTestCapabilities");

    /// <summary>
    /// Gets the stable browser identity used by saved compatibility profiles.
    /// </summary>
    public static ValueTask<string> GetCmafBrowserIdentityAsync(this IJSRuntime js) =>
        js.InvokeAsync<string>("getCmafBrowserIdentity");

    /// <summary>
    /// Initializes live CMAF playback.
    /// </summary>
    public static ValueTask<TResult> InitializeCmafPlayerAsync<TResult, TCallback>(
        this IJSRuntime js,
        string videoElementId,
        string manifestUrl,
        string? protocolFallbackUrl,
        string preferredAudio,
        string preferredVideo,
        bool hasSourceVideoRendition,
        string? sourceVideoCodec,
        string? fallbackAudioCodec,
        object subtitles,
        DotNetObjectReference<TCallback> callbackReference,
        string sessionId)
        where TCallback : class =>
        js.InvokeAsync<TResult>(
            "initCmafPlayer",
            videoElementId,
            manifestUrl,
            protocolFallbackUrl,
            preferredAudio,
            preferredVideo,
            hasSourceVideoRendition,
            sourceVideoCodec,
            fallbackAudioCodec,
            subtitles,
            callbackReference,
            sessionId);

    /// <summary>
    /// Initializes exact-presentation CMAF compatibility playback.
    /// </summary>
    public static ValueTask<TResult> InitializeCmafTestPlayerAsync<TResult, TCallback>(
        this IJSRuntime js,
        string videoElementId,
        string manifestUrl,
        string? expectedVideoCodec,
        string? expectedAudioCodec,
        string? subtitleUrl,
        DotNetObjectReference<TCallback> callbackReference,
        string sessionId)
        where TCallback : class =>
        js.InvokeAsync<TResult>(
            "initCmafTestPlayer",
            videoElementId,
            manifestUrl,
            expectedVideoCodec,
            expectedAudioCodec,
            subtitleUrl,
            callbackReference,
            sessionId);

    /// <summary>
    /// Starts a browser-owned CMAF session request.
    /// </summary>
    public static ValueTask<TResult> StartCmafSessionAsync<TResult>(this IJSRuntime js, string startUrl, object? requestBody, string startupId) =>
        js.InvokeAsync<TResult>("startCmafSession", startUrl, requestBody, startupId);

    /// <summary>
    /// Cancels an in-flight CMAF session request.
    /// </summary>
    public static ValueTask CancelCmafStartAsync(this IJSRuntime js, string startupId) =>
        js.InvokeVoidAsync("cancelCmafStart", startupId);

    /// <summary>
    /// Stops the browser media player associated with an element.
    /// </summary>
    public static ValueTask StopMediaPlayerAsync(this IJSRuntime js, string videoElementId) =>
        js.InvokeVoidAsync("stopMediaPlayer", videoElementId);

    /// <summary>
    /// Stops a server CMAF session.
    /// </summary>
    public static ValueTask StopCmafSessionAsync(this IJSRuntime js, string sessionId, string? stopUrl = null) =>
        stopUrl is null
            ? js.InvokeVoidAsync("stopCmafSession", sessionId)
            : js.InvokeVoidAsync("stopCmafSession", sessionId, stopUrl);
}
