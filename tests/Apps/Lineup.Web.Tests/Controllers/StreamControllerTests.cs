using System.Net;
using System.Diagnostics;
using System.Reflection;
using Lineup.Extensibility.Streaming;
using Lineup.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Lineup.Web.Tests.Controllers;

/// <summary>Verifies stream transport validation, routing, and lifecycle boundaries.</summary>
public sealed class StreamControllerTests
{
    /// <summary>Verifies disconnecting before readiness terminates the unregistered encoder and removes its files.</summary>
    [Fact]
    public async Task StartCmafCompatibilityTest_RequestCanceled_CleansStartingEncoder()
    {
        // Arrange
        await using var fixture = new CompatibilityStartupFixture("waiting");

        // Act
        var starting = fixture.Controller.StartCmafCompatibilityTest(new CmafCompatibilityTestRequest());
        await fixture.RequestCancellation.CancelAsync();
        var result = await starting;

        // Assert
        Assert.IsType<EmptyResult>(result);
        Assert.False(fixture.IsEncoderRunning);
        Assert.False(Directory.Exists(fixture.SessionDirectory));
        Assert.Empty(fixture.Outputs.GetViewers());
    }

    /// <summary>Verifies the real readiness deadline terminates an encoder that never produces manifests.</summary>
    [Fact]
    public async Task StartCmafCompatibilityTest_ReadinessTimeout_CleansStartingEncoder()
    {
        // Arrange
        await using var fixture = new CompatibilityStartupFixture("waiting");

        // Act
        var result = await fixture.Controller.StartCmafCompatibilityTest(new CmafCompatibilityTestRequest());

        // Assert
        Assert.Equal(StatusCodes.Status504GatewayTimeout, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.False(fixture.IsEncoderRunning);
        Assert.False(Directory.Exists(fixture.SessionDirectory));
        Assert.Empty(fixture.Outputs.GetViewers());
    }

    /// <summary>Verifies storage and registration failures release the encoder even before it has a viewer alias.</summary>
    [Theory]
    [InlineData("io")]
    [InlineData("registration")]
    public async Task StartCmafCompatibilityTest_StartupFailure_CleansStartingEncoder(string failure)
    {
        // Arrange
        await using var fixture = new CompatibilityStartupFixture(failure);

        // Act
        var result = await fixture.Controller.StartCmafCompatibilityTest(new CmafCompatibilityTestRequest());

        // Assert
        Assert.Equal(StatusCodes.Status500InternalServerError, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.False(fixture.IsEncoderRunning);
        Assert.False(Directory.Exists(fixture.SessionDirectory));
        Assert.Empty(fixture.Outputs.GetViewers());
    }

    /// <summary>Verifies an encoder exiting before readiness does not leave an unregistered artifact directory.</summary>
    [Fact]
    public async Task StartCmafCompatibilityTest_EncoderExits_CleansStartingArtifacts()
    {
        // Arrange
        await using var fixture = new CompatibilityStartupFixture("exit");

        // Act
        var result = await fixture.Controller.StartCmafCompatibilityTest(new CmafCompatibilityTestRequest());

        // Assert
        Assert.Equal(StatusCodes.Status502BadGateway, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.False(fixture.IsEncoderRunning);
        Assert.False(Directory.Exists(fixture.SessionDirectory));
        Assert.Empty(fixture.Outputs.GetViewers());
    }

    /// <summary>Verifies successful startup transfers ownership until the viewer explicitly stops its presentation.</summary>
    [Fact]
    public async Task StartCmafCompatibilityTest_Ready_PreservesEncoderUntilStopped()
    {
        // Arrange
        await using var fixture = new CompatibilityStartupFixture("ready");

        // Act
        var result = await fixture.Controller.StartCmafCompatibilityTest(new CmafCompatibilityTestRequest());

        // Assert
        var response = Assert.IsType<CmafCompatibilityTestResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.True(fixture.IsEncoderRunning);
        Assert.True(Directory.Exists(fixture.SessionDirectory));
        Assert.Single(fixture.Outputs.GetViewers());
        Assert.IsType<OkObjectResult>(fixture.Controller.StopCmaf(response.SessionId));
        Assert.False(fixture.IsEncoderRunning);
        Assert.False(Directory.Exists(fixture.SessionDirectory));
        Assert.Empty(fixture.Outputs.GetViewers());
    }

    /// <summary>Verifies an incomplete manifest can be retried without registering the same viewer twice.</summary>
    [Fact]
    public async Task StartCmafCompatibilityTest_PartialManifest_RetriesBeforeOwnershipTransfer()
    {
        // Arrange
        await using var fixture = new CompatibilityStartupFixture("partial");

        // Act
        var result = await fixture.Controller.StartCmafCompatibilityTest(new CmafCompatibilityTestRequest());

        // Assert
        var response = Assert.IsType<CmafCompatibilityTestResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.True(fixture.IsEncoderRunning);
        Assert.Equal(2, fixture.ManifestReads);
        Assert.Single(fixture.Outputs.GetViewers());
        Assert.Equal(1, fixture.Outputs.ActiveOutputCount);
        Assert.IsType<OkObjectResult>(fixture.Controller.StopCmaf(response.SessionId));
        Assert.False(fixture.IsEncoderRunning);
        Assert.False(Directory.Exists(fixture.SessionDirectory));
    }

    /// <summary>Verifies unexpected startup exceptions propagate only after resources have been released.</summary>
    [Fact]
    public async Task StartCmafCompatibilityTest_UnexpectedParserFailure_CleansBeforePropagating()
    {
        // Arrange
        await using var fixture = new CompatibilityStartupFixture("unexpected");

        // Act
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Controller.StartCmafCompatibilityTest(new CmafCompatibilityTestRequest()));

        // Assert
        Assert.False(fixture.IsEncoderRunning);
        Assert.False(Directory.Exists(fixture.SessionDirectory));
        Assert.Empty(fixture.Outputs.GetViewers());
    }

    /// <summary>Verifies unavailable synthetic encoders are rejected before a compatibility session starts.</summary>
    [Fact]
    public async Task StartCmafCompatibilityTest_Ac4ReturnsUnprocessableEntity()
    {
        // Arrange
        var controller = CreateController();
        var request = new CmafCompatibilityTestRequest
        {
            AudioCodec = CmafTestAudioCodec.Ac4,
            ChannelLayout = CmafTestChannelLayout.Stereo
        };

        // Act
        var result = await controller.StartCmafCompatibilityTest(request);

        // Assert
        Assert.Contains("AC-4 encoder", Assert.IsType<UnprocessableEntityObjectResult>(result).Value?.ToString(), StringComparison.Ordinal);
    }

    /// <summary>Verifies traversal and unexpected artifact names are rejected.</summary>
    [Theory]
    [InlineData("../stream.m3u8")]
    [InlineData("nested/stream.m3u8")]
    [InlineData("unexpected.txt")]
    public void TryResolveCmafFilePath_TraversalOrInvalidName_ReturnsFalse(string filename)
    {
        // Arrange
        var directory = Path.Combine(AppContext.BaseDirectory, "cmaf-path-tests");

        // Act
        var result = StreamController.TryResolveCmafFilePath(directory, filename, out _);

        // Assert
        Assert.False(result);
    }

    /// <summary>Verifies generated CMAF artifact basenames resolve inside the session directory.</summary>
    [Theory]
    [InlineData("manifest.mpd")]
    [InlineData("master.m3u8")]
    [InlineData("init-0.mp4")]
    [InlineData("chunk-0-00001.m4s")]
    [InlineData("captions-0.vtt")]
    public void TryResolveCmafFilePath_GeneratedBasename_ReturnsContainedCanonicalPath(string filename)
    {
        // Arrange
        var directory = Path.Combine(AppContext.BaseDirectory, "cmaf-path-tests");

        // Act
        var result = StreamController.TryResolveCmafFilePath(directory, filename, out var resolved);

        // Assert
        Assert.True(result);
        Assert.StartsWith(Path.GetFullPath(directory) + Path.DirectorySeparatorChar, resolved, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Verifies stopping an unknown CMAF viewer remains an idempotent not-found response.</summary>
    [Fact]
    public void StopCmaf_UnknownViewer_ReturnsNotFound()
    {
        // Arrange
        var controller = CreateController();

        // Act
        var result = controller.StopCmaf(Guid.NewGuid().ToString("N"));

        // Assert
        Assert.IsType<NotFoundObjectResult>(result);
    }

    /// <summary>Verifies stopping a registered viewer releases its process owner and artifact directory.</summary>
    [Fact]
    public async Task StopCmaf_RegisteredViewer_CleansResources()
    {
        // Arrange
        var controller = CreateController();
        var registration = await RegisterCmafSessionAsync(controller);

        // Act
        var result = controller.StopCmaf(registration.SessionId);

        // Assert
        Assert.IsType<OkObjectResult>(result);
        Assert.False(Directory.Exists(registration.Directory));
    }

    /// <summary>Verifies same-channel viewer registrations retain independent lifecycle aliases.</summary>
    [Fact]
    public async Task StopCmaf_SameChannelViewers_StopIndependently()
    {
        // Arrange
        var controller = CreateController();
        var first = await RegisterCmafSessionAsync(controller);
        var second = await RegisterCmafSessionAsync(controller);

        // Act
        var firstResult = controller.StopCmaf(first.SessionId);

        // Assert
        Assert.IsType<OkObjectResult>(firstResult);
        Assert.False(Directory.Exists(first.Directory));
        Assert.True(Directory.Exists(second.Directory));
        Assert.IsType<OkObjectResult>(controller.StopCmaf(second.SessionId));
    }

    /// <summary>Verifies a shared CMAF output lifetime is independent from the initiating HTTP request.</summary>
    [Fact]
    public async Task PendingCmafOutput_RequestAborted_DoesNotCancelOutputLifetime()
    {
        // Arrange
        var controller = CreateController();
        using var requestCancellation = new CancellationTokenSource();
        controller.HttpContext.RequestAborted = requestCancellation.Token;
        var pendingType = typeof(StreamController).GetNestedType("PendingCmafOutput", BindingFlags.NonPublic);
        Assert.NotNull(pendingType);
        var constructor = Assert.Single(pendingType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
        var pending = constructor.Invoke([controller]);
        Assert.NotNull(pending);
        var lifetimeProperty = pendingType.GetProperty("LifetimeToken", BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(lifetimeProperty);
        var lifetimeToken = Assert.IsType<CancellationToken>(lifetimeProperty.GetValue(pending));

        // Act
        requestCancellation.Cancel();

        // Assert
        Assert.False(lifetimeToken.IsCancellationRequested);
        await Assert.IsAssignableFrom<IAsyncDisposable>(pending).DisposeAsync();
        Assert.True(lifetimeToken.IsCancellationRequested);
    }

    /// <summary>Verifies contained playlist access returns the generated physical artifact.</summary>
    [Fact]
    public async Task GetCmafFile_RegisteredViewer_ReturnsPlaylist()
    {
        // Arrange
        var controller = CreateController();
        var registration = await RegisterCmafSessionAsync(controller);

        // Act
        var result = controller.GetCmafFile(registration.SessionId, "master.m3u8");

        // Assert
        var file = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal("application/vnd.apple.mpegurl", file.ContentType);
        Assert.Equal(registration.PlaylistPath, file.FileName);
        Assert.IsType<OkObjectResult>(controller.StopCmaf(registration.SessionId));
    }

    /// <summary>Verifies invalid viewer identifiers cannot escape into CMAF artifact handling.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("not-a-session")]
    [InlineData("../viewer")]
    public void GetCmafFile_UnknownViewer_ReturnsNotFound(string sessionId)
    {
        // Arrange
        var controller = CreateController();

        // Act
        var result = controller.GetCmafFile(sessionId, "master.m3u8");

        // Assert
        Assert.IsType<NotFoundObjectResult>(result);
    }

    /// <summary>Verifies missing subtitle sidecars return an empty incremental response.</summary>
    [Fact]
    public async Task GetCmafFile_SubtitleNotCreatedYet_ReturnsEmptyChunk()
    {
        // Arrange
        var controller = CreateController();
        var registration = await RegisterCmafSessionAsync(controller);

        // Act
        var result = controller.GetCmafFile(registration.SessionId, "captions-0.vtt");

        // Assert
        Assert.Empty(Assert.IsType<FileContentResult>(result).FileContents);
        Assert.IsType<OkObjectResult>(controller.StopCmaf(registration.SessionId));
    }

    /// <summary>Verifies subtitle reads return only bytes after the requested offset.</summary>
    [Fact]
    public async Task GetCmafFile_SubtitleOffset_ReturnsIncrementalChunk()
    {
        // Arrange
        var controller = CreateController();
        var registration = await RegisterCmafSessionAsync(controller);
        await File.WriteAllTextAsync(
            Path.Combine(registration.Directory, "captions-0.vtt"),
            "WEBVTT\n\ncue",
            TestContext.Current.CancellationToken);

        // Act
        var result = controller.GetCmafFile(registration.SessionId, "captions-0.vtt", offset: 7);

        // Assert
        Assert.Equal("\ncue", System.Text.Encoding.UTF8.GetString(Assert.IsType<FileContentResult>(result).FileContents));
        Assert.IsType<OkObjectResult>(controller.StopCmaf(registration.SessionId));
    }

    /// <summary>Verifies negative subtitle offsets are rejected.</summary>
    [Fact]
    public async Task GetCmafFile_NegativeSubtitleOffset_ReturnsBadRequest()
    {
        // Arrange
        var controller = CreateController();
        var registration = await RegisterCmafSessionAsync(controller);

        // Act
        var result = controller.GetCmafFile(registration.SessionId, "captions-0.vtt", offset: -1);

        // Assert
        Assert.IsType<BadRequestObjectResult>(result);
        Assert.IsType<OkObjectResult>(controller.StopCmaf(registration.SessionId));
    }

    /// <summary>Verifies an active viewer cannot request a traversal path.</summary>
    [Fact]
    public async Task GetCmafFile_TraversalForActiveViewer_ReturnsBadRequest()
    {
        // Arrange
        var controller = CreateController();
        var registration = await RegisterCmafSessionAsync(controller);

        // Act
        var result = controller.GetCmafFile(registration.SessionId, "../stream.m3u8");

        // Assert
        Assert.IsType<BadRequestObjectResult>(result);
        Assert.IsType<OkObjectResult>(controller.StopCmaf(registration.SessionId));
    }

    /// <summary>Verifies missing generated media returns not found.</summary>
    [Fact]
    public async Task GetCmafFile_MissingMedia_ReturnsNotFound()
    {
        // Arrange
        var controller = CreateController();
        var registration = await RegisterCmafSessionAsync(controller);

        // Act
        var result = controller.GetCmafFile(registration.SessionId, "init-0.mp4");

        // Assert
        Assert.IsType<NotFoundObjectResult>(result);
        Assert.IsType<OkObjectResult>(controller.StopCmaf(registration.SessionId));
    }

    /// <summary>Verifies copied HEVC manifests receive the required browser codec string.</summary>
    [Fact]
    public async Task GetCmafFile_HevcManifest_RewritesMissingCodec()
    {
        // Arrange
        var controller = CreateController();
        var registration = await RegisterCmafSessionAsync(controller, sourceVideoCodec: "hvc1.1.6.L93.B0");
        await File.WriteAllTextAsync(
            Path.Combine(registration.Directory, CmafStreamPlanner.HlsManifestName),
            "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1000000,CODECS=\",mp4a.40.2\"\nmedia_0.m3u8",
            TestContext.Current.CancellationToken);

        // Act
        var result = controller.GetCmafFile(registration.SessionId, CmafStreamPlanner.HlsManifestName);

        // Assert
        Assert.Contains("CODECS=\"hvc1.1.6.L93.B0,mp4a.40.2\"", Assert.IsType<ContentResult>(result).Content);
        Assert.IsType<OkObjectResult>(controller.StopCmaf(registration.SessionId));
    }

    /// <summary>Verifies HLS media playlists use DASH media timestamps for synchronized program dates.</summary>
    [Fact]
    public async Task GetCmafFile_HlsMediaPlaylist_RewritesProgramDates()
    {
        // Arrange
        var controller = CreateController();
        var registration = await RegisterCmafSessionAsync(controller);
        await File.WriteAllTextAsync(
            Path.Combine(registration.Directory, CmafStreamPlanner.DashManifestName),
            """
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" availabilityStartTime="2026-10-06T05:42:38.169Z">
              <Period><AdaptationSet><Representation id="0">
                <SegmentTemplate timescale="60000" startNumber="2">
                  <SegmentTimeline><S t="174174" d="120120" /></SegmentTimeline>
                </SegmentTemplate>
              </Representation></AdaptationSet></Period>
            </MPD>
            """,
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(registration.Directory, "media_0.m3u8"),
            """
            #EXTM3U
            #EXTINF:2.002000,
            #EXT-X-PROGRAM-DATE-TIME:2026-10-06T05:42:38.000Z
            chunk-0-00001.m4s
            #EXTINF:2.002000,
            #EXT-X-PROGRAM-DATE-TIME:2026-10-06T05:42:40.002Z
            chunk-0-00002.m4s
            """,
            TestContext.Current.CancellationToken);

        // Act
        var result = controller.GetCmafFile(registration.SessionId, "media_0.m3u8");

        // Assert
        var content = Assert.IsType<ContentResult>(result);
        Assert.Contains("#EXT-X-PROGRAM-DATE-TIME:2026-10-06T05:42:39.070Z", content.Content);
        Assert.IsType<OkObjectResult>(controller.StopCmaf(registration.SessionId));
    }

    /// <summary>Verifies DASH playback preserves FFmpeg's native audio and video presentation timeline.</summary>
    [Fact]
    public async Task GetCmafFile_DashManifest_PreservesNativePresentationTimeline()
    {
        // Arrange
        var controller = CreateController();
        var registration = await RegisterCmafSessionAsync(controller);
        await File.WriteAllTextAsync(
            Path.Combine(registration.Directory, CmafStreamPlanner.DashManifestName),
            """
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" availabilityStartTime="2026-10-06T06:17:53.955Z">
              <Period>
                <AdaptationSet contentType="video"><Representation id="0">
                  <SegmentTemplate timescale="60000"><SegmentTimeline><S t="135135" d="120120" /></SegmentTimeline></SegmentTemplate>
                </Representation></AdaptationSet>
                <AdaptationSet contentType="audio"><Representation id="1">
                  <SegmentTemplate timescale="48000"><SegmentTimeline><S t="0" d="95232" /></SegmentTimeline></SegmentTemplate>
                </Representation></AdaptationSet>
              </Period>
            </MPD>
            """,
            TestContext.Current.CancellationToken);

        // Act
        var result = controller.GetCmafFile(registration.SessionId, CmafStreamPlanner.DashManifestName);

        // Assert
        var content = Assert.IsType<ContentResult>(result);
        Assert.Contains("availabilityStartTime=\"2026-10-06T06:17:53.955Z\"", content.Content);
        Assert.DoesNotContain("presentationTimeOffset", content.Content);
        Assert.IsType<OkObjectResult>(controller.StopCmaf(registration.SessionId));
    }

    /// <summary>Verifies known protected metadata returns promptly without reserving a physical source.</summary>
    [Fact]
    public async Task StartCmafStream_KnownProtectedReturnError_DoesNotAcquireSource()
    {
        // Arrange
        var route = CreateResolvedRoute(protectedContent: true);
        var routes = Substitute.For<IStreamingRouteResolver>();
        routes.ResolveVirtualAsync(Arg.Any<VirtualDeviceId>(), "9.1", Arg.Any<CancellationToken>()).Returns(route);
        var multiplexer = Substitute.For<ITunerStreamMultiplexer>();
        var controller = CreateController(
            routes: routes,
            protectedContentMode: ProtectedContentMode.ReturnError,
            multiplexer: multiplexer);

        // Act
        var result = await controller.StartCmafStream("9.1", new CmafStreamRequest());

        // Assert
        var error = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, error.StatusCode);
        Assert.Contains("\"code\":\"protected_content\"", System.Text.Json.JsonSerializer.Serialize(error.Value), StringComparison.Ordinal);
        await multiplexer.DidNotReceive().SubscribeFirstAvailableAsync(Arg.Any<ResolvedStreamingRoute>(), Arg.Any<CancellationToken>());
    }

    /// <summary>Verifies source-acquisition failures are translated through the public management-hosted CMAF endpoint.</summary>
    [Fact]
    public async Task StartCmafStream_SourceAcquisitionFailure_ReturnsStructuredServiceUnavailable()
    {
        // Arrange
        var route = CreateResolvedRoute();
        var routes = Substitute.For<IStreamingRouteResolver>();
        routes.ResolveVirtualAsync(Arg.Any<VirtualDeviceId>(), "9.1", Arg.Any<CancellationToken>()).Returns(route);
        var multiplexer = Substitute.For<ITunerStreamMultiplexer>();
        var candidate = new StreamAcquisitionCandidateFailure
        {
            DeviceId = route.Candidates[0].SourceChannelId.PhysicalDeviceId,
            ChannelId = route.Candidates[0].SourceChannelId,
            Category = PhysicalStreamFailureCategory.Unavailable,
            Code = "source_unavailable",
            Message = "The physical source could not be opened.",
            Retryable = true
        };
        var failure = StreamAcquisitionException.Create([candidate], [new IOException("Sensitive source diagnostic.")]);
        multiplexer.SubscribeFirstAvailableAsync(route, Arg.Any<CancellationToken>())
            .Returns<ValueTask<ResolvedTunerStream>>(_ => throw failure);
        var controller = CreateController(routes: routes, multiplexer: multiplexer);

        // Act
        var result = await controller.StartCmafStream("9.1", new CmafStreamRequest());

        // Assert
        var error = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, error.StatusCode);
        var json = System.Text.Json.JsonSerializer.Serialize(error.Value);
        Assert.Contains("\"code\":\"source_unavailable\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Sensitive", json, StringComparison.Ordinal);
    }

    /// <summary>Verifies the server owns a complete CMAF startup deadline and returns a structured timeout before the browser disconnects.</summary>
    [Fact]
    public async Task StartCmafStream_StartupDeadlineExpires_ReturnsStructuredGatewayTimeout()
    {
        // Arrange
        var routes = Substitute.For<IStreamingRouteResolver>();
        routes.ResolveVirtualAsync(Arg.Any<VirtualDeviceId>(), "9.1", Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, call.Arg<CancellationToken>());
                return (ResolvedStreamingRoute?)CreateResolvedRoute();
            });
        var controller = CreateController(routes: routes, cmafStartupTimeout: TimeSpan.FromMilliseconds(50));

        // Act
        var result = await controller.StartCmafStream("9.1", new CmafStreamRequest());

        // Assert
        var error = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status504GatewayTimeout, error.StatusCode);
        var json = System.Text.Json.JsonSerializer.Serialize(error.Value);
        Assert.Contains("\"code\":\"cmaf_startup_timeout\"", json, StringComparison.Ordinal);
        Assert.Contains("\"stage\":\"startup\"", json, StringComparison.Ordinal);
    }

    /// <summary>Verifies a temporary source-drain pump cannot delay the structured startup timeout response.</summary>
    [Fact]
    public async Task StartCmafStream_StartupDeadlineExpires_DoesNotWaitForSourceDrainPump()
    {
        // Arrange
        var route = CreateResolvedRoute();
        var routes = Substitute.For<IStreamingRouteResolver>();
        routes.ResolveVirtualAsync(Arg.Any<VirtualDeviceId>(), "9.1", Arg.Any<CancellationToken>()).Returns(route);
        var multiplexer = Substitute.For<ITunerStreamMultiplexer>();
        multiplexer.SubscribeFirstAvailableAsync(route, Arg.Any<CancellationToken>())
            .Returns(new ResolvedTunerStream(route.Candidates[0], new DelayedReadStream(TimeSpan.FromSeconds(1))));
        var mediaProbe = Substitute.For<IMediaProbeService>();
        mediaProbe.ProbeAsync(
                Arg.Any<StreamAcquisitionDescriptor>(),
                Arg.Any<StreamAcquisitionFactory>(),
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, call.Arg<CancellationToken>());
                return new MediaProbeResult([], null);
            });
        var controller = CreateController(
            routes: routes,
            multiplexer: multiplexer,
            mediaProbe: mediaProbe,
            cmafStartupTimeout: TimeSpan.FromMilliseconds(50));
        var stopwatch = Stopwatch.StartNew();

