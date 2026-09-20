using Bunit;
using Lineup.Core.Storage;
using Lineup.HDHomeRun.Api.Models;
using Lineup.HDHomeRun.Device.Protocol;
using Lineup.Web.Components.Pages;
using Lineup.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Lineup.Web.Tests.Components.Pages;

/// <summary>
/// Verifies the Watch page media-player lifecycle.
/// </summary>
public class WatchTests
{
    /// <summary>
    /// Verifies that manual tuning starts playback without cached guide data.
    /// </summary>
    [Fact]
    public void ManualTuneWithoutGuideData_StartsRequestedChannel()
    {
        // Arrange
        using var context = new BunitContext();
        var repository = Substitute.For<IEpgRepository>();
        repository.GetChannelsAsync().Returns([]);
        repository.GetProgramsAsync(Arg.Any<DateTime?>(), Arg.Any<DateTime?>()).Returns([]);
        var settingsService = Substitute.For<IAppSettingsService>();
        settingsService.Settings.Returns(new AppSettings());
        context.Services.AddSingleton(repository);
        context.Services.AddSingleton(settingsService);
        AddWatchRuntimeServices(context);
        context.JSInterop.SetupVoid("stopMediaPlayer", "videoPlayer").SetVoidResult();
        context.JSInterop.Setup<string?>("initFmp4Player", invocation => invocation.Arguments.Count == 3).SetResult(null);
        var component = context.Render<Watch>();

        // Act
        component.Find("#manualTuneChannel").Input(" 42.1 ");
        component.Find("form").Submit();

        // Assert
        component.WaitForAssertion(() =>
        {
            Assert.NotNull(component.Find("#videoPlayer"));
            Assert.Contains("42.1", component.Markup);
            Assert.Contains("Manual Tune", component.Markup);
            var initialization = Assert.Single(context.JSInterop.Invocations, invocation => invocation.Identifier == "initFmp4Player");
            Assert.Contains("/api/stream/fmp4/42.1?", Assert.IsType<string>(initialization.Arguments[1]));
        });
    }

    /// <summary>
    /// Verifies that manual tuning rejects values that are not virtual channel numbers.
    /// </summary>
    [Fact]
    public void ManualTuneWithInvalidChannel_ShowsValidationWithoutStartingPlayback()
    {
        // Arrange
        using var context = new BunitContext();
        var repository = Substitute.For<IEpgRepository>();
        repository.GetChannelsAsync().Returns([]);
        repository.GetProgramsAsync(Arg.Any<DateTime?>(), Arg.Any<DateTime?>()).Returns([]);
        var settingsService = Substitute.For<IAppSettingsService>();
        settingsService.Settings.Returns(new AppSettings());
        context.Services.AddSingleton(repository);
        context.Services.AddSingleton(settingsService);
        AddWatchRuntimeServices(context);
        var component = context.Render<Watch>();

        // Act
        component.Find("#manualTuneChannel").Input("channel five");
        component.Find("form").Submit();

        // Assert
        component.WaitForAssertion(() =>
        {
            Assert.Contains("Enter a channel number using digits", component.Markup);
            Assert.Empty(component.FindAll("#videoPlayer"));
            Assert.DoesNotContain(context.JSInterop.Invocations, invocation => invocation.Identifier == "initFmp4Player");
        });
    }

