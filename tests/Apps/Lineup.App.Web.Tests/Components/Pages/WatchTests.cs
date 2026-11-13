using Bunit;
using Bunit.JSInterop;
using Lineup.App.Web.Components.Pages;
using Lineup.App.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using NSubstitute;
using System.Reflection;
using System.Text.Json;
using Xunit;

namespace Lineup.App.Web.Tests.Components.Pages;

/// <summary>
/// Verifies the Watch page lifecycle and independent preferences.
/// </summary>
public partial class WatchTests
{
    private const string PreferencesStorageKey = "lineup-watch-cmaf-preferences-v2";

    /// <summary>Verifies channel numbers are ordered numerically instead of alphabetically.</summary>
    [Fact]
    public async Task ChannelList_MixedChannelNumbers_OrdersByNumericValue()
    {
        // Arrange
        var repository = Substitute.For<ILineupGuideReader>();
        repository.GetChannelDirectoryAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new GuideChannelSegment { GuideNumber = "100.1", GuideName = "Hundred" },
            new GuideChannelSegment { GuideNumber = "10.1", GuideName = "Ten" },
            new GuideChannelSegment { GuideNumber = "7.2", GuideName = "Seven Two" },
            new GuideChannelSegment { GuideNumber = "2.2", GuideName = "Two Two" },
            new GuideChannelSegment { GuideNumber = "7.10", GuideName = "Seven Ten" },
            new GuideChannelSegment { GuideNumber = "2", GuideName = "Two" }
        ]);
        repository.GetProgramsAsync(Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>()).Returns([]);
        await using var context = CreateContext(repository: repository);

        // Act
        var component = context.Render<Watch>();

        // Assert
        component.WaitForAssertion(() =>
            Assert.Equal(
                ["2", "2.2", "7.10", "7.2", "10.1", "100.1"],
                component.FindAll(".cmaf-channel-number").Select(element => element.TextContent.Trim())), timeout: LineupWebBunitContext.WaitTimeout);
    }

    /// <summary>
    /// Verifies a browser without a completed compatibility profile can launch the complete Watch Test suite.
    /// </summary>
    [Fact]
    public async Task MissingCompatibilityProfile_ShowsRunAllWatchTestLink()
    {
        // Arrange
        await using var context = CreateContext();

        // Act
        var component = context.Render<Watch>();

        // Assert
        component.WaitForAssertion(() =>
        {
            var link = component.Find("#runCmafCompatibilityTests");
            Assert.Equal("/watch-test?runAll=true", link.GetAttribute("href"));
            Assert.Contains("Run Compatibility Tests", link.TextContent, StringComparison.Ordinal);
        }, timeout: LineupWebBunitContext.WaitTimeout);
    }

    /// <summary>
    /// Verifies a valid compatibility profile for the current browser suppresses the Watch Test prompt.
    /// </summary>
    [Fact]
    public async Task CurrentCompatibilityProfile_HidesRunAllWatchTestLink()
    {
        // Arrange
        var profile = CreateCompatibilityProfile();
        await using var context = CreateContext();
        context.JSInterop.Setup<string?>("localStorage.getItem", CmafCompatibilityProfile.StorageKey).SetResult(JsonSerializer.Serialize(profile));
        context.JSInterop.Setup<string>("getCmafBrowserIdentity").SetResult("test-browser");

        // Act
        var component = context.Render<Watch>();

        // Assert
        component.WaitForAssertion(() => Assert.Empty(component.FindAll("#runCmafCompatibilityTests")), timeout: LineupWebBunitContext.WaitTimeout);
    }

    /// <summary>
    /// Verifies the player area communicates that a CMAF stream is being prepared.
    /// </summary>
    [Fact]
    public async Task StreamStartup_ShowsAccessibleLoadingOverlay()
    {
        // Arrange
        await using var context = CreateContext();
        var component = context.Render<Watch>();
        var loadingField = typeof(Watch).GetField("_isPlayerLoading", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(loadingField);

        // Act
        loadingField.SetValue(component.Instance, true);
        component.Render();

        // Assert
        var loading = component.Find("#cmafStreamLoading");
        Assert.Equal("status", loading.GetAttribute("role"));
        Assert.Equal("polite", loading.GetAttribute("aria-live"));
        Assert.Contains("Setting up stream", loading.TextContent, StringComparison.Ordinal);
        Assert.Single(loading.QuerySelectorAll(".spinner-border"));
        Assert.DoesNotContain("Select a channel to start watching", component.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies manual stream settings are hidden by default and reveal Browser profile choices when enabled.
    /// </summary>
    [Fact]
    public async Task StreamOverrides_DefaultHiddenAndRevealBrowserProfileChoices()
    {
        // Arrange
        await using var context = CreateContext();
        var component = context.Render<Watch>();
        var initiallyHidden = component.Find("#cmafOverrideSettings").ClassList.Contains("d-none");

        // Act
        await component.Find("#cmafOverrideStreamSettings").ChangeAsync(new ChangeEventArgs { Value = true });

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
        }, timeout: LineupWebBunitContext.WaitTimeout);
    }

    /// <summary>
    /// Verifies an audio-only UI override keeps video on the measured browser profile in the typed request.
    /// </summary>
    [Fact]
    public async Task StreamOverrides_AudioOnly_SendsMeasuredProfileWithVideoPreserved()
    {
        // Arrange
        var profile = CreateCompatibilityProfile();
        await using var context = CreateContext();
        context.JSInterop.Setup<string?>("localStorage.getItem", CmafCompatibilityProfile.StorageKey).SetResult(JsonSerializer.Serialize(profile));
        context.JSInterop.Setup<string>("getCmafBrowserIdentity").SetResult("test-browser");
        ConfigureSuccessfulSession(context);
        var component = context.Render<Watch>(parameters => parameters.Add(page => page.ChannelNumber, "42.1"));
        component.WaitForAssertion(() => Assert.NotNull(component.Find("#cmafVideoPlayer")), timeout: LineupWebBunitContext.WaitTimeout);

        // Act
        await component.Find("#cmafOverrideStreamSettings").ChangeAsync(new ChangeEventArgs { Value = true });
        await component.Find("#cmafPreferredAudio").ChangeAsync(new ChangeEventArgs { Value = CmafPreferredAudio.Fallback.ToString() });

        // Assert
        component.WaitForAssertion(() =>
        {
            var start = context.JSInterop.Invocations.Last(invocation => invocation.Identifier == "startCmafSession");
            var request = Assert.IsType<CmafStreamRequest>(start.Arguments[1]);
            Assert.True(request.Overrides.Enabled);
            Assert.Equal(CmafPreferredAudio.Fallback, request.Overrides.Audio);
            Assert.Null(request.Overrides.Video);
            Assert.NotNull(request.CompatibilityProfile);
        }, timeout: LineupWebBunitContext.WaitTimeout);
    }

    /// <summary>
    /// Verifies a completed profile enables the typed Auto tune request.
    /// </summary>
    [Fact]
    public async Task CompletedProfile_StartsTypedAutomaticSession()
    {
        // Arrange
        var profile = CreateCompatibilityProfile();
        await using var context = CreateContext();
        context.JSInterop.Setup<string?>("localStorage.getItem", CmafCompatibilityProfile.StorageKey).SetResult(JsonSerializer.Serialize(profile));
        context.JSInterop.Setup<string>("getCmafBrowserIdentity").SetResult("test-browser");
        ConfigureSuccessfulSession(context);

        // Act
        var component = context.Render<Watch>(parameters => parameters.Add(page => page.ChannelNumber, "42.1"));

        // Assert
        component.WaitForAssertion(() =>
        {
            var start = Assert.Single(context.JSInterop.Invocations, invocation => invocation.Identifier == "startCmafSession");
            Assert.Equal("http://localhost:8600/media/start/42.1", start.Arguments[0]);
            var request = Assert.IsType<CmafStreamRequest>(start.Arguments[1]);
            Assert.Equal(CmafPreferredVideo.Auto, request.PreferredVideo);
            Assert.Equal(CmafPreferredAudio.Auto, request.PreferredAudio);
            Assert.NotNull(request.CompatibilityProfile);
        }, timeout: LineupWebBunitContext.WaitTimeout);
    }

    /// <summary>Verifies the Web Player can use the management host without a dedicated listener.</summary>
    [Theory]
    [InlineData("http")]
    [InlineData("https")]
    public async Task ManagementHostedPlayer_StartsSessionOnManagementApi(string pageScheme)
    {
        // Arrange
        var settings = Substitute.For<ILineupSettingsService>();
        settings.Settings.Returns(new LineupSettings
        {
            Streaming = new StreamingConfiguration
            {
                VirtualDevices = [WebPlayerVirtualTunerDriver.CreateBuiltIn()]
            }
        });
        await using var context = CreateContext(settingsService: settings);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo($"{pageScheme}://localhost/watch");
        ConfigureSuccessfulSession(context);

        // Act
        var component = context.Render<Watch>(parameters => parameters.Add(page => page.ChannelNumber, "42.1"));

        // Assert
        component.WaitForAssertion(() =>
        {
            var start = Assert.Single(context.JSInterop.Invocations, invocation => invocation.Identifier == "startCmafSession");
            Assert.Equal("/api/stream/cmaf/start/42.1", start.Arguments[0]);
        }, timeout: LineupWebBunitContext.WaitTimeout);
    }

    /// <summary>Verifies dedicated media selection uses public transport, preserves compatible binding order and normalizes prefixes.</summary>
    [Theory]
    [InlineData("https", "https://media.example/secure/media/start/42.1", "http://media.example/plain", "https://media.example/secure")]
    [InlineData("http", "http://media.example/plain/media/start/42.1", "http://media.example/plain/", "https://media.example/secure/")]
    [InlineData("http", "https://media.example/secure/media/start/42.1", "https://media.example/secure/", "http://media.example/plain/")]
    [InlineData("https", "https://media.example/media/start/42.1", "https://media.example/")]
    [InlineData("http", "http://media.example/media/start/42.1", "http://media.example/")]
    [InlineData("http", "http://media.example/media/start/42.1", "not-a-uri", "http://media.example/")]
    [InlineData("https", "https://media.example/media/start/42.1", "ftp://media.example/", "https://media.example/")]
    public async Task DedicatedPlayer_CompatiblePublicBindings_StartsExpectedMediaUrl(string pageScheme, string expectedStartUrl, params string[] publicUris)
    {
        // Arrange
        var configuration = CreateHostedWebPlayerSettings();
        configuration.Streaming = configuration.Streaming with
        {
            VirtualDevices =
            [
                configuration.Streaming.VirtualDevices[0] with
                {
                    Hosting = new VirtualOutputHostingDefinition
                    {
                        Bindings = publicUris.Select(uri => new VirtualOutputBindingDefinition { PublicBaseUri = uri }).ToArray()
                    }
                }
            ]
        };
        var settings = Substitute.For<ILineupSettingsService>();
        settings.Settings.Returns(configuration);
        await using var context = CreateContext(settingsService: settings);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo($"{pageScheme}://localhost/watch");
        ConfigureSuccessfulSession(context);

        // Act
        var component = context.Render<Watch>(parameters => parameters.Add(page => page.ChannelNumber, "42.1"));

        // Assert
        component.WaitForAssertion(() =>
        {
            var start = Assert.Single(context.JSInterop.Invocations, invocation => invocation.Identifier == "startCmafSession");
            Assert.Equal(expectedStartUrl, start.Arguments[0]);
            Assert.NotNull(component.Find("#cmafVideoPlayer"));
        }, timeout: LineupWebBunitContext.WaitTimeout);
    }

    /// <summary>Verifies incompatible dedicated transport fails before issuing a browser media request, including HTTP loopback on HTTPS pages.</summary>
    [Theory]
    [InlineData("https", "http://media.example/")]
    [InlineData("https", "http://localhost:8600/")]
    [InlineData("https", "")]
    [InlineData("http", "ftp://media.example/")]
    [InlineData("http", "not-a-uri")]
    public async Task DedicatedPlayer_NoCompatiblePublicBinding_ShowsConfigurationError(string pageScheme, string publicUri)
    {
        // Arrange
        var configuration = CreateHostedWebPlayerSettings();
        configuration.Streaming = configuration.Streaming with
        {
            VirtualDevices =
            [
                configuration.Streaming.VirtualDevices[0] with
                {
                    Hosting = new VirtualOutputHostingDefinition { Bindings = [new VirtualOutputBindingDefinition { PublicBaseUri = publicUri }] }
                }
            ]
        };
        var settings = Substitute.For<ILineupSettingsService>();
        settings.Settings.Returns(configuration);
        await using var context = CreateContext(settingsService: settings);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo($"{pageScheme}://localhost/watch");

        // Act
        var component = context.Render<Watch>(parameters => parameters.Add(page => page.ChannelNumber, "42.1"));

        // Assert
        component.WaitForAssertion(() =>
        {
            Assert.Contains(pageScheme == "https"
                ? "Configure an HTTPS listener or an HTTPS reverse-proxy URL."
                : "The Web Player dedicated listener is not configured.", component.Markup, StringComparison.Ordinal);
            Assert.DoesNotContain(context.JSInterop.Invocations, invocation => invocation.Identifier == "startCmafSession");
            Assert.Empty(component.FindAll("#cmafVideoPlayer"));
            Assert.Empty(component.FindAll("#cmafStreamLoading"));
        }, timeout: LineupWebBunitContext.WaitTimeout);
    }

    /// <summary>
    /// Verifies a current stored source preference cannot bypass a measured browser profile when overrides are disabled.
    /// </summary>
    [Fact]
    public async Task CompletedProfile_CurrentSourcePreferenceWithoutOverrides_StartsTypedAutomaticSession()
    {
        // Arrange
        var profile = CreateCompatibilityProfile();
        await using var context = CreateContext(
            """{"PolicyVersion":1,"Protocol":0,"Quality":0,"PreferredVideo":0,"PreferredAudio":0,"FallbackAudio":0,"Overrides":{"Enabled":false}}""");
        context.JSInterop.Setup<string?>("localStorage.getItem", CmafCompatibilityProfile.StorageKey).SetResult(JsonSerializer.Serialize(profile));
        context.JSInterop.Setup<string>("getCmafBrowserIdentity").SetResult("test-browser");
        ConfigureSuccessfulSession(context);

        // Act
        var component = context.Render<Watch>(parameters => parameters.Add(page => page.ChannelNumber, "42.1"));

        // Assert
        component.WaitForAssertion(() =>
        {
            var start = Assert.Single(context.JSInterop.Invocations, invocation => invocation.Identifier == "startCmafSession");
            var request = Assert.IsType<CmafStreamRequest>(start.Arguments[1]);
            Assert.Equal(CmafPreferredVideo.Auto, request.PreferredVideo);
            Assert.Equal(CmafPreferredAudio.Auto, request.PreferredAudio);
            Assert.False(request.Overrides.Enabled);
            Assert.NotNull(request.CompatibilityProfile);
        }, timeout: LineupWebBunitContext.WaitTimeout);
    }

    /// <summary>
    /// Verifies Auto mode prefers DASH and provides HLS fallback for the same server session.
    /// </summary>
    [Fact]
    public async Task DirectRoute_AutoModeStartsSharedDashAndCmafSession()
    {
        // Arrange
        await using var context = CreateContext();
        ConfigureSuccessfulSession(context);

        // Act
        var component = context.Render<Watch>(parameters => parameters.Add(page => page.ChannelNumber, "42.1"));

        // Assert
        component.WaitForAssertion(() =>
        {
            Assert.NotNull(component.Find("#cmafVideoPlayer"));
            var start = Assert.Single(context.JSInterop.Invocations, invocation => invocation.Identifier == "startCmafSession");
            Assert.Equal(3, start.Arguments.Count);
            var startUrl = Assert.IsType<string>(start.Arguments[0]);
            var request = Assert.IsType<CmafStreamRequest>(start.Arguments[1]);
            Assert.False(string.IsNullOrWhiteSpace(Assert.IsType<string>(start.Arguments[2])));
            Assert.Equal("http://localhost:8600/media/start/42.1", startUrl);
            Assert.Equal(CmafPreferredVideo.Source, request.PreferredVideo);
            Assert.Equal(CmafPreferredAudio.Source, request.PreferredAudio);
            var initialize = Assert.Single(context.JSInterop.Invocations, invocation => invocation.Identifier == "initCmafPlayer");
            Assert.Equal("/api/stream/cmaf/session-1/manifest.mpd", initialize.Arguments[1]);
            Assert.Equal("/api/stream/cmaf/session-1/master.m3u8", initialize.Arguments[2]);
            Assert.Equal("Source", initialize.Arguments[3]);
            Assert.Equal("Source", initialize.Arguments[4]);
            Assert.Equal(false, initialize.Arguments[5]);
            Assert.Null(initialize.Arguments[6]);
            Assert.Equal("aac", initialize.Arguments[7]);
            var subtitles = Assert.IsAssignableFrom<IReadOnlyList<Watch.CmafSubtitleResponse>>(initialize.Arguments[8]);
            Assert.Collection(
                subtitles,
                subtitle => Assert.Equal(2, subtitle.SourceIndex),
                subtitle => Assert.Equal(3, subtitle.SourceIndex));
        }, timeout: LineupWebBunitContext.WaitTimeout);
    }

    /// <summary>
    /// Verifies selecting H.264 fallback persists the video preference and restarts the presentation.
    /// </summary>
    [Fact]
    public async Task PreferredVideoChange_PersistsAndRestartsWithH264Fallback()
    {
        // Arrange
        await using var context = CreateContext();
        ConfigureSuccessfulSession(context);
        var component = context.Render<Watch>(parameters => parameters.Add(page => page.ChannelNumber, "105.1"));
        component.WaitForAssertion(() => Assert.NotNull(component.Find("#cmafVideoPlayer")), timeout: LineupWebBunitContext.WaitTimeout);

        // Act
        await component.Find("#cmafPreferredVideo").ChangeAsync(new ChangeEventArgs { Value = CmafPreferredVideo.Fallback.ToString() });

        // Assert
        component.WaitForAssertion(() =>
        {
            var starts = context.JSInterop.Invocations.Where(invocation => invocation.Identifier == "startCmafSession").ToArray();
            Assert.Equal(2, starts.Length);
            Assert.Equal(CmafPreferredVideo.Fallback, GetStartRequest(starts[^1]).PreferredVideo);
            var storageWrite = Assert.Single(
                context.JSInterop.Invocations,
                invocation => invocation.Identifier == "localStorage.setItem" && Equals(invocation.Arguments[0], PreferencesStorageKey));
            Assert.Contains("\"PreferredVideo\":1", Assert.IsType<string>(storageWrite.Arguments[1]), StringComparison.Ordinal);
        }, timeout: LineupWebBunitContext.WaitTimeout);
    }

    /// <summary>
    /// Verifies Shaka UI audio changes update the hosted-stream details without restarting the server session.
    /// </summary>
    [Fact]
    public async Task ShakaAudioRenditionChange_UpdatesDetailsWithoutRestart()
    {
        // Arrange
        await using var context = CreateContext();
        context.JSInterop
            .Setup<Watch.CmafStartResponse>("startCmafSession", _ => true)
            .SetResult(new(
                "session-1",
                "/api/stream/cmaf/session-1/master.m3u8",
                "/api/stream/cmaf/session-1/manifest.mpd"));
        context.JSInterop
            .Setup<Watch.CmafPlayerResult>("initCmafPlayer", _ => true)
            .SetResult(new(
                true,
                null,
                "/api/stream/cmaf/session-1/manifest.mpd",
                Audio: new("eng · Fallback AAC Stereo", "mp4a.40.2", 2, "11", "eng"),
                Video: new("avc1.64002a", 1920, 1080)));
        var component = context.Render<Watch>(parameters => parameters.Add(page => page.ChannelNumber, "42.1"));
        component.WaitForAssertion(() => Assert.NotNull(component.Find("#cmafVideoPlayer")), timeout: LineupWebBunitContext.WaitTimeout);
        using var details = JsonDocument.Parse("""{"id":"12","label":"spa · Source","language":"spa","codec":"ac-3","channels":2}""");

        // Act
        await component.InvokeAsync(() => component.Instance.OnCmafPlayerEvent(new("audio-track-changed", "Audio track changed.", details.RootElement, "session-1")));

        // Assert
        component.WaitForAssertion(() =>
        {
            Assert.Single(context.JSInterop.Invocations, invocation => invocation.Identifier == "startCmafSession");
            Assert.Empty(component.FindAll("#cmafAudioTrack"));
            Assert.False(component.Find("#cmafVideoPlayer").HasAttribute("controls"));
            Assert.True(component.Find("#cmafVideoPlayer").ParentElement?.HasAttribute("data-lineup-cmaf-container"));
            Assert.Contains("spa · Source", component.Markup);
            Assert.Contains("ac-3", component.Markup);
        }, timeout: LineupWebBunitContext.WaitTimeout);
    }

    /// <summary>
    /// Verifies a fatal player failure stops the missing server session and its browser pollers.
    /// </summary>
    [Fact]
    public async Task FatalPlayerFailure_StopsPlaybackSession()
    {
        // Arrange
        await using var context = CreateContext();
        ConfigureSuccessfulSession(context);
        var component = context.Render<Watch>(parameters => parameters.Add(page => page.ChannelNumber, "42.1"));
        component.WaitForAssertion(() => Assert.NotNull(component.Find("#cmafVideoPlayer")), timeout: LineupWebBunitContext.WaitTimeout);

        // Act
        await component.InvokeAsync(() => component.Instance.OnCmafPlayerEvent(
            new("error", "The stream session is no longer available.", null, "session-1")));

        // Assert
        component.WaitForAssertion(() =>
        {
            Assert.Contains("The stream session is no longer available.", component.Markup);
            Assert.Contains(
                context.JSInterop.Invocations,
                invocation => invocation.Identifier == "stopMediaPlayer" && Equals(invocation.Arguments[0], "cmafVideoPlayer"));
            Assert.Contains(
                context.JSInterop.Invocations,
                invocation => invocation.Identifier == "stopCmafSession" && Equals(invocation.Arguments[0], "session-1"));
        }, timeout: LineupWebBunitContext.WaitTimeout);
    }

    /// <summary>
    /// Verifies selecting another channel clears the previous channel error before the replacement stream finishes starting.
    /// </summary>
    [Fact]
    public async Task SelectChannel_PreviousError_ClearsBeforeReplacementStarts()
    {
        // Arrange
        var repository = Substitute.For<ILineupGuideReader>();
        repository.GetChannelDirectoryAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new GuideChannelSegment { GuideNumber = "2.1", GuideName = "First" },
            new GuideChannelSegment { GuideNumber = "2.2", GuideName = "Second" }
        ]);
        repository.GetProgramsAsync(Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>()).Returns([]);
        await using var context = CreateContext(repository: repository);
        context.JSInterop.SetupVoid("stopCmafSession", "session-2").SetVoidResult();
        context.JSInterop
            .Setup<Watch.CmafStartResponse>(
                "startCmafSession",
                invocation => Assert.IsType<string>(invocation.Arguments[0]).EndsWith("/media/start/2.1", StringComparison.Ordinal))
            .SetResult(new("session-1", "/api/stream/cmaf/session-1/master.m3u8", "/api/stream/cmaf/session-1/manifest.mpd"));
        var replacementStart = context.JSInterop.Setup<Watch.CmafStartResponse>(
            "startCmafSession",
            invocation => Assert.IsType<string>(invocation.Arguments[0]).EndsWith("/media/start/2.2", StringComparison.Ordinal));
        context.JSInterop
            .Setup<Watch.CmafPlayerResult>("initCmafPlayer", _ => true)
            .SetResult(new(true, null, "/api/stream/cmaf/session-1/manifest.mpd"));
        var component = context.Render<Watch>(parameters => parameters.Add(page => page.ChannelNumber, "2.1"));
        component.WaitForAssertion(() => Assert.Contains(context.JSInterop.Invocations, invocation => invocation.Identifier == "initCmafPlayer"), timeout: LineupWebBunitContext.WaitTimeout);
        await component.InvokeAsync(() => component.Instance.OnCmafPlayerEvent(new("error", "First channel failed.", null, "session-1")));
        component.WaitForAssertion(() => Assert.Contains("First channel failed.", component.Markup, StringComparison.Ordinal), timeout: LineupWebBunitContext.WaitTimeout);

        // Act
        var replacementTune = component.FindAll(".cmaf-channel-item")
            .Single(item => item.TextContent.Contains("2.2", StringComparison.Ordinal)).ClickAsync(new());

        // Assert
        component.WaitForAssertion(() =>
        {
            Assert.DoesNotContain("First channel failed.", component.Markup, StringComparison.Ordinal);
            Assert.Contains(
                context.JSInterop.Invocations,
                invocation => invocation.Identifier == "startCmafSession" &&
                    Assert.IsType<string>(invocation.Arguments[0]).EndsWith("/media/start/2.2", StringComparison.Ordinal));
        }, timeout: LineupWebBunitContext.WaitTimeout);
        replacementStart.SetResult(new("session-2", "/api/stream/cmaf/session-2/master.m3u8", "/api/stream/cmaf/session-2/manifest.mpd"));
        await replacementTune;
    }

    /// <summary>
    /// Verifies an unsupported source-video presentation retries H.264 without changing the saved Source preference.
    /// </summary>
    [Fact]
    public async Task UnsupportedSourceVideo_RetriesWithH264WithoutChangingPreference()
    {
        // Arrange
        await using var context = CreateContext();
        context.JSInterop
            .Setup<Watch.CmafStartResponse>(
                "startCmafSession",
                invocation => GetStartRequest(invocation).PreferredVideo == CmafPreferredVideo.Source)
            .SetResult(
                new(
                    "source-session",
                    "/api/stream/cmaf/source-session/master.m3u8",
                    "/api/stream/cmaf/source-session/manifest.mpd",
                    HasSourceVideoRendition: true,
                    SourceVideoCodec: "hvc1.2.4.L123"));
        context.JSInterop
            .Setup<Watch.CmafStartResponse>(
                "startCmafSession",
                invocation => GetStartRequest(invocation).PreferredVideo == CmafPreferredVideo.Fallback)
            .SetResult(new("fallback-session", "/api/stream/cmaf/fallback-session/master.m3u8", "/api/stream/cmaf/fallback-session/manifest.mpd"));
        context.JSInterop
            .Setup<Watch.CmafPlayerResult>(
                "initCmafPlayer",
                invocation => Equals(invocation.Arguments[1], "/api/stream/cmaf/source-session/manifest.mpd"))
            .SetResult(new(false, "Shaka Error 4032", null, 4032, SourceVideoSupported: false));
        context.JSInterop
            .Setup<Watch.CmafPlayerResult>(
                "initCmafPlayer",
                invocation => Equals(invocation.Arguments[1], "/api/stream/cmaf/fallback-session/manifest.mpd"))
            .SetResult(new(true, null, "/api/stream/cmaf/fallback-session/manifest.mpd", Video: new("avc1.64002a", 1920, 1080)));
        context.JSInterop.SetupVoid("stopCmafSession", "source-session").SetVoidResult();
        context.JSInterop.SetupVoid("stopCmafSession", "fallback-session").SetVoidResult();

        // Act
        var component = context.Render<Watch>(parameters => parameters.Add(page => page.ChannelNumber, "105.1"));

        // Assert
        component.WaitForAssertion(() =>
        {
            var starts = context.JSInterop.Invocations.Where(invocation => invocation.Identifier == "startCmafSession").ToArray();
            Assert.Equal(2, starts.Length);
            Assert.Equal(CmafPreferredVideo.Source, GetStartRequest(starts[0]).PreferredVideo);
            Assert.Equal(CmafPreferredVideo.Fallback, GetStartRequest(starts[1]).PreferredVideo);
            Assert.Equal(nameof(CmafPreferredVideo.Source), component.Find("#cmafPreferredVideo").GetAttribute("value"));
            Assert.Contains("Playing video:", component.Markup);
            Assert.Contains("avc1.64002a", component.Markup);
            Assert.Contains("1920x1080", component.Markup);
            var notification = Assert.Single(context.Services.GetRequiredService<IStatusNotificationService>().Notifications);
            Assert.Equal("Source video is unavailable in this browser. Retrying this stream with H.264.", notification.Message);
            Assert.DoesNotContain(context.JSInterop.Invocations, invocation => invocation.Identifier == "localStorage.setItem");
        }, timeout: LineupWebBunitContext.WaitTimeout);
    }

    /// <summary>
    /// Verifies a source-video decoder failure during initialization retries H.264 without changing the saved Source preference.
    /// </summary>
    [Fact]
    public async Task SourceVideoInitializationDecodeFailure_RetriesWithH264WithoutChangingPreference()
    {
        // Arrange
        var profile = CreateCompatibilityProfile();
        await using var context = CreateContext();
        context.JSInterop.Setup<string?>("localStorage.getItem", CmafCompatibilityProfile.StorageKey).SetResult(JsonSerializer.Serialize(profile));
        context.JSInterop.Setup<string>("getCmafBrowserIdentity").SetResult("test-browser");
        context.JSInterop
            .Setup<Watch.CmafStartResponse>(
                "startCmafSession",
                invocation => GetStartRequest(invocation).PreferredVideo == CmafPreferredVideo.Auto)
            .SetResult(
                new(
                    "source-session",
                    "/api/stream/cmaf/source-session/master.m3u8",
                    "/api/stream/cmaf/source-session/manifest.mpd",
                    HasSourceVideoRendition: true));
        context.JSInterop
            .Setup<Watch.CmafStartResponse>(
                "startCmafSession",
                invocation => GetStartRequest(invocation).PreferredVideo == CmafPreferredVideo.Fallback)
            .SetResult(new("fallback-session", "/api/stream/cmaf/fallback-session/master.m3u8", "/api/stream/cmaf/fallback-session/manifest.mpd"));
        context.JSInterop
            .Setup<Watch.CmafPlayerResult>(
                "initCmafPlayer",
                invocation => Equals(invocation.Arguments[1], "/api/stream/cmaf/source-session/manifest.mpd"))
            .SetResult(new(false, "Shaka Error 3016", null, 3016));
        context.JSInterop
            .Setup<Watch.CmafPlayerResult>(
                "initCmafPlayer",
                invocation => Equals(invocation.Arguments[1], "/api/stream/cmaf/fallback-session/manifest.mpd"))
            .SetResult(new(true, null, "/api/stream/cmaf/fallback-session/manifest.mpd", Video: new("avc1.64002a", 853, 480)));
        context.JSInterop.SetupVoid("stopCmafSession", "source-session").SetVoidResult();
        context.JSInterop.SetupVoid("stopCmafSession", "fallback-session").SetVoidResult();

        // Act
        var component = context.Render<Watch>(parameters => parameters.Add(page => page.ChannelNumber, "40.3"));

        // Assert
        component.WaitForAssertion(() =>
        {
            var starts = context.JSInterop.Invocations.Where(invocation => invocation.Identifier == "startCmafSession").ToArray();
            Assert.Equal(2, starts.Length);
            var initialRequest = GetStartRequest(starts[0]);
            var fallbackRequest = GetStartRequest(starts[1]);
            Assert.Equal(CmafPreferredVideo.Auto, initialRequest.PreferredVideo);
            Assert.Equal(CmafPreferredVideo.Fallback, fallbackRequest.PreferredVideo);
            Assert.Equal(CmafPreferredAudio.Auto, fallbackRequest.PreferredAudio);
            Assert.NotNull(initialRequest.CompatibilityProfile);
            Assert.NotNull(fallbackRequest.CompatibilityProfile);
            Assert.Equal(nameof(CmafPreferredVideo.Source), component.Find("#cmafPreferredVideo").GetAttribute("value"));
            Assert.Contains("avc1.64002a", component.Markup);
            Assert.DoesNotContain(context.JSInterop.Invocations, invocation => invocation.Identifier == "localStorage.setItem");
            Assert.DoesNotContain(context.JSInterop.Invocations, invocation => invocation.Identifier == "localStorage.removeItem");
        }, timeout: LineupWebBunitContext.WaitTimeout);
    }

    /// <summary>
    /// Verifies an H.264 fallback decoder failure during initialization does not start another fallback session.
    /// </summary>
    [Fact]
    public async Task FallbackVideoInitializationDecodeFailure_DoesNotRetry()
    {
        // Arrange
        await using var context = CreateContext("""{"Protocol":0,"Quality":0,"PreferredAudio":0,"PreferredVideo":1,"FallbackAudio":0,"SubtitleTrack":null}""");
        context.JSInterop
            .Setup<Watch.CmafStartResponse>(
                "startCmafSession",
                invocation => GetStartRequest(invocation).PreferredVideo == CmafPreferredVideo.Fallback)
            .SetResult(new("fallback-session", "/api/stream/cmaf/fallback-session/master.m3u8", "/api/stream/cmaf/fallback-session/manifest.mpd"));
        context.JSInterop
            .Setup<Watch.CmafPlayerResult>("initCmafPlayer", _ => true)
            .SetResult(new(false, "Shaka Error 3016", null, 3016));
        context.JSInterop.SetupVoid("stopCmafSession", "fallback-session").SetVoidResult();

        // Act
        var component = context.Render<Watch>(parameters => parameters.Add(page => page.ChannelNumber, "40.3"));

        // Assert
        component.WaitForAssertion(() =>
        {
            var start = Assert.Single(context.JSInterop.Invocations, invocation => invocation.Identifier == "startCmafSession");
            Assert.Equal(CmafPreferredVideo.Fallback, GetStartRequest(start).PreferredVideo);
            Assert.Contains("Shaka Error 3016", component.Markup);
        }, timeout: LineupWebBunitContext.WaitTimeout);
    }

    /// <summary>
    /// Verifies a runtime source-video decoder failure restarts once with H.264 rather than encoding fallback video continuously.
    /// </summary>
    [Fact]
    public async Task SourceH264RuntimeFailure_RestartsWithH264()
    {
        // Arrange
        var profile = CreateCompatibilityProfile();
        await using var context = CreateContext();
        context.JSInterop.Setup<string?>("localStorage.getItem", CmafCompatibilityProfile.StorageKey).SetResult(JsonSerializer.Serialize(profile));
        context.JSInterop.Setup<string>("getCmafBrowserIdentity").SetResult("test-browser");
        context.JSInterop
            .Setup<Watch.CmafStartResponse>(
                "startCmafSession",
                invocation => GetStartRequest(invocation).PreferredVideo == CmafPreferredVideo.Auto)
            .SetResult(
                new(
                    "source-session",
                    "/api/stream/cmaf/source-session/master.m3u8",
                    "/api/stream/cmaf/source-session/manifest.mpd",
                    HasSourceVideoRendition: true,
                    SourceVideoCodec: null));
        context.JSInterop
            .Setup<Watch.CmafStartResponse>(
                "startCmafSession",
                invocation => GetStartRequest(invocation).PreferredVideo == CmafPreferredVideo.Fallback)
            .SetResult(new("fallback-session", "/api/stream/cmaf/fallback-session/master.m3u8", "/api/stream/cmaf/fallback-session/manifest.mpd"));
        context.JSInterop
            .Setup<Watch.CmafPlayerResult>("initCmafPlayer", _ => true)
            .SetResult(new(true, null, "/api/stream/cmaf/source-session/manifest.mpd", Video: new("avc1.4d401e", 853, 480)));
        context.JSInterop.SetupVoid("stopCmafSession", "source-session").SetVoidResult();
        context.JSInterop.SetupVoid("stopCmafSession", "fallback-session").SetVoidResult();
        var component = context.Render<Watch>(parameters => parameters.Add(page => page.ChannelNumber, "104.1"));
        component.WaitForAssertion(() => Assert.Contains("avc1.4d401e", component.Markup), timeout: LineupWebBunitContext.WaitTimeout);
        var initialize = Assert.Single(
            context.JSInterop.Invocations,
            invocation => invocation.Identifier == "initCmafPlayer" &&
                Equals(invocation.Arguments[1], "/api/stream/cmaf/source-session/manifest.mpd"));
        Assert.Equal(true, initialize.Arguments[5]);
        Assert.Null(initialize.Arguments[6]);

        // Act
        await component.InvokeAsync(() => component.Instance.OnCmafPlayerEvent(
            new("video-fallback-required", "Source video failed in this browser. Restarting this stream with H.264.", null, "source-session")));

        // Assert
        component.WaitForAssertion(() =>
        {
            var starts = context.JSInterop.Invocations.Where(invocation => invocation.Identifier == "startCmafSession").ToArray();
            Assert.Equal(2, starts.Length);
            var fallbackRequest = GetStartRequest(starts[1]);
            Assert.Equal(CmafPreferredVideo.Fallback, fallbackRequest.PreferredVideo);
            Assert.Equal(CmafPreferredAudio.Auto, fallbackRequest.PreferredAudio);
            Assert.NotNull(fallbackRequest.CompatibilityProfile);
            Assert.Equal(nameof(CmafPreferredVideo.Source), component.Find("#cmafPreferredVideo").GetAttribute("value"));
            Assert.DoesNotContain(context.JSInterop.Invocations, invocation => invocation.Identifier == "localStorage.removeItem");
        }, timeout: LineupWebBunitContext.WaitTimeout);
    }

    /// <summary>
    /// Verifies explicit HLS mode does not silently provide a protocol fallback.
    /// </summary>
    [Fact]
    public async Task StoredHlsMode_InitializesOnlyHlsManifest()
    {
        // Arrange
        await using var context = CreateContext("""{"Protocol":1,"Quality":0,"PreferredAudio":0,"FallbackAudio":0,"SubtitleTrack":null}""");
        ConfigureSuccessfulSession(context);

        // Act
        var component = context.Render<Watch>(parameters => parameters.Add(page => page.ChannelNumber, "42.1"));

        // Assert
        component.WaitForAssertion(() =>
        {
            var initialize = Assert.Single(context.JSInterop.Invocations, invocation => invocation.Identifier == "initCmafPlayer");
            Assert.Equal("/api/stream/cmaf/session-1/master.m3u8", initialize.Arguments[1]);
            Assert.Null(initialize.Arguments[2]);
            Assert.Equal(nameof(CmafFallbackAudio.AacStereo), component.Find("#cmafFallbackAudio").GetAttribute("value"));
        }, timeout: LineupWebBunitContext.WaitTimeout);
    }

    /// <summary>
    /// Verifies a fallback-only server retry informs the user without changing the Source preference.
    /// </summary>
    [Fact]
    public async Task SourceAudioMuxerFallback_NotifiesWithoutChangingPreference()
    {
        // Arrange
        await using var context = CreateContext();
        ConfigureSuccessfulSession(context, sourceAudioFallbackApplied: true);

        // Act
        var component = context.Render<Watch>(parameters => parameters.Add(page => page.ChannelNumber, "104.1"));

        // Assert
        component.WaitForAssertion(() =>
        {
            Assert.Equal(nameof(CmafPreferredAudio.Source), component.Find("#cmafPreferredAudio").GetAttribute("value"));
            var notification = Assert.Single(context.Services.GetRequiredService<IStatusNotificationService>().Notifications);
            Assert.Contains("Using Fallback AAC Stereo for this stream", notification.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(
                context.JSInterop.Invocations,
                invocation => invocation.Identifier == "localStorage.setItem" && Equals(invocation.Arguments[0], PreferencesStorageKey));
        }, timeout: LineupWebBunitContext.WaitTimeout);
    }

    /// <summary>
    /// Verifies selecting fallback audio persists only the CMAF preference and restarts the presentation.
    /// </summary>
    [Fact]
    public async Task PreferredAudioChange_PersistsAndRestartsWithFallback()
    {
        // Arrange
        await using var context = CreateContext();
        ConfigureSuccessfulSession(context);
        var component = context.Render<Watch>(parameters => parameters.Add(page => page.ChannelNumber, "42.1"));
        component.WaitForAssertion(() =>
        {
            Assert.NotNull(component.Find("#cmafVideoPlayer"));
            Assert.Contains(context.JSInterop.Invocations, invocation => invocation.Identifier == "initCmafPlayer");
        }, timeout: LineupWebBunitContext.WaitTimeout);

        // Act
        await component.Find("#cmafPreferredAudio").ChangeAsync(new ChangeEventArgs { Value = CmafPreferredAudio.Fallback.ToString() });

        // Assert
        component.WaitForAssertion(() =>
        {
            var starts = context.JSInterop.Invocations.Where(invocation => invocation.Identifier == "startCmafSession").ToArray();
            Assert.Equal(2, starts.Length);
            Assert.Equal(CmafPreferredAudio.Fallback, GetStartRequest(starts[^1]).PreferredAudio);
            var storageWrite = Assert.Single(
                context.JSInterop.Invocations,
                invocation => invocation.Identifier == "localStorage.setItem" && Equals(invocation.Arguments[0], PreferencesStorageKey));
            Assert.Contains("\"PreferredAudio\":1", Assert.IsType<string>(storageWrite.Arguments[1]), StringComparison.Ordinal);
        }, timeout: LineupWebBunitContext.WaitTimeout);
    }

    /// <summary>
    /// Verifies fallback choices are quality ordered while AAC Stereo remains the compatibility default.
    /// </summary>
    [Fact]
    public async Task FallbackAudio_DefaultAndOptions_AreCompatibilityAware()
    {
        // Arrange
        await using var context = CreateContext();
        ConfigureSuccessfulSession(context);

        // Act
        var component = context.Render<Watch>(parameters => parameters.Add(page => page.ChannelNumber, "42.1"));

        // Assert
        component.WaitForAssertion(() =>
        {
            var select = component.Find("#cmafFallbackAudio");
            Assert.Equal(nameof(CmafFallbackAudio.AacStereo), select.GetAttribute("value"));
            Assert.Equal(
                ["Browser profile", "EAC3", "AC3", "AAC up to 7.1", "AAC up to 5.1", "AAC Stereo"],
                select.QuerySelectorAll("option").Select(option => option.TextContent.Trim()).ToArray());
        }, timeout: LineupWebBunitContext.WaitTimeout);
    }

    /// <summary>
    /// Verifies selecting EAC3 persists the fallback profile and sends it to the CMAF API.
    /// </summary>
    [Fact]
    public async Task FallbackAudioChange_PersistsAndRestartsWithEac3()
    {
        // Arrange
        await using var context = CreateContext();
        ConfigureSuccessfulSession(context);
        var component = context.Render<Watch>(parameters => parameters.Add(page => page.ChannelNumber, "42.1"));
        component.WaitForAssertion(() => Assert.NotNull(component.Find("#cmafVideoPlayer")), timeout: LineupWebBunitContext.WaitTimeout);

        // Act
        await component.Find("#cmafFallbackAudio").ChangeAsync(new ChangeEventArgs { Value = CmafFallbackAudio.Eac3.ToString() });

        // Assert
        component.WaitForAssertion(() =>
        {
            var starts = context.JSInterop.Invocations.Where(invocation => invocation.Identifier == "startCmafSession").ToArray();
            Assert.Equal(2, starts.Length);
            Assert.Equal(CmafFallbackAudio.Eac3, GetStartRequest(starts[^1]).FallbackAudio);
            var storageWrite = Assert.Single(
                context.JSInterop.Invocations,
                invocation => invocation.Identifier == "localStorage.setItem" && Equals(invocation.Arguments[0], PreferencesStorageKey));
            Assert.Contains("\"FallbackAudio\":4", Assert.IsType<string>(storageWrite.Arguments[1]), StringComparison.Ordinal);
        }, timeout: LineupWebBunitContext.WaitTimeout);
    }

    /// <summary>
    /// Verifies a browser that rejects the configured fallback gets a temporary AAC Stereo session without changing the saved preference.
    /// </summary>
    [Fact]
    public async Task UnsupportedAc3Presentation_RetriesWithAacStereoWithoutChangingPreference()
    {
        // Arrange
        await using var context = CreateContext("""{"Protocol":2,"Quality":0,"PreferredAudio":1,"FallbackAudio":3,"SubtitleTrack":null}""");
        context.JSInterop
            .Setup<Watch.CmafStartResponse>(
                "startCmafSession",
                invocation => GetStartRequest(invocation).FallbackAudio == CmafFallbackAudio.Ac3)
            .SetResult(
                new(
                    "ac3-session",
                    "/api/stream/cmaf/ac3-session/master.m3u8",
                    "/api/stream/cmaf/ac3-session/manifest.mpd",
                    FallbackAudioCodec: "ac3"));
        context.JSInterop
            .Setup<Watch.CmafStartResponse>(
                "startCmafSession",
                invocation => GetStartRequest(invocation).FallbackAudio == CmafFallbackAudio.AacStereo)
            .SetResult(new("aac-session", "/api/stream/cmaf/aac-session/master.m3u8", "/api/stream/cmaf/aac-session/manifest.mpd"));
        context.JSInterop
            .Setup<Watch.CmafPlayerResult>(
                "initCmafPlayer",
                invocation => Equals(invocation.Arguments[1], "/api/stream/cmaf/ac3-session/manifest.mpd"))
            .SetResult(new(false, "Shaka Error 4032", null, 4032, FallbackAudioSupported: false));
        context.JSInterop
            .Setup<Watch.CmafPlayerResult>(
                "initCmafPlayer",
                invocation => Equals(invocation.Arguments[1], "/api/stream/cmaf/aac-session/manifest.mpd"))
            .SetResult(new(true, null, "/api/stream/cmaf/aac-session/manifest.mpd", Audio: new("Fallback AAC Stereo", "mp4a.40.2", 2)));
        context.JSInterop.SetupVoid("stopCmafSession", "ac3-session").SetVoidResult();
        context.JSInterop.SetupVoid("stopCmafSession", "aac-session").SetVoidResult();

        // Act
        var component = context.Render<Watch>(parameters => parameters.Add(page => page.ChannelNumber, "105.1"));

        // Assert
        component.WaitForAssertion(() =>
        {
            var starts = context.JSInterop.Invocations.Where(invocation => invocation.Identifier == "startCmafSession").ToArray();
            Assert.Equal(2, starts.Length);
            Assert.Equal(CmafFallbackAudio.Ac3, GetStartRequest(starts[0]).FallbackAudio);
            Assert.Equal(CmafFallbackAudio.AacStereo, GetStartRequest(starts[1]).FallbackAudio);
            Assert.Equal(nameof(CmafFallbackAudio.Ac3), component.Find("#cmafFallbackAudio").GetAttribute("value"));
            var notification = Assert.Single(context.Services.GetRequiredService<IStatusNotificationService>().Notifications);
            Assert.Equal("AC3 is unavailable in this browser. Retrying this stream with AAC Stereo.", notification.Message);
            Assert.Contains("Playing audio:", component.Markup);
            Assert.Contains("Fallback AAC Stereo", component.Markup);
            Assert.Contains("mp4a.40.2", component.Markup);
            Assert.DoesNotContain(
                context.JSInterop.Invocations,
                invocation => invocation.Identifier == "localStorage.setItem");
        }, timeout: LineupWebBunitContext.WaitTimeout);
    }

    /// <summary>
    /// Verifies a generic Shaka 4032 does not replace AC3 when the browser reports AC3 support.
    /// </summary>
    [Fact]
    public async Task SupportedAc3Presentation_GenericUnsupportedError_DoesNotRetryAac()
    {
        // Arrange
        await using var context = CreateContext("""{"Protocol":2,"Quality":0,"PreferredAudio":1,"FallbackAudio":3,"SubtitleTrack":null}""");
        context.JSInterop
            .Setup<Watch.CmafStartResponse>("startCmafSession", _ => true)
            .SetResult(
                new(
                    "ac3-session",
                    "/api/stream/cmaf/ac3-session/master.m3u8",
                    "/api/stream/cmaf/ac3-session/manifest.mpd",
                    FallbackAudioCodec: "ac3"));
        context.JSInterop
            .Setup<Watch.CmafPlayerResult>("initCmafPlayer", _ => true)
            .SetResult(new(false, "Shaka Error 4032", null, 4032, FallbackAudioSupported: true));
        context.JSInterop.SetupVoid("stopCmafSession", "ac3-session").SetVoidResult();

        // Act
        var component = context.Render<Watch>(parameters => parameters.Add(page => page.ChannelNumber, "105.1"));

        // Assert
        component.WaitForAssertion(() =>
        {
            Assert.Single(context.JSInterop.Invocations, invocation => invocation.Identifier == "startCmafSession");
            Assert.Contains("Shaka Error 4032", component.Markup, StringComparison.Ordinal);
            Assert.Empty(context.Services.GetRequiredService<IStatusNotificationService>().Notifications);
        }, timeout: LineupWebBunitContext.WaitTimeout);
    }

    /// <summary>Verifies teardown after the prerender scope expires resets only existing reconnect state without resolving services.</summary>
    /// <param name="initialized">Whether playback has already created and used a reconnect policy.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dispose_ExpiredServiceScope_DoesNotInitializeReconnectPolicy(bool initialized)
    {
        // Arrange
        var registry = Substitute.For<IActiveStreamRegistry>();
        registry.GetActiveStreams().Returns([]);
        await using var context = CreateContext(registry: registry);
        var component = context.Render<Watch>();
        var instance = component.Instance;
        var policyField = Assert.IsAssignableFrom<FieldInfo>(typeof(Watch).GetField("_reconnectPolicy", BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.Null(policyField.GetValue(instance));
        PlaybackReconnectPolicy? policy = null;
        if (initialized)
        {
            policy = new PlaybackReconnectPolicy(TimeProvider.System);
            policy.MarkFailed();
            Assert.True(policy.TryNextAttempt(out _));
            policyField.SetValue(instance, policy);
        }
        using var expired = new ServiceCollection().BuildServiceProvider();
        var servicesProperty = Assert.IsAssignableFrom<PropertyInfo>(typeof(Watch).GetProperty("Services", BindingFlags.Instance | BindingFlags.NonPublic));
        servicesProperty.SetValue(instance, expired);
        expired.Dispose();
        registry.ClearReceivedCalls();

        // Act
        await context.DisposeComponentsAsync();
        var failure = context.Renderer.UnhandledException.IsCompletedSuccessfully ? await context.Renderer.UnhandledException : null;

        // Assert
        Assert.Null(failure);
        Assert.Same(policy, policyField.GetValue(instance));
        registry.Received(1).RequestStopByClientId(Arg.Any<string>());
        if (policy is not null)
        {
            Assert.True(policy.TryNextAttempt(out var delay));
            Assert.Equal(TimeSpan.FromSeconds(1), delay);
        }
    }

    /// <summary>
    /// Verifies component disposal destroys the player and explicitly stops its CMAF session.
    /// </summary>
    [Fact]
    public async Task Dispose_StopsPlayerAndServerSession()
    {
        // Arrange
        await using var context = CreateContext();
        ConfigureSuccessfulSession(context);
        var component = context.Render<Watch>(parameters => parameters.Add(page => page.ChannelNumber, "42.1"));
        component.WaitForAssertion(() =>
        {
            Assert.NotNull(component.Find("#cmafVideoPlayer"));
            Assert.Contains(context.JSInterop.Invocations, invocation => invocation.Identifier == "initCmafPlayer");
        }, timeout: LineupWebBunitContext.WaitTimeout);

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

    /// <summary>Server shutdown and client-scoped registry cleanup precede delayed browser destruction.</summary>
    [Fact]
    public async Task Stop_DelayedPlayerDestruction_ReleasesOwnedServerBeforePlayerCompletes()
    {
        // Arrange
        var registry = Substitute.For<IActiveStreamRegistry>();
        registry.GetActiveStreams().Returns([]);
        await using var context = CreateContext(registry: registry);
        ConfigureSuccessfulSession(context);
        var component = context.Render<Watch>(parameters => parameters.Add(page => page.ChannelNumber, "42.1"));
        component.WaitForAssertion(() => Assert.Contains(context.JSInterop.Invocations, invocation => invocation.Identifier == "initCmafPlayer"), timeout: LineupWebBunitContext.WaitTimeout);
        var stopPlayer = context.JSInterop.SetupVoid("stopMediaPlayer", _ => true);
        var clientId = Assert.IsType<string>(typeof(Watch).GetField("_clientId", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(component.Instance));
        registry.ClearReceivedCalls();

        // Act
        var stopping = component.InvokeAsync(() => component.Instance.OnCmafPlayerEvent(new("error", "Playback failed.", null, "session-1")));
        component.WaitForAssertion(() => Assert.Contains(context.JSInterop.Invocations, invocation => invocation.Identifier == "stopMediaPlayer"), timeout: LineupWebBunitContext.WaitTimeout);
        var serverStoppedBeforePlayer = context.JSInterop.Invocations.Any(invocation => invocation.Identifier == "stopCmafSession");
        var registryStoppedBeforePlayer = registry.ReceivedCalls().Any(call => call.GetMethodInfo().Name == nameof(IActiveStreamRegistry.RequestStopByClientId));
        var pending = !stopping.IsCompleted;
        var disposing = component.InvokeAsync(() => component.Instance.DisposeAsync().AsTask());
        stopPlayer.SetVoidResult();
        await Task.WhenAll(stopping, disposing);

        // Assert
        Assert.True(serverStoppedBeforePlayer);
        Assert.True(registryStoppedBeforePlayer);
        Assert.True(pending);
        Assert.Single(context.JSInterop.Invocations, invocation => invocation.Identifier == "stopCmafSession" && Equals(invocation.Arguments[0], "session-1"));
        registry.Received().RequestStopByClientId(clientId);
        Assert.All(registry.ReceivedCalls().Where(call => call.GetMethodInfo().Name == nameof(IActiveStreamRegistry.RequestStopByClientId)),
            call => Assert.Equal(clientId, call.GetArguments()[0]));
    }

    /// <summary>Player teardown failure cannot prevent stopping the captured server session.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stop_PlayerDestructionFails_StillStopsServer(bool disconnected)
    {
        // Arrange
        await using var context = CreateContext();
        ConfigureSuccessfulSession(context);
        var component = context.Render<Watch>(parameters => parameters.Add(page => page.ChannelNumber, "42.1"));
        component.WaitForAssertion(() => Assert.Contains(context.JSInterop.Invocations, invocation => invocation.Identifier == "initCmafPlayer"), timeout: LineupWebBunitContext.WaitTimeout);
        var stopPlayer = context.JSInterop.SetupVoid("stopMediaPlayer", _ => true);
        if (disconnected)
        {
            stopPlayer.SetException(new JSDisconnectedException("Disconnected"));
        }
        else
        {
            stopPlayer.SetException(new JSException("Destroy failed"));
        }

        // Act
        var error = await Record.ExceptionAsync(() =>
            component.InvokeAsync(() => component.Instance.OnCmafPlayerEvent(new("error", "Playback failed.", null, "session-1"))));
        stopPlayer.SetVoidResult();

        // Assert
        Assert.Null(error);
        if (!disconnected)
        {
            Assert.Contains(context.Services.GetRequiredService<IStatusNotificationService>().Notifications,
                notification => notification.Message.Contains("Destroy failed", StringComparison.Ordinal));
        }
        Assert.Single(context.JSInterop.Invocations, invocation => invocation.Identifier == "stopCmafSession" && Equals(invocation.Arguments[0], "session-1"));
    }

    /// <summary>A failed server stop is visible and retains ownership for a subsequent shutdown attempt.</summary>
    [Fact]
    public async Task Stop_ServerStopFails_RetriesCapturedSessionDuringDisposal()
    {
        // Arrange
        await using var context = CreateContext();
        ConfigureSuccessfulSession(context);
        var component = context.Render<Watch>(parameters => parameters.Add(page => page.ChannelNumber, "42.1"));
        component.WaitForAssertion(() => Assert.Contains(context.JSInterop.Invocations, invocation => invocation.Identifier == "initCmafPlayer"), timeout: LineupWebBunitContext.WaitTimeout);
        var stopServer = context.JSInterop.SetupVoid("stopCmafSession", "session-1");
        stopServer.SetException(new JSException("CMAF session stop failed: HTTP 503."));

        // Act
        var error = await Record.ExceptionAsync(() =>
            component.InvokeAsync(() => component.Instance.OnCmafPlayerEvent(new("error", "Playback failed.", null, "session-1"))));
        var playerStopped = context.JSInterop.Invocations.Any(invocation => invocation.Identifier == "stopMediaPlayer");
        stopServer.SetVoidResult();
        await component.InvokeAsync(() => component.Instance.DisposeAsync().AsTask());

        // Assert
        Assert.Null(error);
        Assert.Contains(context.Services.GetRequiredService<IStatusNotificationService>().Notifications,
            notification => notification.Message.Contains("HTTP 503", StringComparison.Ordinal));
        Assert.True(playerStopped);
        Assert.Equal(2, context.JSInterop.Invocations.Count(invocation => invocation.Identifier == "stopCmafSession" && Equals(invocation.Arguments[0], "session-1")));
    }

    /// <summary>Navigation stops the owned server session before awaiting browser destruction.</summary>
    [Fact]
    public async Task Navigation_DelayedPlayerDestruction_StopsServerBeforeLeaving()
    {
        // Arrange
        await using var context = CreateContext();
        ConfigureSuccessfulSession(context);
        var component = context.Render<Watch>(parameters => parameters.Add(page => page.ChannelNumber, "42.1"));
        component.WaitForAssertion(() => Assert.Contains(context.JSInterop.Invocations, invocation => invocation.Identifier == "initCmafPlayer"), timeout: LineupWebBunitContext.WaitTimeout);
        var stopPlayer = context.JSInterop.SetupVoid("stopMediaPlayer", _ => true);
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        navigation.LocationChanged += (_, _) => arrived.TrySetResult();

        // Act
        await component.InvokeAsync(() => navigation.NavigateTo("/next"));
        component.WaitForAssertion(() => Assert.Contains(context.JSInterop.Invocations, invocation => invocation.Identifier == "stopCmafSession"), timeout: LineupWebBunitContext.WaitTimeout);
        var pendingUri = navigation.Uri;
        stopPlayer.SetVoidResult();
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5), Xunit.TestContext.Current.CancellationToken);

        // Assert
        Assert.DoesNotContain("/next", pendingUri, StringComparison.Ordinal);
        Assert.EndsWith("/next", navigation.Uri, StringComparison.Ordinal);
        Assert.Single(context.JSInterop.Invocations, invocation => invocation.Identifier == "stopCmafSession" && Equals(invocation.Arguments[0], "session-1"));
    }

    /// <summary>
    /// Verifies disposal cancels an in-flight start and stops a server session that still completes after cancellation.
    /// </summary>
    [Fact]
    public async Task Dispose_DuringPendingStart_CancelsAndStopsLateSession()
    {
        // Arrange
        await using var context = CreateContext();
        var start = context.JSInterop.Setup<Watch.CmafStartResponse>("startCmafSession", _ => true);
        context.JSInterop.SetupVoid("stopCmafSession", "late-session").SetVoidResult();
        var component = context.Render<Watch>(parameters => parameters.Add(page => page.ChannelNumber, "42.1"));
        component.WaitForAssertion(() => Assert.Contains(context.JSInterop.Invocations, invocation => invocation.Identifier == "startCmafSession"), timeout: LineupWebBunitContext.WaitTimeout);
        var startInvocation = Assert.Single(context.JSInterop.Invocations, invocation => invocation.Identifier == "startCmafSession");
        var startupId = Assert.IsType<string>(startInvocation.Arguments[2]);

        // Act
        await component.InvokeAsync(() => component.Instance.DisposeAsync().AsTask());
        start.SetResult(new("late-session", "/api/stream/cmaf/late-session/master.m3u8", "/api/stream/cmaf/late-session/manifest.mpd"));

        // Assert
        component.WaitForAssertion(() =>
        {
            Assert.Contains(
                context.JSInterop.Invocations,
                invocation => invocation.Identifier == "cancelCmafStart" && Equals(invocation.Arguments[0], startupId));
            Assert.Contains(
                context.JSInterop.Invocations,
                invocation => invocation.Identifier == "stopCmafSession" && Equals(invocation.Arguments[0], "late-session"));
            Assert.DoesNotContain(context.JSInterop.Invocations, invocation => invocation.Identifier == "initCmafPlayer");
        }, timeout: LineupWebBunitContext.WaitTimeout);
    }

    /// <summary>
    /// Verifies a route change made during startup is tuned after the pending start completes.
    /// </summary>
    [Fact]
    public async Task RouteChange_DuringPendingStart_TunesLatestChannel()
    {
        // Arrange
        await using var context = CreateContext();
        var start = context.JSInterop.Setup<Watch.CmafStartResponse>("startCmafSession", _ => true);
        context.JSInterop
            .Setup<Watch.CmafPlayerResult>("initCmafPlayer", _ => true)
            .SetResult(new(true, null, "/api/stream/cmaf/session-1/manifest.mpd"));
        context.JSInterop.SetupVoid("stopCmafSession", "session-1").SetVoidResult();
        var component = context.Render<Watch>(parameters => parameters.Add(page => page.ChannelNumber, "42.1"));
        component.WaitForAssertion(() => Assert.Single(
            context.JSInterop.Invocations,
            invocation => invocation.Identifier == "startCmafSession"), timeout: LineupWebBunitContext.WaitTimeout);
        var setParameters = typeof(Watch).GetMethod("OnParametersSetAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(setParameters);

        // Act
        typeof(Watch).GetProperty(nameof(Watch.ChannelNumber))!.SetValue(component.Instance, "105.1");
        await component.InvokeAsync(() => Assert.IsAssignableFrom<Task>(setParameters.Invoke(component.Instance, null)));
        start.SetResult(new("session-1", "/api/stream/cmaf/session-1/master.m3u8", "/api/stream/cmaf/session-1/manifest.mpd"));

        // Assert
        component.WaitForAssertion(() =>
        {
            var starts = context.JSInterop.Invocations
                .Where(invocation => invocation.Identifier == "startCmafSession")
                .ToArray();
            Assert.Equal(2, starts.Length);
            Assert.Contains("/105.1", Assert.IsType<string>(starts[1].Arguments[0]), StringComparison.Ordinal);
        }, timeout: LineupWebBunitContext.WaitTimeout);
    }

    /// <summary>
    /// Verifies a stop operation cannot terminate a replacement session assigned while browser cleanup is pending.
    /// </summary>
    [Fact]
    public async Task Stop_WhenSessionIsReplacedDuringBrowserCleanup_StopsCapturedSession()
    {
        // Arrange
        await using var context = CreateContext();
        ConfigureSuccessfulSession(context);
        context.JSInterop.SetupVoid("stopCmafSession", "replacement-session").SetVoidResult();
        var component = context.Render<Watch>(parameters => parameters.Add(page => page.ChannelNumber, "42.1"));
        component.WaitForAssertion(() => Assert.Contains(context.JSInterop.Invocations, invocation => invocation.Identifier == "initCmafPlayer"), timeout: LineupWebBunitContext.WaitTimeout);
        var stopPlayer = context.JSInterop.SetupVoid(
            "stopMediaPlayer",
            invocation => invocation.Arguments.Count == 2 && Equals(invocation.Arguments[0], "cmafVideoPlayer"));
        var stop = component.InvokeAsync(() => component.Instance.OnCmafPlayerEvent(new("error", "Playback failed.", null, "session-1")));
        component.WaitForAssertion(() => Assert.Contains(context.JSInterop.Invocations, invocation => invocation.Identifier == "stopMediaPlayer"), timeout: LineupWebBunitContext.WaitTimeout);
        var sessionField = typeof(Watch).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(sessionField);
        sessionField.SetValue(
            component.Instance,
            new Watch.CmafStartResponse(
                "replacement-session",
                "/api/stream/cmaf/replacement-session/master.m3u8",
                "/api/stream/cmaf/replacement-session/manifest.mpd"));

        // Act
        stopPlayer.SetVoidResult();
        await stop;

        // Assert
        Assert.Contains(
            context.JSInterop.Invocations,
            invocation => invocation.Identifier == "stopCmafSession" && Equals(invocation.Arguments[0], "session-1"));
        Assert.DoesNotContain(
            context.JSInterop.Invocations,
            invocation => invocation.Identifier == "stopCmafSession" && Equals(invocation.Arguments[0], "replacement-session"));
    }

    /// <summary>
    /// Verifies a queued callback from a superseded player cannot stop or retune the replacement session.
    /// </summary>
    /// <param name="kind">The stale player event category.</param>
    /// <param name="message">The stale player event message.</param>
    [Theory]
    [InlineData("error", "Stale playback failed.")]
    [InlineData("video-fallback-required", "Stale source video failed.")]
    public async Task PlayerEvent_FromSupersededSession_DoesNotMutateReplacement(string kind, string message)
    {
        // Arrange
        await using var context = CreateContext();
        ConfigureSuccessfulSession(context);
        context.JSInterop.SetupVoid("stopCmafSession", "replacement-session").SetVoidResult();
        var component = context.Render<Watch>(parameters => parameters.Add(page => page.ChannelNumber, "42.1"));
        component.WaitForAssertion(() => Assert.Contains(context.JSInterop.Invocations, invocation => invocation.Identifier == "initCmafPlayer"), timeout: LineupWebBunitContext.WaitTimeout);
        var sessionField = typeof(Watch).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(sessionField);
        sessionField.SetValue(
            component.Instance,
            new Watch.CmafStartResponse(
                "replacement-session",
                "/api/stream/cmaf/replacement-session/master.m3u8",
                "/api/stream/cmaf/replacement-session/manifest.mpd"));
        var invocationCount = context.JSInterop.Invocations.Count;

        // Act
        await component.InvokeAsync(() => component.Instance.OnCmafPlayerEvent(new(kind, message, null, "session-1")));

        // Assert
        Assert.Equal(invocationCount, context.JSInterop.Invocations.Count);
        Assert.DoesNotContain(message, component.Markup, StringComparison.Ordinal);
        Assert.Empty(context.Services.GetRequiredService<IStatusNotificationService>().Notifications);
    }

    /// <summary>
    /// Verifies a superseded player initialization cannot overwrite the replacement session state.
    /// </summary>
    [Fact]
    public async Task PlayerInitialization_WhenSessionWasReplaced_IgnoresStaleResult()
    {
        // Arrange
        await using var context = CreateContext();
        context.JSInterop.SetupVoid("stopCmafSession", "session-2").SetVoidResult();
        context.JSInterop
            .Setup<Watch.CmafStartResponse>("startCmafSession", _ => true)
            .SetResult(new("session-1", "/api/stream/cmaf/session-1/master.m3u8", "/api/stream/cmaf/session-1/manifest.mpd"));
        var initialization = context.JSInterop.Setup<Watch.CmafPlayerResult>("initCmafPlayer", _ => true);
        var component = context.Render<Watch>(parameters => parameters.Add(page => page.ChannelNumber, "42.1"));
        component.WaitForAssertion(() => Assert.Contains(context.JSInterop.Invocations, invocation => invocation.Identifier == "initCmafPlayer"), timeout: LineupWebBunitContext.WaitTimeout);
        var sessionField = typeof(Watch).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic);
        var playerVideoField = typeof(Watch).GetField("_playerVideo", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(sessionField);
        Assert.NotNull(playerVideoField);
        sessionField.SetValue(
            component.Instance,
            new Watch.CmafStartResponse(
                "session-2",
                "/api/stream/cmaf/session-2/master.m3u8",
                "/api/stream/cmaf/session-2/manifest.mpd"));

        // Act
        initialization.SetResult(new(true, null, "/api/stream/cmaf/session-1/manifest.mpd", Video: new("stale-codec", 1920, 1080)));

        // Assert
        component.WaitForAssertion(() => Assert.Null(playerVideoField.GetValue(component.Instance)), timeout: LineupWebBunitContext.WaitTimeout);
    }

    /// <summary>
    /// Verifies all prepared text and caption sidecars are supplied to the player and omitted from the burn-in selector.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PreparedSubtitles_ArePlayerControlledAndAbsentFromBurnInSelector(bool dedicated)
    {
        // Arrange
        var settings = Substitute.For<ILineupSettingsService>();
        settings.Settings.Returns(CreateHostedWebPlayerSettings(dedicated));
        await using var context = CreateContext(settingsService: settings);
        ConfigureSuccessfulSession(context);
        var component = context.Render<Watch>(parameters => parameters.Add(page => page.ChannelNumber, "42.1"));
        component.WaitForAssertion(() => Assert.NotNull(component.Find("#cmafVideoPlayer")), timeout: LineupWebBunitContext.WaitTimeout);
        var clientIdField = typeof(Watch).GetField("_clientId", BindingFlags.Instance | BindingFlags.NonPublic);
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
            ClientId = dedicated ? null : clientId,
            WorkerMediaSessionId = dedicated ? "session-1" : null
        });

        // Assert
        component.WaitForAssertion(() =>
        {
            var starts = context.JSInterop.Invocations.Where(invocation => invocation.Identifier == "startCmafSession").ToArray();
            Assert.Single(starts);
            Assert.Empty(component.FindAll("#cmafSubtitleTrack"));
            var initialize = Assert.Single(context.JSInterop.Invocations, invocation => invocation.Identifier == "initCmafPlayer");
            var subtitles = Assert.IsAssignableFrom<IReadOnlyList<Watch.CmafSubtitleResponse>>(initialize.Arguments[8]);
            Assert.Contains(subtitles, subtitle => subtitle.IsEmbeddedClosedCaptions && subtitle.SourceIndex == 3);
            Assert.Contains("WebVTT sidecar", component.Markup);
            Assert.Contains("sidecar", component.Markup);
        }, timeout: LineupWebBunitContext.WaitTimeout);
    }

    /// <summary>
    /// Verifies bitmap subtitles retain the explicit restart-and-burn-in path.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BitmapSubtitles_RestartsWithBurnInSelection(bool dedicated)
    {
        // Arrange
        var settings = Substitute.For<ILineupSettingsService>();
        settings.Settings.Returns(CreateHostedWebPlayerSettings(dedicated));
        await using var context = CreateContext(settingsService: settings);
        ConfigureSuccessfulSession(context);
        var component = context.Render<Watch>(parameters => parameters.Add(page => page.ChannelNumber, "42.1"));
        component.WaitForAssertion(() => Assert.NotNull(component.Find("#cmafVideoPlayer")), timeout: LineupWebBunitContext.WaitTimeout);
        var clientIdField = typeof(Watch).GetField("_clientId", BindingFlags.Instance | BindingFlags.NonPublic);
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
            ClientId = dedicated ? null : clientId,
            WorkerMediaSessionId = dedicated ? "session-1" : null
        });
        component.WaitForAssertion(() => Assert.NotNull(component.Find("#cmafSubtitleTrack")), timeout: LineupWebBunitContext.WaitTimeout);
        component.WaitForAssertion(() =>
        {
            Assert.Contains("not included", component.Markup);
            Assert.DoesNotContain("not-mapped", component.Markup);
        }, timeout: LineupWebBunitContext.WaitTimeout);

        // Act
        await component.Find("#cmafSubtitleTrack").ChangeAsync(new ChangeEventArgs { Value = "4" });

        // Assert
        component.WaitForAssertion(() =>
        {
            var starts = context.JSInterop.Invocations.Where(invocation => invocation.Identifier == "startCmafSession").ToArray();
            Assert.Equal(2, starts.Length);
            var request = GetStartRequest(starts[^1]);
            Assert.Equal(4, request.SubtitleTrack);
            Assert.Equal(SubtitlePresentation.BurnIn, request.SubtitlePresentation);
        }, timeout: LineupWebBunitContext.WaitTimeout);
    }

    /// <summary>Verifies dedicated metadata matches the media ID rather than a broker ID, foreign viewer, or absent identity.</summary>
    [Fact]
    public async Task DedicatedMetadata_ForeignAndMissingMediaIds_DoNotDisplaceOwnedStream()
    {
        // Arrange
        await using var context = CreateContext();
        ConfigureSuccessfulSession(context);
        var component = context.Render<Watch>(parameters => parameters.Add(page => page.ChannelNumber, "42.1"));
        component.WaitForAssertion(() => Assert.NotNull(component.Find("#cmafVideoPlayer")), timeout: LineupWebBunitContext.WaitTimeout);
        var registry = context.Services.GetRequiredService<IActiveStreamRegistry>();
        var owned = new ActiveStreamSnapshot("broker-owned", "42.1", HostedStreamFormat.Cmaf, DateTime.UtcNow, null,
        [
            new ActiveStreamTrack(MediaTrackType.Subtitle, "dvb_subtitle", "not-mapped", null, null, null, null, null, null, null)
            {
                SourceIndex = 4,
                Title = "Owned bitmap",
                SubtitlePresentation = SubtitlePresentation.BurnIn
            }
        ])
        { WorkerMediaSessionId = "session-1" };
        var foreign = owned with
        {
            SessionId = "broker-foreign",
            ClientId = Assert.IsType<string>(typeof(Watch).GetField("_clientId", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(component.Instance)),
            WorkerMediaSessionId = "different-media-session",
            StartedAtUtc = owned.StartedAtUtc.AddSeconds(1),
            Tracks = [owned.Tracks[0] with { SourceIndex = 8, Title = "Foreign bitmap" }]
        };

        // Act
        registry.Register(owned);
        registry.Register(foreign);
        registry.Register(foreign with { SessionId = "broker-unidentified", ClientId = null, WorkerMediaSessionId = null, StartedAtUtc = owned.StartedAtUtc.AddSeconds(2) });
        await RefreshStreamInfoAsync(component);

        // Assert
        Assert.Contains("Owned bitmap", component.Find("#cmafSubtitleTrack option[value='4']").TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("Foreign bitmap", component.Markup, StringComparison.Ordinal);
        Assert.Empty(component.FindAll("#cmafSubtitleTrack option[value='8']"));
    }

    /// <summary>Verifies remote stop notifications stop only the owned dedicated or management playback.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ActiveStreamStopRequested_MatchingOwner_StopsPlayback(bool dedicated)
    {
        // Arrange
        var settings = Substitute.For<ILineupSettingsService>();
        settings.Settings.Returns(CreateHostedWebPlayerSettings(dedicated));
        await using var context = CreateContext(settingsService: settings);
        ConfigureSuccessfulSession(context);
        var component = context.Render<Watch>(parameters => parameters.Add(page => page.ChannelNumber, "42.1"));
        component.WaitForAssertion(() => Assert.NotNull(component.Find("#cmafVideoPlayer")), timeout: LineupWebBunitContext.WaitTimeout);
        var clientId = Assert.IsType<string>(typeof(Watch).GetField("_clientId", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(component.Instance));
        var registry = context.Services.GetRequiredService<IActiveStreamRegistry>();
        var stopped = 0;
        var owned = new ActiveStreamSnapshot("broker-owned", "42.1", HostedStreamFormat.Cmaf, DateTime.UtcNow, null, [])
        {
            ClientId = dedicated ? null : clientId,
            WorkerMediaSessionId = dedicated ? "session-1" : null
        };
        registry.Register(owned, () => stopped++);
        registry.Register(owned with { SessionId = "broker-foreign", ClientId = clientId, WorkerMediaSessionId = "other-session" }, () => { });

        // Act
        await component.InvokeAsync(() => Assert.True(registry.RequestStop("broker-foreign")));
        Assert.DoesNotContain(context.JSInterop.Invocations, invocation => invocation.Identifier == "stopCmafSession");
        Assert.NotNull(component.Find("#cmafVideoPlayer"));
        await component.InvokeAsync(() => Assert.True(registry.RequestStop("broker-owned")));

        // Assert
        component.WaitForAssertion(() =>
        {
            Assert.Equal(1, stopped);
            Assert.Empty(component.FindAll("#cmafVideoPlayer"));
            var serverStop = Assert.Single(context.JSInterop.Invocations, invocation => invocation.Identifier == "stopCmafSession");
            Assert.Equal("session-1", serverStop.Arguments[0]);
        }, timeout: LineupWebBunitContext.WaitTimeout);
    }

    /// <summary>Verifies an old worker notification cannot stop replacement playback.</summary>
    [Fact]
    public async Task ActiveStreamStopRequested_PreviousMediaSession_IgnoresStaleNotification()
    {
        // Arrange
        await using var context = CreateContext();
        ConfigureSuccessfulSession(context);
        var component = context.Render<Watch>(parameters => parameters.Add(page => page.ChannelNumber, "42.1"));
        component.WaitForAssertion(() => Assert.NotNull(component.Find("#cmafVideoPlayer")), timeout: LineupWebBunitContext.WaitTimeout);
        ConfigureSuccessfulSession(context, sessionId: "session-2");
        component.Render(parameters => parameters.Add(page => page.ChannelNumber, "43.1"));
        component.WaitForAssertion(() => Assert.Equal(2, context.JSInterop.Invocations.Count(invocation => invocation.Identifier == "startCmafSession")), timeout: LineupWebBunitContext.WaitTimeout);
        var registry = context.Services.GetRequiredService<IActiveStreamRegistry>();
        registry.Register(new ActiveStreamSnapshot("stale-broker", "42.1", HostedStreamFormat.Cmaf, DateTime.UtcNow, null, [])
        {
            WorkerMediaSessionId = "session-1",
            ClientId = Assert.IsType<string>(typeof(Watch).GetField("_clientId", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(component.Instance))
        }, () => { });

        // Act
        await component.InvokeAsync(() => Assert.True(registry.RequestStop("stale-broker")));
        await RefreshStreamInfoAsync(component);

        // Assert
        Assert.NotNull(component.Find("#cmafVideoPlayer"));
        var serverStop = Assert.Single(context.JSInterop.Invocations, invocation => invocation.Identifier == "stopCmafSession");
        Assert.Equal("session-1", serverStop.Arguments[0]);
        Assert.DoesNotContain(context.JSInterop.Invocations, invocation => invocation.Identifier == "stopCmafSession" && Equals(invocation.Arguments[0], "session-2"));
    }

    /// <summary>Verifies dedicated broker ownership is released even when browser-side server shutdown fails.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Dispose_DedicatedServerStopFails_ReleasesOnlyOwnedBroker(bool disconnected)
    {
        // Arrange
        await using var context = CreateContext();
        ConfigureSuccessfulSession(context);
        var component = context.Render<Watch>(parameters => parameters.Add(page => page.ChannelNumber, "42.1"));
        component.WaitForAssertion(() => Assert.NotNull(component.Find("#cmafVideoPlayer")), timeout: LineupWebBunitContext.WaitTimeout);
        var registry = context.Services.GetRequiredService<IActiveStreamRegistry>();
        var ownedStops = 0;
        var foreignStops = 0;
        var owned = new ActiveStreamSnapshot("broker-owned", "42.1", HostedStreamFormat.Cmaf, DateTime.UtcNow, null, []) { WorkerMediaSessionId = "session-1" };
        registry.Register(owned, () => ownedStops++);
        registry.Register(owned with { SessionId = "broker-foreign", WorkerMediaSessionId = "other-session" }, () => foreignStops++);
        Exception failure = disconnected ? new JSDisconnectedException("Browser disconnected.") : new JSException("Server stop failed.");
        context.JSInterop.SetupVoid("stopCmafSession", "session-1").SetException(failure);

        // Act
        await component.InvokeAsync(() => component.Instance.DisposeAsync().AsTask());

        // Assert
        Assert.Equal(1, ownedStops);
        Assert.Equal(0, foreignStops);
        Assert.False(registry.RequestStop("broker-owned"));
        Assert.True(registry.RequestStop("broker-foreign"));
        Assert.Equal(1, foreignStops);
    }

    /// <summary>
    /// Verifies CMAF retains Watch channel metadata and detailed tuner and hosted-stream diagnostics.
    /// </summary>
    [Fact]
    public async Task ChannelAndStreamInformation_MatchesWatchDetails()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;
        var repository = Substitute.For<ILineupGuideReader>();
        repository.GetChannelDirectoryAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new GuideChannelSegment { GuideNumber = "2.1", GuideName = "Test Channel", Favorite = true, DRM = true }
        ]);
        repository.GetProgramsAsync(Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>()).Returns(
        [
            new GuideCacheProgram
            {
                GuideNumber = "2.1",
                Title = "Current Show",
                EpisodeTitle = "Current Episode",
                StartTime = now.AddMinutes(-5).ToUnixTimeSeconds(),
                EndTime = now.AddMinutes(55).ToUnixTimeSeconds()
            }
        ]);
        var deviceState = Substitute.For<IPhysicalDeviceStatusService>();
        deviceState.Devices.Returns(
        [
            new PhysicalDeviceRuntimeStatus
            {
                Device = new PhysicalDeviceSnapshot
                {
                    Id = PhysicalDeviceId.New(),
                    Kind = "test",
                    Name = "Test",
                    Capacity = new PhysicalTunerCapacity(1)
                },
                Status = new PhysicalDeviceStatusSnapshot
                {
                    Device = new PhysicalDeviceSnapshot
                    {
                        Id = PhysicalDeviceId.New(),
                        Kind = "test",
                        Name = "Test",
                        Capacity = new PhysicalTunerCapacity(1)
                    },
                    ObservedAtUtc = DateTimeOffset.UtcNow,
                    Receivers =
                    [
                        new PhysicalReceiverStatus
                        {
                            Index = 1,
                            VirtualChannel = "2.1",
                            Target = "http",
                            LockType = "8vsb",
                            SignalStrength = 85,
                            SignalQuality = 92,
                            SymbolQuality = 100,
                            BitsPerSecond = 19_000_000,
                            PacketsPerSecond = 1_200
                        }
                    ]
                }
            }
        ]);
        var settings = Substitute.For<ILineupSettingsService>();
        settings.Settings.Returns(CreateHostedWebPlayerSettings());
        var registry = new ActiveStreamRegistry();
        await using var context = CreateContext(repository: repository, deviceState: deviceState, settingsService: settings, registry: registry);
        ConfigureSuccessfulSession(context);
        var component = context.Render<Watch>();
        var clientIdField = typeof(Watch).GetField("_clientId", BindingFlags.Instance | BindingFlags.NonPublic);
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
        await component.WaitForElement(".cmaf-channel-item", timeout: LineupWebBunitContext.WaitTimeout).ClickAsync(new());

        // Assert
        component.WaitForAssertion(() =>
        {
            var channel = component.Find(".cmaf-channel-item");
            Assert.Contains("Current Show", channel.TextContent);
            Assert.NotNull(channel.QuerySelector("[aria-label='Favorite channel']"));
            Assert.NotNull(channel.QuerySelector("[aria-label='DRM-protected channel']"));
            Assert.Contains("Current Show", component.Markup);
            Assert.Contains("Current Episode", component.Markup);
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
        }, timeout: LineupWebBunitContext.WaitTimeout);
    }

    private static BunitContext CreateContext(
        string? preferencesJson = null,
        ILineupGuideReader? repository = null,
        IPhysicalDeviceStatusService? deviceState = null,
        ILineupSettingsService? settingsService = null,
        IActiveStreamRegistry? registry = null)
    {
        var context = new BunitContext();
        if (repository is null)
        {
            repository = Substitute.For<ILineupGuideReader>();
            repository.GetChannelDirectoryAsync(Arg.Any<CancellationToken>()).Returns([]);
            repository.GetProgramsAsync(Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>()).Returns([]);
        }
        if (deviceState is null)
        {
            deviceState = Substitute.For<IPhysicalDeviceStatusService>();
            deviceState.Devices.Returns([]);
        }
        if (settingsService is null)
        {
            settingsService = Substitute.For<ILineupSettingsService>();
            settingsService.Settings.Returns(CreateHostedWebPlayerSettings());
        }
        context.Services.AddSingleton(repository);
        context.Services.AddSingleton(deviceState);
        context.Services.AddSingleton(settingsService);
        context.Services.AddSingleton(registry ?? new ActiveStreamRegistry());
        context.Services.AddSingleton<IStatusNotificationService>(new StatusNotificationService());
        context.Services.AddScoped<IBrowserDataStore, BrowserDataStore>();
        context.JSInterop.Setup<string?>("localStorage.getItem", PreferencesStorageKey).SetResult(preferencesJson);
        context.JSInterop.Setup<string?>("localStorage.getItem", CmafCompatibilityProfile.StorageKey).SetResult(null);
        context.JSInterop.SetupVoid("localStorage.setItem", _ => true).SetVoidResult();
        context.JSInterop.SetupVoid("cancelCmafStart", _ => true).SetVoidResult();
        context.JSInterop.SetupVoid("stopMediaPlayer", _ => true).SetVoidResult();
        context.JSInterop.SetupVoid("stopCmafSession", "session-1").SetVoidResult();
        return context;
    }

    private static void ConfigureSuccessfulSession(BunitContext context, bool sourceAudioFallbackApplied = false, string sessionId = "session-1")
    {
        context.JSInterop
            .Setup<Watch.CmafStartResponse>("startCmafSession", _ => true)
            .SetResult(new(
                sessionId,
                $"/api/stream/cmaf/{sessionId}/master.m3u8",
                $"/api/stream/cmaf/{sessionId}/manifest.mpd",
                sourceAudioFallbackApplied,
                "Fallback AAC Stereo",
                [
                    new(2, "English", "eng", $"/api/stream/cmaf/{sessionId}/captions-2.vtt", false),
                    new(3, "Closed Captions", "eng", $"/api/stream/cmaf/{sessionId}/captions-3.vtt", true)
                ],
                FallbackAudioCodec: "aac"));
        context.JSInterop
            .Setup<Watch.CmafPlayerResult>("initCmafPlayer", _ => true)
            .SetResult(new(
                true,
                null,
                $"/api/stream/cmaf/{sessionId}/manifest.mpd",
                Audio: new("Fallback AAC Stereo", "mp4a.40.2", 2),
                Video: new("avc1.64002a", 1920, 1080)));
        context.JSInterop.SetupVoid("stopCmafSession", sessionId).SetVoidResult();
        context.JSInterop.SetupVoid("stopCmafSession", sessionId, $"/api/stream/cmaf/stop/{sessionId}").SetVoidResult();
    }

    private static CmafStreamRequest GetStartRequest(JSRuntimeInvocation invocation) =>
        Assert.IsType<CmafStreamRequest>(invocation.Arguments[1]);

    private static Task RefreshStreamInfoAsync(IRenderedComponent<Watch> component) => component.InvokeAsync(() =>
    {
        var refresh = typeof(Watch).GetMethod("RefreshStreamInfoAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(refresh);
        return Assert.IsAssignableFrom<Task>(refresh.Invoke(component.Instance, null));
    });

    private static LineupSettings CreateHostedWebPlayerSettings(bool dedicated = true) =>
        new()
        {
            Streaming = new StreamingConfiguration
            {
                VirtualDevices =
                [
                    WebPlayerVirtualTunerDriver.CreateBuiltIn() with
                    {
                        Hosting = dedicated ? new VirtualOutputHostingDefinition
                        {
                            Bindings =
                            [
                                new VirtualOutputBindingDefinition
                                {
                                    PublicBaseUri = "http://localhost:8600/"
                                }
                            ]
                        } : null
                    }
                ]
            }
        };

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