        // Act
        var result = await controller.StartCmafStream("9.1", new CmafStreamRequest());

        // Assert
        Assert.Equal(StatusCodes.Status504GatewayTimeout, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(500), $"Startup timeout took {stopwatch.Elapsed}.");
    }

    private static StreamController CreateController(
        StreamingTopologyStore? store = null,
        IStreamingRouteResolver? routes = null,
        IProtectedContentSlateService? slate = null,
        DisabledChannelMode disabledMode = DisabledChannelMode.ReturnError,
        ProtectedContentMode protectedContentMode = ProtectedContentMode.ReturnError,
        IActiveStreamRegistry? activeStreams = null,
        int maximumConcurrentStreams = 0,
        ITunerStreamMultiplexer? multiplexer = null,
        IMediaProbeService? mediaProbe = null,
        ITransientDataStore? transientData = null,
        ICmafOutputCoordinator? cmafOutputs = null,
        TimeSpan? cmafStartupTimeout = null,
        ICmafCompatibilityTestService? compatibilityTestService = null)
    {
        var settings = Substitute.For<ILineupSettingsService>();
        settings.Settings.Returns(new LineupSettings
        {
            DisabledChannelMode = disabledMode,
            ProtectedContentMode = protectedContentMode,
            MaximumConcurrentStreams = maximumConcurrentStreams,
            Streaming = new StreamingConfiguration
            {
                VirtualDevices =
                [
                    new VirtualDeviceDefinition
                    {
                        Id = VirtualDeviceId.New(),
                        Name = "HDHomeRun",
                        Adapter = new AdapterConfiguration(
                            HdHomeRunVirtualTunerDriver.AdapterKind,
                            System.Text.Json.JsonSerializer.SerializeToElement(new HdHomeRunVirtualTunerConfiguration()))
                    },
                    WebPlayerVirtualTunerDriver.CreateBuiltIn()
                ]
            }
        });
        var controller = new StreamController(
            Substitute.For<IHttpClientFactory>(),
            Substitute.For<ILineupGuideReader>(),
            settings,
            store ?? CreateTopologyStore(),
            mediaProbe ?? Substitute.For<IMediaProbeService>(),
            activeStreams ?? new ActiveStreamRegistry(),
            slate ?? Substitute.For<IProtectedContentSlateService>(),
            multiplexer ?? Substitute.For<ITunerStreamMultiplexer>(),
            NullLogger<StreamController>.Instance,
            transientData: transientData ?? new TransientDataStore(Path.Combine(AppContext.BaseDirectory, "stream-controller-artifacts")),
            streamRoutes: routes,
            cmafOutputs: cmafOutputs,
            cmafStartupTimeout: cmafStartupTimeout,
            compatibilityTestService: compatibilityTestService)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { Response = { Body = new MemoryStream() } }
            }
        };
        return controller;
    }

    private static StreamingTopologyStore CreateTopologyStore()
    {
        var options = new DbContextOptionsBuilder<EpgDbContext>()
            .UseSqlite($"Data Source={Path.Combine(AppContext.BaseDirectory, $"stream-controller-{Guid.NewGuid():N}.db")}")
            .Options;
        return new StreamingTopologyStore(new PooledDbContextFactory<EpgDbContext>(options));
    }

    private static ResolvedStreamingRoute CreateResolvedRoute(bool protectedContent = false)
    {
        var physicalId = PhysicalDeviceId.New();
        var physical = Substitute.For<IPhysicalTunerDevice>();
        physical.Snapshot.Returns(new PhysicalDeviceSnapshot
        {
            Id = physicalId,
            Kind = "test",
            Name = "Test",
            Capacity = new PhysicalTunerCapacity(1)
        });
        var source = new SourceChannelId(physicalId, "9.1");
        var acquisition = new StreamAcquisitionDescriptor(
            physicalId,
            new PhysicalAcquisitionKey(source, "none"),
            new PhysicalTunerCapacity(1));
        var physicalProfile = new PhysicalTranscodeProfile();
        var diagnosticUri = new Uri("http://device/auto/v9.1");
        return new ResolvedStreamingRoute
        {
            VirtualDevice = new VirtualDeviceDefinition
            {
                Id = VirtualDeviceId.New(),
                Name = "Virtual",
                Adapter = new AdapterConfiguration("test", System.Text.Json.JsonSerializer.SerializeToElement(new { }))
            },
            PhysicalDevice = physical,
            SourceChannelId = source,
            SourceCandidates = [source],
            DiagnosticUri = diagnosticUri,
            Acquisition = acquisition,
            Candidates =
            [
                new ResolvedStreamingCandidate
                {
                    PhysicalDevice = physical,
                    SourceChannelId = source,
                    Acquisition = acquisition,
                    PhysicalProfile = physicalProfile,
                    DiagnosticUri = diagnosticUri,
                    SourceMetadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["drm"] = protectedContent.ToString() }
                }
            ],
            PhysicalProfile = physicalProfile,
            VirtualProfile = new VirtualTranscodeProfile()
        };
    }

    private static async Task<StreamingTopologyStore> CreateDisabledChannelStoreAsync(string guideNumber)
    {
        var store = CreateTopologyStore();
        var deviceId = PhysicalDeviceId.New();
        var channelId = new SourceChannelId(deviceId, guideNumber);
        await store.ReconcileAsync(
            deviceId,
            [new PhysicalSourceChannel { Id = channelId, ProviderNumber = guideNumber, ProviderName = "Disabled" }],
            TestContext.Current.CancellationToken);
        await store.SetEnabledAsync(channelId, enabled: false, TestContext.Current.CancellationToken);
        return store;
    }

    private static async Task<CmafRegistration> RegisterCmafSessionAsync(StreamController controller, string? sourceVideoCodec = null)
    {
        var sessionId = Guid.NewGuid().ToString("N");
        var transientData = new TransientDataStore(Path.Combine(AppContext.BaseDirectory, "stream-controller-artifacts"));
        var directory = transientData.CreateSessionDirectory(WebPlayerTransientWorkspaces.Cmaf, sessionId);
        var playlistPath = transientData.GetFilePath(directory, "master.m3u8");
        await File.WriteAllTextAsync(playlistPath, "#EXTM3U", TestContext.Current.CancellationToken);
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = "--version",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        });
        Assert.NotNull(process);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
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
        SetProperty(sessionType, session, "SourceVideoCodec", sourceVideoCodec);
        var register = typeof(StreamController).GetMethod("RegisterCmafSessionAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(register);
        var task = Assert.IsAssignableFrom<Task>(register.Invoke(controller, [session, TestContext.Current.CancellationToken]));
        await task;
        return new CmafRegistration(sessionId, directory, playlistPath);
    }

    private static void SetProperty(Type type, object instance, string name, object? value)
    {
        var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(property);
        property.SetValue(instance, value);
    }

    private sealed class DelayedReadStream(TimeSpan delay) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _ = buffer;
            _ = cancellationToken;
            await Task.Delay(delay);
            return 0;
        }
    }

    private sealed class CompatibilityStartupFixture : IAsyncDisposable
    {
        private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("lineup-watch-test-startup-");
        private readonly Process _encoder;
        private readonly Process _observer;

        internal CompatibilityStartupFixture(string scenario)
        {
            var configuration = new DirectoryInfo(AppContext.BaseDirectory).Name;
            var executable = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "Lineup.FakeFFmpeg", configuration, "Lineup.FakeFFmpeg.dll"));
            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add(executable);
            startInfo.ArgumentList.Add("hold");
            _encoder = Process.Start(startInfo) ?? throw new InvalidOperationException("The deterministic compatibility encoder could not be started.");
            _observer = Process.GetProcessById(_encoder.Id);
            var files = new TransientDataStore(_root.FullName);
            var transient = Substitute.For<ITransientDataStore>();
            transient.CreateSessionDirectory(Arg.Any<string>(), Arg.Any<string>()).Returns(call =>
            {
                SessionDirectory = files.CreateSessionDirectory(call.ArgAt<string>(0), call.ArgAt<string>(1));
                File.WriteAllText(Path.Combine(SessionDirectory, "ownership.txt"), "probe");
                return SessionDirectory;
            });
            transient.GetFilePath(Arg.Any<string>(), Arg.Any<string>()).Returns(call => files.GetFilePath(call.ArgAt<string>(0), call.ArgAt<string>(1)));
            transient.FileExists(Arg.Any<string>()).Returns(call => scenario == "io"
                ? throw new IOException("Synthetic manifest storage failure.")
                : files.FileExists(call.Arg<string>()));
            transient.EnumerateFiles(Arg.Any<string>(), Arg.Any<string>()).Returns(call => files.EnumerateFiles(call.ArgAt<string>(0), call.ArgAt<string>(1)));
            transient.OpenRead(Arg.Any<string>()).Returns(call =>
            {
                ManifestReads++;
                if (scenario == "unexpected" || scenario == "partial" && ManifestReads == 1)
                {
                    return new MemoryStream(System.Text.Encoding.UTF8.GetBytes(scenario == "unexpected" ? " " : "<MPD>"));
                }
                return files.OpenRead(call.Arg<string>());
            });
            transient.When(store => store.DeleteDirectory(Arg.Any<string>())).Do(call => files.DeleteDirectory(call.Arg<string>()));
            var service = Substitute.For<ICmafCompatibilityTestService>();
            service.Start(Arg.Any<VirtualTranscodeProfile>(), Arg.Any<CmafCompatibilityTestRequest>(), Arg.Any<string>()).Returns(call =>
            {
                if (scenario is "ready" or "partial" or "registration" or "unexpected")
                {
                    File.WriteAllText(call.Arg<string>(), """
                        <MPD><Period><AdaptationSet>
                          <Representation mimeType="video/mp4" codecs="avc1.64002a" />
                          <Representation mimeType="audio/mp4" codecs="mp4a.40.2" />
                        </AdaptationSet></Period></MPD>
                        """);
                    File.WriteAllText(Path.Combine(SessionDirectory, CmafStreamPlanner.HlsManifestName), "#EXTM3U");
                    File.WriteAllBytes(Path.Combine(SessionDirectory, "chunk-0-00001.m4s"), [0]);
                    File.WriteAllBytes(Path.Combine(SessionDirectory, "chunk-1-00001.m4s"), [0]);
                }
                if (scenario == "exit")
                {
                    _observer.Kill(entireProcessTree: true);
                    _observer.WaitForExit();
                }
                return _encoder;
            });
            if (scenario == "registration")
            {
                Outputs.StopAsync().AsTask().GetAwaiter().GetResult();
            }
            Controller = CreateController(transientData: transient, cmafOutputs: Outputs, compatibilityTestService: service);
            Controller.HttpContext.RequestAborted = RequestCancellation.Token;
        }

        internal StreamController Controller { get; }

        internal CmafOutputCoordinator Outputs { get; } = new();

        internal CancellationTokenSource RequestCancellation { get; } = new();

        internal string SessionDirectory { get; private set; } = string.Empty;

        internal int ManifestReads { get; private set; }

        internal bool IsEncoderRunning => !_observer.HasExited;

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            await Outputs.DisposeAsync();
            if (!_observer.HasExited)
            {
                _observer.Kill(entireProcessTree: true);
                await _observer.WaitForExitAsync(CancellationToken.None);
            }
            _encoder.Dispose();
            _observer.Dispose();
            RequestCancellation.Dispose();
            _root.Delete(recursive: true);
        }
    }

    private sealed record CmafRegistration(string SessionId, string Directory, string PlaylistPath);
}