    /// <summary>
    /// Verifies that a direct Watch URL tunes a channel that is absent from guide data.
    /// </summary>
    [Fact]
    public void DirectUrlWithoutGuideData_StartsRequestedChannel()
    {
        // Arrange
        using var context = new BunitContext();
        var repository = Substitute.For<IEpgRepository>();
        repository.GetChannelsAsync().Returns([]);
        repository.GetProgramsAsync(Arg.Any<DateTime?>(), Arg.Any<DateTime?>()).Returns([]);
        var settingsService = Substitute.For<IAppSettingsService>();
        settingsService.Settings.Returns(new AppSettings());
        context.Services.AddSingleton(repository);
        context.Services.AddSingleton(settingsService);
        AddWatchRuntimeServices(context);
        context.JSInterop.SetupVoid("stopMediaPlayer", "videoPlayer").SetVoidResult();
        context.JSInterop.Setup<string?>("initFmp4Player", invocation => invocation.Arguments.Count == 3).SetResult(null);

        // Act
        var component = context.Render<Watch>(parameters => parameters.Add(page => page.ChannelNumber, "42.1"));

        // Assert
        component.WaitForAssertion(() =>
        {
            Assert.NotNull(component.Find("#videoPlayer"));
            Assert.Contains("Manual Tune", component.Markup);
            var initialization = Assert.Single(context.JSInterop.Invocations, invocation => invocation.Identifier == "initFmp4Player");
            Assert.Contains("/api/stream/fmp4/42.1?", Assert.IsType<string>(initialization.Arguments[1]));
        });
    }

    /// <summary>
    /// Verifies that cached channel identifiers retain their existing selection behavior.
    /// </summary>
    [Fact]
    public void CachedChannelWithNonstandardIdentifier_StartsSelectedChannel()
    {
        // Arrange
        using var context = new BunitContext();
        var repository = Substitute.For<IEpgRepository>();
        repository.GetChannelsAsync().Returns(
        [
            new HDHomeRunChannelEpgSegment { GuideNumber = "7-1", GuideName = "Existing Channel" }
        ]);
        repository.GetProgramsAsync(Arg.Any<DateTime?>(), Arg.Any<DateTime?>()).Returns([]);
        var settingsService = Substitute.For<IAppSettingsService>();
        settingsService.Settings.Returns(new AppSettings());
        context.Services.AddSingleton(repository);
        context.Services.AddSingleton(settingsService);
        AddWatchRuntimeServices(context);
        context.JSInterop.SetupVoid("stopMediaPlayer", "videoPlayer").SetVoidResult();
        context.JSInterop.Setup<string?>("initFmp4Player", invocation => invocation.Arguments.Count == 3).SetResult(null);
        var component = context.Render<Watch>();

        // Act
        component.Find(".channel-item").Click();

        // Assert
        component.WaitForAssertion(() =>
        {
            Assert.Contains("Existing Channel", component.Markup);
            var initialization = Assert.Single(context.JSInterop.Invocations, invocation => invocation.Identifier == "initFmp4Player");
            Assert.Contains("/api/stream/fmp4/7-1?", Assert.IsType<string>(initialization.Arguments[1]));
        });
    }

    /// <summary>
    /// Verifies that disposing an inactive page does not attempt browser interop.
    /// </summary>
    [Fact]
    public void DisposeBeforePlayback_DoesNotInvokePlayerShutdown()
    {
        // Arrange
        using var context = new BunitContext();
        var repository = Substitute.For<IEpgRepository>();
        repository.GetChannelsAsync().Returns([]);
        repository.GetProgramsAsync(Arg.Any<DateTime?>(), Arg.Any<DateTime?>()).Returns([]);

        var settingsService = Substitute.For<IAppSettingsService>();
        settingsService.Settings.Returns(new AppSettings());
        context.Services.AddSingleton(repository);
        context.Services.AddSingleton(settingsService);
        AddWatchRuntimeServices(context);

        var component = context.Render<Watch>();

        // Act
        component.Dispose();

        // Assert
        Assert.DoesNotContain(context.JSInterop.Invocations, invocation => invocation.Identifier == "stopMediaPlayer");
    }

