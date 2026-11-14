using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Lineup.App.Web.Tests.Services;

/// <summary>Verifies virtual MPEG-TS output ownership and topology metadata.</summary>
public sealed class VirtualMpegTsOutputServiceTests
{
    /// <summary>Verifies the temporary source reservation is actively drained during probing and its pump is stopped on success or failure.</summary>
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    public async Task OpenAsync_Probing_DrainsAndReleasesTemporaryReservation(bool physicalOutput, bool probeFails, bool drainFails)
    {
        // Arrange
        var virtualId = VirtualDeviceId.New();
        var route = CreateRoute(virtualId, protectedContent: false);
        var routes = Substitute.For<IStreamingRouteResolver>();
        routes.ResolveVirtualAsync(virtualId, "112.1", Arg.Any<CancellationToken>()).Returns(route);
        var settings = Substitute.For<ILineupSettingsService>();
        settings.Settings.Returns(new LineupSettings());
        var reservation = new ReservationStream { FailOnCancel = drainFails };
        using var produced = new CorrelatedStream("shared");
        var multiplexer = Substitute.For<ITunerStreamMultiplexer, IEffectiveTunerStreamMultiplexer>();
        multiplexer.SubscribeFirstAvailableAsync(route, Arg.Any<CancellationToken>()).Returns(new ResolvedTunerStream(route.Candidates[0], reservation));
        var media = new MediaProbeResult([], null);
        var probe = Substitute.For<IMediaProbeService>();
        probe.ProbeAsync(Arg.Any<StreamAcquisitionDescriptor>(), Arg.Any<StreamAcquisitionFactory>(), Arg.Any<string>(),
            Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>()).Returns(async _ =>
            {
                await reservation.Started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
                if (probeFails)
                {
                    throw new InvalidOperationException("Controlled probe failure.");
                }
                return media;
            });
        var effective = (IEffectiveTunerStreamMultiplexer)multiplexer;
        effective.GetEffectiveSource(media, route.PhysicalProfile).Returns(media);
        effective.SubscribeEffectiveAsync(Arg.Any<StreamAcquisitionDescriptor>(), Arg.Any<StreamAcquisitionFactory>(), Arg.Any<string>(),
            Arg.Any<MediaProbeResult>(), Arg.Any<PhysicalTranscodeProfile>(), Arg.Any<LineupSettings>(), Arg.Any<CancellationToken>()).Returns(produced);
        effective.SubscribeMpegTsAsync(Arg.Any<StreamAcquisitionDescriptor>(), Arg.Any<StreamAcquisitionFactory>(), Arg.Any<string>(),
            Arg.Any<MediaProbeResult>(), Arg.Any<PhysicalTranscodeProfile>(), Arg.Any<VirtualTranscodeProfile>(), Arg.Any<LineupSettings>(), Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>()).Returns(produced);
        var service = new VirtualMpegTsOutputService(routes, settings, probe, multiplexer, Substitute.For<IChannelSlateStreamFactory>(), NullLogger<VirtualMpegTsOutputService>.Instance);

        // Act
        var failure = await Record.ExceptionAsync(async () =>
        {
            await using var output = physicalOutput
                ? await service.OpenPhysicalOutputAsync(virtualId, "112.1", TestContext.Current.CancellationToken)
                : await service.OpenAsync(virtualId, "112.1", TestContext.Current.CancellationToken);
        });

        // Assert
        if (drainFails)
        {
            Assert.Equal("Controlled reservation failure.", Assert.IsType<IOException>(failure).Message);
            Assert.False(produced.CanRead);
        }
        else if (probeFails)
        {
            Assert.Equal("Controlled probe failure.", Assert.IsType<InvalidOperationException>(failure).Message);
        }
        else
        {
            Assert.Null(failure);
        }
        Assert.True(reservation.Started.Task.IsCompletedSuccessfully);
        Assert.True(reservation.Stopped);
        Assert.False(reservation.CanRead);
        Assert.Same(Stream.Null, reservation.Destination);
    }

