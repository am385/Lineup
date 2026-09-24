using Bunit;
using Lineup.Core;
using Lineup.Core.Storage;
using Lineup.HDHomeRun.Api.Models;
using Lineup.HDHomeRun.Device.Models;
using Lineup.HDHomeRun.Device.Protocol;
using Lineup.Web.Components.Pages;
using Lineup.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using System.Reflection;
using System.Text.Json;
using Xunit;

namespace Lineup.Web.Tests.Components.Pages;

/// <summary>
/// Verifies the Watch page media-player lifecycle.
/// </summary>
public class WatchTests
{
    private const string PreferencesStorageKey = "lineup-watch-preferences-v1";

    /// <summary>
    /// Verifies discovered enabled channels appear even when no guide has been imported.
    /// </summary>
    [Fact]
    public async Task PhysicalChannelWithoutGuideData_IsListed()
    {
        // Arrange
        using var context = new BunitContext();
        var repository = Substitute.For<IEpgRepository>();
        repository.GetChannelsAsync().Returns([]);
        repository.GetProgramsAsync(Arg.Any<DateTime?>(), Arg.Any<DateTime?>()).Returns([]);
        var settingsService = Substitute.For<IAppSettingsService>();
        settingsService.Settings.Returns(new AppSettings());
        var channelStore = new ChannelLineupStore(Path.Combine(Path.GetTempPath(), $"lineup-watch-{Guid.NewGuid():N}.db"));
        await channelStore.StoreAsync(
        [
            new HDHomeRunChannel
            {
                GuideNumber = "42.1",
                GuideName = "Discovered Channel",
                Favorite = true,
                URL = "http://device/auto/v42.1"
            }
        ], Xunit.TestContext.Current.CancellationToken);
        context.Services.AddSingleton(repository);
        context.Services.AddSingleton(settingsService);
        AddWatchRuntimeServices(context, channelStore: channelStore);

        // Act
        var component = context.Render<Watch>();

        // Assert
        component.WaitForAssertion(() =>
        {
            var channel = Assert.Single(component.FindAll(".channel-item"));
            Assert.Contains("42.1", channel.TextContent);
            Assert.Contains("Discovered Channel", channel.TextContent);
            Assert.NotNull(channel.QuerySelector(".bi-star-fill"));
        });
    }

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
        context.JSInterop.Setup<string?>("initFmp4Player", invocation => invocation.Arguments.Count == 4).SetResult(null);
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
    /// Verifies changing audio output restarts Watch playback with the selected layout.
    /// </summary>
    [Fact]
    public void UpTo7Point1AudioOutput_RestartsPlaybackWithSessionOverride()
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
        component.Find("#manualTuneChannel").Input("42.1");
        component.Find("form").Submit();
        component.WaitForAssertion(() => Assert.NotNull(component.Find("#videoPlayer")));

        // Act
        component.Find("#audioOutput").Change(WatchAudioOutput.UpTo7Point1.ToString());