    /// <summary>
    /// Verifies that Stop awaits browser-side player shutdown before removing the video element.
    /// </summary>
    [Fact]
    public async Task Stop_AwaitsPlayerShutdownBeforeRemovingVideo()
    {
        // Arrange
        using var context = new BunitContext();
        var repository = Substitute.For<IEpgRepository>();
        repository.GetChannelsAsync().Returns(
        [
            new HDHomeRunChannelEpgSegment
            {
                GuideNumber = "2.1",
                GuideName = "Test Channel"
            }
        ]);
        repository.GetProgramsAsync(Arg.Any<DateTime?>(), Arg.Any<DateTime?>()).Returns([]);

        var settingsService = Substitute.For<IAppSettingsService>();
        settingsService.Settings.Returns(new AppSettings());
        context.Services.AddSingleton(repository);
        context.Services.AddSingleton(settingsService);
        AddWatchRuntimeServices(context);

        context.JSInterop.SetupVoid("stopMediaPlayer", "videoPlayer").SetVoidResult();
        context.JSInterop.Setup<string?>("initFmp4Player", invocation => invocation.Arguments.Count == 3).SetResult(null);

        // Act
        var component = context.Render<Watch>(parameters => parameters.Add(page => page.ChannelNumber, "2.1"));
        // Assert
        component.WaitForAssertion(() => Assert.NotNull(component.Find("#videoPlayer")));
        var pendingShutdown = context.JSInterop.SetupVoid("stopMediaPlayer", "videoPlayer");

        var stopTask = component.Find("button.btn-danger").ClickAsync(new());
        await Task.Yield();

        Assert.NotNull(component.Find("#videoPlayer"));
        Assert.False(stopTask.IsCompleted);

        pendingShutdown.SetVoidResult();
        await stopTask;
        component.WaitForAssertion(() => Assert.Empty(component.FindAll("#videoPlayer")));
    }

    /// <summary>
    /// Verifies that an explicit registry stop closes the matching Watch player.
    /// </summary>
    [Fact]
    public void ActiveStreamStopRequest_StopsMatchingWatchPlayer()
    {
        // Arrange
        using var context = new BunitContext();
        var repository = Substitute.For<IEpgRepository>();
        repository.GetChannelsAsync().Returns(
        [
            new HDHomeRunChannelEpgSegment
            {
                GuideNumber = "2.1",
                GuideName = "Test Channel"
            }
        ]);
        repository.GetProgramsAsync(Arg.Any<DateTime?>(), Arg.Any<DateTime?>()).Returns([]);
        var settingsService = Substitute.For<IAppSettingsService>();
        settingsService.Settings.Returns(new AppSettings());
        var registry = new ActiveStreamRegistry();
        context.Services.AddSingleton(repository);
        context.Services.AddSingleton(settingsService);
        AddWatchRuntimeServices(context, activeStreamRegistry: registry);
        context.JSInterop.SetupVoid("stopMediaPlayer", "videoPlayer").SetVoidResult();
        context.JSInterop.Setup<string?>("initFmp4Player", invocation => invocation.Arguments.Count == 3).SetResult(null);
        // Act
        var component = context.Render<Watch>(parameters => parameters.Add(page => page.ChannelNumber, "2.1"));
        // Assert
        component.WaitForAssertion(() => Assert.NotNull(component.Find("#videoPlayer")));
        var initialization = Assert.Single(context.JSInterop.Invocations, invocation => invocation.Identifier == "initFmp4Player");
        var streamUrl = Assert.IsType<string>(initialization.Arguments[1]);
        var clientIdStart = streamUrl.IndexOf("clientId=", StringComparison.Ordinal) + "clientId=".Length;
        var clientId = streamUrl[clientIdStart..].Split('&')[0];
        registry.Register(
            new ActiveStreamSnapshot("session", "2.1", HostedStreamFormat.FragmentedMp4, DateTime.UtcNow, null, []) { ClientId = clientId },
            () => { });

        var stopped = registry.RequestStop("session");

        Assert.True(stopped);
        component.WaitForAssertion(() =>
        {
            Assert.Empty(component.FindAll("#videoPlayer"));
            Assert.Contains(context.JSInterop.Invocations, invocation => invocation.Identifier == "stopMediaPlayer");
        });
    }

