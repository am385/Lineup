using Bunit;
using Lineup.Core;
using Lineup.Core.Storage;
using Lineup.HDHomeRun.Api.Models;
using Lineup.Web.Components.Pages;
using Lineup.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using NSubstitute;
using System.Reflection;
using Xunit;

namespace Lineup.Web.Tests.Components.Pages;

/// <summary>
/// Verifies the shared CMAF Watch page lifecycle and independent preferences.
/// </summary>
public class WatchCmafTests
{
    private const string PreferencesStorageKey = "lineup-watch-cmaf-preferences-v1";

    /// <summary>
    /// Verifies Auto mode prefers DASH and provides HLS fallback for the same server session.
    /// </summary>
    [Fact]
    public void DirectRoute_AutoModeStartsSharedDashAndHlsSession()
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
            Assert.Contains("preferredAudio=Source", startUrl, StringComparison.Ordinal);
            var initialize = Assert.Single(context.JSInterop.Invocations, invocation => invocation.Identifier == "initCmafPlayer");
            Assert.Equal("/api/stream/cmaf/session-1/manifest.mpd", initialize.Arguments[1]);
            Assert.Equal("/api/stream/cmaf/session-1/master.m3u8", initialize.Arguments[2]);
            Assert.Equal("Source", initialize.Arguments[3]);
            var subtitles = Assert.IsAssignableFrom<IReadOnlyList<WatchCmaf.CmafSubtitleResponse>>(initialize.Arguments[4]);
            Assert.Collection(
                subtitles,
                subtitle => Assert.Equal(2, subtitle.SourceIndex),
                subtitle => Assert.Equal(3, subtitle.SourceIndex));
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
                ["EAC3", "AC3", "AAC up to 7.1", "AAC up to 5.1", "AAC Stereo"],
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
            HostedStreamFormat.Hls,
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
            var subtitles = Assert.IsAssignableFrom<IReadOnlyList<WatchCmaf.CmafSubtitleResponse>>(initialize.Arguments[4]);
            Assert.Contains(subtitles, subtitle => subtitle.IsEmbeddedClosedCaptions && subtitle.SourceIndex == 3);
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
            HostedStreamFormat.Hls,
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

    private static BunitContext CreateContext(string? preferencesJson = null)
    {
        BunitContext.DefaultWaitTimeout = TimeSpan.FromSeconds(5);
        var context = new BunitContext();
        var repository = Substitute.For<IEpgRepository>();
        repository.GetChannelsAsync().Returns([]);
        repository.GetProgramsAsync(Arg.Any<DateTime?>(), Arg.Any<DateTime?>()).Returns([]);
        var deviceState = Substitute.For<IDeviceStateService>();
        deviceState.TunerStatuses.Returns([]);
        context.Services.AddSingleton(repository);
        context.Services.AddSingleton(deviceState);
        context.Services.AddSingleton<IActiveStreamRegistry>(new ActiveStreamRegistry());
        context.Services.AddSingleton(CreateEmptyChannelStore());
        context.Services.AddSingleton<IStatusNotificationService>(new StatusNotificationService());
        context.Services.AddScoped<IBrowserDataStore, BrowserDataStore>();
        context.JSInterop.Setup<string?>("localStorage.getItem", PreferencesStorageKey).SetResult(preferencesJson);
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
                ]));
        context.JSInterop
            .Setup<WatchCmaf.CmafPlayerResult>("initCmafPlayer", _ => true)
            .SetResult(new(true, null, "/api/stream/cmaf/session-1/manifest.mpd"));
    }

    private static ChannelLineupStore CreateEmptyChannelStore()
    {
        var store = new ChannelLineupStore(Path.Combine(Path.GetTempPath(), $"lineup-watch-cmaf-{Guid.NewGuid():N}.db"));
        _ = store.ReadAsync(Xunit.TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        return store;
    }
}
