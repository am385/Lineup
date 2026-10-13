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
            DiagnosticUri = new Uri("http://device.local/auto/v2.3")
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
        probe.ProbeAsync(acquisition, Arg.Any<StreamAcquisitionFactory>(), candidate.DiagnosticUri.AbsoluteUri, Arg.Any<CancellationToken>())
            .Returns(source);
        var multiplexer = Substitute.For<ITunerStreamMultiplexer, IEffectiveTunerStreamMultiplexer>();
        multiplexer.SubscribeFirstAvailableAsync(route, Arg.Any<CancellationToken>())
            .Returns(new ResolvedTunerStream(candidate, new MemoryStream([1])));
        var effective = (IEffectiveTunerStreamMultiplexer)multiplexer;
        effective.SubscribeMpegTsAsync(
                acquisition,
                Arg.Any<StreamAcquisitionFactory>(),
                candidate.DiagnosticUri.AbsoluteUri,
                source,
                candidate.PhysicalProfile,
                route.VirtualProfile,
                settings.Settings,
                TimeSpan.Zero,
                Arg.Any<CancellationToken>())
            .Returns(new CorrelatedStream("virtual-output-node"));
        effective.GetEffectiveSource(source, candidate.PhysicalProfile).Returns(effectiveSource);
        var service = new VirtualMpegTsOutputService(routes, settings, probe, multiplexer, NullLogger<VirtualMpegTsOutputService>.Instance);

        // Act
        await using var output = await service.OpenAsync(virtualId, "2.3", TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(physicalId, output.Flow.PhysicalDeviceId);
        Assert.Equal(sourceChannel, output.Flow.SourceChannelId);
        Assert.Equal(virtualId, output.Flow.VirtualDeviceId);
        Assert.Equal("virtual-output-node", output.Flow.ProducerNodeId);
        Assert.Same(source, output.RawSource);
        Assert.Same(effectiveSource, output.Source);
    }

    private sealed class CorrelatedStream(string producerNodeId) : MemoryStream, IStreamProducerCorrelation
    {
        public string ProducerNodeId { get; } = producerNodeId;
    }
}