    /// <summary>
    /// Verifies that changing Watch quality restarts only the active player with a per-session override.
    /// </summary>
    [Fact]
    public void QualityChange_RestartsActivePlayerWithOverride()
    {
        // Arrange
        using var context = new BunitContext();
        var repository = Substitute.For<IEpgRepository>();
        repository.GetChannelsAsync().Returns(
        [
            new HDHomeRunChannelEpgSegment
            {
                GuideNumber = "2.1",
                GuideName = "Test Channel"
            }
        ]);
        repository.GetProgramsAsync(Arg.Any<DateTime?>(), Arg.Any<DateTime?>()).Returns([]);
        var settingsService = Substitute.For<IAppSettingsService>();
        settingsService.Settings.Returns(new AppSettings());
        context.Services.AddSingleton(repository);
        context.Services.AddSingleton(settingsService);
        AddWatchRuntimeServices(context);
        context.JSInterop.SetupVoid("stopMediaPlayer", "videoPlayer").SetVoidResult();
        context.JSInterop.Setup<string?>("initFmp4Player", invocation => invocation.Arguments.Count == 3).SetResult(null);
        // Act
        var component = context.Render<Watch>(parameters => parameters.Add(page => page.ChannelNumber, "2.1"));
        // Assert
        component.WaitForAssertion(() => Assert.NotNull(component.Find("#videoPlayer")));

        component.Find("#streamQuality").Change(nameof(WebPlayerQuality.Low));

        component.WaitForAssertion(() =>
        {
            var initializations = context.JSInterop.Invocations
                .Where(invocation => invocation.Identifier == "initFmp4Player")
                .ToArray();
            Assert.True(initializations.Length >= 2);
            Assert.Contains("quality=Low", Assert.IsType<string>(initializations[^1].Arguments[1]));
            Assert.Contains(context.JSInterop.Invocations, invocation => invocation.Identifier == "stopMediaPlayer");
        });
        Assert.Equal(nameof(WebPlayerQuality.Low), component.Find("#streamQuality").GetAttribute("value"));
    }

    /// <summary>
    /// Verifies audio and subtitle choices restart only this Watch client and preserve quality.
    /// </summary>
    [Fact]
    public void TrackSelection_RestartsCurrentPlayerWithValidatedIndexes()
    {
        // Arrange
        using var context = new BunitContext();
        var repository = Substitute.For<IEpgRepository>();
        repository.GetChannelsAsync().Returns(
        [
            new HDHomeRunChannelEpgSegment { GuideNumber = "2.1", GuideName = "Test Channel" }
        ]);
        repository.GetProgramsAsync(Arg.Any<DateTime?>(), Arg.Any<DateTime?>()).Returns([]);
        var settingsService = Substitute.For<IAppSettingsService>();
        settingsService.Settings.Returns(new AppSettings());
        var registry = new ActiveStreamRegistry();
        registry.Register(new ActiveStreamSnapshot(
            "source",
            "2.1",
            HostedStreamFormat.FragmentedMp4,
            DateTime.UtcNow,
            null,
            [
                Track(1, MediaTrackType.Audio, "ac3", "eng", selected: true),
                Track(3, MediaTrackType.Audio, "ac3", "spa"),
                Track(5, MediaTrackType.Subtitle, "subrip", "eng", SubtitlePresentation.WebVtt),
                Track(7, MediaTrackType.Subtitle, "dvb_subtitle", "spa", SubtitlePresentation.BurnIn)
            ]));
        context.Services.AddSingleton(repository);
        context.Services.AddSingleton(settingsService);
        AddWatchRuntimeServices(context, activeStreamRegistry: registry);
        context.JSInterop.SetupVoid("stopMediaPlayer", "videoPlayer").SetVoidResult();
        context.JSInterop.Setup<string?>("initFmp4Player", invocation => invocation.Arguments.Count is 3 or 4).SetResult(null);
        var component = context.Render<Watch>(parameters => parameters.Add(page => page.ChannelNumber, "2.1"));
        component.WaitForAssertion(() => Assert.Equal(2, component.FindAll("#audioTrack option").Count));

        // Act
        component.Find("#audioTrack").Change("3");
        component.WaitForAssertion(() => Assert.Contains("audioTrack=3", LastStreamUrl(context)));
        component.Find("#subtitleTrack").Change("5");

        // Assert
        component.WaitForAssertion(() =>
        {
            var invocation = context.JSInterop.Invocations.Last(item => item.Identifier == "initFmp4Player");
            var streamUrl = Assert.IsType<string>(invocation.Arguments[1]);
            Assert.Contains("audioTrack=3", streamUrl);
            Assert.Contains("subtitleTrack=5", streamUrl);
            Assert.Equal(4, invocation.Arguments.Count);
            Assert.Contains("burn-in; higher CPU", component.Markup);
        });
    }

