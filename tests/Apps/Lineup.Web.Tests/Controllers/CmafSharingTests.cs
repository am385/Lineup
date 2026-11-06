using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Lineup.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Lineup.Web.Tests.Controllers;

/// <summary>Verifies management-hosted CMAF sharing through actual controllers, coordinator lifetimes, storage, and encoder processes.</summary>
public sealed class CmafSharingTests
{
    /// <summary>Verifies advertised-but-undiscovered audio is explicit while legitimate declared video-only startup remains supported.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Start_VideoOnlyProbe_DistinguishesAdvertisedAudio(bool expectsAudio)
    {
        // Arrange
        await using var fixture = new Fixture
        {
            AudioHint = expectsAudio ? "AC4" : "none",
            ProbeOverride = new MediaProbeResult([new MediaTrackMetadata(0, MediaTrackType.Video, "h264", null, 1280, 720, null, null)], null)
        };
        var request = fixture.BeginStart("viewer");

        // Act
        var result = await request.Start;

        // Assert
        if (expectsAudio)
        {
            var failure = Assert.IsType<ObjectResult>(result);
            Assert.Equal(StatusCodes.Status502BadGateway, failure.StatusCode);
            Assert.Contains("media_audio_missing", System.Text.Json.JsonSerializer.Serialize(failure.Value), StringComparison.Ordinal);
            Assert.Equal(0, fixture.EncoderCount);
        }
        else
        {
            Assert.IsType<OkObjectResult>(result);
        }
    }

