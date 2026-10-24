using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Lineup.Web.Tests.Services;

/// <summary>Verifies the Web transport adapter preserves Core graph ownership and effective-source behavior.</summary>
public sealed class TunerStreamMultiplexerTests
{
    /// <summary>Verifies subscriptions delegate acquisition identity and ownership to the Core graph.</summary>
    [Fact]
    public async Task SubscribeAsync_Descriptor_UsesGraphAndDisposesLease()
    {
        // Arrange
        var graph = Substitute.For<IStreamingSessionGraph>();
        var lease = Substitute.For<IStreamSessionLease>();
        lease.Stream.Returns(new MemoryStream([1, 2, 3]));
        lease.ProducerNodeId.Returns("producer-node");
        graph.AcquireAsync(Arg.Any<StreamAcquisitionDescriptor>(), Arg.Any<StreamAcquisitionFactory>(), Arg.Any<CancellationToken>())
            .Returns(lease);
        var multiplexer = CreateMultiplexer(graph);
        var descriptor = CreateDescriptor();
        StreamAcquisitionFactory factory = _ => throw new InvalidOperationException();

        // Act
        string producerNodeId;
        await using (var stream = await multiplexer.SubscribeAsync(descriptor, factory, TestContext.Current.CancellationToken))
        {
            Assert.Equal(1, stream.ReadByte());
            producerNodeId = Assert.IsAssignableFrom<IStreamProducerCorrelation>(stream).ProducerNodeId;
        }

        // Assert
        await graph.Received(1).AcquireAsync(descriptor, factory, Arg.Any<CancellationToken>());
        await lease.Received(1).DisposeAsync();
        Assert.Equal("producer-node", producerNodeId);
    }