    /// <summary>
    /// Verifies that a diagnosed tuner restriction is displayed after media initialization fails.
    /// </summary>
    [Fact]
    public void PlayerInitializationFailure_DisplaysDiagnosedTunerError()
    {
        // Arrange
        using var context = new BunitContext();
        var repository = Substitute.For<IEpgRepository>();
        repository.GetChannelsAsync().Returns(
        [
            new HDHomeRunChannelEpgSegment
            {
                GuideNumber = "117.1",
                GuideName = "Protected Channel"
            }
        ]);
        repository.GetProgramsAsync(Arg.Any<DateTime?>(), Arg.Any<DateTime?>()).Returns([]);

        var settingsService = Substitute.For<IAppSettingsService>();
        settingsService.Settings.Returns(new AppSettings());
        context.Services.AddSingleton(repository);
        context.Services.AddSingleton(settingsService);
        AddWatchRuntimeServices(context);

        context.JSInterop.SetupVoid("stopMediaPlayer", "videoPlayer").SetVoidResult();
        context.JSInterop
            .Setup<string?>("initFmp4Player", invocation => invocation.Arguments.Count == 3)
            .SetResult("811 Content Protection Required");

        // Act
        var component = context.Render<Watch>(parameters => parameters.Add(page => page.ChannelNumber, "117.1"));

        // Assert
        component.WaitForAssertion(() =>
        {
            Assert.Contains("811 Content Protection Required", component.Markup);
            Assert.Contains("Content Protected", component.Markup);
            Assert.Contains("authorized device or application", component.Markup);
            Assert.Empty(component.FindAll("#videoPlayer"));
        });
    }

    /// <summary>
    /// Verifies that cached DRM channels display an accessible protection marker.
    /// </summary>
    [Fact]
    public void DrmChannel_DisplaysProtectionMarker()
    {
        // Arrange
        using var context = new BunitContext();
        var repository = Substitute.For<IEpgRepository>();
        repository.GetChannelsAsync().Returns(
        [
            new HDHomeRunChannelEpgSegment { GuideNumber = "117.1", GuideName = "Protected", DRM = true }
        ]);
        repository.GetProgramsAsync(Arg.Any<DateTime?>(), Arg.Any<DateTime?>()).Returns([]);
        var settingsService = Substitute.For<IAppSettingsService>();
        settingsService.Settings.Returns(new AppSettings());
        context.Services.AddSingleton(repository);
        context.Services.AddSingleton(settingsService);
        AddWatchRuntimeServices(context);

        // Act
        var component = context.Render<Watch>();

        // Assert
        component.WaitForAssertion(() =>
        {
            var marker = component.Find("[aria-label='DRM-protected channel']");
            Assert.Contains("bi-shield-lock-fill", marker.ClassList);
        });
    }

