using System.Collections;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using Lineup.Core;
using Lineup.Core.Storage;
using Lineup.HDHomeRun.Api.Models;
using Lineup.HDHomeRun.Device.Models;
using Lineup.Web.Controllers;
using Lineup.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Lineup.Web.Tests.Controllers;

/// <summary>
/// Verifies security-sensitive stream controller behavior.
/// </summary>
public class StreamControllerTests
{
    /// <summary>
    /// Verifies capability-aware startup rejects incomplete client profiles before opening a tuner.
    /// </summary>
    [Fact]
    public async Task StartCmafStreamV2_IncompleteProfile_ReturnsBadRequest()
    {
        // Arrange
        var controller = CreateLifecycleController(new ActiveStreamRegistry());
        var request = new CmafStreamRequest
        {
            CompatibilityProfile = new CmafCompatibilityProfile
            {
                BrowserIdentity = "test-browser",
                CompletedAtUtc = DateTimeOffset.UtcNow,
                Claims = new CmafBrowserClaims(),
                Results = []
            }
        };

        // Act
        var result = await controller.StartCmafStreamV2("42.1", request);

        // Assert
        var response = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("incomplete", response.Value?.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Verifies capability-aware startup rejects invalid override enum values before opening a tuner.
    /// </summary>
    [Fact]
    public async Task StartCmafStreamV2_InvalidOverride_ReturnsBadRequest()
    {
        // Arrange
        var controller = CreateLifecycleController(new ActiveStreamRegistry());
        var request = new CmafStreamRequest
        {
            Overrides = new CmafStreamOverrides
            {
                Enabled = true,
                Video = (CmafPreferredVideo)99
            }
        };

        // Act
        var result = await controller.StartCmafStreamV2("42.1", request);

        // Assert
        var response = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("video override", response.Value?.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Verifies unavailable synthetic encoders are rejected before a compatibility session starts.
    /// </summary>
    [Fact]
    public async Task StartCmafCompatibilityTest_Ac4ReturnsUnprocessableEntity()
    {
        // Arrange
        var controller = CreateLifecycleController(new ActiveStreamRegistry());
        var request = new CmafCompatibilityTestRequest
        {
            AudioCodec = CmafTestAudioCodec.Ac4,
            ChannelLayout = CmafTestChannelLayout.Stereo
        };

        // Act
        var result = await controller.StartCmafCompatibilityTest(request);

        // Assert
        var response = Assert.IsType<UnprocessableEntityObjectResult>(result);
        Assert.Contains("AC-4 encoder", response.Value?.ToString(), StringComparison.Ordinal);
    }

    private static readonly TransientDataStore TestTransientData =
        new(Path.Combine(Path.GetTempPath(), $"lineup-stream-controller-tests-{Environment.ProcessId}"));

    /// <summary>
    /// Verifies disabled MPEG-TS requests return before acquiring tuner capacity.
    /// </summary>
    [Fact]
    public async Task Stream_DisabledChannel_ReturnsForbiddenWithoutTuner()
    {
        // Arrange
        var store = await CreateDisabledChannelStoreAsync("9.1");
        var capacity = Substitute.For<ITunerCapacityLeaseRegistry>();
        var controller = CreateDisabledChannelController(store, DisabledChannelMode.ReturnError, capacity, Substitute.For<IProtectedContentSlateService>());

        // Act
        var result = await controller.Stream("9.1");

        // Assert
        var forbidden = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
        Assert.Empty(capacity.ReceivedCalls());
    }

    /// <summary>
    /// Verifies disabled Watch requests return before acquiring tuner capacity.
    /// </summary>
    [Fact]
    public async Task WatchStream_DisabledChannel_ReturnsForbiddenWithoutTuner()
    {
        // Arrange
        var store = await CreateDisabledChannelStoreAsync("9.1");
        var capacity = Substitute.For<ITunerCapacityLeaseRegistry>();
        var controller = CreateDisabledChannelController(store, DisabledChannelMode.ReturnError, capacity, Substitute.For<IProtectedContentSlateService>());

        // Act
        var result = await controller.StartHlsStream("9.1");

        // Assert
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Empty(capacity.ReceivedCalls());
    }

    /// <summary>
    /// Verifies slate mode serves disabled MPEG-TS without acquiring tuner capacity.
    /// </summary>
    [Fact]
    public async Task Stream_DisabledChannelSlate_UsesSyntheticMediaWithoutTuner()
    {
        // Arrange
        var store = await CreateDisabledChannelStoreAsync("9.1");
        var capacity = Substitute.For<ITunerCapacityLeaseRegistry>();
        var slate = Substitute.For<IProtectedContentSlateService>();
        var controller = CreateDisabledChannelController(store, DisabledChannelMode.StreamSlate, capacity, slate);

        // Act
        var result = await controller.Stream("9.1");

        // Assert
        Assert.IsType<EmptyResult>(result);
        await slate.Received(1).StreamAsync(HostedStreamFormat.MpegTs, "9.1", Arg.Any<Stream>(), Arg.Any<CancellationToken>(), ChannelSlateReason.DisabledChannel);
        Assert.Empty(capacity.ReceivedCalls());
    }

    /// <summary>
    /// Verifies disabled pipe slates respect the hosted-stream limit without starting FFmpeg or acquiring a tuner.
    /// </summary>
    [Fact]
    public async Task DisabledChannelSlate_AtStreamLimit_ReturnsTooManyRequestsWithoutStartingSlate()
    {
        // Arrange
        var store = await CreateDisabledChannelStoreAsync("9.1");
        var capacity = Substitute.For<ITunerCapacityLeaseRegistry>();
        var slate = Substitute.For<IProtectedContentSlateService>();
        var activeStreams = new ActiveStreamRegistry();
        var existing = ActiveStreamPlanFactory.CreateMpegTs("existing", "2.1", DateTime.UtcNow, new MediaProbeResult([], null), new AppSettings());
        Assert.True(activeStreams.TryRegister(existing, 1, () => { }));
        var controller = CreateDisabledChannelController(store, DisabledChannelMode.StreamSlate, capacity, slate, activeStreams, maximumConcurrentStreams: 1);

        // Act
        var result = await controller.Stream("9.1");
        var statusCode = Assert.IsType<ObjectResult>(result).StatusCode;

        // Assert
        Assert.Equal(StatusCodes.Status429TooManyRequests, statusCode);
        Assert.Empty(slate.ReceivedCalls());
        Assert.Empty(capacity.ReceivedCalls());
        Assert.Equal("existing", Assert.Single(activeStreams.GetActiveStreams()).SessionId);
    }

    /// <summary>
    /// Verifies a disabled CMAF slate requested through the HLS compatibility route is rejected before its FFmpeg process or tuner capacity starts.
    /// </summary>
    [Fact]
    public async Task StartHlsStream_DisabledChannelAtStreamLimit_ReturnsTooManyRequestsWithoutStartingSlate()
    {
        // Arrange
        var store = await CreateDisabledChannelStoreAsync("9.1");
        var capacity = Substitute.For<ITunerCapacityLeaseRegistry>();
        var slate = Substitute.For<IProtectedContentSlateService>();
        var activeStreams = new ActiveStreamRegistry();
        var existing = ActiveStreamPlanFactory.CreateMpegTs("existing", "2.1", DateTime.UtcNow, new MediaProbeResult([], null), new AppSettings());
        Assert.True(activeStreams.TryRegister(existing, 1, () => { }));
        var controller = CreateDisabledChannelController(store, DisabledChannelMode.StreamSlate, capacity, slate, activeStreams, maximumConcurrentStreams: 1);

        // Act
        var result = await controller.StartHlsStream("9.1");

        // Assert
        var rejected = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status429TooManyRequests, rejected.StatusCode);
        Assert.Empty(slate.ReceivedCalls());
        Assert.Empty(capacity.ReceivedCalls());
        Assert.Equal("existing", Assert.Single(activeStreams.GetActiveStreams()).SessionId);
    }

    /// <summary>
    /// Verifies a stop request during CMAF admission prevents slate startup without aborting the completed HLS compatibility request context.
    /// </summary>
    [Fact]
    public async Task StartHlsStream_DisabledChannelStoppedDuringAdmission_DoesNotStartSlate()
    {
        // Arrange
        var store = await CreateDisabledChannelStoreAsync("9.1");
        var capacity = Substitute.For<ITunerCapacityLeaseRegistry>();
        var slate = Substitute.For<IProtectedContentSlateService>();
        var activeStreams = Substitute.For<IActiveStreamRegistry>();
        activeStreams.TryRegister(Arg.Any<ActiveStreamSnapshot>(), Arg.Any<int>(), Arg.Any<Action>())
            .Returns(call =>
            {
                call.Arg<Action>()();
                return true;
            });
        var controller = CreateDisabledChannelController(store, DisabledChannelMode.StreamSlate, capacity, slate, activeStreams, maximumConcurrentStreams: 1);
        var lifetime = new TestRequestLifetimeFeature();
        controller.HttpContext.Features.Set<IHttpRequestLifetimeFeature>(lifetime);

        // Act
        var result = await controller.StartHlsStream("9.1");

        // Assert
        Assert.IsType<EmptyResult>(result);
        Assert.False(lifetime.RequestAborted.IsCancellationRequested);
        Assert.Empty(slate.ReceivedCalls());
        Assert.Empty(capacity.ReceivedCalls());
        activeStreams.Received(1).Unregister(Arg.Any<string>());
    }

    /// <summary>
    /// Verifies Dashboard Stop cancels disabled pipe slates and removes their active-stream registration.
    /// </summary>
    [Fact]
    public async Task DisabledChannelSlate_RequestStop_CancelsSlateAndCleansRegistry()
    {
        // Arrange
        var store = await CreateDisabledChannelStoreAsync("9.1");
        var capacity = Substitute.For<ITunerCapacityLeaseRegistry>();
        var slateStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slate = Substitute.For<IProtectedContentSlateService>();
        slate.StreamAsync(HostedStreamFormat.MpegTs, "9.1", Arg.Any<Stream>(), Arg.Any<CancellationToken>(), ChannelSlateReason.DisabledChannel)
            .Returns(async call =>
            {
                slateStarted.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, call.ArgAt<CancellationToken>(3));
            });
        var activeStreams = new ActiveStreamRegistry();
        var controller = CreateDisabledChannelController(store, DisabledChannelMode.StreamSlate, capacity, slate, activeStreams, maximumConcurrentStreams: 1);
        var lifetime = new TestRequestLifetimeFeature();
        controller.HttpContext.Features.Set<IHttpRequestLifetimeFeature>(lifetime);

        // Act
        Task streamTask = controller.Stream("9.1");
        await slateStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        var activeStream = Assert.Single(activeStreams.GetActiveStreams());
        var sessionId = activeStream.SessionId;
        var stopped = activeStreams.RequestStop(sessionId);
        await streamTask.WaitAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.True(stopped);
        Assert.Null(activeStream.ClientId);
        Assert.Empty(activeStreams.GetActiveStreams());
        Assert.Empty(capacity.ReceivedCalls());
    }

    /// <summary>
    /// Verifies an MPEG-TS DRM slate that fails before normal registration still respects the hosted-stream limit.
    /// </summary>
    [Fact]
    public async Task Stream_ProtectedSlateAtStreamLimit_ReturnsTooManyRequestsWithoutStartingSlate()
    {
        // Arrange
        var activeStreams = new ActiveStreamRegistry();
        var existing = ActiveStreamPlanFactory.CreateMpegTs("existing", "2.1", DateTime.UtcNow, new MediaProbeResult([], null), new AppSettings());
        Assert.True(activeStreams.TryRegister(existing, 1, () => { }));
        var slate = Substitute.For<IProtectedContentSlateService>();
        var capacity = new TunerCapacityLeaseRegistry();
        var controller = CreateMpegTsProtectedSlateController(activeStreams, slate, capacity, maximumConcurrentStreams: 1);

        // Act
        var result = await controller.Stream("20.1");

        // Assert
        var rejected = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status429TooManyRequests, rejected.StatusCode);
        Assert.Empty(slate.ReceivedCalls());
        Assert.Equal("existing", Assert.Single(activeStreams.GetActiveStreams()).SessionId);
        Assert.Empty(capacity.GetDiagnostics());
    }

    /// <summary>
    /// Verifies Dashboard Stop cancels an MPEG-TS DRM slate that failed before normal stream registration.
    /// </summary>
    [Fact]
    public async Task Stream_ProtectedSlateRequestStop_CancelsSlateAndCleansRegistry()
    {
        // Arrange
        var slateStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slate = Substitute.For<IProtectedContentSlateService>();
        slate.StreamAsync(HostedStreamFormat.MpegTs, "20.1", Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                slateStarted.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, call.ArgAt<CancellationToken>(3));
            });
        var activeStreams = new ActiveStreamRegistry();
        var capacity = new TunerCapacityLeaseRegistry();
        var controller = CreateMpegTsProtectedSlateController(activeStreams, slate, capacity, maximumConcurrentStreams: 1);
        controller.HttpContext.Features.Set<IHttpRequestLifetimeFeature>(new TestRequestLifetimeFeature());