    /// <summary>Verifies conversion failures produce a structured gateway error without returning private processor output.</summary>
    [Fact]
    public async Task Start_ProcessorFails_ReturnsSanitizedStructuredFailure()
    {
        // Arrange
        await using var fixture = new Fixture { ProcessFailure = new MediaProcessException("PRIVATE processor detail at http://internal.invalid/source") };
        var request = fixture.BeginStart("viewer", startupTimeout: TimeSpan.FromSeconds(30));

        // Act
        var result = Assert.IsType<ObjectResult>(await request.Start);

        // Assert
        Assert.Equal(StatusCodes.Status502BadGateway, result.StatusCode);
        var body = System.Text.Json.JsonSerializer.Serialize(result.Value);
        Assert.Contains("media_process_failed", body, StringComparison.Ordinal);
        Assert.Contains("process", body, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE", body, StringComparison.Ordinal);
        Assert.DoesNotContain("internal.invalid", body, StringComparison.Ordinal);
    }

    /// <summary>Verifies a terminal upstream subscription failure is returned before the unrelated presentation-readiness deadline.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Start_UpstreamFails_ReturnsTerminalFailurePromptly(bool processFailure)
    {
        // Arrange
        await using var fixture = new Fixture
        {
            HoldReadiness = true,
            InputFailure = processFailure ? new MediaProcessException("Upstream FFmpeg exited with code 234.") : new IOException("Upstream stream failed.")
        };
        var first = fixture.BeginStart("owner", startupTimeout: TimeSpan.FromSeconds(30));

        // Act
        var result = await first.Start.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(StatusCodes.Status502BadGateway, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.False(fixture.IsEncoderRunning);
        Assert.False(fixture.HasEncoderDirectory);
    }

    /// <summary>Verifies distinct viewers share media but retain separate URLs, diagnostics, and stop ownership.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Start_DifferentViewers_ShareProducerAndStopIndependently(bool stopInitiator)
    {
        // Arrange
        await using var fixture = new Fixture();
        var first = fixture.BeginStart("viewer-a");
        var firstResponse = await ResponseAsync(first.Start);
        var second = fixture.BeginStart("viewer-b");
        var secondResponse = await ResponseAsync(second.Start);

        // Act
        first.Cancellation.Cancel();
        using var firstFragment = Assert.IsType<FileStream>(Assert.IsType<FileStreamResult>(first.Controller.GetCmafFile(firstResponse.SessionId, "chunk-0-00001.m4s")).FileStream);
        using var secondFragment = Assert.IsType<FileStream>(Assert.IsType<FileStreamResult>(second.Controller.GetCmafFile(secondResponse.SessionId, "chunk-0-00001.m4s")).FileStream);

        // Assert
        Assert.Equal(1, fixture.EncoderCount);
        var output = Assert.Single(fixture.Outputs.GetDiagnostics());
        Assert.Equal(2, output.ViewerCount);
        Assert.Equal(1, fixture.ActiveInputs);
        Assert.Equal(firstFragment.Name, secondFragment.Name);
        Assert.NotEqual(firstResponse.SessionId, secondResponse.SessionId);
        Assert.Equal($"/api/stream/cmaf/{firstResponse.SessionId}/manifest.mpd", firstResponse.DashManifestUrl);
        Assert.Equal($"/api/stream/cmaf/{secondResponse.SessionId}/master.m3u8", secondResponse.HlsManifestUrl);
        Assert.Equal($"/api/stream/cmaf/{secondResponse.SessionId}/captions-2.vtt", Assert.Single(secondResponse.Subtitles).Url);
        Assert.IsType<FileContentResult>(second.Controller.GetCmafFile(secondResponse.SessionId, "captions-2.vtt"));
        var active = fixture.ActiveStreams.GetActiveStreams();
        Assert.Equal(["viewer-a", "viewer-b"], active.Select(stream => stream.ClientId).Order());
        Assert.Equal(2, active.Select(stream => stream.ClientAddress).Distinct().Count());
        Assert.All(active, stream => Assert.Equal(output.OutputId, stream.Flow!.ReusableOutputId));

        // Act
        var stopped = stopInitiator ? first : second;
        var survivor = stopInitiator ? second : first;
        var stoppedId = stopInitiator ? firstResponse.SessionId : secondResponse.SessionId;
        var survivingId = stopInitiator ? secondResponse.SessionId : firstResponse.SessionId;
        var stop = stopped.Controller.StopCmaf(stoppedId);

        // Assert
        Assert.IsType<OkObjectResult>(stop);
        Assert.IsType<NotFoundObjectResult>(stopped.Controller.GetCmafFile(stoppedId, "manifest.mpd"));
        Assert.IsType<ContentResult>(survivor.Controller.GetCmafFile(survivingId, "manifest.mpd"));
        Assert.Equal(1, Assert.Single(fixture.Outputs.GetDiagnostics()).ViewerCount);
        Assert.Single(fixture.ActiveStreams.GetActiveStreams());
        Assert.True(fixture.IsEncoderRunning);
        Assert.True(File.Exists(firstFragment.Name));

        // Act
        Assert.IsType<OkObjectResult>(survivor.Controller.StopCmaf(survivingId));
        await WaitUntilAsync(() => fixture.ActiveInputs == 0);

        // Assert
        Assert.Empty(fixture.Outputs.GetViewers());
        Assert.Equal(0, fixture.Outputs.ActiveOutputCount);
        Assert.Empty(fixture.ActiveStreams.GetActiveStreams());
        Assert.False(fixture.IsEncoderRunning);
        Assert.False(File.Exists(firstFragment.Name));
    }

    /// <summary>Verifies expiry removes only the idle alias and the final expiry releases the producer.</summary>
    [Fact]
    public async Task Start_OneViewerExpires_ActiveViewerKeepsSharedProducer()
    {
        // Arrange
        await using var fixture = new Fixture();
        var first = fixture.BeginStart("idle");
        var firstResponse = await ResponseAsync(first.Start);
        var second = fixture.BeginStart("active");
        var secondResponse = await ResponseAsync(second.Start);
        Assert.Single(fixture.Outputs.GetDiagnostics());

        // Act
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(100));
        Assert.IsType<ContentResult>(second.Controller.GetCmafFile(secondResponse.SessionId, "manifest.mpd"));
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(100));
        await WaitUntilAsync(() => fixture.Outputs.GetViewers().Count == 1);

        // Assert
        Assert.Equal(secondResponse.SessionId, Assert.Single(fixture.Outputs.GetViewers()).ViewerSessionId.ToString());
        Assert.IsType<NotFoundObjectResult>(first.Controller.GetCmafFile(firstResponse.SessionId, "manifest.mpd"));
        Assert.Single(fixture.ActiveStreams.GetActiveStreams());
        Assert.Equal(1, fixture.EncoderCount);
        Assert.True(fixture.IsEncoderRunning);
        Assert.Equal(1, fixture.ActiveInputs);

        // Act
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(200));
        await WaitUntilAsync(() => fixture.Outputs.ActiveOutputCount == 0 && fixture.ActiveInputs == 0 && !fixture.IsEncoderRunning && !fixture.HasEncoderDirectory);