    /// <summary>
    /// Verifies that cached favorite channels display an accessible favorite marker.
    /// </summary>
    [Fact]
    public void FavoriteChannel_DisplaysFavoriteMarker()
    {
        // Arrange
        using var context = new BunitContext();
        var repository = Substitute.For<IEpgRepository>();
        repository.GetChannelsAsync().Returns([new HDHomeRunChannelEpgSegment { GuideNumber = "7.1", GuideName = "Favorite", Favorite = true }]);
        repository.GetProgramsAsync(Arg.Any<DateTime?>(), Arg.Any<DateTime?>()).Returns([]);
        var settingsService = Substitute.For<IAppSettingsService>();
        settingsService.Settings.Returns(new AppSettings());
        context.Services.AddSingleton(repository);
        context.Services.AddSingleton(settingsService);
        AddWatchRuntimeServices(context);

        // Act
        var component = context.Render<Watch>();

        // Assert
        component.WaitForAssertion(() =>
        {
            var marker = component.Find("[aria-label='Favorite channel']");
            Assert.Contains("bi-star-fill", marker.ClassList);
        });
    }

    /// <summary>
    /// Verifies that the selected channel displays its tuner and hosted stream details.
    /// </summary>
    [Fact]
    public void SelectedChannel_DisplaysTunerAndHostedStreamDetails()
    {
        // Arrange
        using var context = new BunitContext();
        var repository = Substitute.For<IEpgRepository>();
        repository.GetChannelsAsync().Returns(
        [
            new HDHomeRunChannelEpgSegment { GuideNumber = "2.1", GuideName = "Test Channel" }
        ]);
        repository.GetProgramsAsync(Arg.Any<DateTime?>(), Arg.Any<DateTime?>()).Returns([]);
        var settingsService = Substitute.For<IAppSettingsService>();
        settingsService.Settings.Returns(new AppSettings());
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
        var registry = new ActiveStreamRegistry();
        registry.Register(new ActiveStreamSnapshot(
            "session1",
            "2.1",
            HostedStreamFormat.FragmentedMp4,
            DateTime.UtcNow,
            18_000_000,
            [
                new ActiveStreamTrack(MediaTrackType.Video, "hevc", "h264", null, 2_500_000, 1920, 1080, null, null, null),
                new ActiveStreamTrack(MediaTrackType.Audio, "ac4", "aac", null, 128_000, null, null, 6, 2, 44_100)
            ]));
        context.Services.AddSingleton(repository);
        context.Services.AddSingleton(settingsService);
        AddWatchRuntimeServices(context, deviceState, registry);
        context.JSInterop.SetupVoid("stopMediaPlayer", "videoPlayer").SetVoidResult();
        context.JSInterop
            .Setup<string?>("initFmp4Player", invocation => invocation.Arguments.Count == 3)
            .SetResult(null);

        // Act
        var component = context.Render<Watch>(parameters => parameters.Add(page => page.ChannelNumber, "2.1"));

        // Assert
        component.WaitForAssertion(() =>
        {
            Assert.Contains("8vsb", component.Markup);
            Assert.Contains("85%", component.Markup);
            Assert.Contains("session1", component.Markup);
            Assert.Contains("hevc", component.Markup);
            Assert.Contains("h264", component.Markup);
            Assert.Contains("ac4", component.Markup);
            Assert.Contains("aac", component.Markup);
        });
    }

    private static void AddWatchRuntimeServices(BunitContext context, IDeviceStateService? deviceState = null, IActiveStreamRegistry? activeStreamRegistry = null)
    {
        if (deviceState == null)
        {
            deviceState = Substitute.For<IDeviceStateService>();
            deviceState.TunerStatuses.Returns([]);
        }

        context.Services.AddSingleton(deviceState);
        context.Services.AddSingleton(activeStreamRegistry ?? new ActiveStreamRegistry());
    }

    private static ActiveStreamTrack Track(int index, MediaTrackType type, string codec, string language, SubtitlePresentation? presentation = null, bool selected = false) =>
        new(type, codec, selected ? "aac" : "not-mapped", null, null, null, null, type == MediaTrackType.Audio ? 2 : null, null, null)
        {
            SourceIndex = index,
            Language = language,
            IsSelected = selected,
            SubtitlePresentation = presentation
        };

    private static string LastStreamUrl(BunitContext context) =>
        Assert.IsType<string>(context.JSInterop.Invocations.Last(item => item.Identifier == "initFmp4Player").Arguments[1]);
}
