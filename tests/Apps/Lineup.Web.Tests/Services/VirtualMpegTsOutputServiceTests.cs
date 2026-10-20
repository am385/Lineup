using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Lineup.Web.Tests.Services;

/// <summary>Verifies virtual MPEG-TS output ownership and topology metadata.</summary>
public sealed class VirtualMpegTsOutputServiceTests
{
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
        public string ProducerNodeId { get; } = producerNodeId;
    }
}