        // Assert
        Assert.Empty(fixture.ActiveStreams.GetActiveStreams());
        Assert.False(fixture.IsEncoderRunning);
    }

    /// <summary>Verifies concurrent starts attach to one pending output and receive independent viewer aliases.</summary>
    [Fact]
    public async Task Start_ConcurrentViewers_SharePendingProducer()
    {
        // Arrange
        await using var fixture = new Fixture { HoldReadiness = true };
        var first = fixture.BeginStart("viewer-a");
        await fixture.EncoderStarted.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Act
        var second = fixture.BeginStart("viewer-b");
        await WaitUntilAsync(() => fixture.Outputs.GetViewers().Count == 2);
        var pendingOutputs = fixture.Outputs.ActiveOutputCount;
        fixture.PublishReadiness();
        var responses = await Task.WhenAll(ResponseAsync(first.Start), ResponseAsync(second.Start));

        // Assert
        Assert.Equal(1, pendingOutputs);
        Assert.Equal(1, fixture.EncoderCount);
        Assert.Equal(2, Assert.Single(fixture.Outputs.GetDiagnostics()).ViewerCount);
        Assert.NotEqual(responses[0].SessionId, responses[1].SessionId);
        Assert.IsType<ContentResult>(first.Controller.GetCmafFile(responses[0].SessionId, "manifest.mpd"));
        Assert.IsType<ContentResult>(second.Controller.GetCmafFile(responses[1].SessionId, "manifest.mpd"));
    }

    /// <summary>Verifies canceling a joining viewer during startup releases only its alias and preserves the pending producer.</summary>
    [Fact]
    public async Task Start_JoiningViewerCanceled_PreservesPendingProducer()
    {
        // Arrange
        await using var fixture = new Fixture { HoldReadiness = true };
        var first = fixture.BeginStart("owner");
        await fixture.EncoderStarted.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var second = fixture.BeginStart("joining");
        await WaitUntilAsync(() => fixture.Outputs.GetViewers().Count == 2);

        // Act
        second.Cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.Start);
        fixture.PublishReadiness();
        var response = await ResponseAsync(first.Start);

        // Assert
        Assert.Equal(1, fixture.EncoderCount);
        Assert.Equal(1, Assert.Single(fixture.Outputs.GetDiagnostics()).ViewerCount);
        Assert.Equal("owner", Assert.Single(fixture.ActiveStreams.GetActiveStreams()).ClientId);
        Assert.True(fixture.IsEncoderRunning);
        Assert.IsType<ContentResult>(first.Controller.GetCmafFile(response.SessionId, "manifest.mpd"));
    }

    /// <summary>Verifies a departing initiator cannot cancel or abandon readiness for another pending viewer.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Start_InitiatorLeavesBeforeReadiness_SurvivorReceivesReadyProducer(bool cancelRequest)
    {
        // Arrange
        await using var fixture = new Fixture { HoldReadiness = true };
        var first = fixture.BeginStart("owner");
        await fixture.EncoderStarted.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var second = fixture.BeginStart("survivor");
        await WaitUntilAsync(() => fixture.Outputs.GetViewers().Count == 2);
        var ownerId = fixture.ActiveStreams.GetActiveStreams().Single(stream => stream.ClientId == "owner").SessionId;

        // Act
        if (cancelRequest)
        {
            first.Cancellation.Cancel();
        }
        else
        {
            Assert.True(fixture.ActiveStreams.RequestStop(ownerId));
        }
        Assert.IsType<EmptyResult>(await first.Start.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        fixture.PublishReadiness();
        var response = await ResponseAsync(second.Start.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        // Assert
        Assert.False(second.Cancellation.IsCancellationRequested);
        Assert.Equal(1, fixture.EncoderCount);
        Assert.Equal(1, Assert.Single(fixture.Outputs.GetDiagnostics()).ViewerCount);
        Assert.Equal("survivor", Assert.Single(fixture.ActiveStreams.GetActiveStreams()).ClientId);
        Assert.True(fixture.IsEncoderRunning);
        Assert.IsType<ContentResult>(second.Controller.GetCmafFile(response.SessionId, "manifest.mpd"));
        Assert.IsType<NotFoundObjectResult>(first.Controller.GetCmafFile(ownerId, "manifest.mpd"));

        // Act
        Assert.IsType<OkObjectResult>(second.Controller.StopCmaf(response.SessionId));
        await WaitUntilAsync(() => fixture.ActiveInputs == 0);

        // Assert
        Assert.Empty(fixture.Outputs.GetViewers());
        Assert.Empty(fixture.ActiveStreams.GetActiveStreams());
        Assert.False(fixture.IsEncoderRunning);
        Assert.False(fixture.HasEncoderDirectory);
    }

    /// <summary>Verifies releasing all pending viewers cancels producer work and cleans its input, process, and directory.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Start_AllViewersLeaveBeforeReadiness_CleansProducer(bool cancelRequests)
    {
        // Arrange
        await using var fixture = new Fixture { HoldReadiness = true };
        var first = fixture.BeginStart("owner");
        await fixture.EncoderStarted.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var second = fixture.BeginStart("joining");
        await WaitUntilAsync(() => fixture.Outputs.GetViewers().Count == 2);

        // Act
        if (cancelRequests)
        {
            first.Cancellation.Cancel();
            second.Cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.Start);
        }
        else
        {
            Assert.Equal(1, fixture.ActiveStreams.RequestStopByClientId("owner"));
            Assert.Equal(1, fixture.ActiveStreams.RequestStopByClientId("joining"));
            Assert.IsType<EmptyResult>(await second.Start.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        }
        Assert.IsType<EmptyResult>(await first.Start.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        await WaitUntilAsync(() => fixture.ActiveInputs == 0 && !fixture.IsEncoderRunning && !fixture.HasEncoderDirectory);

        // Assert
        Assert.Equal(0, fixture.Outputs.ActiveOutputCount);
        Assert.Empty(fixture.Outputs.GetViewers());
        Assert.Empty(fixture.ActiveStreams.GetActiveStreams());
    }

    /// <summary>Verifies producer-owned failure or deadline still reaches a surviving viewer after the initiator leaves.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Start_ProducerFailsAfterInitiatorLeaves_SurvivorReceivesFailureAndCleanup(bool encoderExits)
    {
        // Arrange
        await using var fixture = new Fixture { HoldReadiness = true };
        var first = fixture.BeginStart("owner", startupTimeout: TimeSpan.FromSeconds(2));
        await fixture.EncoderStarted.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var second = fixture.BeginStart("survivor", startupTimeout: TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => fixture.Outputs.GetViewers().Count == 2);
        first.Cancellation.Cancel();
        Assert.IsType<EmptyResult>(await first.Start);

        // Act
        if (encoderExits)
        {
            await fixture.TerminateEncoderAsync();
        }
        var result = Assert.IsType<ObjectResult>(await second.Start.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        await WaitUntilAsync(() => fixture.ActiveInputs == 0 && !fixture.IsEncoderRunning && !fixture.HasEncoderDirectory);

        // Assert
        Assert.False(second.Cancellation.IsCancellationRequested);
        Assert.Equal(encoderExits ? StatusCodes.Status502BadGateway : StatusCodes.Status504GatewayTimeout, result.StatusCode);
        Assert.Equal(0, fixture.Outputs.ActiveOutputCount);
        Assert.Empty(fixture.Outputs.GetViewers());
        Assert.Empty(fixture.ActiveStreams.GetActiveStreams());
    }

    /// <summary>Verifies sharing does not bypass the independently counted viewer admission limit.</summary>
    [Fact]
    public async Task Start_ThirdViewer_StillEnforcesViewerLimit()
    {
        // Arrange
        await using var fixture = new Fixture();
        await ResponseAsync(fixture.BeginStart("viewer-a").Start);
        await ResponseAsync(fixture.BeginStart("viewer-b").Start);

        // Act
        var third = await fixture.BeginStart("viewer-c").Start;

        // Assert
        Assert.Equal(StatusCodes.Status429TooManyRequests, Assert.IsType<ObjectResult>(third).StatusCode);
        Assert.Equal(2, fixture.ActiveStreams.GetActiveStreams().Count);
        Assert.Equal(2, fixture.Outputs.GetViewers().Count);
        Assert.Equal(1, fixture.EncoderCount);
        Assert.Single(fixture.Outputs.GetDiagnostics());
    }

    /// <summary>Verifies media settings and exact source provenance continue to isolate producers even for the same client identity.</summary>
    [Theory]
    [InlineData("audio")]
    [InlineData("quality")]
    [InlineData("profile")]
    [InlineData("physical")]
    [InlineData("channel")]
    public async Task Start_DifferentPlanOrSource_DoesNotShare(string change)
    {
        // Arrange
        await using var fixture = new Fixture();
        var first = fixture.BeginStart("same-viewer");
        var firstResponse = await ResponseAsync(first.Start);
        var request = new CmafStreamRequest();
        var channel = "7.1";
        switch (change)
        {
            case "audio": request = request with { PreferredAudio = CmafPreferredAudio.Fallback, FallbackAudio = CmafFallbackAudio.Ac3 }; break;
            case "quality": request = request with { Quality = WebPlayerQuality.Low }; break;
            case "profile": fixture.Settings.Streaming = fixture.Settings.Streaming with { VirtualDefaults = new VirtualTranscodeProfile { VideoQuality = 18 } }; break;
            case "physical": fixture.PhysicalId = PhysicalDeviceId.New(); break;
            case "channel": channel = "9.1"; break;
            default: throw new ArgumentOutOfRangeException(nameof(change));
        }

        // Act
        var second = fixture.BeginStart("same-viewer", request, channel);
        var secondResponse = await ResponseAsync(second.Start);

        // Assert
        Assert.Equal(2, fixture.EncoderCount);
        Assert.Equal(2, fixture.Outputs.ActiveOutputCount);
        using var firstFile = Assert.IsType<FileStream>(Assert.IsType<FileStreamResult>(first.Controller.GetCmafFile(firstResponse.SessionId, "chunk-0-00001.m4s")).FileStream);
        using var secondFile = Assert.IsType<FileStream>(Assert.IsType<FileStreamResult>(second.Controller.GetCmafFile(secondResponse.SessionId, "chunk-0-00001.m4s")).FileStream);
        Assert.NotEqual(firstFile.Name, secondFile.Name);
        Assert.Equal(2, fixture.ActiveInputs);
    }

    private static async Task<CmafStreamResponse> ResponseAsync(Task<IActionResult> start) =>
        Assert.IsType<CmafStreamResponse>(Assert.IsType<OkObjectResult>(await start).Value);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(5))
            {
                throw new TimeoutException("The expected CMAF sharing state did not become observable.");
            }
            await Task.Delay(10);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("lineup-cmaf-sharing-");
        private readonly ConcurrentQueue<(Process Observer, string Directory)> _encoders = new();
        private readonly TaskCompletionSource _encoderStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<CancellationTokenSource> _cancellations = [];
        private readonly List<Task<IActionResult>> _starts = [];
        private readonly ILineupSettingsService _settings = Substitute.For<ILineupSettingsService>();
        private readonly ITunerStreamMultiplexer _multiplexer = Substitute.For<ITunerStreamMultiplexer>();
        private readonly IMediaProbeService _probe = Substitute.For<IMediaProbeService>();
        private readonly IMediaProcessFactory _processes = Substitute.For<IMediaProcessFactory>();
        private readonly IStreamingRouteResolver _routes = Substitute.For<IStreamingRouteResolver>();
        private readonly HttpClient _diagnosticClient = new(new UnavailableDiagnostics());
        private readonly StreamingTopologyStore _topology;
        private readonly TransientDataStore _files;
        private int _activeInputs;

        /// <summary>Creates isolated SQLite storage, deterministic source capabilities, and real process ownership.</summary>
        internal Fixture()
        {
            _files = new TransientDataStore(_root.FullName);
            _topology = new StreamingTopologyStore(new PooledDbContextFactory<EpgDbContext>(
                new DbContextOptionsBuilder<EpgDbContext>().UseSqlite($"Data Source={Path.Combine(_root.FullName, "topology.db")};Pooling=False").Options));
            _settings.Settings.Returns(Settings);
            Outputs = new CmafOutputCoordinator(Clock, TimeSpan.FromMilliseconds(200));
            _routes.ResolveVirtualAsync(Arg.Any<VirtualDeviceId>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call => Route(call.Arg<string>()));
            _multiplexer.SubscribeFirstAvailableAsync(Arg.Any<ResolvedStreamingRoute>(), Arg.Any<CancellationToken>())
                .Returns(call => new ResolvedTunerStream(call.Arg<ResolvedStreamingRoute>().Candidates[0], new BlockingInput()));
            _multiplexer.SubscribeAsync(Arg.Any<StreamAcquisitionDescriptor>(), Arg.Any<StreamAcquisitionFactory>(), Arg.Any<CancellationToken>()).Returns(_ =>
            {
                if (InputFailure is not null)
                {
                    throw InputFailure;
                }
                Interlocked.Increment(ref _activeInputs);
                return new BlockingInput(() => Interlocked.Decrement(ref _activeInputs));
            });
            _probe.ProbeAsync(
                Arg.Any<StreamAcquisitionDescriptor>(), Arg.Any<StreamAcquisitionFactory>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>())
                .Returns(_ => ProbeOverride ?? new MediaProbeResult(
                [
                    new MediaTrackMetadata(0, MediaTrackType.Video, "h264", null, null, null, null, null),
                    new MediaTrackMetadata(1, MediaTrackType.Audio, "aac", null, null, null, 2, 48_000) { IsDefault = true },
                    new MediaTrackMetadata(2, MediaTrackType.Subtitle, "webvtt", null, null, null, null, null)
                    {
                        SubtitlePresentation = SubtitlePresentation.WebVtt,
                        Title = "English",
                        Language = "eng"
                    }
                ], null));
            _processes.Start(Arg.Any<MediaProcessRequest>()).Returns(call => StartEncoder(call.Arg<MediaProcessRequest>()));
        }

        /// <summary>Gets the controllable logical inactivity clock.</summary>
        internal LogicalClock Clock { get; } = new();

        /// <summary>Gets the real shared-output coordinator.</summary>
        internal CmafOutputCoordinator Outputs { get; }

        /// <summary>Gets independently admitted viewer diagnostics.</summary>
        internal ActiveStreamRegistry ActiveStreams { get; } = new();

        /// <summary>Gets mutable trusted settings for plan-isolation checks.</summary>
        internal LineupSettings Settings { get; } = new()
        {
            MaximumConcurrentStreams = 2,
            Streaming = new StreamingConfiguration { VirtualDevices = [WebPlayerVirtualTunerDriver.CreateBuiltIn()], KeepWarmPeriodSeconds = 0 }
        };

        /// <summary>Gets or sets the exact physical source selected by subsequent requests.</summary>
        internal PhysicalDeviceId PhysicalId { get; set; } = PhysicalDeviceId.New();

        /// <summary>Gets or sets whether startup waits for explicitly published readiness artifacts.</summary>
        internal bool HoldReadiness { get; set; }

        /// <summary>Gets or sets a terminal failure from the upstream producer subscription.</summary>
        internal Exception? InputFailure { get; set; }

        /// <summary>Gets or sets a terminal failure while creating the output processor.</summary>
        internal MediaProcessException? ProcessFailure { get; set; }

        /// <summary>Gets or sets deterministic probe metadata for audio-discovery regressions.</summary>
        internal MediaProbeResult? ProbeOverride { get; set; }

        /// <summary>Gets or sets the source's normalized audio declaration.</summary>
        internal string? AudioHint { get; set; }

        /// <summary>Gets notification that the real encoder process has started.</summary>
        internal Task EncoderStarted => _encoderStarted.Task;

        /// <summary>Gets the number of actual encoder processes started.</summary>
        internal int EncoderCount => _encoders.Count;

        /// <summary>Gets whether any owned encoder process remains alive.</summary>
        internal bool IsEncoderRunning => _encoders.Any(encoder => !encoder.Observer.HasExited);

        /// <summary>Gets whether any owned producer directory remains on disk.</summary>
        internal bool HasEncoderDirectory => _encoders.Any(encoder => Directory.Exists(encoder.Directory));

        /// <summary>Gets the number of live producer input subscriptions.</summary>
        internal int ActiveInputs => Volatile.Read(ref _activeInputs);

        /// <summary>Starts a real controller request with its own client metadata and cancellation lifetime.</summary>
        internal (StreamController Controller, CancellationTokenSource Cancellation, Task<IActionResult> Start) BeginStart(
            string clientId,
            CmafStreamRequest? request = null,
            string channel = "7.1",
            TimeSpan? startupTimeout = null)
        {
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            _cancellations.Add(cancellation);
            var httpClients = Substitute.For<IHttpClientFactory>();
            httpClients.CreateClient("StreamProxy").Returns(_diagnosticClient);
            var controller = new StreamController(
                httpClients, Substitute.For<ILineupGuideReader>(), _settings, _topology, _probe, ActiveStreams,
                Substitute.For<IProtectedContentSlateService>(), _multiplexer, NullLogger<StreamController>.Instance,
                transientData: _files, mediaProcessFactory: _processes, streamRoutes: _routes, cmafOutputs: Outputs, cmafStartupTimeout: startupTimeout)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext { RequestAborted = cancellation.Token }
                }
            };
            controller.HttpContext.Connection.RemoteIpAddress = IPAddress.Loopback;
            controller.HttpContext.Connection.RemotePort = 10_000 + _cancellations.Count;
            var start = controller.StartCmafStream(channel, (request ?? new CmafStreamRequest()) with { ClientId = clientId });
            _starts.Add(start);
            return (controller, cancellation, start);
        }

        /// <summary>Releases readiness for current and subsequent encoders using deterministic manifests, fragments, and captions.</summary>
        internal void PublishReadiness()
        {
            HoldReadiness = false;
            foreach (var encoder in _encoders)
            {
                PublishReadiness(encoder.Directory);
            }
        }

        /// <summary>Exits owned encoders before readiness without using external hardware or network requests.</summary>
        internal async Task TerminateEncoderAsync()
        {
            foreach (var encoder in _encoders)
            {
                encoder.Observer.Kill(entireProcessTree: true);
                await encoder.Observer.WaitForExitAsync(TestContext.Current.CancellationToken);
            }
        }

        private static void PublishReadiness(string directory)
        {
            File.WriteAllText(Path.Combine(directory, "manifest.mpd"), """
                <MPD xmlns="urn:mpeg:dash:schema:mpd:2011"><Period>
                  <AdaptationSet contentType="video"><Representation id="0" codecs="avc1.64001f" /></AdaptationSet>
                  <AdaptationSet contentType="audio"><Representation id="1" codecs="mp4a.40.2" /></AdaptationSet>
                </Period></MPD>
                """);
            File.WriteAllText(Path.Combine(directory, "master.m3u8"), "#EXTM3U\n");
            File.WriteAllText(Path.Combine(directory, "captions-2.vtt"), "WEBVTT\n\n00:00:00.000 --> 00:00:02.000\nCaption\n");
            foreach (var representation in new[] { 0, 1 })
            {
                File.WriteAllBytes(Path.Combine(directory, $"init-{representation}.mp4"), [0]);
                File.WriteAllBytes(Path.Combine(directory, $"chunk-{representation}-00001.m4s"), [0]);
                File.WriteAllBytes(Path.Combine(directory, $"chunk-{representation}-00002.m4s"), [0]);
            }
        }

        private Process StartEncoder(MediaProcessRequest request)
        {
            if (ProcessFailure is not null)
            {
                throw ProcessFailure;
            }
            var directory = request.WorkingDirectory ?? throw new InvalidOperationException("The CMAF encoder request is missing its owned output directory.");
            var configuration = new DirectoryInfo(AppContext.BaseDirectory).Name;
            var executable = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "Lineup.FakeFFmpeg", configuration, "Lineup.FakeFFmpeg.dll"));
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.Environment.Remove("LINEUP_FAKE_FFMPEG_FAIL");
            start.ArgumentList.Add(executable);
            start.ArgumentList.Add("hold");
            var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start the deterministic CMAF encoder.");
            _encoders.Enqueue((Process.GetProcessById(process.Id), directory));
            if (!HoldReadiness)
            {
                PublishReadiness(directory);
            }
            _encoderStarted.TrySetResult();
            return process;
        }

        private ResolvedStreamingRoute Route(string channel)
        {
            var physical = Substitute.For<IPhysicalTunerDevice>();
            physical.Snapshot.Returns(new PhysicalDeviceSnapshot { Id = PhysicalId, Kind = "test", Name = "Source", Capacity = new PhysicalTunerCapacity(2) });
            var source = new SourceChannelId(PhysicalId, channel);
            var acquisition = new StreamAcquisitionDescriptor(PhysicalId, new PhysicalAcquisitionKey(source, "none"), new PhysicalTunerCapacity(2));
            var candidate = new ResolvedStreamingCandidate
            {
                PhysicalDevice = physical,
                SourceChannelId = source,
                Acquisition = acquisition,
                PhysicalProfile = new PhysicalTranscodeProfile(),
                SourceMetadata = AudioHint is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["audioCodec"] = AudioHint },
                DiagnosticUri = new Uri($"http://source.invalid/auto/v{channel}")
            };
            return new ResolvedStreamingRoute
            {
                VirtualDevice = Assert.Single(Settings.Streaming.VirtualDevices),
                PhysicalDevice = physical,
                SourceChannelId = source,
                SourceCandidates = [source],
                Candidates = [candidate],
                Acquisition = acquisition,
                PhysicalProfile = candidate.PhysicalProfile,
                VirtualProfile = Settings.Streaming.VirtualDefaults,
                DiagnosticUri = candidate.DiagnosticUri
            };
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            foreach (var cancellation in _cancellations)
            {
                cancellation.Cancel();
            }
            try
            {
                try
                {
                    await Task.WhenAll(_starts).WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (OperationCanceledException) when (_cancellations.All(cancellation => cancellation.IsCancellationRequested))
                {
                }
            }
            finally
            {
                await Outputs.DisposeAsync();
                foreach (var encoder in _encoders)
                {
                    if (!encoder.Observer.HasExited)
                    {
                        encoder.Observer.Kill(entireProcessTree: true);
                    }
                    await encoder.Observer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    encoder.Observer.Dispose();
                }
                await WaitUntilAsync(() => ActiveInputs == 0);
                foreach (var cancellation in _cancellations)
                {
                    cancellation.Dispose();
                }
                _diagnosticClient.Dispose();
                _root.Delete(recursive: true);
            }
        }
    }

    private sealed class UnavailableDiagnostics : HttpMessageHandler
    {
        /// <inheritdoc />
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("No live tuner diagnostics are used by this test."));
    }

    private sealed class LogicalClock : TimeProvider
    {
        private long _timestamp;

        /// <inheritdoc />
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        /// <inheritdoc />
        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);

        /// <summary>Advances logical elapsed time while retaining the standard timer scheduler.</summary>
        internal void Advance(TimeSpan duration) => Interlocked.Add(ref _timestamp, duration.Ticks);
    }

    private sealed class BlockingInput(Action? disposed = null) : Stream
    {
        private int _disposed;

        /// <inheritdoc />
        public override bool CanRead => true;
        /// <inheritdoc />
        public override bool CanSeek => false;
        /// <inheritdoc />
        public override bool CanWrite => false;
        /// <inheritdoc />
        public override long Length => throw new NotSupportedException();
        /// <inheritdoc />
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        /// <inheritdoc />
        public override void Flush() => throw new NotSupportedException();
        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        /// <inheritdoc />
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        /// <inheritdoc />
        public override void SetLength(long value) => throw new NotSupportedException();
        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        /// <inheritdoc />
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                disposed?.Invoke();
            }
            base.Dispose(disposing);
        }
    }
}