    /// <summary>Verifies the output identifies the selected physical source, virtual destination, and producer node.</summary>
    [Fact]
    public async Task OpenAsync_ResolvedRoute_PreservesStreamingFlowContext()
    {
        // Arrange
        var physicalId = PhysicalDeviceId.New();
        var virtualId = VirtualDeviceId.New();
        var sourceChannel = new SourceChannelId(physicalId, "2.3");
        var physicalDevice = Substitute.For<IPhysicalTunerDevice>();
        physicalDevice.Snapshot.Returns(new PhysicalDeviceSnapshot
        {
            Id = physicalId,
            Kind = "hdhomerun",
            Name = "Antenna",
            Capacity = new PhysicalTunerCapacity(4)
        });
        var acquisition = new StreamAcquisitionDescriptor(
            physicalId,
            new PhysicalAcquisitionKey(sourceChannel, "none"),
            new PhysicalTunerCapacity(4));
        var candidate = new ResolvedStreamingCandidate
        {
            PhysicalDevice = physicalDevice,
            SourceChannelId = sourceChannel,
            Acquisition = acquisition,
            PhysicalProfile = new PhysicalTranscodeProfile(),
            DiagnosticUri = new Uri("http://device.local/auto/v2.3"),
            SourceMetadata = new Dictionary<string, string> { ["audioCodec"] = "AC3" }
        };
        var route = new ResolvedStreamingRoute
        {
            VirtualDevice = new VirtualDeviceDefinition
            {
                Id = virtualId,
                Name = "Remote output",
                Adapter = new AdapterConfiguration("lineup-link", JsonSerializer.SerializeToElement(new { }))
            },
            PhysicalDevice = physicalDevice,
            SourceChannelId = sourceChannel,
            SourceCandidates = [sourceChannel],
            DiagnosticUri = candidate.DiagnosticUri,
            Acquisition = acquisition,
            Candidates = [candidate],
            PhysicalProfile = candidate.PhysicalProfile,
            VirtualProfile = new VirtualTranscodeProfile()
        };
        var routes = Substitute.For<IStreamingRouteResolver>();
        routes.ResolveVirtualAsync(virtualId, "2.3", Arg.Any<CancellationToken>()).Returns(route);
        var settings = Substitute.For<ILineupSettingsService>();
        settings.Settings.Returns(new LineupSettings());
        var source = new MediaProbeResult([], null);
        var effectiveSource = new MediaProbeResult(
            [new MediaTrackMetadata(0, MediaTrackType.Video, "h264", null, 1920, 1080, null, null)],
            null);
        var probe = Substitute.For<IMediaProbeService>();
        probe.ProbeAsync(
                acquisition,
                Arg.Any<StreamAcquisitionFactory>(),
                candidate.DiagnosticUri.AbsoluteUri,
                candidate.SourceMetadata,
                Arg.Any<CancellationToken>())
            .Returns(source);
        var multiplexer = Substitute.For<ITunerStreamMultiplexer, IEffectiveTunerStreamMultiplexer>();
        multiplexer.SubscribeFirstAvailableAsync(route, Arg.Any<CancellationToken>())
            .Returns(new ResolvedTunerStream(candidate, new MemoryStream([1])));
        var effective = (IEffectiveTunerStreamMultiplexer)multiplexer;
        effective.SubscribeEffectiveAsync(
                acquisition,
                Arg.Any<StreamAcquisitionFactory>(),
                candidate.DiagnosticUri.AbsoluteUri,
                source,
                candidate.PhysicalProfile,
                settings.Settings,
                Arg.Any<CancellationToken>())
            .Returns(new CorrelatedStream("virtual-output-node"));
        effective.GetEffectiveSource(source, candidate.PhysicalProfile).Returns(effectiveSource);
        var service = new VirtualMpegTsOutputService(
            routes,
            settings,
            probe,
            multiplexer,
            Substitute.For<IChannelSlateStreamFactory>(),
            NullLogger<VirtualMpegTsOutputService>.Instance);

        // Act
        await using var output = await service.OpenPhysicalOutputAsync(virtualId, "2.3", TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(physicalId, output.Flow.PhysicalDeviceId);
        Assert.Equal(sourceChannel, output.Flow.SourceChannelId);
        Assert.Equal(virtualId, output.Flow.VirtualDeviceId);
        Assert.Equal("virtual-output-node", output.Flow.ProducerNodeId);
        Assert.Same(source, output.RawSource);
        var outputVideo = Assert.Single(output.Source.Tracks);
        Assert.Equal(0, outputVideo.Index);
        Assert.Equal(effectiveSource.Tracks[0].Codec, outputVideo.Codec);
        await effective.DidNotReceive().SubscribeMpegTsAsync(
            Arg.Any<StreamAcquisitionDescriptor>(),
            Arg.Any<StreamAcquisitionFactory>(),
            Arg.Any<string>(),
            Arg.Any<MediaProbeResult>(),
            Arg.Any<PhysicalTranscodeProfile>(),
            Arg.Any<VirtualTranscodeProfile>(),
            Arg.Any<LineupSettings>(),
            Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>Verifies known protected metadata returns a structured failure before any physical acquisition.</summary>
    [Fact]
    public async Task OpenAsync_KnownProtectedReturnError_DoesNotAcquireSource()
    {
        // Arrange
        var virtualId = VirtualDeviceId.New();
        var route = CreateRoute(virtualId, protectedContent: true);
        var routes = Substitute.For<IStreamingRouteResolver>();
        routes.ResolveVirtualAsync(virtualId, "112.1", Arg.Any<CancellationToken>()).Returns(route);
        var settings = Substitute.For<ILineupSettingsService>();
        settings.Settings.Returns(new LineupSettings { ProtectedContentMode = ProtectedContentMode.ReturnError });
        var probe = Substitute.For<IMediaProbeService>();
        var multiplexer = Substitute.For<ITunerStreamMultiplexer>();
        var service = new VirtualMpegTsOutputService(
            routes,
            settings,
            probe,
            multiplexer,
            Substitute.For<IChannelSlateStreamFactory>(),
            NullLogger<VirtualMpegTsOutputService>.Instance);

        // Act
        var exception = await Assert.ThrowsAsync<StreamAcquisitionException>(
            () => service.OpenAsync(virtualId, "112.1", TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(PhysicalStreamFailureCategory.ProtectedContent, exception.Category);
        Assert.Equal("protected_content", exception.Code);
        Assert.False(exception.Retryable);
        await multiplexer.DidNotReceive().SubscribeFirstAvailableAsync(Arg.Any<ResolvedStreamingRoute>(), Arg.Any<CancellationToken>());
        await probe.DidNotReceive().ProbeAsync(
            Arg.Any<StreamAcquisitionDescriptor>(),
            Arg.Any<StreamAcquisitionFactory>(),
            Arg.Any<string>(),
            Arg.Any<IReadOnlyDictionary<string, string>>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>Verifies known protected metadata opens a synthetic slate without consuming a physical source.</summary>
    [Fact]
    public async Task OpenAsync_KnownProtectedStreamSlate_ReturnsSyntheticMediaWithoutAcquisition()
    {
        // Arrange
        var virtualId = VirtualDeviceId.New();
        var route = CreateRoute(virtualId, protectedContent: true);
        var routes = Substitute.For<IStreamingRouteResolver>();
        routes.ResolveVirtualAsync(virtualId, "112.1", Arg.Any<CancellationToken>()).Returns(route);
        var settings = Substitute.For<ILineupSettingsService>();
        settings.Settings.Returns(new LineupSettings { ProtectedContentMode = ProtectedContentMode.StreamSlate });
        var slateStream = new MemoryStream([1, 2, 3]);
        var slates = Substitute.For<IChannelSlateStreamFactory>();
        slates.OpenMpegTs("112.1").Returns(slateStream);
        var multiplexer = Substitute.For<ITunerStreamMultiplexer>();
        var service = new VirtualMpegTsOutputService(
            routes,
            settings,
            Substitute.For<IMediaProbeService>(),
            multiplexer,
            slates,
            NullLogger<VirtualMpegTsOutputService>.Instance);

        // Act
        await using var output = await service.OpenAsync(virtualId, "112.1", TestContext.Current.CancellationToken);

        // Assert
        Assert.Same(slateStream, output.Stream);
        Assert.Equal(["h264", "aac"], output.Source.Tracks.Select(track => track.Codec));
        Assert.Equal("Protected content slate", output.Flow.SyntheticSource);
        Assert.Equal(virtualId, output.Flow.VirtualDeviceId);
        await multiplexer.DidNotReceive().SubscribeFirstAvailableAsync(Arg.Any<ResolvedStreamingRoute>(), Arg.Any<CancellationToken>());
    }

    /// <summary>Verifies live protection applies the slate policy only after failover and preserves cancellation and explicit failures.</summary>
    /// <param name="physicalOutput">Whether to open physical-stage output.</param>
    /// <param name="outcome">The requested policy or terminal failure.</param>
    [Theory]
    [InlineData(false, "slate")]
    [InlineData(true, "slate")]
    [InlineData(false, "error")]
    [InlineData(true, "error")]
    [InlineData(false, "mixed")]
    [InlineData(true, "mixed")]
    [InlineData(false, "canceled")]
    [InlineData(true, "canceled")]
    [InlineData(false, "slate-failed")]
    [InlineData(true, "slate-failed")]
    public async Task OpenAsync_LiveProtection_PreservesPolicyAndFailureSemantics(bool physicalOutput, string outcome)
    {
        // Arrange
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var virtualId = VirtualDeviceId.New();
        var route = CreateFailingRoute(
            virtualId,
            outcome == "mixed" ? PhysicalStreamFailureCategory.Unavailable : PhysicalStreamFailureCategory.ProtectedContent,
            outcome == "canceled" ? cancellation.Cancel : null);
        var routes = Substitute.For<IStreamingRouteResolver>();
        routes.ResolveVirtualAsync(virtualId, "112.1", Arg.Any<CancellationToken>()).Returns(route);
        var settings = Substitute.For<ILineupSettingsService>();
        settings.Settings.Returns(new LineupSettings
        {
            ProtectedContentMode = outcome == "error" ? ProtectedContentMode.ReturnError : ProtectedContentMode.StreamSlate
        });
        var capacity = new PhysicalCapacityLeaseRegistry();
        var graph = new StreamingSessionGraph(capacity, NullLogger<StreamingSessionGraph>.Instance);
        var multiplexer = new TunerStreamMultiplexer(graph, Substitute.For<IMediaProcessFactory>(), NullLogger<TunerStreamMultiplexer>.Instance);
        var probe = Substitute.For<IMediaProbeService>();
        using var slate = new MemoryStream([1, 2, 3]);
        var slateFailure = new MpegTsTranscodeException("Controlled slate failure.", new InvalidOperationException());
        var slates = Substitute.For<IChannelSlateStreamFactory>();
        slates.OpenMpegTs("112.1").Returns(_ => outcome == "slate-failed" ? throw slateFailure : slate);
        var service = new VirtualMpegTsOutputService(routes, settings, probe, multiplexer, slates, NullLogger<VirtualMpegTsOutputService>.Instance);
        VirtualMpegTsOutput? output = null;
        try
        {
            // Act
            var failure = await Record.ExceptionAsync(async () =>
            {
                output = physicalOutput
                    ? await service.OpenPhysicalOutputAsync(virtualId, "112.1", cancellation.Token)
                    : await service.OpenAsync(virtualId, "112.1", cancellation.Token);
            });

            // Assert
            foreach (var candidate in route.Candidates)
            {
                await candidate.PhysicalDevice.Received(1).OpenStreamAsync(
                    Arg.Is<PhysicalStreamRequest>(request => request.ChannelId == candidate.SourceChannelId), Arg.Any<CancellationToken>());
            }
            Assert.Empty(capacity.GetDiagnostics());
            Assert.Empty(probe.ReceivedCalls());
            if (outcome == "slate")
            {
                Assert.Null(failure);
                Assert.NotNull(output);
                Assert.Same(slate, output.Stream);
                Assert.Equal(["h264", "aac"], output.Source.Tracks.Select(track => track.Codec));
                Assert.Same(output.RawSource, output.Source);
                Assert.Equal(route.VirtualProfile, output.Profile);
                Assert.Equal(virtualId, output.Flow.VirtualDeviceId);
                Assert.Equal("Protected content slate", output.Flow.SyntheticSource);
                Assert.Null(output.Flow.PhysicalDeviceId);
                Assert.Null(output.Flow.SourceChannelId);
                await output.DisposeAsync();
                Assert.False(slate.CanRead);
            }
            else if (outcome == "canceled")
            {
                Assert.IsAssignableFrom<OperationCanceledException>(failure);
            }
            else if (outcome == "slate-failed")
            {
                Assert.Same(slateFailure, failure);
            }
            else
            {
                var acquisition = Assert.IsType<StreamAcquisitionException>(failure);
                Assert.Equal(outcome == "mixed" ? PhysicalStreamFailureCategory.Unavailable : PhysicalStreamFailureCategory.ProtectedContent, acquisition.Category);
                Assert.Equal(2, acquisition.CandidateFailures.Count);
                Assert.Equal(outcome == "mixed", acquisition.Retryable);
            }
            slates.Received(outcome is "slate" or "slate-failed" ? 1 : 0).OpenMpegTs("112.1");
        }
        finally
        {
            if (output is not null)
            {
                await output.DisposeAsync();
            }
            await graph.StopAsync(CancellationToken.None);
        }
    }

    private static ResolvedStreamingRoute CreateFailingRoute(VirtualDeviceId virtualId, PhysicalStreamFailureCategory fallbackFailure, Action? beforeLastFailure = null)
    {
        var route = CreateRoute(virtualId, protectedContent: false);
        var candidates = new[] { route.Candidates[0], CreateRoute(virtualId, protectedContent: false).Candidates[0] };
        for (var index = 0; index < candidates.Length; index++)
        {
            var candidate = candidates[index];
            var last = index == candidates.Length - 1;
            var category = last ? fallbackFailure : PhysicalStreamFailureCategory.ProtectedContent;
            candidate.PhysicalDevice.OpenStreamAsync(Arg.Any<PhysicalStreamRequest>(), Arg.Any<CancellationToken>())
                .Returns<ValueTask<IPhysicalStreamLease>>(_ =>
                {
                    if (last)
                    {
                        beforeLastFailure?.Invoke();
                    }
                    throw new PhysicalStreamAcquisitionException(
                        category,
                        category == PhysicalStreamFailureCategory.ProtectedContent ? "protected_content" : "source_unavailable",
                        "Controlled physical source failure.",
                        category != PhysicalStreamFailureCategory.ProtectedContent,
                        candidate.SourceChannelId.PhysicalDeviceId,
                        candidate.SourceChannelId);
                });
        }
        return route with { Candidates = candidates, SourceCandidates = candidates.Select(candidate => candidate.SourceChannelId).ToArray() };
    }

    private static ResolvedStreamingRoute CreateRoute(VirtualDeviceId virtualId, bool protectedContent)
    {
        var physicalId = PhysicalDeviceId.New();
        var sourceChannel = new SourceChannelId(physicalId, "112.1");
        var physicalDevice = Substitute.For<IPhysicalTunerDevice>();
        physicalDevice.Snapshot.Returns(new PhysicalDeviceSnapshot
        {
            Id = physicalId,
            Kind = "hdhomerun",
            Name = "Antenna",
            Capacity = new PhysicalTunerCapacity(4)
        });
        var acquisition = new StreamAcquisitionDescriptor(
            physicalId,
            new PhysicalAcquisitionKey(sourceChannel, "none"),
            new PhysicalTunerCapacity(4));
        var candidate = new ResolvedStreamingCandidate
        {
            PhysicalDevice = physicalDevice,
            SourceChannelId = sourceChannel,
            Acquisition = acquisition,
            PhysicalProfile = new PhysicalTranscodeProfile(),
            DiagnosticUri = new Uri("http://device.local/auto/v112.1"),
            SourceMetadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["drm"] = protectedContent.ToString() }
        };
        return new ResolvedStreamingRoute
        {
            VirtualDevice = new VirtualDeviceDefinition
            {
                Id = virtualId,
                Name = "Protected output",
                Adapter = new AdapterConfiguration("test", JsonSerializer.SerializeToElement(new { }))
            },
            PhysicalDevice = physicalDevice,
            SourceChannelId = sourceChannel,
            SourceCandidates = [sourceChannel],
            DiagnosticUri = candidate.DiagnosticUri,
            Acquisition = acquisition,
            Candidates = [candidate],
            PhysicalProfile = candidate.PhysicalProfile,
            VirtualProfile = new VirtualTranscodeProfile()
        };
    }

    private sealed class CorrelatedStream(string producerNodeId) : MemoryStream, IStreamProducerCorrelation
    {
        /// <summary>Gets the producer node ID.</summary>
        public string ProducerNodeId { get; } = producerNodeId;
    }

    private sealed class ReservationStream : MemoryStream
    {
        /// <summary>Gets the started.</summary>
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>Gets or sets the stopped.</summary>
        public bool Stopped { get; private set; }
        /// <summary>Gets or sets the destination.</summary>
        public Stream? Destination { get; private set; }
        /// <summary>Gets or sets the fail on cancel.</summary>
        public bool FailOnCancel { get; init; }

        /// <inheritdoc />
        public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            Destination = destination;
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally
            {
                Stopped = true;
                if (FailOnCancel)
                {
                    throw new IOException("Controlled reservation failure.");
                }
            }
        }
    }
}