    /// <summary>Verifies progressive effective sources use the unmodified shared acquisition.</summary>
    [Fact]
    public async Task SubscribeEffectiveAsync_ProgressiveSource_UsesRawGraphAcquisition()
    {
        // Arrange
        var graph = Substitute.For<IStreamingSessionGraph>();
        var lease = Substitute.For<IStreamSessionLease>();
        lease.Stream.Returns(new MemoryStream());
        graph.AcquireAsync(Arg.Any<StreamAcquisitionDescriptor>(), Arg.Any<StreamAcquisitionFactory>(), Arg.Any<CancellationToken>())
            .Returns(lease);
        var multiplexer = CreateMultiplexer(graph);
        var source = new MediaProbeResult(
            [new MediaTrackMetadata(0, MediaTrackType.Video, "h264", null, 1920, 1080, null, null) { ScanType = VideoScanType.Progressive }],
            null);
        var profile = new PhysicalTranscodeProfile();

        // Act
        await using var stream = await multiplexer.SubscribeEffectiveAsync(
            CreateDescriptor(),
            _ => throw new InvalidOperationException(),
            "source",
            source,
            profile,
            new LineupSettings(),
            TestContext.Current.CancellationToken);

        // Assert
        await graph.Received(1).AcquireAsync(
            Arg.Any<StreamAcquisitionDescriptor>(),
            Arg.Any<StreamAcquisitionFactory>(),
            Arg.Any<CancellationToken>());
        await graph.DidNotReceive().AcquireTransformAsync(
            Arg.Any<StreamAcquisitionDescriptor>(),
            Arg.Any<StreamTransformKey>(),
            Arg.Any<StreamAcquisitionFactory>(),
            Arg.Any<IStreamTransform>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>Verifies effective metadata reflects requested physical normalization.</summary>
    [Fact]
    public void GetEffectiveSource_InterlacedNormalization_ReturnsProgressiveH264()
    {
        // Arrange
        var multiplexer = CreateMultiplexer(Substitute.For<IStreamingSessionGraph>());
        var source = new MediaProbeResult(
            [new MediaTrackMetadata(0, MediaTrackType.Video, "mpeg2video", null, 1920, 1080, null, null) { ScanType = VideoScanType.Interlaced }],
            null);
        var profile = new PhysicalTranscodeProfile { Deinterlace = SourceDeinterlacePolicy.SourceFrameRate };

        // Act
        var effective = multiplexer.GetEffectiveSource(source, profile);

        // Assert
        var video = Assert.Single(effective.Tracks);
        Assert.Equal(VideoScanType.Progressive, video.ScanType);
        Assert.Equal("h264", video.Codec);
    }

    /// <summary>Verifies MPEG-TS subscriptions build one ordered graph pipeline and pass the configured warm period.</summary>
    [Fact]
    public async Task SubscribeMpegTsAsync_PhysicalAndVirtualTransforms_UsesOrderedPipeline()
    {
        // Arrange
        var graph = Substitute.For<IStreamingSessionGraph>();
        var lease = Substitute.For<IStreamSessionLease>();
        lease.Stream.Returns(new MemoryStream());
        IReadOnlyList<StreamTransformStage>? capturedStages = null;
        graph.AcquirePipelineAsync(
                Arg.Any<StreamAcquisitionDescriptor>(),
                Arg.Any<StreamAcquisitionFactory>(),
                Arg.Do<IReadOnlyList<StreamTransformStage>>(stages => capturedStages = stages),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(lease);
        var multiplexer = CreateMultiplexer(graph);
        var source = new MediaProbeResult(
        [
            new MediaTrackMetadata(0, MediaTrackType.Video, "h264", null, 1920, 1080, null, null) { ScanType = VideoScanType.Progressive },
            new MediaTrackMetadata(1, MediaTrackType.Audio, "ac4", null, null, null, 6, 48_000)
        ], null);
        var physicalProfile = new PhysicalTranscodeProfile { Ac4Audio = OutputAudioPolicy.Ac3 };
        var keepWarm = TimeSpan.FromSeconds(15);

        // Act
        await using var stream = await multiplexer.SubscribeMpegTsAsync(
            CreateDescriptor(),
            _ => throw new InvalidOperationException(),
            "source",
            source,
            physicalProfile,
            new VirtualTranscodeProfile { Ac4Audio = OutputAudioPolicy.Ac3 },
            new LineupSettings(),
            keepWarm,
            TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(capturedStages);
        Assert.Equal(["source-normalization", "virtual-mpegts"], capturedStages.Select(stage => stage.Key.Stage));
        await graph.Received(1).AcquirePipelineAsync(
            Arg.Any<StreamAcquisitionDescriptor>(),
            Arg.Any<StreamAcquisitionFactory>(),
            Arg.Any<IReadOnlyList<StreamTransformStage>>(),
            keepWarm,
            Arg.Any<CancellationToken>());
    }

    /// <summary>Verifies host shutdown delegates ordered graph cleanup.</summary>
    [Fact]
    public async Task StopAsync_DelegatesToGraph()
    {
        // Arrange
        var graph = Substitute.For<IStreamingSessionGraph>();
        var multiplexer = CreateMultiplexer(graph);

        // Act
        await multiplexer.StopAsync(TestContext.Current.CancellationToken);

        // Assert
        await graph.Received(1).StopAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>Verifies acquisition failures propagate without manufacturing a transport stream.</summary>
    [Fact]
    public async Task SubscribeAsync_GraphFailure_Propagates()
    {
        // Arrange
        var graph = Substitute.For<IStreamingSessionGraph>();
        graph.AcquireAsync(Arg.Any<StreamAcquisitionDescriptor>(), Arg.Any<StreamAcquisitionFactory>(), Arg.Any<CancellationToken>())
            .Returns(_ => ValueTask.FromException<IStreamSessionLease>(new IOException("source failed")));
        var multiplexer = CreateMultiplexer(graph);

        // Act
        var operation = multiplexer.SubscribeAsync(
            CreateDescriptor(),
            _ => throw new InvalidOperationException(),
            TestContext.Current.CancellationToken).AsTask();

        // Assert
        await Assert.ThrowsAsync<IOException>(() => operation);
    }

    /// <summary>Verifies progressive effective metadata is preserved without allocation or rewriting.</summary>
    [Fact]
    public void GetEffectiveSource_ProgressiveInput_ReturnsOriginalMetadata()
    {
        // Arrange
        var multiplexer = CreateMultiplexer(Substitute.For<IStreamingSessionGraph>());
        var source = new MediaProbeResult(
            [new MediaTrackMetadata(0, MediaTrackType.Video, "h264", 2_000_000, 1280, 720, null, null) { ScanType = VideoScanType.Progressive }],
            2_000_000);

        // Act
        var effective = multiplexer.GetEffectiveSource(source, new PhysicalTranscodeProfile());

        // Assert
        Assert.Same(source, effective);
    }

    private static TunerStreamMultiplexer CreateMultiplexer(IStreamingSessionGraph graph) =>
        new(graph, Substitute.For<IMediaProcessFactory>(), NullLogger<TunerStreamMultiplexer>.Instance);

    private static StreamAcquisitionDescriptor CreateDescriptor()
    {
        var deviceId = PhysicalDeviceId.New();
        return new StreamAcquisitionDescriptor(
            deviceId,
            new PhysicalAcquisitionKey(new SourceChannelId(deviceId, "7.1"), "none"),
            new PhysicalTunerCapacity(1));
    }
}
