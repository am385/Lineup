using Bunit;
using Lineup.Web.Components.Pages;
using Lineup.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using System.Text.Json;
using Xunit;

namespace Lineup.Web.Tests.Components.Pages;

/// <summary>
/// Verifies Watch Test browser diagnostics, layout, and compatibility-profile persistence.
/// </summary>
public class WatchTestTests
{
    /// <summary>
    /// Verifies a playback error completes a pending compatibility case as failed before its result can be persisted.
    /// </summary>
    [Fact]
    public async Task OnCmafTestPlayerError_WhileCaseIsPending_CompletesCaseAsFailure()
    {
        // Arrange
        using var context = CreateContext();
        var component = context.Render<WatchTest>();
        component.WaitForAssertion(() => Assert.Contains("Browser Claims", component.Markup, StringComparison.Ordinal));
        var pendingField = typeof(WatchTest).GetField("_pendingPlayerResult", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(pendingField);
        var pending = new TaskCompletionSource<WatchTest.CmafTestPlayerResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        pendingField.SetValue(component.Instance, pending);
        using var details = JsonDocument.Parse("""{"severity":2,"category":4,"code":4032}""");

        // Act
        await component.InvokeAsync(() => component.Instance.OnCmafTestPlayerError(
            new("Decoder failed after playback started.", 4032, details.RootElement.Clone())));
        var result = await pending.Task.WaitAsync(Xunit.TestContext.Current.CancellationToken);

        // Assert
        Assert.False(result.Success);
        Assert.False(result.PlaybackStarted);
        Assert.Equal("Decoder failed after playback started.", result.Error);
        Assert.Equal(4032, result.ErrorCode);
        Assert.Equal(4032, result.ErrorDetails?.GetProperty("code").GetInt32());
    }

    /// <summary>
    /// Verifies the Watch launch flag automatically runs and saves the complete compatibility suite.
    /// </summary>
    [Fact]
    public void RunAllOnLoad_StartsCompleteCompatibilitySuite()
    {
        // Arrange
        using var context = CreateContext();
        context.JSInterop
            .Setup<CmafCompatibilityTestResponse>("startCmafSession", _ => true)
            .SetResult(new("test-1", "/api/stream/cmaf/test-1/manifest.mpd", "avc1.64002a", "mp4a.40.2", 8, 1920, 1080, "/api/stream/cmaf/test-1/captions-0.vtt"));
        context.JSInterop
            .Setup<WatchTest.CmafTestPlayerResult>("initCmafTestPlayer", _ => true)
            .SetResult(new(
                true,
                true,
                Video: new("avc1.64002a", 1920, 1080),
                Audio: new("Audio", "mp4a.40.2", 8),
                SubtitleCueVisible: true));
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/watch-test?runAll=true");

        // Act
        var component = context.Render<WatchTest>();

        // Assert
        component.WaitForAssertion(() =>
        {
            Assert.Equal(
                CmafCompatibilityTestCatalog.All.Count(test => !test.IsUnavailable),
                context.JSInterop.Invocations.Count(invocation => invocation.Identifier == "startCmafSession"));
            Assert.Contains(
                context.JSInterop.Invocations,
                invocation => invocation.Identifier == "localStorage.setItem" &&
                    Equals(invocation.Arguments[0], CmafCompatibilityProfile.StorageKey));
        }, TimeSpan.FromSeconds(15));
        var startedSessionCount = context.JSInterop.Invocations.Count(invocation => invocation.Identifier == "startCmafSession");
        component.Render();
        Assert.Equal(startedSessionCount, context.JSInterop.Invocations.Count(invocation => invocation.Identifier == "startCmafSession"));
    }

    /// <summary>
    /// Verifies Run All measures the server-owned catalog and atomically saves a complete browser profile.
    /// </summary>
    [Fact]
    public void RunAll_CompletedSuite_WritesCompatibilityProfile()
    {
        // Arrange
        using var context = CreateContext();
        context.JSInterop
            .Setup<CmafCompatibilityTestResponse>("startCmafSession", _ => true)
            .SetResult(new("test-1", "/api/stream/cmaf/test-1/manifest.mpd", "avc1.64002a", "mp4a.40.2", 8, 1920, 1080, "/api/stream/cmaf/test-1/captions-0.vtt"));
        context.JSInterop
            .Setup<WatchTest.CmafTestPlayerResult>("initCmafTestPlayer", _ => true)
            .SetResult(new(
                true,
                true,
                Video: new("avc1.64002a", 1920, 1080),
                Audio: new("Audio", "mp4a.40.2", 8),
                SubtitleCueVisible: true));
        var component = context.Render<WatchTest>();
        component.WaitForAssertion(() => Assert.Contains("Browser Claims", component.Markup, StringComparison.Ordinal));

        // Act
        component.Find("#runAllCompatibilityTests").Click();

        // Assert
        component.WaitForAssertion(() =>
        {
            var write = Assert.Single(
                context.JSInterop.Invocations,
                invocation => invocation.Identifier == "localStorage.setItem" && Equals(invocation.Arguments[0], CmafCompatibilityProfile.StorageKey));
            var json = Assert.IsType<string>(write.Arguments[1]);
            Assert.Contains($"\"SchemaVersion\":{CmafCompatibilityProfile.CurrentSchemaVersion}", json, StringComparison.Ordinal);
            Assert.Contains("\"CaseId\":\"video-hevc-main10-1080p\"", json, StringComparison.Ordinal);
            Assert.Contains("\"CaseId\":\"subtitle-webvtt\"", json, StringComparison.Ordinal);
            Assert.Contains("\"Status\":2", json, StringComparison.Ordinal);
            Assert.Contains("Browser compatibility profile saved", context.Services.GetRequiredService<IStatusNotificationService>().Notifications.Single().Message, StringComparison.Ordinal);
        }, TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// Verifies Run All centers progress text across the complete track instead of clipping it to the filled bar.
    /// </summary>
    [Fact]
    public async Task RunAll_InProgress_OverlaysProgressTextAcrossTrack()
    {
        // Arrange
        using var context = CreateContext();
        context.JSInterop
            .Setup<CmafCompatibilityTestResponse>("startCmafSession", _ => true)
            .SetResult(new("test-1", "/api/stream/cmaf/test-1/manifest.mpd", "avc1.64002a", "mp4a.40.2", 2, 1920, 1080));
        context.JSInterop.Setup<WatchTest.CmafTestPlayerResult>("initCmafTestPlayer", _ => true);
        var component = context.Render<WatchTest>();
        component.WaitForAssertion(() => Assert.Contains("Browser Claims", component.Markup, StringComparison.Ordinal));
        var runAll = typeof(WatchTest).GetMethod("RunAllAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(runAll);
        Task runTask = null!;
        await component.InvokeAsync(() =>
        {
            runTask = Assert.IsAssignableFrom<Task>(runAll.Invoke(component.Instance, null));
        });
        component.WaitForAssertion(() => Assert.Contains(context.JSInterop.Invocations, invocation => invocation.Identifier == "initCmafTestPlayer"));

        // Act
        var progress = component.Find("[role='progressbar']");
        var progressText = component.Find("#compatibilityProgressText");

        // Assert
        Assert.Contains("position-relative", progress.ClassList);
        Assert.Empty(progress.QuerySelector(".progress-bar")!.TextContent.Trim());
        Assert.Contains("position-absolute", progressText.ClassList);
        Assert.Equal($"0 / {CmafCompatibilityTestCatalog.All.Count}", progressText.TextContent.Trim());
        component.Find("#cancelCompatibilityTests").Click();
        await runTask.WaitAsync(TimeSpan.FromSeconds(1), Xunit.TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Verifies a successful compatibility case clears the page-level error left by the preceding case.
    /// </summary>
    [Fact]
    public async Task RunCase_AfterPreviousFailure_ClearsPageError()
    {
        // Arrange
        using var context = CreateContext();
        context.JSInterop
            .Setup<CmafCompatibilityTestResponse>("startCmafSession", _ => true)
            .SetResult(new("test-1", "/api/stream/cmaf/test-1/manifest.mpd", "avc1.64002a", "mp4a.40.2", 2, 1920, 1080));
        context.JSInterop
            .Setup<WatchTest.CmafTestPlayerResult>("initCmafTestPlayer", _ => true)
            .SetResult(new(true, true, Video: new("avc1.64002a", 1920, 1080)));
        var component = context.Render<WatchTest>();
        component.WaitForAssertion(() => Assert.Contains("Browser Claims", component.Markup, StringComparison.Ordinal));
        var errorField = typeof(WatchTest).GetField("_errorMessage", BindingFlags.Instance | BindingFlags.NonPublic);
        var runCase = typeof(WatchTest).GetMethod("RunCaseAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(errorField);
        Assert.NotNull(runCase);
        errorField.SetValue(component.Instance, "Shaka Error 4032");
        component.Render();
        var test = CmafCompatibilityTestCatalog.All.Single(candidate => candidate.Id == "video-h264-1080p");

        // Act
        Task runTask = null!;
        await component.InvokeAsync(() =>
        {
            runTask = Assert.IsAssignableFrom<Task>(runCase.Invoke(component.Instance, [test, Xunit.TestContext.Current.CancellationToken]));
        });
        await runTask.WaitAsync(TimeSpan.FromSeconds(1), Xunit.TestContext.Current.CancellationToken);

        // Assert
        component.WaitForAssertion(() => Assert.Empty(component.FindAll(".alert-danger")));
    }

    /// <summary>
    /// Verifies browser codec claims are displayed without starting a media session.
    /// </summary>
    [Fact]
    public void BrowserClaims_RenderBeforePlayback()
    {
        // Arrange
        using var context = CreateContext();

        // Act
        var component = context.Render<WatchTest>();

        // Assert
        component.WaitForAssertion(() =>
        {
            Assert.Contains("Browser Claims", component.Markup, StringComparison.Ordinal);
            Assert.Contains("Not supported", component.Markup, StringComparison.Ordinal);
            Assert.DoesNotContain(context.JSInterop.Invocations, invocation => invocation.Identifier == "startCmafSession");
            Assert.DoesNotContain(context.JSInterop.Invocations, invocation => invocation.Identifier == "localStorage.setItem");
        });
    }

    /// <summary>
    /// Verifies the page places automated controls and results in the requested regions without the legacy settings action.
    /// </summary>
    [Fact]
    public void Layout_AutomatedActionsResultsAndClaims_AreReorganized()
    {
        // Arrange
        using var context = CreateContext();

        // Act
        var component = context.Render<WatchTest>();

        // Assert
        component.WaitForAssertion(() =>
        {
            Assert.NotNull(component.Find("#automatedCompatibilityActions #runAllCompatibilityTests"));
            Assert.Equal(CmafCompatibilityTestCatalog.All.Count, component.FindAll("#compatibilityTestResults tbody tr").Count);
            Assert.Equal(CmafCompatibilityTestCatalog.All.Count, component.FindAll("#compatibilityTestResults button").Count);
            Assert.True(component.Markup.IndexOf("id=\"automatedCompatibilityProfile\"", StringComparison.Ordinal) <
                component.Markup.IndexOf("id=\"exactTestSelection\"", StringComparison.Ordinal));
            Assert.True(component.Markup.IndexOf("id=\"exactTestSelection\"", StringComparison.Ordinal) <
                component.Markup.IndexOf("id=\"browserClaims\"", StringComparison.Ordinal));
            Assert.Contains("Not run", component.Markup, StringComparison.Ordinal);
            Assert.Contains("Front Left → Front Right", component.Find("#channelTestSequence").TextContent, StringComparison.Ordinal);
            Assert.DoesNotContain("Save as Watch Settings", component.Markup, StringComparison.Ordinal);
            Assert.DoesNotContain(
                context.JSInterop.Invocations,
                invocation => invocation.Identifier == "localStorage.setItem" && Equals(invocation.Arguments[0], CmafWatchPreferences.StorageKey));
        });
    }

    /// <summary>
    /// Verifies an individual unavailable case updates the in-progress matrix without starting media or saving an incomplete profile.
    /// </summary>
    [Fact]
    public void RunSingleCase_BeforeCompleteProfile_AccumulatesWithoutSaving()
    {
        // Arrange
        using var context = CreateContext();
        var component = context.Render<WatchTest>();
        component.WaitForAssertion(() => Assert.Contains("Browser Claims", component.Markup, StringComparison.Ordinal));

        // Act
        component.Find("#runCompatibilityTest-audio-ac4-unavailable").Click();

        // Assert
        component.WaitForAssertion(() =>
        {
            var row = component.Find("[data-test-case='audio-ac4-unavailable']");
            Assert.Contains(nameof(CmafCapabilityStatus.Unavailable), row.TextContent, StringComparison.Ordinal);
            Assert.DoesNotContain(context.JSInterop.Invocations, invocation => invocation.Identifier == "startCmafSession");
            Assert.DoesNotContain(
                context.JSInterop.Invocations,
                invocation => invocation.Identifier == "localStorage.setItem" && Equals(invocation.Arguments[0], CmafCompatibilityProfile.StorageKey));
        });
    }

    /// <summary>
    /// Verifies rerunning one row replaces only that result and persists a complete valid profile.
    /// </summary>
    [Fact]
    public void RunSingleCase_ExistingProfile_ReplacesRowAndSavesCompleteProfile()
    {
        // Arrange
        var storedProfile = CreateCompatibilityProfile("video-h264-1080p");
        using var context = CreateContext(storedProfile);
        context.JSInterop
            .Setup<CmafCompatibilityTestResponse>("startCmafSession", _ => true)
            .SetResult(new("test-1", "/api/stream/cmaf/test-1/manifest.mpd", "avc1.64002a", "mp4a.40.2", 2, 1920, 1080));
        context.JSInterop
            .Setup<WatchTest.CmafTestPlayerResult>("initCmafTestPlayer", _ => true)
            .SetResult(new(
                true,
                true,
                "/api/stream/cmaf/test-1/manifest.mpd",
                true,
                true,
                new("AAC 2ch", "mp4a.40.2", 2),
                new("avc1.64002a", 1920, 1080)));
        var component = context.Render<WatchTest>();
        component.WaitForAssertion(() => Assert.Contains(nameof(CmafCapabilityStatus.Failed), component.Find("[data-test-case='video-h264-1080p']").TextContent, StringComparison.Ordinal));

        // Act
        component.Find("#runCompatibilityTest-video-h264-1080p").Click();

        // Assert
        component.WaitForAssertion(() =>
        {
            var write = Assert.Single(
                context.JSInterop.Invocations,
                invocation => invocation.Identifier == "localStorage.setItem" && Equals(invocation.Arguments[0], CmafCompatibilityProfile.StorageKey));
            var saved = JsonSerializer.Deserialize<CmafCompatibilityProfile>(Assert.IsType<string>(write.Arguments[1]));
            Assert.True(CmafCompatibilityProfilePolicy.IsValid(saved));
            Assert.Equal(CmafCapabilityStatus.Passed, saved!.Results.Single(result => result.CaseId == "video-h264-1080p").Status);
            Assert.Equal(
                storedProfile.Results.Single(result => result.CaseId == "protocol-dash"),
                saved.Results.Single(result => result.CaseId == "protocol-dash"));
            Assert.NotNull(component.Find("#observedPlayback"));
            Assert.False(component.Find("#runCompatibilityTest-protocol-dash").HasAttribute("disabled"));
        });
    }

    /// <summary>
    /// Verifies Exact Test Selection requests audible playback after the player starts.
    /// </summary>
    [Fact]
    public void StartManualTest_InitializesAudiblePlayback()
    {
        // Arrange
        using var context = CreateContext();
        context.JSInterop
            .Setup<CmafCompatibilityTestResponse>("startCmafSession", _ => true)
            .SetResult(new("test-1", "/api/stream/cmaf/test-1/manifest.mpd", "avc1.64002a", "mp4a.40.2", 2, 1920, 1080));
        context.JSInterop
            .Setup<WatchTest.CmafTestPlayerResult>("initCmafTestPlayer", _ => true)
            .SetResult(new(true, true, Audio: new("AAC 2ch", "mp4a.40.2", 2), Video: new("avc1.64002a", 1920, 1080)));
        var component = context.Render<WatchTest>();
        component.WaitForAssertion(() => Assert.Contains("Browser Claims", component.Markup, StringComparison.Ordinal));

        // Act
        component.Find("#startManualCompatibilityTest").Click();

        // Assert
        component.WaitForAssertion(() =>
        {
            Assert.Single(context.JSInterop.Invocations, invocation => invocation.Identifier == "initCmafTestPlayer");
            Assert.False(component.Find("#cmafTestVideoPlayer").HasAttribute("muted"));
        });
    }

    /// <summary>
    /// Verifies malformed browser storage does not prevent Watch Test from rendering a fresh profile.
    /// </summary>
    [Fact]
    public void MalformedStoredProfile_RendersFreshTestMatrix()
    {
        // Arrange
        using var context = CreateContext();
        context.JSInterop.Setup<string?>("localStorage.getItem", CmafCompatibilityProfile.StorageKey).SetResult("{not-json");

        // Act
        var component = context.Render<WatchTest>();

        // Assert
        component.WaitForAssertion(() =>
        {
            Assert.Equal(CmafCompatibilityTestCatalog.All.Count, component.FindAll("#compatibilityTestResults tbody tr").Count);
            Assert.Contains("Not yet complete", component.Markup, StringComparison.Ordinal);
            Assert.Contains("Unable to read browser codec capabilities", component.Markup, StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// Verifies selecting AC-4 reports the encoder limitation and cannot start a substituted test.
    /// </summary>
    [Fact]
    public void Ac4Selection_DisablesSyntheticPlayback()
    {
        // Arrange
        using var context = CreateContext();
        var component = context.Render<WatchTest>();
        component.WaitForAssertion(() => Assert.Contains("Browser Claims", component.Markup, StringComparison.Ordinal));

        // Act
        component.Find("#testAudioCodec").Change(CmafTestAudioCodec.Ac4.ToString());

        // Assert
        component.WaitForAssertion(() =>
        {
            Assert.Contains("has no AC-4 encoder", component.Markup, StringComparison.Ordinal);
            Assert.True(component.Find("#startManualCompatibilityTest").HasAttribute("disabled"));
            Assert.DoesNotContain(context.JSInterop.Invocations, invocation => invocation.Identifier == "startCmafSession");
        });
    }

    /// <summary>
    /// Verifies disposing the page cancels an individual test that is waiting for player initialization.
    /// </summary>
    [Fact]
    public async Task DisposeAsync_PendingSingleCase_CompletesRun()
    {
        // Arrange
        using var context = CreateContext();
        context.JSInterop
            .Setup<CmafCompatibilityTestResponse>("startCmafSession", _ => true)
            .SetResult(new("test-1", "/api/stream/cmaf/test-1/manifest.mpd", "avc1.64002a", "mp4a.40.2", 2, 1920, 1080));
        context.JSInterop.Setup<WatchTest.CmafTestPlayerResult>("initCmafTestPlayer", _ => true);
        var component = context.Render<WatchTest>();
        component.WaitForAssertion(() => Assert.Contains("Browser Claims", component.Markup, StringComparison.Ordinal));
        var runSingleCase = typeof(WatchTest).GetMethod("RunSingleCaseAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(runSingleCase);
        var test = CmafCompatibilityTestCatalog.All.Single(candidate => candidate.Id == "video-h264-1080p");
        Task runTask = null!;
        await component.InvokeAsync(() =>
        {
            runTask = Assert.IsAssignableFrom<Task>(runSingleCase.Invoke(component.Instance, [test]));
        });
        component.WaitForAssertion(() => Assert.Contains(context.JSInterop.Invocations, invocation => invocation.Identifier == "initCmafTestPlayer"));
        Assert.True(component.Find("#cmafTestVideoPlayer").HasAttribute("muted"));

        // Act
        await component.Instance.DisposeAsync();

        // Assert
        await runTask.WaitAsync(TimeSpan.FromSeconds(1), Xunit.TestContext.Current.CancellationToken);
    }

    private static BunitContext CreateContext(CmafCompatibilityProfile? storedProfile = null)
    {
        BunitContext.DefaultWaitTimeout = TimeSpan.FromSeconds(5);
        var context = new BunitContext();
        context.Services.AddScoped<IBrowserDataStore, BrowserDataStore>();
        context.Services.AddSingleton<IStatusNotificationService>(new StatusNotificationService());
        context.JSInterop
            .Setup<WatchTest.CmafTestCapabilities>("getCmafTestCapabilities")
            .SetResult(new(true, true, string.Empty, true, false, true, false, false, false));
        context.JSInterop.SetupVoid("stopMediaPlayer", "cmafTestVideoPlayer").SetVoidResult();
        context.JSInterop.SetupVoid("stopCmafSession", "test-1").SetVoidResult();
        context.JSInterop.Setup<string?>("localStorage.getItem", CmafWatchPreferences.StorageKey).SetResult(null);
        context.JSInterop
            .Setup<string?>("localStorage.getItem", CmafCompatibilityProfile.StorageKey)
            .SetResult(storedProfile is null ? null : JsonSerializer.Serialize(storedProfile));
        context.JSInterop.SetupVoid("localStorage.setItem", _ => true).SetVoidResult();
        return context;
    }

    private static CmafCompatibilityProfile CreateCompatibilityProfile(string failedCaseId) =>
        new()
        {
            BrowserIdentity = "unknown",
            CompletedAtUtc = DateTimeOffset.UtcNow,
            Claims = new CmafBrowserClaims(),
            Results = CmafCompatibilityTestCatalog.All
                .Select(test => new CmafCapabilityResult
                {
                    CaseId = test.Id,
                    Kind = test.Kind,
                    Request = test.Request,
                    Status = test.IsUnavailable
                        ? CmafCapabilityStatus.Unavailable
                        : string.Equals(test.Id, failedCaseId, StringComparison.Ordinal)
                            ? CmafCapabilityStatus.Failed
                            : CmafCapabilityStatus.Passed
                })
                .ToArray()
        };
}