        // Act
        var streamTask = controller.Stream("20.1");
        await slateStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        var sessionId = Assert.Single(activeStreams.GetActiveStreams()).SessionId;
        var stopped = activeStreams.RequestStop(sessionId);
        var result = await streamTask.WaitAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.True(stopped);
        Assert.IsType<EmptyResult>(result);
        Assert.Empty(activeStreams.GetActiveStreams());
        Assert.Empty(capacity.GetDiagnostics());
    }

    /// <summary>
    /// Verifies a tuner-confirmed DRM startup failure returns structured 811 diagnostics.
    /// </summary>
    [Fact]
    public async Task Stream_ConfirmedProtectedContent_ReturnsTunerClassification()
    {
        // Arrange
        var controller = CreateMpegTsProtectedSlateController(
            new ActiveStreamRegistry(),
            Substitute.For<IProtectedContentSlateService>(),
            new TunerCapacityLeaseRegistry(),
            maximumConcurrentStreams: 1,
            protectedContentMode: ProtectedContentMode.ReturnError);

        // Act
        var result = await controller.Stream("20.1");

        // Assert
        var forbidden = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
        Assert.Equal(811, GetPropertyValue(forbidden.Value!, "code"));
        Assert.Equal("811 DRM Content", GetPropertyValue(forbidden.Value!, "error"));
        Assert.Equal(false, GetPropertyValue(forbidden.Value!, "inferred"));
        Assert.Equal("tuner", GetPropertyValue(forbidden.Value!, "source"));
    }

    /// <summary>
    /// Verifies a failed startup with no tuner diagnostic uses cached DRM metadata and identifies the result as inferred.
    /// </summary>
    [Fact]
    public async Task Stream_CachedProtectedContentWithoutTunerError_ReturnsInferredClassification()
    {
        // Arrange
        var controller = CreateMpegTsProtectedSlateController(
            new ActiveStreamRegistry(),
            Substitute.For<IProtectedContentSlateService>(),
            new TunerCapacityLeaseRegistry(),
            maximumConcurrentStreams: 1,
            protectedContentMode: ProtectedContentMode.ReturnError,
            tunerError: null,
            cachedDrm: true);

        // Act
        var result = await controller.Stream("20.1");

        // Assert
        var forbidden = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
        Assert.Equal(811, GetPropertyValue(forbidden.Value!, "code"));
        Assert.Equal("811 DRM content inferred from channel lineup metadata after playback startup failed.", GetPropertyValue(forbidden.Value!, "error"));
        Assert.Equal(true, GetPropertyValue(forbidden.Value!, "inferred"));
        Assert.Equal("channel-lineup", GetPropertyValue(forbidden.Value!, "source"));
    }

    /// <summary>
    /// Verifies that IPv4 and IPv6 client endpoints are formatted unambiguously for diagnostics.
    /// </summary>
    [Theory]
    [InlineData("192.0.2.10", 54321, "192.0.2.10:54321")]
    [InlineData("2001:db8::10", 54321, "[2001:db8::10]:54321")]
    public void FormatClientAddress_KnownEndpoint_ReturnsAddressAndPort(string address, int port, string expected)
    {
        // Arrange
        var ipAddress = IPAddress.Parse(address);

        // Act
        var formatted = StreamController.FormatClientAddress(ipAddress, port);

        // Assert
        Assert.Equal(expected, formatted);
    }

    /// <summary>
    /// Verifies that a missing remote address remains unknown.
    /// </summary>
    [Fact]
    public void FormatClientAddress_MissingAddress_ReturnsNull()
    {
        // Arrange
        IPAddress? address = null;

        // Act
        var formatted = StreamController.FormatClientAddress(address, 0);

        // Assert
        Assert.Null(formatted);
    }

    /// <summary>
    /// Verifies that generated manifest, playlist, and segment basenames resolve beneath the CMAF session directory.
    /// </summary>
    [Theory]
    [InlineData("stream.m3u8")]
    [InlineData("stream0.ts")]
    [InlineData("stream1726358400.ts")]
    [InlineData("manifest.mpd")]
    [InlineData("master.m3u8")]
    [InlineData("media_0.m3u8")]
    [InlineData("init-0.mp4")]
    [InlineData("chunk-0-00001.m4s")]
    [InlineData("captions-2.vtt")]
    public void TryResolveCmafFilePath_GeneratedBasename_ReturnsContainedCanonicalPath(string filename)
    {
        // Arrange
        var cmafDirectory = Path.Combine(Environment.CurrentDirectory, "cmaf-session");
        var expectedPath = Path.GetFullPath(Path.Combine(cmafDirectory, filename));

        // Act
        var resolved = StreamController.TryResolveCmafFilePath(cmafDirectory, filename, out var filePath);

        // Assert
        Assert.True(resolved);
        Assert.Equal(expectedPath, filePath);
    }

    /// <summary>
    /// Verifies that traversal, rooted, separator, encoded Windows separator, and invalid CMAF artifact names are rejected.
    /// </summary>
    [Theory]
    [InlineData("../stream.m3u8")]
    [InlineData(@"..\stream.m3u8")]
    [InlineData(@"\stream.m3u8")]
    [InlineData(@"C:\stream.m3u8")]
    [InlineData("nested/stream.m3u8")]
    [InlineData(@"nested\stream.m3u8")]
    [InlineData("..%2fstream.m3u8")]
    [InlineData("..%2Fstream.m3u8")]
    [InlineData("..%5cstream.m3u8")]
    [InlineData("..%5Cstream.m3u8")]
    [InlineData("other.m3u8")]
    [InlineData("stream.ts")]
    [InlineData("stream1.mp4")]
    [InlineData("other.mpd")]
    [InlineData("media_main.m3u8")]
    [InlineData("init-video.mp4")]
    [InlineData("chunk-0-any.m4s")]
    [InlineData("captions.srt")]
    public void TryResolveCmafFilePath_TraversalOrInvalidName_ReturnsFalse(string filename)
    {
        // Arrange
        var cmafDirectory = Path.Combine(Environment.CurrentDirectory, "cmaf-session");

        // Act
        var resolved = StreamController.TryResolveCmafFilePath(cmafDirectory, filename, out var filePath);

        // Assert
        Assert.False(resolved);
        Assert.Equal(string.Empty, filePath);
    }

    /// <summary>
    /// Verifies that an abandoned CMAF session expires and releases all owned resources.
    /// </summary>
    [Fact]
    public async Task CmafSession_Inactive_ExpiresAndCleansResources()
    {
        // Arrange
        var activeStreams = Substitute.For<IActiveStreamRegistry>();
        var capacityLease = Substitute.For<ITunerCapacityLease>();
        var controller = CreateLifecycleController(activeStreams, cmafInactivityTimeout: TimeSpan.FromMilliseconds(30));
        var session = CreateCmafSession(controller, capacityLease, out var sessionId, out var directory);

        // Act
        await WaitUntilAsync(() => !Directory.Exists(directory));

        // Assert
        Assert.False(GetCmafSessions().Contains(sessionId));
        activeStreams.Received().Unregister(sessionId);
        capacityLease.Received().Dispose();
        GC.KeepAlive(session);
    }

    /// <summary>
    /// Verifies that valid CMAF artifact access refreshes inactivity and prevents premature expiration.
    /// </summary>
    [Fact]
    public async Task GetCmafFile_ValidAccess_RefreshesInactivity()
    {
        // Arrange
        var controller = CreateLifecycleController(Substitute.For<IActiveStreamRegistry>(), cmafInactivityTimeout: TimeSpan.FromMilliseconds(150));
        CreateCmafSession(controller, Substitute.For<ITunerCapacityLease>(), out var sessionId, out var directory);
        await Task.Delay(90, TestContext.Current.CancellationToken);

        // Act
        var result = controller.GetCmafFile(sessionId, "stream.m3u8");
        await Task.Delay(90, TestContext.Current.CancellationToken);

        // Assert
        Assert.IsType<PhysicalFileResult>(result);
        Assert.True(Directory.Exists(directory));
        Assert.True(GetCmafSessions().Contains(sessionId));
        Assert.IsType<OkObjectResult>(controller.StopCmaf(sessionId));
    }

    /// <summary>
    /// Verifies CMAF WebVTT clients can poll only bytes appended after their previous offset.
    /// </summary>
    [Fact]
    public void GetCmafFile_WebVttOffset_ReturnsIncrementalChunk()
    {
        // Arrange
        var controller = CreateLifecycleController(Substitute.For<IActiveStreamRegistry>());
        CreateCmafSession(controller, Substitute.For<ITunerCapacityLease>(), out var sessionId, out var directory);
        var subtitlePath = TestTransientData.GetFilePath(directory, "captions-2.vtt");
        File.WriteAllText(subtitlePath, "WEBVTT\n\n00:00:00.000 --> 00:00:01.000\nHello\n\n");

        // Act
        var result = controller.GetCmafFile(sessionId, "captions-2.vtt", offset: 8);

        // Assert
        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("00:00:00.000 --> 00:00:01.000\nHello\n\n", System.Text.Encoding.UTF8.GetString(file.FileContents));
        Assert.Equal("45", controller.Response.Headers["X-Lineup-Subtitle-Offset"]);
        Assert.IsType<OkObjectResult>(controller.StopCmaf(sessionId));
    }

    /// <summary>
    /// Verifies CMAF WebVTT polling remains successful while FFmpeg has not emitted the first caption cue.
    /// </summary>
    [Fact]
    public void GetCmafFile_WebVttNotCreatedYet_ReturnsEmptyChunk()
    {
        // Arrange
        var controller = CreateLifecycleController(Substitute.For<IActiveStreamRegistry>());
        CreateCmafSession(controller, Substitute.For<ITunerCapacityLease>(), out var sessionId, out _);

        // Act
        var result = controller.GetCmafFile(sessionId, "captions-2.vtt", offset: 12);

        // Assert
        var file = Assert.IsType<FileContentResult>(result);
        Assert.Empty(file.FileContents);
        Assert.Equal("text/vtt; charset=utf-8", file.ContentType);
        Assert.Equal("12", controller.Response.Headers["X-Lineup-Subtitle-Offset"]);
        Assert.IsType<OkObjectResult>(controller.StopCmaf(sessionId));
    }

    /// <summary>
    /// Verifies copied HEVC representations receive browser-compatible codec signaling when served.
    /// </summary>
    [Fact]
    public void GetCmafFile_HevcManifest_RewritesMissingCodec()
    {
        // Arrange
        var controller = CreateLifecycleController(Substitute.For<IActiveStreamRegistry>());
        var session = CreateCmafSession(controller, Substitute.For<ITunerCapacityLease>(), out var sessionId, out var directory);
        File.WriteAllText(Path.Combine(directory, CmafStreamPlanner.DashManifestName), "<Representation codecs=\"\" />");
        SetProperty(session.GetType(), session, "SourceVideoCodec", "hvc1.2.4.L123");

        // Act
        var result = controller.GetCmafFile(sessionId, CmafStreamPlanner.DashManifestName);

        // Assert
        var content = Assert.IsType<ContentResult>(result);
        Assert.Contains("codecs=\"hvc1.2.4.L123\"", content.Content, StringComparison.Ordinal);
        Assert.Equal("application/dash+xml", content.ContentType);
        Assert.IsType<OkObjectResult>(controller.StopCmaf(sessionId));
    }

    /// <summary>
    /// Verifies that application shutdown terminates and removes every registered CMAF session.
    /// </summary>
    [Fact]
    public async Task ApplicationStopping_LiveCmafSession_CleansResources()
    {
        // Arrange
        using var stopping = new CancellationTokenSource();
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        lifetime.ApplicationStopping.Returns(stopping.Token);
        var activeStreams = Substitute.For<IActiveStreamRegistry>();
        var capacityLease = Substitute.For<ITunerCapacityLease>();
        var controller = CreateLifecycleController(activeStreams, lifetime);
        CreateCmafSession(controller, capacityLease, out var sessionId, out var directory);

        // Act
        stopping.Cancel();
        await WaitUntilAsync(() => !Directory.Exists(directory));

        // Assert
        Assert.False(GetCmafSessions().Contains(sessionId));
        activeStreams.Received().Unregister(sessionId);
        capacityLease.Received().Dispose();
    }

    /// <summary>
    /// Verifies that same-channel CMAF viewers retain independent sessions and can stop independently.
    /// </summary>
    [Fact]
    public void CmafSession_SameChannelViewers_CoexistAndStopIndependently()
    {
        // Arrange
        var controller = CreateLifecycleController(Substitute.For<IActiveStreamRegistry>());
        CreateCmafSession(controller, Substitute.For<ITunerCapacityLease>(), out var firstSessionId, out var firstDirectory);

        // Act
        CreateCmafSession(controller, Substitute.For<ITunerCapacityLease>(), out var secondSessionId, out var secondDirectory);
        var firstStop = controller.StopCmaf(firstSessionId);

        // Assert
        Assert.IsType<OkObjectResult>(firstStop);
        Assert.False(Directory.Exists(firstDirectory));
        Assert.True(Directory.Exists(secondDirectory));
        Assert.True(GetCmafSessions().Contains(secondSessionId));
        Assert.IsType<OkObjectResult>(controller.StopCmaf(secondSessionId));
        Assert.False(Directory.Exists(secondDirectory));
    }

    private static StreamController CreateProtectedSlateController(TunerCapacityLeaseRegistry registry, IProtectedContentSlateService slateService)
    {
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("StreamProxy").Returns(new HttpClient(new ProtectedContentResponseHandler()));
        var settingsService = Substitute.For<IAppSettingsService>();
        settingsService.Settings.Returns(new AppSettings
        {
            ProtectedContentMode = ProtectedContentMode.StreamSlate
        });
        return new StreamController(
            httpClientFactory,
            Substitute.For<IEpgRepository>(),
            settingsService,
            CreateChannelLineupStore(),
            Substitute.For<IDeviceStateService>(),
            Substitute.For<IHdHomeRunProxyProfileProvider>(),
            Substitute.For<IMpegTsTranscodeService>(),
            Substitute.For<IMediaProbeService>(),
            Substitute.For<IActiveStreamRegistry>(),
            slateService,
            Substitute.For<ITunerStreamMultiplexer>(),
            registry,
            NullLogger<StreamController>.Instance,
            transientData: TestTransientData)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
    }

    private static StreamController CreateMpegTsProtectedSlateController(
        IActiveStreamRegistry activeStreams,
        IProtectedContentSlateService slateService,
        ITunerCapacityLeaseRegistry capacity,
        int maximumConcurrentStreams,
        ProtectedContentMode protectedContentMode = ProtectedContentMode.StreamSlate,
        string? tunerError = "811 DRM Content",
        bool cachedDrm = false)
    {
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("StreamProxy").Returns(new HttpClient(new ProtectedContentResponseHandler(tunerError)));
        var settingsService = Substitute.For<IAppSettingsService>();
        settingsService.Settings.Returns(new AppSettings
        {
            DeviceAddress = "tuner.local",
            ProtectedContentMode = protectedContentMode,
            MaximumConcurrentStreams = maximumConcurrentStreams
        });
        var epgRepository = Substitute.For<IEpgRepository>();
        epgRepository.GetChannelsAsync().Returns(
            cachedDrm
                ? [new HDHomeRunChannelEpgSegment { GuideNumber = "20.1", DRM = true }]
                : []);
        var profiles = Substitute.For<IHdHomeRunProxyProfileProvider>();
        profiles.GetPrimaryProfileAsync(Arg.Any<CancellationToken>()).Returns(new HdHomeRunProxyProfileSnapshot
        {
            Settings = new HdHomeRunProxyProfileSettings(),
            PhysicalDevice = new HDHomeRunDeviceInfo
            {
                FriendlyName = "Tuner",
                ModelNumber = "HDHR",
                FirmwareName = "hdhomerun",
                FirmwareVersion = "1",
                DeviceID = "12345678",
                DeviceAuth = "auth",
                BaseURL = "http://tuner.local",
                LineupURL = "http://tuner.local/lineup.json",
                TunerCount = 1
            },
            IsPrimary = true,
            DeviceId = 0x12345678,
            DeviceAuth = "virtual",
            TunerCount = 1,
            FriendlyName = "Lineup",
            PhysicalBaseUri = new Uri("http://tuner.local/")
        });
        var multiplexer = Substitute.For<ITunerStreamMultiplexer>();
        multiplexer.SubscribeAsync(Arg.Any<Uri>(), Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask<Stream>(new MemoryStream()));
        var probe = Substitute.For<IMediaProbeService>();
        probe.ProbeAsync(Arg.Any<Uri>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<MediaProbeResult>(new MpegTsTranscodeException("DRM")));
        return new StreamController(
            httpClientFactory,
            epgRepository,
            settingsService,
            CreateChannelLineupStore(),
            Substitute.For<IDeviceStateService>(),
            profiles,
            Substitute.For<IMpegTsTranscodeService>(),
            probe,
            activeStreams,
            slateService,
            multiplexer,
            capacity,
            NullLogger<StreamController>.Instance,
            transientData: TestTransientData)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    Response = { Body = new MemoryStream() }
                }
            }
        };
    }

    private static StreamController CreateLifecycleController(IActiveStreamRegistry activeStreams, IHostApplicationLifetime? lifetime = null, TimeSpan? cmafInactivityTimeout = null)
    {
        var settingsService = Substitute.For<IAppSettingsService>();
        settingsService.Settings.Returns(new AppSettings());
        return new StreamController(
            Substitute.For<IHttpClientFactory>(),
            Substitute.For<IEpgRepository>(),
            settingsService,
            CreateChannelLineupStore(),
            Substitute.For<IDeviceStateService>(),
            Substitute.For<IHdHomeRunProxyProfileProvider>(),
            Substitute.For<IMpegTsTranscodeService>(),
            Substitute.For<IMediaProbeService>(),
            activeStreams,
            Substitute.For<IProtectedContentSlateService>(),
            Substitute.For<ITunerStreamMultiplexer>(),
            Substitute.For<ITunerCapacityLeaseRegistry>(),
            NullLogger<StreamController>.Instance,
            lifetime,
            TimeProvider.System,
            cmafInactivityTimeout,
            transientData: TestTransientData)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
    }

    private static object CreateCmafSession(StreamController controller, ITunerCapacityLease capacityLease, out string sessionId, out string directory)
    {
        sessionId = Guid.NewGuid().ToString("N");
        directory = TestTransientData.CreateCmafSessionDirectory(sessionId);
        var playlistPath = TestTransientData.GetFilePath(directory, "stream.m3u8");
        File.WriteAllText(playlistPath, "#EXTM3U");
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = "--version",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        });
        Assert.NotNull(process);
        process.WaitForExit();
        var sessionType = typeof(StreamController).GetNestedType("CmafSession", BindingFlags.NonPublic);
        Assert.NotNull(sessionType);
        var session = Activator.CreateInstance(sessionType);
        Assert.NotNull(session);
        SetProperty(sessionType, session, "SessionId", sessionId);
        SetProperty(sessionType, session, "Channel", "20.1");
        SetProperty(sessionType, session, "Process", process);
        SetProperty(sessionType, session, "CmafDirectory", directory);
        SetProperty(sessionType, session, "PlaylistPath", playlistPath);
        SetProperty(sessionType, session, "StartTime", DateTime.UtcNow);
        SetProperty(sessionType, session, "CapacityLease", capacityLease);
        var register = typeof(StreamController).GetMethod("RegisterCmafSession", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(register);
        register.Invoke(controller, [session]);
        return session;
    }

    private static ChannelLineupStore CreateChannelLineupStore()
    {
        return new ChannelLineupStore(Path.Combine(Path.GetTempPath(), $"lineup-stream-{Guid.NewGuid():N}.db"));
    }

    private static async Task<ChannelLineupStore> CreateDisabledChannelStoreAsync(string guideNumber)
    {
        var store = CreateChannelLineupStore();
        await store.StoreAsync(
            [new Lineup.HDHomeRun.Device.Models.HDHomeRunChannel { GuideNumber = guideNumber, GuideName = "Disabled", URL = $"http://tuner.local/auto/v{guideNumber}" }],
            TestContext.Current.CancellationToken);
        await store.SetChannelEnabledAsync(guideNumber, enabled: false, TestContext.Current.CancellationToken);
        return store;
    }

    private static StreamController CreateDisabledChannelController(
        ChannelLineupStore store,
        DisabledChannelMode mode,
        ITunerCapacityLeaseRegistry capacity,
        IProtectedContentSlateService slate,
        IActiveStreamRegistry? activeStreams = null,
        int maximumConcurrentStreams = 0)
    {
        var settings = Substitute.For<IAppSettingsService>();
        settings.Settings.Returns(new AppSettings
        {
            DeviceAddress = "tuner.local",
            DisabledChannelMode = mode,
            MaximumConcurrentStreams = maximumConcurrentStreams
        });
        var profiles = Substitute.For<IHdHomeRunProxyProfileProvider>();
        profiles.GetPrimaryProfileAsync(Arg.Any<CancellationToken>()).Returns(new HdHomeRunProxyProfileSnapshot
        {
            Settings = new HdHomeRunProxyProfileSettings(),
            PhysicalDevice = new HDHomeRunDeviceInfo
            {
                FriendlyName = "Tuner",
                ModelNumber = "HDHR",
                FirmwareName = "hdhomerun",
                FirmwareVersion = "1",
                DeviceID = "12345678",
                DeviceAuth = "auth",
                BaseURL = "http://tuner.local",
                LineupURL = "http://tuner.local/lineup.json",
                TunerCount = 2
            },
            IsPrimary = true,
            DeviceId = 0x12345678,
            DeviceAuth = "virtual",
            TunerCount = 2,
            FriendlyName = "Lineup",
            PhysicalBaseUri = new Uri("http://tuner.local/")
        });
        return new StreamController(
            Substitute.For<IHttpClientFactory>(),
            Substitute.For<IEpgRepository>(),
            settings,
            store,
            Substitute.For<IDeviceStateService>(),
            profiles,
            Substitute.For<IMpegTsTranscodeService>(),
            Substitute.For<IMediaProbeService>(),
            activeStreams ?? new ActiveStreamRegistry(),
            slate,
            Substitute.For<ITunerStreamMultiplexer>(),
            capacity,
            NullLogger<StreamController>.Instance,
            transientData: TestTransientData)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    Response = { Body = new MemoryStream() }
                }
            }
        };
    }

    private sealed class TestRequestLifetimeFeature : IHttpRequestLifetimeFeature
    {
        private readonly CancellationTokenSource _cancellation = new();

        public CancellationToken RequestAborted
        {
            get => _cancellation.Token;
            set => throw new NotSupportedException();
        }

        public void Abort()
        {
            _cancellation.Cancel();
        }
    }

    private static IDictionary GetCmafSessions()
    {
        var sessions = typeof(StreamController).GetField("_cmafSessions", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(sessions);
        return Assert.IsType<IDictionary>(sessions.GetValue(null), exactMatch: false);
    }

    private static void SetProperty(Type type, object instance, string name, object value)
    {
        var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(property);
        property.SetValue(instance, value);
    }

    private static object? GetPropertyValue(object instance, string name)
    {
        var property = instance.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(property);
        return property.GetValue(instance);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!predicate() && DateTime.UtcNow < timeout)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(predicate());
    }

    private sealed class ProtectedContentResponseHandler(string? tunerError = "811 DRM Content") : HttpMessageHandler
    {
        /// <inheritdoc/>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            if (tunerError is not null)
            {
                response.Headers.Add("X-HDHomeRun-Error", tunerError);
            }
            return Task.FromResult(response);
        }
    }
}
