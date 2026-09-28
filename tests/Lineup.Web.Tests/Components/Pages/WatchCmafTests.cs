using Bunit;
using Lineup.Core;
using Lineup.Core.Storage;
using Lineup.HDHomeRun.Api.Models;
using Lineup.HDHomeRun.Device.Models;
using Lineup.HDHomeRun.Device.Protocol;
using Lineup.Web.Components.Pages;
using Lineup.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using NSubstitute;
using System.Reflection;
using System.Text.Json;
using Xunit;

namespace Lineup.Web.Tests.Components.Pages;

/// <summary>
/// Verifies the shared CMAF Watch page lifecycle and independent preferences.
/// </summary>
public class WatchCmafTests
{
    private const string PreferencesStorageKey = "lineup-watch-cmaf-preferences-v1";

    /// <summary>
    /// Verifies the player area communicates that a CMAF stream is being prepared.
    /// </summary>
    [Fact]
    public void StreamStartup_ShowsAccessibleLoadingOverlay()
    {
        // Arrange
        using var context = CreateContext();
        var component = context.Render<WatchCmaf>();
        var loadingField = typeof(WatchCmaf).GetField("_isPlayerLoading", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(loadingField);

        // Act
        loadingField.SetValue(component.Instance, true);
        component.Render();

        // Assert
        var loading = component.Find("#cmafStreamLoading");
        Assert.Equal("status", loading.GetAttribute("role"));
        Assert.Equal("polite", loading.GetAttribute("aria-live"));
        Assert.Contains("Setting up CMAF stream", loading.TextContent, StringComparison.Ordinal);
        Assert.Single(loading.QuerySelectorAll(".spinner-border"));
        Assert.DoesNotContain("Select a channel to start CMAF playback", component.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies manual stream settings are hidden by default and reveal Browser profile choices when enabled.
    /// </summary>
    [Fact]
    public void StreamOverrides_DefaultHiddenAndRevealBrowserProfileChoices()
    {
        // Arrange
        using var context = CreateContext();
        var component = context.Render<WatchCmaf>();
        var initiallyHidden = component.Find("#cmafOverrideSettings").ClassList.Contains("d-none");

        // Act
        component.Find("#cmafOverrideStreamSettings").Change(true);

        // Assert
        Assert.True(initiallyHidden);
        component.WaitForAssertion(() =>
        {
            Assert.DoesNotContain("d-none", component.Find("#cmafOverrideSettings").ClassList);
            Assert.Equal("Browser profile", component.Find("#cmafPreferredVideo option").TextContent);
            Assert.Equal("Browser profile", component.Find("#cmafPreferredAudio option").TextContent);
            var storageWrite = Assert.Single(
                context.JSInterop.Invocations,
                invocation => invocation.Identifier == "localStorage.setItem" && Equals(invocation.Arguments[0], PreferencesStorageKey));
            Assert.Contains("\"Overrides\":{\"Enabled\":true", Assert.IsType<string>(storageWrite.Arguments[1]), StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// Verifies an audio-only UI override keeps video on the measured browser profile in the typed request.
    /// </summary>
    [Fact]
    public void StreamOverrides_AudioOnly_SendsMeasuredProfileWithVideoPreserved()
    {
        // Arrange
        var profile = CreateCompatibilityProfile();
        using var context = CreateContext();
        context.JSInterop.Setup<string?>("localStorage.getItem", CmafCompatibilityProfile.StorageKey).SetResult(JsonSerializer.Serialize(profile));
        context.JSInterop.Setup<string>("getCmafBrowserIdentity").SetResult("test-browser");
        ConfigureSuccessfulSession(context);
        var component = context.Render<WatchCmaf>(parameters => parameters.Add(page => page.ChannelNumber, "42.1"));
        component.WaitForAssertion(() => Assert.NotNull(component.Find("#cmafVideoPlayer")));

        // Act
        component.Find("#cmafOverrideStreamSettings").Change(true);
        component.Find("#cmafPreferredAudio").Change(CmafPreferredAudio.Fallback.ToString());

        // Assert
        component.WaitForAssertion(() =>
        {
            var start = context.JSInterop.Invocations.Last(invocation => invocation.Identifier == "startCmafSession");
            var request = Assert.IsType<CmafStreamRequest>(start.Arguments[1]);
            Assert.True(request.Overrides.Enabled);
            Assert.Equal(CmafPreferredAudio.Fallback, request.Overrides.Audio);
            Assert.Null(request.Overrides.Video);
            Assert.NotNull(request.CompatibilityProfile);
        });
    }

    /// <summary>
    /// Verifies a completed profile enables the typed Auto tune request.
    /// </summary>
    [Fact]
    public void CompletedProfile_StartsTypedAutomaticSession()
    {
        // Arrange
        var profile = CreateCompatibilityProfile();
        using var context = CreateContext();
        context.JSInterop.Setup<string?>("localStorage.getItem", CmafCompatibilityProfile.StorageKey).SetResult(JsonSerializer.Serialize(profile));
        context.JSInterop.Setup<string>("getCmafBrowserIdentity").SetResult("test-browser");
        ConfigureSuccessfulSession(context);

        // Act
        var component = context.Render<WatchCmaf>(parameters => parameters.Add(page => page.ChannelNumber, "42.1"));

        // Assert
        component.WaitForAssertion(() =>
        {
            var start = Assert.Single(context.JSInterop.Invocations, invocation => invocation.Identifier == "startCmafSession");
            Assert.Equal("/api/stream/cmaf/start-v2/42.1", start.Arguments[0]);
            var request = Assert.IsType<CmafStreamRequest>(start.Arguments[1]);
            Assert.Equal(CmafPreferredVideo.Auto, request.PreferredVideo);
            Assert.Equal(CmafPreferredAudio.Auto, request.PreferredAudio);
            Assert.NotNull(request.CompatibilityProfile);
        });
    }

    /// <summary>
    /// Verifies Auto mode prefers DASH and provides HLS fallback for the same server session.
    /// </summary>
    [Fact]
    public void DirectRoute_AutoModeStartsSharedDashAndCmafSession()
    {
        // Arrange
        using var context = CreateContext();
        ConfigureSuccessfulSession(context);

        // Act
        var component = context.Render<WatchCmaf>(parameters => parameters.Add(page => page.ChannelNumber, "42.1"));

        // Assert
        component.WaitForAssertion(() =>
        {
            Assert.NotNull(component.Find("#cmafVideoPlayer"));
            var start = Assert.Single(context.JSInterop.Invocations, invocation => invocation.Identifier == "startCmafSession");
            var startUrl = Assert.IsType<string>(Assert.Single(start.Arguments));
            Assert.Contains("/api/stream/cmaf/start/42.1?", startUrl, StringComparison.Ordinal);
            Assert.Contains("preferredVideo=Source", startUrl, StringComparison.Ordinal);
            Assert.Contains("preferredAudio=Source", startUrl, StringComparison.Ordinal);
            var initialize = Assert.Single(context.JSInterop.Invocations, invocation => invocation.Identifier == "initCmafPlayer");
            Assert.Equal("/api/stream/cmaf/session-1/manifest.mpd", initialize.Arguments[1]);
            Assert.Equal("/api/stream/cmaf/session-1/master.m3u8", initialize.Arguments[2]);
            Assert.Equal("Source", initialize.Arguments[3]);
            Assert.Equal("Source", initialize.Arguments[4]);
            Assert.Null(initialize.Arguments[5]);
            Assert.Equal("aac", initialize.Arguments[6]);
            var subtitles = Assert.IsAssignableFrom<IReadOnlyList<WatchCmaf.CmafSubtitleResponse>>(initialize.Arguments[7]);
            Assert.Collection(
                subtitles,
                subtitle => Assert.Equal(2, subtitle.SourceIndex),
                subtitle => Assert.Equal(3, subtitle.SourceIndex));
        });
    }

    /// <summary>
    /// Verifies selecting H.264 fallback persists the video preference and restarts the presentation.
    /// </summary>
    [Fact]
    public void PreferredVideoChange_PersistsAndRestartsWithH264Fallback()
    {
        // Arrange
        using var context = CreateContext();
        ConfigureSuccessfulSession(context);
        var component = context.Render<WatchCmaf>(parameters => parameters.Add(page => page.ChannelNumber, "105.1"));
        component.WaitForAssertion(() => Assert.NotNull(component.Find("#cmafVideoPlayer")));

        // Act
        component.Find("#cmafPreferredVideo").Change(CmafPreferredVideo.Fallback.ToString());

        // Assert
        component.WaitForAssertion(() =>
        {
            var starts = context.JSInterop.Invocations.Where(invocation => invocation.Identifier == "startCmafSession").ToArray();
            Assert.Equal(2, starts.Length);
            Assert.Contains("preferredVideo=Fallback", Assert.IsType<string>(starts[^1].Arguments[0]), StringComparison.Ordinal);
            var storageWrite = Assert.Single(
                context.JSInterop.Invocations,
                invocation => invocation.Identifier == "localStorage.setItem" && Equals(invocation.Arguments[0], PreferencesStorageKey));
            Assert.Contains("\"PreferredVideo\":1", Assert.IsType<string>(storageWrite.Arguments[1]), StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// Verifies Shaka UI audio changes update the hosted-stream details without restarting the server session.
    /// </summary>
    [Fact]
    public async Task ShakaAudioRenditionChange_UpdatesDetailsWithoutRestart()
    {
        // Arrange
        using var context = CreateContext();
        context.JSInterop
            .Setup<WatchCmaf.CmafStartResponse>("startCmafSession", _ => true)
            .SetResult(new(
                "session-1",
                "/api/stream/cmaf/session-1/master.m3u8",
                "/api/stream/hls/session-1/master.m3u8",
                "/api/stream/cmaf/session-1/manifest.mpd"));
        context.JSInterop
            .Setup<WatchCmaf.CmafPlayerResult>("initCmafPlayer", _ => true)
            .SetResult(new(
                true,
                null,
                "/api/stream/cmaf/session-1/manifest.mpd",
                Audio: new("eng · Fallback AAC Stereo", "mp4a.40.2", 2, "11", "eng"),
                Video: new("avc1.64002a", 1920, 1080)));
        var component = context.Render<WatchCmaf>(parameters => parameters.Add(page => page.ChannelNumber, "42.1"));
        component.WaitForAssertion(() => Assert.NotNull(component.Find("#cmafVideoPlayer")));
        using var details = JsonDocument.Parse("""{"id":"12","label":"spa · Source","language":"spa","codec":"ac-3","channels":2}""");

        // Act
        await component.InvokeAsync(() => component.Instance.OnCmafPlayerEvent(new("audio-track-changed", "Audio track changed.", details.RootElement)));

        // Assert
        component.WaitForAssertion(() =>
        {
            Assert.Single(context.JSInterop.Invocations, invocation => invocation.Identifier == "startCmafSession");
            Assert.Empty(component.FindAll("#cmafAudioTrack"));
            Assert.False(component.Find("#cmafVideoPlayer").HasAttribute("controls"));
            Assert.True(component.Find("#cmafVideoPlayer").ParentElement?.HasAttribute("data-lineup-cmaf-container"));
            Assert.Contains("spa · Source", component.Markup);
            Assert.Contains("ac-3", component.Markup);
        });
    }

    /// <summary>
    /// Verifies an unsupported source-video presentation retries H.264 without changing the saved Source preference.
    /// </summary>
    [Fact]
    public void UnsupportedSourceVideo_RetriesWithH264WithoutChangingPreference()
    {
        // Arrange
        using var context = CreateContext();
        context.JSInterop
            .Setup<WatchCmaf.CmafStartResponse>(
                "startCmafSession",
                invocation => Assert.IsType<string>(invocation.Arguments[0]).Contains("preferredVideo=Source", StringComparison.Ordinal))
            .SetResult(
                new(
                    "source-session",
                    "/api/stream/cmaf/source-session/master.m3u8",
                    "/api/stream/hls/source-session/master.m3u8",
                    "/api/stream/cmaf/source-session/manifest.mpd",
                    HasSourceVideoRendition: true,
                    SourceVideoCodec: "hvc1.2.4.L123"));
        context.JSInterop
            .Setup<WatchCmaf.CmafStartResponse>(
                "startCmafSession",
                invocation => Assert.IsType<string>(invocation.Arguments[0]).Contains("preferredVideo=Fallback", StringComparison.Ordinal))
            .SetResult(new("fallback-session", "/api/stream/cmaf/fallback-session/master.m3u8", "/api/stream/hls/fallback-session/master.m3u8", "/api/stream/cmaf/fallback-session/manifest.mpd"));
        context.JSInterop
            .Setup<WatchCmaf.CmafPlayerResult>(
                "initCmafPlayer",
                invocation => Equals(invocation.Arguments[1], "/api/stream/cmaf/source-session/manifest.mpd"))
            .SetResult(new(false, "Shaka Error 4032", null, 4032, SourceVideoSupported: false));
        context.JSInterop
            .Setup<WatchCmaf.CmafPlayerResult>(
                "initCmafPlayer",
                invocation => Equals(invocation.Arguments[1], "/api/stream/cmaf/fallback-session/manifest.mpd"))
            .SetResult(new(true, null, "/api/stream/cmaf/fallback-session/manifest.mpd", Video: new("avc1.64002a", 1920, 1080)));
        context.JSInterop.SetupVoid("stopCmafSession", "source-session").SetVoidResult();
        context.JSInterop.SetupVoid("stopCmafSession", "fallback-session").SetVoidResult();

        // Act
        var component = context.Render<WatchCmaf>(parameters => parameters.Add(page => page.ChannelNumber, "105.1"));

        // Assert
        component.WaitForAssertion(() =>
        {
            var starts = context.JSInterop.Invocations.Where(invocation => invocation.Identifier == "startCmafSession").ToArray();
            Assert.Equal(2, starts.Length);
            Assert.Contains("preferredVideo=Source", Assert.IsType<string>(starts[0].Arguments[0]), StringComparison.Ordinal);
            Assert.Contains("preferredVideo=Fallback", Assert.IsType<string>(starts[1].Arguments[0]), StringComparison.Ordinal);
            Assert.Equal(nameof(CmafPreferredVideo.Source), component.Find("#cmafPreferredVideo").GetAttribute("value"));
            Assert.Contains("Playing video:", component.Markup);
            Assert.Contains("avc1.64002a", component.Markup);
            Assert.Contains("1920x1080", component.Markup);
            var notification = Assert.Single(context.Services.GetRequiredService<IStatusNotificationService>().Notifications);
            Assert.Equal("Source video is unavailable in this browser. Retrying this stream with H.264.", notification.Message);
            Assert.DoesNotContain(context.JSInterop.Invocations, invocation => invocation.Identifier == "localStorage.setItem");
        });
    }

    /// <summary>
    /// Verifies a runtime source-video decoder failure restarts once with H.264 rather than encoding fallback video continuously.
    /// </summary>
    [Fact]
    public async Task SourceVideoRuntimeFailure_RestartsWithH264()
    {
        // Arrange
        using var context = CreateContext();
        context.JSInterop
            .Setup<WatchCmaf.CmafStartResponse>(
                "startCmafSession",
                invocation => Assert.IsType<string>(invocation.Arguments[0]).Contains("preferredVideo=Source", StringComparison.Ordinal))
            .SetResult(
                new(
                    "source-session",
                    "/api/stream/cmaf/source-session/master.m3u8",
                    "/api/stream/hls/source-session/master.m3u8",
                    "/api/stream/cmaf/source-session/manifest.mpd",
                    HasSourceVideoRendition: true,
                    SourceVideoCodec: "hvc1.2.4.L153"));
        context.JSInterop
            .Setup<WatchCmaf.CmafStartResponse>(
                "startCmafSession",
                invocation => Assert.IsType<string>(invocation.Arguments[0]).Contains("preferredVideo=Fallback", StringComparison.Ordinal))
            .SetResult(new("fallback-session", "/api/stream/cmaf/fallback-session/master.m3u8", "/api/stream/hls/fallback-session/master.m3u8", "/api/stream/cmaf/fallback-session/manifest.mpd"));
        context.JSInterop
            .Setup<WatchCmaf.CmafPlayerResult>("initCmafPlayer", _ => true)
            .SetResult(new(true, null, "/api/stream/cmaf/source-session/manifest.mpd", Video: new("hvc1.2.4.L153", 1920, 1080)));
        context.JSInterop.SetupVoid("stopCmafSession", "source-session").SetVoidResult();
        context.JSInterop.SetupVoid("stopCmafSession", "fallback-session").SetVoidResult();
        var component = context.Render<WatchCmaf>(parameters => parameters.Add(page => page.ChannelNumber, "104.1"));
        component.WaitForAssertion(() => Assert.Contains("hvc1.2.4.L153", component.Markup));

        // Act
        await component.InvokeAsync(() => component.Instance.OnCmafPlayerEvent(
            new("video-fallback-required", "Source video failed in this browser. Restarting this stream with H.264.", null)));

        // Assert
        component.WaitForAssertion(() =>
        {
            var starts = context.JSInterop.Invocations.Where(invocation => invocation.Identifier == "startCmafSession").ToArray();
            Assert.Equal(2, starts.Length);
            Assert.Contains("preferredVideo=Fallback", Assert.IsType<string>(starts[1].Arguments[0]), StringComparison.Ordinal);
            Assert.Equal(nameof(CmafPreferredVideo.Source), component.Find("#cmafPreferredVideo").GetAttribute("value"));
        });
    }

    /// <summary>
    /// Verifies explicit HLS mode does not silently provide a protocol fallback.
    /// </summary>
    [Fact]
    public void StoredHlsMode_InitializesOnlyHlsManifest()
    {
        // Arrange
        using var context = CreateContext("""{"Protocol":1,"Quality":0,"PreferredAudio":0,"AacFallback":0,"SubtitleTrack":null}""");
        ConfigureSuccessfulSession(context);

        // Act
        var component = context.Render<WatchCmaf>(parameters => parameters.Add(page => page.ChannelNumber, "42.1"));

        // Assert
        component.WaitForAssertion(() =>
        {
            var initialize = Assert.Single(context.JSInterop.Invocations, invocation => invocation.Identifier == "initCmafPlayer");
            Assert.Equal("/api/stream/cmaf/session-1/master.m3u8", initialize.Arguments[1]);
            Assert.Null(initialize.Arguments[2]);
            Assert.Equal(nameof(CmafFallbackAudio.AacStereo), component.Find("#cmafFallbackAudio").GetAttribute("value"));
        });
    }

    /// <summary>
    /// Verifies the prior AAC channel-layout preference migrates to the matching fallback profile.
    /// </summary>
    [Fact]
    public void StoredLegacyAacLayout_MigratesToFallbackProfile()
    {
        // Arrange
        using var context = CreateContext("""{"Protocol":0,"Quality":0,"PreferredAudio":0,"AacFallback":2,"SubtitleTrack":null}""");
        ConfigureSuccessfulSession(context);

        // Act
        var component = context.Render<WatchCmaf>(parameters => parameters.Add(page => page.ChannelNumber, "42.1"));

        // Assert
        component.WaitForAssertion(() =>
            Assert.Equal(nameof(CmafFallbackAudio.AacUpTo7Point1), component.Find("#cmafFallbackAudio").GetAttribute("value")));
    }

    /// <summary>
    /// Verifies a fallback-only server retry informs the user without changing the Source preference.
    /// </summary>
    [Fact]
    public void SourceAudioMuxerFallback_NotifiesWithoutChangingPreference()
    {
        // Arrange
        using var context = CreateContext();
        ConfigureSuccessfulSession(context, sourceAudioFallbackApplied: true);

        // Act
        var component = context.Render<WatchCmaf>(parameters => parameters.Add(page => page.ChannelNumber, "104.1"));

        // Assert
        component.WaitForAssertion(() =>
        {
            Assert.Equal(nameof(CmafPreferredAudio.Source), component.Find("#cmafPreferredAudio").GetAttribute("value"));
            var notification = Assert.Single(context.Services.GetRequiredService<IStatusNotificationService>().Notifications);
            Assert.Contains("Using Fallback AAC Stereo for this stream", notification.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(
                context.JSInterop.Invocations,
                invocation => invocation.Identifier == "localStorage.setItem" && Equals(invocation.Arguments[0], PreferencesStorageKey));
        });
    }

    /// <summary>
    /// Verifies selecting fallback audio persists only the CMAF preference and restarts the presentation.
    /// </summary>
    [Fact]
    public void PreferredAudioChange_PersistsAndRestartsWithFallback()
    {
        // Arrange
        using var context = CreateContext();
        ConfigureSuccessfulSession(context);
        var component = context.Render<WatchCmaf>(parameters => parameters.Add(page => page.ChannelNumber, "42.1"));
        component.WaitForAssertion(() =>
        {
            Assert.NotNull(component.Find("#cmafVideoPlayer"));
            Assert.Contains(context.JSInterop.Invocations, invocation => invocation.Identifier == "initCmafPlayer");
        });

        // Act
        component.Find("#cmafPreferredAudio").Change(CmafPreferredAudio.Fallback.ToString());

        // Assert
        component.WaitForAssertion(() =>
        {
            var starts = context.JSInterop.Invocations.Where(invocation => invocation.Identifier == "startCmafSession").ToArray();
            Assert.Equal(2, starts.Length);
            Assert.Contains("preferredAudio=Fallback", Assert.IsType<string>(starts[^1].Arguments[0]), StringComparison.Ordinal);
            var storageWrite = Assert.Single(
                context.JSInterop.Invocations,
                invocation => invocation.Identifier == "localStorage.setItem" && Equals(invocation.Arguments[0], PreferencesStorageKey));
            Assert.Contains("\"PreferredAudio\":1", Assert.IsType<string>(storageWrite.Arguments[1]), StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// Verifies fallback choices are quality ordered while AAC Stereo remains the compatibility default.
    /// </summary>
    [Fact]
    public void FallbackAudio_DefaultAndOptions_AreCompatibilityAware()
    {
        // Arrange
        using var context = CreateContext();
        ConfigureSuccessfulSession(context);

        // Act
        var component = context.Render<WatchCmaf>(parameters => parameters.Add(page => page.ChannelNumber, "42.1"));

        // Assert
        component.WaitForAssertion(() =>
        {
            var select = component.Find("#cmafFallbackAudio");
            Assert.Equal(nameof(CmafFallbackAudio.AacStereo), select.GetAttribute("value"));
            Assert.Equal(
                ["Browser profile", "EAC3", "AC3", "AAC up to 7.1", "AAC up to 5.1", "AAC Stereo"],
                select.QuerySelectorAll("option").Select(option => option.TextContent.Trim()).ToArray());
        });
    }

    /// <summary>
    /// Verifies selecting EAC3 persists the fallback profile and sends it to the CMAF API.
    /// </summary>
    [Fact]
    public void FallbackAudioChange_PersistsAndRestartsWithEac3()
    {
        // Arrange
        using var context = CreateContext();
        ConfigureSuccessfulSession(context);
        var component = context.Render<WatchCmaf>(parameters => parameters.Add(page => page.ChannelNumber, "42.1"));
        component.WaitForAssertion(() => Assert.NotNull(component.Find("#cmafVideoPlayer")));

        // Act
        component.Find("#cmafFallbackAudio").Change(CmafFallbackAudio.Eac3.ToString());

        // Assert
        component.WaitForAssertion(() =>
        {
            var starts = context.JSInterop.Invocations.Where(invocation => invocation.Identifier == "startCmafSession").ToArray();
            Assert.Equal(2, starts.Length);
            Assert.Contains("fallbackAudio=Eac3", Assert.IsType<string>(starts[^1].Arguments[0]), StringComparison.Ordinal);
            var storageWrite = Assert.Single(
                context.JSInterop.Invocations,
                invocation => invocation.Identifier == "localStorage.setItem" && Equals(invocation.Arguments[0], PreferencesStorageKey));
            Assert.Contains("\"FallbackAudio\":4", Assert.IsType<string>(storageWrite.Arguments[1]), StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// Verifies a browser that rejects the configured fallback gets a temporary AAC Stereo session without changing the saved preference.
    /// </summary>
    [Fact]
    public void UnsupportedAc3Presentation_RetriesWithAacStereoWithoutChangingPreference()
    {
        // Arrange
        using var context = CreateContext("""{"Protocol":2,"Quality":0,"PreferredAudio":1,"FallbackAudio":3,"SubtitleTrack":null}""");
        context.JSInterop
            .Setup<WatchCmaf.CmafStartResponse>(
                "startCmafSession",
                invocation => Assert.IsType<string>(invocation.Arguments[0]).Contains("fallbackAudio=Ac3", StringComparison.Ordinal))
            .SetResult(
                new(
                    "ac3-session",
                    "/api/stream/cmaf/ac3-session/master.m3u8",
                    "/api/stream/hls/ac3-session/master.m3u8",
                    "/api/stream/cmaf/ac3-session/manifest.mpd",
                    FallbackAudioCodec: "ac3"));
        context.JSInterop
            .Setup<WatchCmaf.CmafStartResponse>(
                "startCmafSession",
                invocation => Assert.IsType<string>(invocation.Arguments[0]).Contains("fallbackAudio=AacStereo", StringComparison.Ordinal))
            .SetResult(new("aac-session", "/api/stream/cmaf/aac-session/master.m3u8", "/api/stream/hls/aac-session/master.m3u8", "/api/stream/cmaf/aac-session/manifest.mpd"));
        context.JSInterop
            .Setup<WatchCmaf.CmafPlayerResult>(
                "initCmafPlayer",
                invocation => Equals(invocation.Arguments[1], "/api/stream/cmaf/ac3-session/manifest.mpd"))
            .SetResult(new(false, "Shaka Error 4032", null, 4032, FallbackAudioSupported: false));
        context.JSInterop
            .Setup<WatchCmaf.CmafPlayerResult>(
                "initCmafPlayer",
                invocation => Equals(invocation.Arguments[1], "/api/stream/cmaf/aac-session/manifest.mpd"))
            .SetResult(new(true, null, "/api/stream/cmaf/aac-session/manifest.mpd", Audio: new("Fallback AAC Stereo", "mp4a.40.2", 2)));
        context.JSInterop.SetupVoid("stopCmafSession", "ac3-session").SetVoidResult();
        context.JSInterop.SetupVoid("stopCmafSession", "aac-session").SetVoidResult();

        // Act
        var component = context.Render<WatchCmaf>(parameters => parameters.Add(page => page.ChannelNumber, "105.1"));

        // Assert
        component.WaitForAssertion(() =>
        {
            var starts = context.JSInterop.Invocations.Where(invocation => invocation.Identifier == "startCmafSession").ToArray();
            Assert.Equal(2, starts.Length);
            Assert.Contains("fallbackAudio=Ac3", Assert.IsType<string>(starts[0].Arguments[0]), StringComparison.Ordinal);
            Assert.Contains("fallbackAudio=AacStereo", Assert.IsType<string>(starts[1].Arguments[0]), StringComparison.Ordinal);
            Assert.Equal(nameof(CmafFallbackAudio.Ac3), component.Find("#cmafFallbackAudio").GetAttribute("value"));
            var notification = Assert.Single(context.Services.GetRequiredService<IStatusNotificationService>().Notifications);
            Assert.Equal("AC3 is unavailable in this browser. Retrying this stream with AAC Stereo.", notification.Message);
            Assert.Contains("Playing audio:", component.Markup);
            Assert.Contains("Fallback AAC Stereo", component.Markup);
            Assert.Contains("mp4a.40.2", component.Markup);
            Assert.DoesNotContain(
                context.JSInterop.Invocations,
                invocation => invocation.Identifier == "localStorage.setItem");
        });
    }

    /// <summary>
    /// Verifies a generic Shaka 4032 does not replace AC3 when the browser reports AC3 support.
    /// </summary>
    [Fact]
    public void SupportedAc3Presentation_GenericUnsupportedError_DoesNotRetryAac()
    {
        // Arrange
        using var context = CreateContext("""{"Protocol":2,"Quality":0,"PreferredAudio":1,"FallbackAudio":3,"SubtitleTrack":null}""");
        context.JSInterop
            .Setup<WatchCmaf.CmafStartResponse>("startCmafSession", _ => true)
            .SetResult(
                new(
                    "ac3-session",
                    "/api/stream/cmaf/ac3-session/master.m3u8",
                    "/api/stream/hls/ac3-session/master.m3u8",
                    "/api/stream/cmaf/ac3-session/manifest.mpd",
                    FallbackAudioCodec: "ac3"));
        context.JSInterop
            .Setup<WatchCmaf.CmafPlayerResult>("initCmafPlayer", _ => true)
            .SetResult(new(false, "Shaka Error 4032", null, 4032, FallbackAudioSupported: true));
        context.JSInterop.SetupVoid("stopCmafSession", "ac3-session").SetVoidResult();

        // Act
        var component = context.Render<WatchCmaf>(parameters => parameters.Add(page => page.ChannelNumber, "105.1"));

        // Assert
        component.WaitForAssertion(() =>
        {
            Assert.Single(context.JSInterop.Invocations, invocation => invocation.Identifier == "startCmafSession");
            Assert.Contains("Shaka Error 4032", component.Markup, StringComparison.Ordinal);
            Assert.Empty(context.Services.GetRequiredService<IStatusNotificationService>().Notifications);
        });
    }

    /// <summary>
    /// Verifies component disposal destroys the player and explicitly stops its CMAF session.
    /// </summary>
    [Fact]
    public async Task Dispose_StopsPlayerAndServerSession()
    {
        // Arrange
        using var context = CreateContext();
        ConfigureSuccessfulSession(context);
        var component = context.Render<WatchCmaf>(parameters => parameters.Add(page => page.ChannelNumber, "42.1"));
        component.WaitForAssertion(() =>
        {
            Assert.NotNull(component.Find("#cmafVideoPlayer"));
            Assert.Contains(context.JSInterop.Invocations, invocation => invocation.Identifier == "initCmafPlayer");
        });

        // Act
        await component.Instance.DisposeAsync();

        // Assert
        Assert.Contains(
            context.JSInterop.Invocations,
            invocation => invocation.Identifier == "stopMediaPlayer" && Equals(invocation.Arguments[0], "cmafVideoPlayer"));
        Assert.Contains(
            context.JSInterop.Invocations,
            invocation => invocation.Identifier == "stopCmafSession" && Equals(invocation.Arguments[0], "session-1"));
    }

    /// <summary>
    /// Verifies all prepared text and caption sidecars are supplied to the player and omitted from the burn-in selector.
    /// </summary>
    [Fact]
    public void PreparedSubtitles_ArePlayerControlledAndAbsentFromBurnInSelector()
    {
        // Arrange
        using var context = CreateContext();
        ConfigureSuccessfulSession(context);
        var component = context.Render<WatchCmaf>(parameters => parameters.Add(page => page.ChannelNumber, "42.1"));
        component.WaitForAssertion(() => Assert.NotNull(component.Find("#cmafVideoPlayer")));
        var clientIdField = typeof(WatchCmaf).GetField("_clientId", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(clientIdField);
        var clientId = Assert.IsType<string>(clientIdField.GetValue(component.Instance));
        var registry = context.Services.GetRequiredService<IActiveStreamRegistry>();

        // Act
        registry.Register(new ActiveStreamSnapshot(
            "session-1",
            "42.1",
            HostedStreamFormat.Cmaf,
            DateTime.UtcNow,
            null,
            [
                new ActiveStreamTrack(MediaTrackType.Audio, "ac3", "copy", null, null, null, null, 2, 2, 48_000)
                {
                    SourceIndex = 1,
                    IsSelected = true
                },
                new ActiveStreamTrack(MediaTrackType.Subtitle, "eia_608", "webvtt", null, null, null, null, null, null, null)
                {
                    SourceIndex = 3,
                    Title = "Closed Captions",
                    IsSelected = false,
                    IsEmbeddedClosedCaptions = true,
                    SubtitlePresentation = SubtitlePresentation.WebVtt
                }
            ])
        {
            ClientId = clientId
        });

        // Assert
        component.WaitForAssertion(() =>
        {
            var starts = context.JSInterop.Invocations.Where(invocation => invocation.Identifier == "startCmafSession").ToArray();
            Assert.Single(starts);
            Assert.Empty(component.FindAll("#cmafSubtitleTrack"));
            var initialize = Assert.Single(context.JSInterop.Invocations, invocation => invocation.Identifier == "initCmafPlayer");
            var subtitles = Assert.IsAssignableFrom<IReadOnlyList<WatchCmaf.CmafSubtitleResponse>>(initialize.Arguments[7]);
            Assert.Contains(subtitles, subtitle => subtitle.IsEmbeddedClosedCaptions && subtitle.SourceIndex == 3);
            Assert.Contains("WebVTT sidecar", component.Markup);
            Assert.Contains("sidecar", component.Markup);
        });
    }

    /// <summary>
    /// Verifies bitmap subtitles retain the explicit restart-and-burn-in path.
    /// </summary>
    [Fact]
    public void BitmapSubtitles_RestartsWithBurnInSelection()
    {
        // Arrange
        using var context = CreateContext();
        ConfigureSuccessfulSession(context);
        var component = context.Render<WatchCmaf>(parameters => parameters.Add(page => page.ChannelNumber, "42.1"));
        component.WaitForAssertion(() => Assert.NotNull(component.Find("#cmafVideoPlayer")));
        var clientIdField = typeof(WatchCmaf).GetField("_clientId", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(clientIdField);
        var clientId = Assert.IsType<string>(clientIdField.GetValue(component.Instance));
        context.Services.GetRequiredService<IActiveStreamRegistry>().Register(new ActiveStreamSnapshot(
            "session-1",
            "42.1",
            HostedStreamFormat.Cmaf,
            DateTime.UtcNow,
            null,
            [
                new ActiveStreamTrack(MediaTrackType.Subtitle, "dvb_subtitle", "not-mapped", null, null, null, null, null, null, null)
                {
                    SourceIndex = 4,
                    Title = "Bitmap subtitles",
                    SubtitlePresentation = SubtitlePresentation.BurnIn
                }
            ])
        {
            ClientId = clientId
        });
        component.WaitForAssertion(() => Assert.NotNull(component.Find("#cmafSubtitleTrack")));
        component.WaitForAssertion(() =>
        {
            Assert.Contains("not included", component.Markup);
            Assert.DoesNotContain("not-mapped", component.Markup);
        });

        // Act
        component.Find("#cmafSubtitleTrack").Change("4");

        // Assert
        component.WaitForAssertion(() =>
        {
            var starts = context.JSInterop.Invocations.Where(invocation => invocation.Identifier == "startCmafSession").ToArray();
            Assert.Equal(2, starts.Length);
            var url = Assert.IsType<string>(starts[^1].Arguments[0]);
            Assert.Contains("subtitleTrack=4", url, StringComparison.Ordinal);
            Assert.Contains("subtitlePresentation=BurnIn", url, StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// Verifies CMAF retains Watch channel metadata and detailed tuner and hosted-stream diagnostics.
    /// </summary>
    [Fact]
    public async Task ChannelAndStreamInformation_MatchesWatchDetails()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;
        var repository = Substitute.For<IEpgRepository>();
        repository.GetChannelsAsync().Returns(
        [
            new HDHomeRunChannelEpgSegment { GuideNumber = "2.1", GuideName = "Test Channel", Favorite = true, DRM = true }
        ]);
        repository.GetProgramsAsync(Arg.Any<DateTime?>(), Arg.Any<DateTime?>()).Returns(
        [
            new HDHomeRunProgram
            {
                GuideNumber = "2.1",
                Title = "Current Show",
                EpisodeTitle = "Current Episode",
                StartTime = now.AddMinutes(-5).ToUnixTimeSeconds(),
                EndTime = now.AddMinutes(55).ToUnixTimeSeconds()
            }
        ]);
        var deviceState = Substitute.For<IDeviceStateService>();
        deviceState.TunerStatuses.Returns(
        [
            new TunerStatus
            {
                TunerIndex = 1,
                VirtualChannel = "2.1",
                Target = "http",
                LockType = "8vsb",
                SignalStrength = 85,
                SignalToNoiseQuality = 92,
                SymbolErrorQuality = 100,
                BitsPerSecond = 19_000_000,
                PacketsPerSecond = 1_200
            }
        ]);
        var settings = Substitute.For<IAppSettingsService>();
        settings.Settings.Returns(new AppSettings { DeviceAddress = "192.0.2.10" });
        var channelStore = new ChannelLineupStore(Path.Combine(Path.GetTempPath(), $"lineup-watch-cmaf-{Guid.NewGuid():N}.db"));
        await channelStore.StoreAsync(
        [
            new HDHomeRunChannel
            {
                GuideNumber = "2.1",
                GuideName = "Test Channel",
                Favorite = true,
                DRM = true,
                URL = "http://device/auto/v2.1"
            }
        ], Xunit.TestContext.Current.CancellationToken);
        var registry = new ActiveStreamRegistry();
        using var context = CreateContext(repository: repository, deviceState: deviceState, settingsService: settings, channelStore: channelStore, registry: registry);
        ConfigureSuccessfulSession(context);
        var component = context.Render<WatchCmaf>();
        var clientIdField = typeof(WatchCmaf).GetField("_clientId", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(clientIdField);
        var clientId = Assert.IsType<string>(clientIdField.GetValue(component.Instance));
        registry.Register(new ActiveStreamSnapshot(
            "session-1",
            "2.1",
            HostedStreamFormat.Cmaf,
            DateTime.UtcNow,
            18_000_000,
            [
                new ActiveStreamTrack(MediaTrackType.Video, "hevc", "h264", 15_000_000, 2_500_000, 1920, 1080, null, null, null) { SourceIndex = 0, IsSelected = true },
                new ActiveStreamTrack(MediaTrackType.Audio, "ac4", "copy", 512_000, 512_000, null, null, 6, 6, 48_000)
                {
                    SourceIndex = 1,
                    IsSelected = true,
                    OutputTitle = "Source"
                },
                new ActiveStreamTrack(MediaTrackType.Audio, "ac4", "aac", 512_000, 128_000, null, null, 6, 2, 48_000)
                {
                    SourceIndex = 1,
                    IsSelected = true,
                    OutputTitle = "Fallback AAC Stereo"
                }
            ])
        {
            ClientId = clientId
        });

        // Act
        component.WaitForElement(".cmaf-channel-item").Click();

        // Assert
        component.WaitForAssertion(() =>
        {
            var channel = component.Find(".cmaf-channel-item");
            Assert.Contains("Current Show", channel.TextContent);
            Assert.NotNull(channel.QuerySelector("[aria-label='Favorite channel']"));
            Assert.NotNull(channel.QuerySelector("[aria-label='DRM-protected channel']"));
            Assert.Contains("Current Show", component.Markup);
            Assert.Contains("Current Episode", component.Markup);
            Assert.Contains("192.0.2.10", component.Markup);
            Assert.Contains("8vsb", component.Markup);
            Assert.Contains("85%", component.Markup);
            Assert.Contains("19 Mbps", component.Markup);
            Assert.Contains("session-1", component.Markup);
            Assert.Contains("hevc", component.Markup);
            Assert.Contains("h264", component.Markup);
            Assert.Contains("Source total: 18 Mbps", component.Markup);
            Assert.Contains("CMAF", component.Markup);
            Assert.Contains("Playback protocol:", component.Markup);
            Assert.Contains("DASH", component.Markup);
            Assert.Contains("included", component.Markup);
            Assert.Contains("playing", component.Markup);
        });
    }

    private static BunitContext CreateContext(
        string? preferencesJson = null,
        IEpgRepository? repository = null,
        IDeviceStateService? deviceState = null,
        IAppSettingsService? settingsService = null,
        ChannelLineupStore? channelStore = null,
        IActiveStreamRegistry? registry = null)
    {
        BunitContext.DefaultWaitTimeout = TimeSpan.FromSeconds(5);
        var context = new BunitContext();
        if (repository is null)
        {
            repository = Substitute.For<IEpgRepository>();
            repository.GetChannelsAsync().Returns([]);
            repository.GetProgramsAsync(Arg.Any<DateTime?>(), Arg.Any<DateTime?>()).Returns([]);
        }
        if (deviceState is null)
        {
            deviceState = Substitute.For<IDeviceStateService>();
            deviceState.TunerStatuses.Returns([]);
        }
        if (settingsService is null)
        {
            settingsService = Substitute.For<IAppSettingsService>();
            settingsService.Settings.Returns(new AppSettings());
        }
        context.Services.AddSingleton(repository);
        context.Services.AddSingleton(deviceState);
        context.Services.AddSingleton(settingsService);
        context.Services.AddSingleton(registry ?? new ActiveStreamRegistry());
        context.Services.AddSingleton(channelStore ?? CreateEmptyChannelStore());
        context.Services.AddSingleton<IStatusNotificationService>(new StatusNotificationService());
        context.Services.AddScoped<IBrowserDataStore, BrowserDataStore>();
        context.JSInterop.Setup<string?>("localStorage.getItem", PreferencesStorageKey).SetResult(preferencesJson);
        context.JSInterop.Setup<string?>("localStorage.getItem", CmafCompatibilityProfile.StorageKey).SetResult(null);
        context.JSInterop.SetupVoid("localStorage.setItem", _ => true).SetVoidResult();
        context.JSInterop.SetupVoid("stopMediaPlayer", "cmafVideoPlayer").SetVoidResult();
        context.JSInterop.SetupVoid("stopCmafSession", "session-1").SetVoidResult();
        return context;
    }

    private static void ConfigureSuccessfulSession(BunitContext context, bool sourceAudioFallbackApplied = false)
    {
        context.JSInterop
            .Setup<WatchCmaf.CmafStartResponse>("startCmafSession", _ => true)
            .SetResult(new(
                "session-1",
                "/api/stream/cmaf/session-1/master.m3u8",
                "/api/stream/hls/session-1/master.m3u8",
                "/api/stream/cmaf/session-1/manifest.mpd",
                sourceAudioFallbackApplied,
                "Fallback AAC Stereo",
                [
                    new(2, "English", "eng", "/api/stream/cmaf/session-1/captions-2.vtt", false),
                    new(3, "Closed Captions", "eng", "/api/stream/cmaf/session-1/captions-3.vtt", true)
                ],
                FallbackAudioCodec: "aac"));
        context.JSInterop
            .Setup<WatchCmaf.CmafPlayerResult>("initCmafPlayer", _ => true)
            .SetResult(new(
                true,
                null,
                "/api/stream/cmaf/session-1/manifest.mpd",
                Audio: new("Fallback AAC Stereo", "mp4a.40.2", 2),
                Video: new("avc1.64002a", 1920, 1080)));
    }

    private static ChannelLineupStore CreateEmptyChannelStore()
    {
        var store = new ChannelLineupStore(Path.Combine(Path.GetTempPath(), $"lineup-watch-cmaf-{Guid.NewGuid():N}.db"));
        _ = store.ReadAsync(Xunit.TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        return store;
    }

    private static CmafCompatibilityProfile CreateCompatibilityProfile() =>
        new()
        {
            BrowserIdentity = "test-browser",
            CompletedAtUtc = DateTimeOffset.UtcNow,
            Claims = new CmafBrowserClaims(),
            Results = CmafCompatibilityTestCatalog.All
                .Select(test => new CmafCapabilityResult
                {
                    CaseId = test.Id,
                    Kind = test.Kind,
                    Request = test.Request,
                    Status = test.IsUnavailable ? CmafCapabilityStatus.Unavailable : CmafCapabilityStatus.Passed
                })
                .ToArray()
        };
}