        // Assert
        component.WaitForAssertion(() =>
        {
            var initializations = context.JSInterop.Invocations.Where(invocation => invocation.Identifier == "initFmp4Player").ToArray();
            Assert.Equal(2, initializations.Length);
            Assert.Contains("audioOutput=UpTo7Point1", Assert.IsType<string>(initializations[^1].Arguments[1]));
        });
    }

    /// <summary>
    /// Verifies unsupported source audio temporarily falls back to Stereo AAC without changing the preference.
    /// </summary>
    [Fact]
    public void SourceAudioOutput_PlaybackFailureFallsBackToStereo()
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
        AddWatchRuntimeServices(
            context,
            preferencesJson: """{"Quality":0,"AudioOutput":3,"SubtitlesEnabled":false,"Subtitle":null}""");
        context.JSInterop.SetupVoid("stopMediaPlayer", "videoPlayer").SetVoidResult();
        context.JSInterop.SetupVoid("localStorage.setItem", _ => true).SetVoidResult();
        context.JSInterop
            .Setup<string?>(
                "initFmp4Player",
                invocation => Assert.IsType<string>(invocation.Arguments[1]).Contains("audioOutput=Source", StringComparison.Ordinal))
            .SetResult("The source audio codec is not supported.");
        context.JSInterop
            .Setup<string?>(
                "initFmp4Player",
                invocation => Assert.IsType<string>(invocation.Arguments[1]).Contains("audioOutput=Stereo", StringComparison.Ordinal))
            .SetResult(null);

        // Act
        var component = context.Render<Watch>(parameters => parameters.Add(page => page.ChannelNumber, "42.1"));

        // Assert
        component.WaitForAssertion(() =>
        {
            var initializations = context.JSInterop.Invocations.Where(invocation => invocation.Identifier == "initFmp4Player").ToArray();
            Assert.Equal(2, initializations.Length);
            Assert.Contains("audioOutput=Source", Assert.IsType<string>(initializations[0].Arguments[1]));
            Assert.Contains("audioOutput=Stereo", Assert.IsType<string>(initializations[1].Arguments[1]));
            Assert.Equal(nameof(WatchAudioOutput.Source), component.Find("#audioOutput").GetAttribute("value"));
            var notifications = context.Services.GetRequiredService<IStatusNotificationService>();
            var notification = Assert.Single(notifications.Notifications);
            Assert.Equal("Source audio could not be played by this browser. Retrying this stream with Stereo AAC.", notification.Message);
            Assert.True(notification.IsError);
            Assert.DoesNotContain(
                context.JSInterop.Invocations,
                invocation => invocation.Identifier == "localStorage.setItem");
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
    /// Verifies that leaving an active Watch page explicitly stops its server-side stream.
    /// </summary>
    [Fact]
    public async Task DisposeDuringPlayback_StopsOwnedActiveStream()
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
        var streamStopped = false;
        context.Services.AddSingleton(repository);
        context.Services.AddSingleton(settingsService);
        AddWatchRuntimeServices(context, activeStreamRegistry: registry);
        context.JSInterop.SetupVoid("stopMediaPlayer", "videoPlayer").SetVoidResult();
        context.JSInterop.Setup<string?>("initFmp4Player", invocation => invocation.Arguments.Count == 3).SetResult(null);
        var component = context.Render<Watch>(parameters => parameters.Add(page => page.ChannelNumber, "2.1"));
        component.WaitForAssertion(() => Assert.NotNull(component.Find("#videoPlayer")));
        component.WaitForAssertion(() => Assert.Single(context.JSInterop.Invocations, invocation => invocation.Identifier == "initFmp4Player"));
        var streamUrl = LastStreamUrl(context);
        var clientIdStart = streamUrl.IndexOf("clientId=", StringComparison.Ordinal) + "clientId=".Length;
        var clientId = streamUrl[clientIdStart..].Split('&')[0];
        registry.Register(
            new ActiveStreamSnapshot("session", "2.1", HostedStreamFormat.FragmentedMp4, DateTime.UtcNow, null, []) { ClientId = clientId },
            () =>
            {
                streamStopped = true;
                registry.Unregister("session");
            });

        // Act
        await component.Instance.DisposeAsync();

        // Assert
        Assert.True(streamStopped);
        Assert.Empty(registry.GetActiveStreams());
        Assert.Contains(context.JSInterop.Invocations, invocation => invocation.Identifier == "stopMediaPlayer");
    }

    /// <summary>
    /// Verifies that client-side navigation stops the active stream before the Watch page is removed.
    /// </summary>
    [Fact]
    public void NavigationAwayDuringPlayback_StopsOwnedActiveStream()
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
        var streamStopped = false;
        context.Services.AddSingleton(repository);
        context.Services.AddSingleton(settingsService);
        AddWatchRuntimeServices(context, activeStreamRegistry: registry);
        context.JSInterop.SetupVoid("stopMediaPlayer", "videoPlayer").SetVoidResult();
        context.JSInterop.Setup<string?>("initFmp4Player", invocation => invocation.Arguments.Count == 3).SetResult(null);
        var component = context.Render<Watch>(parameters => parameters.Add(page => page.ChannelNumber, "2.1"));
        component.WaitForAssertion(() => Assert.NotNull(component.Find("#videoPlayer")));
        component.WaitForAssertion(() => Assert.Single(context.JSInterop.Invocations, invocation => invocation.Identifier == "initFmp4Player"));
        var streamUrl = LastStreamUrl(context);
        var clientIdStart = streamUrl.IndexOf("clientId=", StringComparison.Ordinal) + "clientId=".Length;
        var clientId = streamUrl[clientIdStart..].Split('&')[0];
        registry.Register(
            new ActiveStreamSnapshot("session", "2.1", HostedStreamFormat.FragmentedMp4, DateTime.UtcNow, null, []) { ClientId = clientId },
            () =>
            {
                streamStopped = true;
                registry.Unregister("session");
            });
        var navigation = context.Services.GetRequiredService<NavigationManager>();

        // Act
        navigation.NavigateTo("/dashboard");

        // Assert
        component.WaitForAssertion(() =>
        {
            Assert.True(streamStopped);
            Assert.Empty(registry.GetActiveStreams());
            Assert.Contains(context.JSInterop.Invocations, invocation => invocation.Identifier == "stopMediaPlayer");
        });
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
            () => registry.Unregister("session"));

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
            Assert.Contains("subtitlePresentation=WebVtt", streamUrl);
            Assert.Equal(4, invocation.Arguments.Count);
            Assert.Contains("burn-in; higher CPU", component.Markup);
            var savedState = JsonDocument.Parse(
                Assert.IsType<string>(
                    context.JSInterop.Invocations.Last(item => item.Identifier == "localStorage.setItem").Arguments[1])).RootElement;
            Assert.True(savedState.GetProperty("SubtitlesEnabled").GetBoolean());
            Assert.Equal("eng", savedState.GetProperty("Subtitle").GetProperty("Language").GetString());
            Assert.False(savedState.TryGetProperty("AudioTrack", out _));
        });
    }

    /// <summary>
    /// Verifies selecting embedded ATSC captions carries their extraction mode through the restart URL.
    /// </summary>
    [Fact]
    public void EmbeddedCaptionSelection_RestartIdentifiesSyntheticTrack()
    {
        // Arrange
        using var context = new BunitContext();
        var repository = Substitute.For<IEpgRepository>();
        repository.GetChannelsAsync().Returns([]);
        repository.GetProgramsAsync(Arg.Any<DateTime?>(), Arg.Any<DateTime?>()).Returns([]);
        var settingsService = Substitute.For<IAppSettingsService>();
        settingsService.Settings.Returns(new AppSettings());
        var registry = Substitute.For<IActiveStreamRegistry>();
        registry.GetActiveStreams().Returns(
        [
            new ActiveStreamSnapshot(
                "stream",
                "2.6",
                HostedStreamFormat.FragmentedMp4,
                DateTime.UtcNow,
                null,
                [Track(2, MediaTrackType.Subtitle, "eia_608", "und", SubtitlePresentation.WebVtt) with { IsEmbeddedClosedCaptions = true }])
        ]);
        context.Services.AddSingleton(repository);
        context.Services.AddSingleton(settingsService);
        AddWatchRuntimeServices(context, activeStreamRegistry: registry);
        context.JSInterop.SetupVoid("stopMediaPlayer", "videoPlayer").SetVoidResult();
        context.JSInterop.Setup<string?>("initFmp4Player", invocation => invocation.Arguments.Count is 3 or 4).SetResult(null);
        var component = context.Render<Watch>(parameters => parameters.Add(page => page.ChannelNumber, "2.6"));
        component.WaitForElement("#subtitleTrack");

        // Act
        component.Find("#subtitleTrack").Change("2");

        // Assert
        component.WaitForAssertion(() =>
        {
            var streamUrl = LastStreamUrl(context);
            Assert.Contains("subtitleTrack=2", streamUrl);
            Assert.Contains("subtitlePresentation=WebVtt", streamUrl);
            Assert.Contains("embeddedCaptions=true", streamUrl);
        });
    }

    /// <summary>
    /// Verifies saved quality and audio output are restored before direct-route playback starts.
    /// </summary>
    [Fact]
    public void SavedPlaybackPreferences_DirectRouteStartsOnceWithRestoredValues()
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
        AddWatchRuntimeServices(
            context,
            preferencesJson: """{"Quality":3,"AudioOutput":2,"SubtitlesEnabled":false,"Subtitle":null}""");
        context.JSInterop.SetupVoid("stopMediaPlayer", "videoPlayer").SetVoidResult();
        context.JSInterop.Setup<string?>("initFmp4Player", invocation => invocation.Arguments.Count == 3).SetResult(null);

        // Act
        var component = context.Render<Watch>(parameters => parameters.Add(page => page.ChannelNumber, "42.1"));

        // Assert
        component.WaitForAssertion(() =>
        {
            var initialization = Assert.Single(context.JSInterop.Invocations, invocation => invocation.Identifier == "initFmp4Player");
            var url = Assert.IsType<string>(initialization.Arguments[1]);
            Assert.Contains("quality=Low", url);
            Assert.Contains("audioOutput=UpTo7Point1", url);
            Assert.Equal(nameof(WebPlayerQuality.Low), component.Find("#streamQuality").GetAttribute("value"));
            Assert.Equal(nameof(WatchAudioOutput.UpTo7Point1), component.Find("#audioOutput").GetAttribute("value"));
        });
    }

    /// <summary>
    /// Verifies quality and audio-output changes persist the complete Watch preference state.
    /// </summary>
    [Fact]
    public void PlaybackPreferenceChanges_AreSavedToBrowserStorage()
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
        context.JSInterop.SetupVoid("localStorage.setItem", _ => true).SetVoidResult();
        var component = context.Render<Watch>();

        // Act
        component.Find("#streamQuality").Change(nameof(WebPlayerQuality.Medium));
        component.Find("#audioOutput").Change(nameof(WatchAudioOutput.UpTo5Point1));

        // Assert
        component.WaitForAssertion(() =>
        {
            var savedState = JsonDocument.Parse(
                Assert.IsType<string>(
                    context.JSInterop.Invocations.Last(item => item.Identifier == "localStorage.setItem").Arguments[1])).RootElement;
            Assert.Equal((int)WebPlayerQuality.Medium, savedState.GetProperty("Quality").GetInt32());
            Assert.Equal((int)WatchAudioOutput.UpTo5Point1, savedState.GetProperty("AudioOutput").GetInt32());
        });
    }

    /// <summary>
    /// Verifies invalid saved preferences retain the compatibility defaults.
    /// </summary>
    [Fact]
    public void InvalidSavedPreferences_FallBackToDefaults()
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
        AddWatchRuntimeServices(context, preferencesJson: """{"Quality":99,"AudioOutput":99,"SubtitlesEnabled":true,"Subtitle":null}""");

        // Act
        var component = context.Render<Watch>();

        // Assert
        Assert.Equal(nameof(WebPlayerQuality.AppDefault), component.Find("#streamQuality").GetAttribute("value"));
        Assert.Equal(nameof(WatchAudioOutput.Stereo), component.Find("#audioOutput").GetAttribute("value"));
    }

    /// <summary>
    /// Verifies malformed browser storage does not prevent the Watch page from using safe defaults.
    /// </summary>
    [Fact]
    public void MalformedSavedPreferences_FallBackToDefaults()
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
        AddWatchRuntimeServices(context, preferencesJson: "{invalid");

        // Act
        var component = context.Render<Watch>();

        // Assert
        Assert.Equal(nameof(WebPlayerQuality.AppDefault), component.Find("#streamQuality").GetAttribute("value"));
        Assert.Equal(nameof(WatchAudioOutput.Stereo), component.Find("#audioOutput").GetAttribute("value"));
    }

    /// <summary>
    /// Verifies a saved subtitle language maps to the current stream index before playback begins.
    /// </summary>
    [Fact]
    public void SavedSubtitlePreference_MatchingTrackIsSelected()
    {
        // Arrange
        using var context = new BunitContext();
        var repository = Substitute.For<IEpgRepository>();
        repository.GetChannelsAsync().Returns([]);
        repository.GetProgramsAsync(Arg.Any<DateTime?>(), Arg.Any<DateTime?>()).Returns([]);
        var settingsService = Substitute.For<IAppSettingsService>();
        settingsService.Settings.Returns(new AppSettings());
        var registry = new ActiveStreamRegistry();
        registry.Register(new ActiveStreamSnapshot(
            "source",
            "42.1",
            HostedStreamFormat.FragmentedMp4,
            DateTime.UtcNow,
            null,
            [Track(7, MediaTrackType.Subtitle, "subrip", "eng", SubtitlePresentation.WebVtt)]));
        context.Services.AddSingleton(repository);
        context.Services.AddSingleton(settingsService);
        AddWatchRuntimeServices(
            context,
            activeStreamRegistry: registry,
            preferencesJson: """{"Quality":0,"AudioOutput":0,"SubtitlesEnabled":true,"Subtitle":{"Language":"eng","Title":null,"SourceCodec":"eia_608","IsForced":false,"IsHearingImpaired":false}}""");
        context.JSInterop.Setup<string?>("initFmp4Player", invocation => invocation.Arguments.Count == 4).SetResult(null);

        // Act
        var component = context.Render<Watch>(parameters => parameters.Add(page => page.ChannelNumber, "42.1"));

        // Assert
        component.WaitForAssertion(() =>
        {
            var initialization = Assert.Single(context.JSInterop.Invocations, invocation => invocation.Identifier == "initFmp4Player");
            Assert.Contains("subtitleTrack=7", Assert.IsType<string>(initialization.Arguments[1]));
            Assert.Equal(4, initialization.Arguments.Count);
        });
    }

    /// <summary>
    /// Verifies a saved subtitle preference falls back to the first supported track when its language is unavailable.
    /// </summary>
    [Fact]
    public void SavedSubtitlePreference_UnavailableLanguageSelectsFirstSupportedTrack()
    {
        // Arrange
        using var context = new BunitContext();
        var repository = Substitute.For<IEpgRepository>();
        repository.GetChannelsAsync().Returns([]);
        repository.GetProgramsAsync(Arg.Any<DateTime?>(), Arg.Any<DateTime?>()).Returns([]);
        var settingsService = Substitute.For<IAppSettingsService>();
        settingsService.Settings.Returns(new AppSettings());
        var registry = new ActiveStreamRegistry();
        registry.Register(new ActiveStreamSnapshot(
            "source",
            "42.1",
            HostedStreamFormat.FragmentedMp4,
            DateTime.UtcNow,
            null,
            [Track(7, MediaTrackType.Subtitle, "subrip", string.Empty, SubtitlePresentation.WebVtt)]));
        context.Services.AddSingleton(repository);
        context.Services.AddSingleton(settingsService);
        AddWatchRuntimeServices(
            context,
            activeStreamRegistry: registry,
            preferencesJson: """{"Quality":0,"AudioOutput":0,"SubtitlesEnabled":true,"Subtitle":{"Language":"eng","Title":null,"SourceCodec":"subrip","IsForced":false,"IsHearingImpaired":false}}""");
        context.JSInterop.Setup<string?>("initFmp4Player", invocation => invocation.Arguments.Count == 3).SetResult(null);

        // Act
        var component = context.Render<Watch>(parameters => parameters.Add(page => page.ChannelNumber, "42.1"));

        // Assert
        component.WaitForAssertion(() =>
        {
            var initialization = Assert.Single(context.JSInterop.Invocations, invocation => invocation.Identifier == "initFmp4Player");
            Assert.Contains("subtitleTrack=7", Assert.IsType<string>(initialization.Arguments[1]));
            Assert.Equal(4, initialization.Arguments.Count);
            Assert.DoesNotContain(context.JSInterop.Invocations, invocation => invocation.Identifier == "localStorage.setItem");
        });
    }

    /// <summary>
    /// Verifies an embedded preference survives a regular-subtitle fallback and is restored when switching back.
    /// </summary>
    [Fact]
    public void ChannelSwitch_EmbeddedToRegularAndBackPreservesExplicitPreference()
    {
        // Arrange
        using var context = new BunitContext();
        var repository = Substitute.For<IEpgRepository>();
        repository.GetChannelsAsync().Returns([]);
        repository.GetProgramsAsync(Arg.Any<DateTime?>(), Arg.Any<DateTime?>()).Returns([]);
        var settingsService = Substitute.For<IAppSettingsService>();
        settingsService.Settings.Returns(new AppSettings());
        var registry = new ActiveStreamRegistry();
        registry.Register(new ActiveStreamSnapshot(
            "embedded",
            "2.6",
            HostedStreamFormat.FragmentedMp4,
            DateTime.UtcNow,
            null,
            [
                Track(2, MediaTrackType.Subtitle, "eia_608", string.Empty, SubtitlePresentation.WebVtt) with
                {
                    IsEmbeddedClosedCaptions = true,
                    Title = "Closed Captions"
                }
            ]));
        registry.Register(new ActiveStreamSnapshot(
            "regular",
            "3.1",
            HostedStreamFormat.FragmentedMp4,
            DateTime.UtcNow,
            null,
            [Track(8, MediaTrackType.Subtitle, "subrip", "eng", SubtitlePresentation.WebVtt)]));
        context.Services.AddSingleton(repository);
        context.Services.AddSingleton(settingsService);
        AddWatchRuntimeServices(
            context,
            activeStreamRegistry: registry,
            preferencesJson:
                """
                {
                  "Quality": 0,
                  "AudioOutput": 0,
                  "SubtitlesEnabled": true,
                  "Subtitle": {
                    "Language": null,
                    "Title": "Closed Captions",
                    "SourceCodec": "eia_608",
                    "IsForced": false,
                    "IsHearingImpaired": false,
                    "IsEmbeddedClosedCaptions": true
                  }
                }
                """);
        context.JSInterop.SetupVoid("stopMediaPlayer", "videoPlayer").SetVoidResult();
        context.JSInterop.Setup<string?>("initFmp4Player", invocation => invocation.Arguments.Count == 4).SetResult(null);
        var component = context.Render<Watch>(parameters => parameters.Add(page => page.ChannelNumber, "2.6"));
        component.WaitForAssertion(() => Assert.Contains("embeddedCaptions=true", LastStreamUrl(context)));

        // Act
        component.Find("#manualTuneChannel").Input("3.1");
        component.Find("form").Submit();
        component.WaitForAssertion(() =>
        {
            var regularUrl = LastStreamUrl(context);
            Assert.Contains("/api/stream/fmp4/3.1?", regularUrl);
            Assert.Contains("subtitleTrack=8", regularUrl);
            Assert.DoesNotContain("embeddedCaptions=true", regularUrl);
        });
        component.Find("#manualTuneChannel").Input("2.6");
        component.Find("form").Submit();

        // Assert
        component.WaitForAssertion(() =>
        {
            var embeddedUrl = LastStreamUrl(context);
            Assert.Contains("/api/stream/fmp4/2.6?", embeddedUrl);
            Assert.Contains("subtitleTrack=2", embeddedUrl);
            Assert.Contains("embeddedCaptions=true", embeddedUrl);
            Assert.DoesNotContain(context.JSInterop.Invocations, invocation => invocation.Identifier == "localStorage.setItem");
        });
    }

    /// <summary>
    /// Verifies same-language subtitles take priority over an earlier supported fallback.
    /// </summary>
    [Fact]
    public void SavedSubtitlePreference_SameLanguageTakesPriorityOverFirstSupportedTrack()
    {
        // Arrange
        using var context = new BunitContext();
        var repository = Substitute.For<IEpgRepository>();
        repository.GetChannelsAsync().Returns([]);
        repository.GetProgramsAsync(Arg.Any<DateTime?>(), Arg.Any<DateTime?>()).Returns([]);
        var settingsService = Substitute.For<IAppSettingsService>();
        settingsService.Settings.Returns(new AppSettings());
        var registry = new ActiveStreamRegistry();
        registry.Register(new ActiveStreamSnapshot(
            "source",
            "42.1",
            HostedStreamFormat.FragmentedMp4,
            DateTime.UtcNow,
            null,
            [
                Track(5, MediaTrackType.Subtitle, "subrip", "spa", SubtitlePresentation.WebVtt),
                Track(7, MediaTrackType.Subtitle, "subrip", "eng", SubtitlePresentation.WebVtt)
            ]));
        context.Services.AddSingleton(repository);
        context.Services.AddSingleton(settingsService);
        AddWatchRuntimeServices(
            context,
            activeStreamRegistry: registry,
            preferencesJson:
                """
                {
                  "Quality": 0,
                  "AudioOutput": 0,
                  "SubtitlesEnabled": true,
                  "Subtitle": {
                    "Language": "eng",
                    "Title": "Previous",
                    "SourceCodec": "eia_608",
                    "IsForced": false,
                    "IsHearingImpaired": false,
                    "IsEmbeddedClosedCaptions": true
                  }
                }
                """);
        context.JSInterop.Setup<string?>("initFmp4Player", invocation => invocation.Arguments.Count == 4).SetResult(null);

        // Act
        var component = context.Render<Watch>(parameters => parameters.Add(page => page.ChannelNumber, "42.1"));

        // Assert
        component.WaitForAssertion(() =>
        {
            var streamUrl = LastStreamUrl(context);
            Assert.Contains("subtitleTrack=7", streamUrl);
            Assert.Contains("subtitlePresentation=WebVtt", streamUrl);
            Assert.DoesNotContain("embeddedCaptions=true", streamUrl);
        });
    }

    /// <summary>
    /// Verifies embedded-caption preferences safely fall back to a regular subtitle without changing browser storage.
    /// </summary>
    [Fact]
    public void SavedEmbeddedCaptionPreference_RegularSubtitleFallbackUsesCurrentTrackMetadata()
    {
        // Arrange
        using var context = new BunitContext();
        var repository = Substitute.For<IEpgRepository>();
        repository.GetChannelsAsync().Returns([]);
        repository.GetProgramsAsync(Arg.Any<DateTime?>(), Arg.Any<DateTime?>()).Returns([]);
        var settingsService = Substitute.For<IAppSettingsService>();
        settingsService.Settings.Returns(new AppSettings());
        var registry = new ActiveStreamRegistry();
        registry.Register(new ActiveStreamSnapshot(
            "source",
            "42.1",
            HostedStreamFormat.FragmentedMp4,
            DateTime.UtcNow,
            null,
            [Track(8, MediaTrackType.Subtitle, "dvb_subtitle", "eng", SubtitlePresentation.BurnIn)]));
        context.Services.AddSingleton(repository);
        context.Services.AddSingleton(settingsService);
        AddWatchRuntimeServices(
            context,
            activeStreamRegistry: registry,
            preferencesJson:
                """
                {
                  "Quality": 0,
                  "AudioOutput": 0,
                  "SubtitlesEnabled": true,
                  "Subtitle": {
                    "Language": "eng",
                    "Title": "Closed Captions",
                    "SourceCodec": "eia_608",
                    "IsForced": false,
                    "IsHearingImpaired": false,
                    "IsEmbeddedClosedCaptions": true
                  }
                }
                """);
        context.JSInterop.Setup<string?>("initFmp4Player", invocation => invocation.Arguments.Count == 3).SetResult(null);

        // Act
        var component = context.Render<Watch>(parameters => parameters.Add(page => page.ChannelNumber, "42.1"));

        // Assert
        component.WaitForAssertion(() =>
        {
            var streamUrl = LastStreamUrl(context);
            Assert.Contains("subtitleTrack=8", streamUrl);
            Assert.Contains("subtitlePresentation=BurnIn", streamUrl);
            Assert.DoesNotContain("embeddedCaptions=true", streamUrl);
            Assert.Equal(3, context.JSInterop.Invocations.Last(invocation => invocation.Identifier == "initFmp4Player").Arguments.Count);
            Assert.DoesNotContain(context.JSInterop.Invocations, invocation => invocation.Identifier == "localStorage.setItem");
        });
    }

    /// <summary>
    /// Verifies regular subtitle preferences can fall back to embedded captions using extractor retry metadata.
    /// </summary>
    [Fact]
    public void SavedRegularSubtitlePreference_EmbeddedCaptionFallbackUsesCurrentTrackMetadata()
    {
        // Arrange
        using var context = new BunitContext();
        var repository = Substitute.For<IEpgRepository>();
        repository.GetChannelsAsync().Returns([]);
        repository.GetProgramsAsync(Arg.Any<DateTime?>(), Arg.Any<DateTime?>()).Returns([]);
        var settingsService = Substitute.For<IAppSettingsService>();
        settingsService.Settings.Returns(new AppSettings());
        var registry = new ActiveStreamRegistry();
        registry.Register(new ActiveStreamSnapshot(
            "source",
            "42.1",
            HostedStreamFormat.FragmentedMp4,
            DateTime.UtcNow,
            null,
            [
                Track(2, MediaTrackType.Subtitle, "eia_608", "und", SubtitlePresentation.WebVtt) with
                {
                    IsEmbeddedClosedCaptions = true,
                    Title = "Closed Captions"
                }
            ]));
        context.Services.AddSingleton(repository);
        context.Services.AddSingleton(settingsService);
        AddWatchRuntimeServices(
            context,
            activeStreamRegistry: registry,
            preferencesJson:
                """
                {
                  "Quality": 0,
                  "AudioOutput": 0,
                  "SubtitlesEnabled": true,
                  "Subtitle": {
                    "Language": "eng",
                    "Title": null,
                    "SourceCodec": "subrip",
                    "IsForced": false,
                    "IsHearingImpaired": false,
                    "IsEmbeddedClosedCaptions": false
                  }
                }
                """);
        context.JSInterop.Setup<string?>("initFmp4Player", invocation => invocation.Arguments.Count == 4).SetResult(null);

        // Act
        var component = context.Render<Watch>(parameters => parameters.Add(page => page.ChannelNumber, "42.1"));

        // Assert
        component.WaitForAssertion(() =>
        {
            var streamUrl = LastStreamUrl(context);
            Assert.Contains("subtitleTrack=2", streamUrl);
            Assert.Contains("subtitlePresentation=WebVtt", streamUrl);
            Assert.Contains("embeddedCaptions=true", streamUrl);
        });
    }

    /// <summary>
    /// Verifies a language-less preference restores a track with the same title before using fallback order.
    /// </summary>
    [Fact]
    public void SavedLanguageLessSubtitlePreference_MatchingTitleTakesPriority()
    {
        // Arrange
        using var context = new BunitContext();
        var repository = Substitute.For<IEpgRepository>();
        repository.GetChannelsAsync().Returns([]);
        repository.GetProgramsAsync(Arg.Any<DateTime?>(), Arg.Any<DateTime?>()).Returns([]);
        var settingsService = Substitute.For<IAppSettingsService>();
        settingsService.Settings.Returns(new AppSettings());
        var registry = new ActiveStreamRegistry();
        registry.Register(new ActiveStreamSnapshot(
            "source",
            "42.1",
            HostedStreamFormat.FragmentedMp4,
            DateTime.UtcNow,
            null,
            [
                Track(4, MediaTrackType.Subtitle, "subrip", "spa", SubtitlePresentation.WebVtt) with { Title = "Other" },
                Track(6, MediaTrackType.Subtitle, "subrip", string.Empty, SubtitlePresentation.WebVtt) with { Title = "CC" }
            ]));
        context.Services.AddSingleton(repository);
        context.Services.AddSingleton(settingsService);
        AddWatchRuntimeServices(
            context,
            activeStreamRegistry: registry,
            preferencesJson:
                """
                {
                  "Quality": 0,
                  "AudioOutput": 0,
                  "SubtitlesEnabled": true,
                  "Subtitle": {
                    "Language": null,
                    "Title": "CC",
                    "SourceCodec": "eia_608",
                    "IsForced": false,
                    "IsHearingImpaired": false,
                    "IsEmbeddedClosedCaptions": true
                  }
                }
                """);
        context.JSInterop.Setup<string?>("initFmp4Player", invocation => invocation.Arguments.Count == 4).SetResult(null);

        // Act
        var component = context.Render<Watch>(parameters => parameters.Add(page => page.ChannelNumber, "42.1"));

        // Assert
        component.WaitForAssertion(() => Assert.Contains("subtitleTrack=6", LastStreamUrl(context)));
    }

    /// <summary>
    /// Verifies unsupported subtitle tracks do not satisfy the enabled-subtitle fallback.
    /// </summary>
    [Fact]
    public void SavedSubtitlePreference_NoSupportedTrackLeavesSubtitlesOffForChannel()
    {
        // Arrange
        using var context = new BunitContext();
        var repository = Substitute.For<IEpgRepository>();
        repository.GetChannelsAsync().Returns([]);
        repository.GetProgramsAsync(Arg.Any<DateTime?>(), Arg.Any<DateTime?>()).Returns([]);
        var settingsService = Substitute.For<IAppSettingsService>();
        settingsService.Settings.Returns(new AppSettings());
        var registry = new ActiveStreamRegistry();
        registry.Register(new ActiveStreamSnapshot(
            "source",
            "42.1",
            HostedStreamFormat.FragmentedMp4,
            DateTime.UtcNow,
            null,
            [Track(7, MediaTrackType.Subtitle, "unknown", "eng", SubtitlePresentation.Unsupported)]));
        context.Services.AddSingleton(repository);
        context.Services.AddSingleton(settingsService);
        AddWatchRuntimeServices(
            context,
            activeStreamRegistry: registry,
            preferencesJson: """{"Quality":0,"AudioOutput":0,"SubtitlesEnabled":true,"Subtitle":{"Language":"eng","Title":null,"SourceCodec":"subrip","IsForced":false,"IsHearingImpaired":false}}""");
        context.JSInterop.Setup<string?>("initFmp4Player", invocation => invocation.Arguments.Count == 3).SetResult(null);

        // Act
        var component = context.Render<Watch>(parameters => parameters.Add(page => page.ChannelNumber, "42.1"));

        // Assert
        component.WaitForAssertion(() =>
        {
            Assert.DoesNotContain("subtitleTrack=", LastStreamUrl(context));
            Assert.DoesNotContain(context.JSInterop.Invocations, invocation => invocation.Identifier == "localStorage.setItem");
        });
    }

    /// <summary>
    /// Verifies a channel switch does not restore track indexes from the preceding client-owned stream.
    /// </summary>
    [Fact]
    public void ChannelSwitch_PreviousClientStreamDoesNotRestoreStaleTracks()
    {
        // Arrange
        using var context = new BunitContext();
        var repository = Substitute.For<IEpgRepository>();
        repository.GetChannelsAsync().Returns([]);
        repository.GetProgramsAsync(Arg.Any<DateTime?>(), Arg.Any<DateTime?>()).Returns([]);
        var settingsService = Substitute.For<IAppSettingsService>();
        settingsService.Settings.Returns(new AppSettings());
        var registry = new ActiveStreamRegistry();
        context.Services.AddSingleton(repository);
        context.Services.AddSingleton(settingsService);
        AddWatchRuntimeServices(
            context,
            activeStreamRegistry: registry,
            preferencesJson: """{"Quality":0,"AudioOutput":0,"SubtitlesEnabled":true,"Subtitle":{"Language":"eng","Title":null,"SourceCodec":"subrip","IsForced":false,"IsHearingImpaired":false}}""");
        context.JSInterop.SetupVoid("stopMediaPlayer", "videoPlayer").SetVoidResult();
        context.JSInterop.Setup<string?>("initFmp4Player", invocation => invocation.Arguments.Count is 3 or 4).SetResult(null);
        var component = context.Render<Watch>();
        component.Find("#manualTuneChannel").Input("42.1");
        component.Find("form").Submit();
        component.WaitForAssertion(() => Assert.Single(context.JSInterop.Invocations, invocation => invocation.Identifier == "initFmp4Player"));
        var initialStreamUri = new Uri($"http://localhost{LastStreamUrl(context)}");
        var clientId = initialStreamUri.Query
            .TrimStart('?')
            .Split('&')
            .Select(parameter => parameter.Split('=', 2))
            .Single(parameter => parameter[0] == "clientId")[1];
        registry.Register(new ActiveStreamSnapshot(
            "previous",
            "42.1",
            HostedStreamFormat.FragmentedMp4,
            DateTime.UtcNow,
            null,
            [
                Track(3, MediaTrackType.Audio, "ac3", "eng", selected: true),
                Track(5, MediaTrackType.Subtitle, "subrip", "eng", SubtitlePresentation.WebVtt)
            ])
        {
            ClientId = clientId
        });

        // Act
        component.Find("#manualTuneChannel").Input("43.1");
        component.Find("form").Submit();

        // Assert
        component.WaitForAssertion(() =>
        {
            var initializations = context.JSInterop.Invocations.Where(invocation => invocation.Identifier == "initFmp4Player").ToArray();
            Assert.Equal(2, initializations.Length);
            var streamUrl = Assert.IsType<string>(initializations[^1].Arguments[1]);
            Assert.Contains("/api/stream/fmp4/43.1?", streamUrl);
            Assert.DoesNotContain("audioTrack=", streamUrl);
            Assert.DoesNotContain("subtitleTrack=", streamUrl);
        });
    }

    /// <summary>
    /// Verifies selecting Off persists the disabled subtitle default and removes the stream override.
    /// </summary>
    [Fact]
    public void SubtitleOff_PersistsDisabledPreference()
    {
        // Arrange
        using var context = new BunitContext();
        var repository = Substitute.For<IEpgRepository>();
        repository.GetChannelsAsync().Returns([]);
        repository.GetProgramsAsync(Arg.Any<DateTime?>(), Arg.Any<DateTime?>()).Returns([]);
        var settingsService = Substitute.For<IAppSettingsService>();
        settingsService.Settings.Returns(new AppSettings());
        var registry = new ActiveStreamRegistry();
        registry.Register(new ActiveStreamSnapshot(
            "source",
            "42.1",
            HostedStreamFormat.FragmentedMp4,
            DateTime.UtcNow,
            null,
            [Track(7, MediaTrackType.Subtitle, "subrip", "eng", SubtitlePresentation.WebVtt)]));
        context.Services.AddSingleton(repository);
        context.Services.AddSingleton(settingsService);
        AddWatchRuntimeServices(
            context,
            activeStreamRegistry: registry,
            preferencesJson: """{"Quality":0,"AudioOutput":0,"SubtitlesEnabled":true,"Subtitle":{"Language":"eng","Title":null,"SourceCodec":"subrip","IsForced":false,"IsHearingImpaired":false}}""");
        context.JSInterop.SetupVoid("stopMediaPlayer", "videoPlayer").SetVoidResult();
        context.JSInterop.Setup<string?>("initFmp4Player", invocation => invocation.Arguments.Count is 3 or 4).SetResult(null);
        var component = context.Render<Watch>(parameters => parameters.Add(page => page.ChannelNumber, "42.1"));
        component.WaitForAssertion(() => Assert.Equal("7", component.Find("#subtitleTrack").GetAttribute("value")));

        // Act
        component.Find("#subtitleTrack").Change("-1");

        // Assert
        component.WaitForAssertion(() =>
        {
            Assert.DoesNotContain("subtitleTrack=", LastStreamUrl(context));
            var savedState = JsonDocument.Parse(
                Assert.IsType<string>(
                    context.JSInterop.Invocations.Last(item => item.Identifier == "localStorage.setItem").Arguments[1])).RootElement;
            Assert.False(savedState.GetProperty("SubtitlesEnabled").GetBoolean());
        });
    }

    /// <summary>
    /// Verifies saved subtitles are applied when source track metadata arrives after initial playback.
    /// </summary>
    [Fact]
    public async Task SavedSubtitlePreference_DelayedTrackMetadataRestartsWithMatch()
    {
        // Arrange
        using var context = new BunitContext();
        var repository = Substitute.For<IEpgRepository>();
        repository.GetChannelsAsync().Returns([]);
        repository.GetProgramsAsync(Arg.Any<DateTime?>(), Arg.Any<DateTime?>()).Returns([]);
        var settingsService = Substitute.For<IAppSettingsService>();
        settingsService.Settings.Returns(new AppSettings());
        var registry = new ActiveStreamRegistry();
        context.Services.AddSingleton(repository);
        context.Services.AddSingleton(settingsService);
        AddWatchRuntimeServices(
            context,
            activeStreamRegistry: registry,
            preferencesJson: """{"Quality":0,"AudioOutput":0,"SubtitlesEnabled":true,"Subtitle":{"Language":"eng","Title":null,"SourceCodec":"subrip","IsForced":false,"IsHearingImpaired":false}}""");
        context.JSInterop.SetupVoid("stopMediaPlayer", "videoPlayer").SetVoidResult();
        context.JSInterop.Setup<string?>("initFmp4Player", invocation => invocation.Arguments.Count is 3 or 4).SetResult(null);
        var component = context.Render<Watch>(parameters => parameters.Add(page => page.ChannelNumber, "42.1"));
        component.WaitForAssertion(() => Assert.Single(context.JSInterop.Invocations, invocation => invocation.Identifier == "initFmp4Player"));

        // Act
        registry.Register(new ActiveStreamSnapshot(
            "source",
            "42.1",
            HostedStreamFormat.FragmentedMp4,
            DateTime.UtcNow,
            null,
            [Track(7, MediaTrackType.Subtitle, "subrip", "eng", SubtitlePresentation.WebVtt)]));
        var refreshMethod = typeof(Watch).GetMethod("RefreshStreamInfoAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(refreshMethod);
        await component.InvokeAsync(() => Assert.IsType<Task>(refreshMethod.Invoke(component.Instance, null), exactMatch: false));

        // Assert
        component.WaitForAssertion(() =>
        {
            var initializations = context.JSInterop.Invocations.Where(invocation => invocation.Identifier == "initFmp4Player").ToArray();
            Assert.Equal(2, initializations.Length);
            Assert.Contains("subtitleTrack=7", Assert.IsType<string>(initializations[^1].Arguments[1]));
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

    private static void AddWatchRuntimeServices(
        BunitContext context,
        IDeviceStateService? deviceState = null,
        IActiveStreamRegistry? activeStreamRegistry = null,
        ChannelLineupStore? channelStore = null,
        string? preferencesJson = null)
    {
        BunitContext.DefaultWaitTimeout = TimeSpan.FromSeconds(5);
        if (deviceState == null)
        {
            deviceState = Substitute.For<IDeviceStateService>();
            deviceState.TunerStatuses.Returns([]);
        }

        context.Services.AddSingleton(deviceState);
        context.Services.AddSingleton(activeStreamRegistry ?? new ActiveStreamRegistry());
        context.Services.AddSingleton(channelStore ?? CreateEmptyChannelStore());
        context.Services.AddScoped<IBrowserDataStore, BrowserDataStore>();
        context.JSInterop.Setup<string?>("localStorage.getItem", PreferencesStorageKey).SetResult(preferencesJson);
        context.JSInterop.SetupVoid("localStorage.setItem", _ => true).SetVoidResult();
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

    private static ChannelLineupStore CreateEmptyChannelStore()
    {
        var store = new ChannelLineupStore(Path.Combine(Path.GetTempPath(), $"lineup-watch-{Guid.NewGuid():N}.db"));
        _ = store.ReadAsync(Xunit.TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        return store;
    }
}
