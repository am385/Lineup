using System.Text.Json;
using NSubstitute;
using Xunit;

namespace Lineup.App.Web.Tests.Services;

/// <summary>Verifies configured and runtime streaming-flow projection.</summary>
public sealed class StreamingFlowSnapshotServiceTests
{
    /// <summary>Verifies each path follows its exact producer ancestry rather than borrowing another channel's encoder on the same device.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GetSnapshot_SourceAndOutputEncoders_RemainStageAndChannelSpecific(bool sourceFallback)
    {
        // Arrange
        var physicalId = PhysicalDeviceId.New();
        var virtualId = VirtualDeviceId.New();
        var sourceEncoding = new VideoEncodingSnapshot(sourceFallback ? "libx264" : "h264_amf", HardwareVideoEncoder.Amf, sourceFallback);
        var copyEncoding = new VideoEncodingSnapshot("copy", HardwareVideoEncoder.Nvenc, false);
        var gpuOutputEncoding = new VideoEncodingSnapshot("h264_qsv", HardwareVideoEncoder.Qsv, false);
        var first = ActiveStream("first", "localhost", physicalId, virtualId, new(physicalId, "7.1"));
        var second = ActiveStream("second", "localhost", physicalId, virtualId, new(physicalId, "9.1"));
        first = first with { Flow = first.Flow! with { ProducerNodeId = "output-a" } };
        second = second with { Flow = second.Flow! with { ProducerNodeId = "output-b" } };
        var settings = Substitute.For<ILineupSettingsService>();
        settings.Settings.Returns(new LineupSettings { Streaming = Configuration(physicalId, virtualId) });
        var graph = Substitute.For<IStreamingSessionGraph>();
        graph.GetDiagnostics().Returns(new StreamingSessionGraphDiagnostics(
        [
            new("acquisition", "a", 1, true) { NodeId = "a", PhysicalDeviceId = physicalId, SourceChannelId = new(physicalId, "7.1") },
            new("transform", "physical-a", 1, true) { NodeId = "physical-a", ParentNodeId = "a", Stage = "source-normalization", VideoEncoding = sourceEncoding },
            new("transform", "output-a", 1, true) { NodeId = "output-a", ParentNodeId = "physical-a", Stage = "virtual-mpegts", VideoEncoding = copyEncoding },
            new("acquisition", "b", 1, true) { NodeId = "b", PhysicalDeviceId = physicalId, SourceChannelId = new(physicalId, "9.1") },
            new("transform", "output-b", 1, true) { NodeId = "output-b", ParentNodeId = "b", Stage = "virtual-mpegts", VideoEncoding = gpuOutputEncoding }
        ], []));
        var streams = Substitute.For<IActiveStreamRegistry>();
        streams.GetActiveStreams().Returns([first, second]);
        var devices = Substitute.For<IPhysicalDeviceStatusService>();
        devices.Devices.Returns([]);
        var service = new StreamingFlowSnapshotService(settings, graph, [], streams, devices);

        // Act
        var snapshot = service.GetSnapshot();

        // Assert
        var firstPath = snapshot.ActivePaths.Single(path => path.SessionId == "first");
        var secondPath = snapshot.ActivePaths.Single(path => path.SessionId == "second");
        Assert.Equal(sourceEncoding, firstPath.Stages.Single(stage => stage.Kind == StreamingFlowStageKind.SourceProcessing).VideoEncoding);
        Assert.Equal(copyEncoding, firstPath.Stages.Single(stage => stage.Kind == StreamingFlowStageKind.OutputProcessing).VideoEncoding);
        var sourceCopy = secondPath.Stages.Single(stage => stage.Kind == StreamingFlowStageKind.SourceProcessing);
        Assert.Equal("copy", sourceCopy.VideoEncoding!.Encoder);
        Assert.Equal("Pass-through", sourceCopy.Label);
        Assert.Equal(gpuOutputEncoding, secondPath.Stages.Single(stage => stage.Kind == StreamingFlowStageKind.OutputProcessing).VideoEncoding);
        var physicalNode = snapshot.Nodes.Single(node => node.Lane == StreamingFlowLane.PhysicalProcessing);
        Assert.Contains(sourceEncoding, physicalNode.VideoEncodings);
        Assert.Contains(sourceCopy.VideoEncoding, physicalNode.VideoEncodings);
        Assert.Equal(2, physicalNode.VideoEncodings.Count);
        Assert.Equal(2, snapshot.Nodes.Single(node => node.Lane == StreamingFlowLane.VirtualProcessing).VideoEncodings.Count);
    }

    /// <summary>Verifies encoder badges describe actual runtime outcomes rather than a configured GPU preference.</summary>
    [Theory]
    [InlineData("h264_amf", false)]
    [InlineData("libx264", true)]
    [InlineData("copy", false)]
    [InlineData(null, false)]
    public void GetSnapshot_ReportedEncoder_ProjectsActualOutcomeWithoutGuessing(string? encoder, bool fallback)
    {
        // Arrange
        var physicalId = PhysicalDeviceId.New();
        var virtualId = VirtualDeviceId.New();
        var configuration = Configuration(physicalId, virtualId) with
        {
            VirtualDefaults = new() { HardwareEncoding = new() { Encoder = HardwareVideoEncoder.Amf } }
        };
        var settings = Substitute.For<ILineupSettingsService>();
        settings.Settings.Returns(new LineupSettings { Streaming = configuration });
        var graph = Substitute.For<IStreamingSessionGraph>();
        graph.GetDiagnostics().Returns(new StreamingSessionGraphDiagnostics([], []));
        var stream = ActiveStream("one", "127.0.0.1", physicalId, virtualId, new SourceChannelId(physicalId, "7.1")) with
        {
            RequestedVideoEncoder = HardwareVideoEncoder.Amf,
            ActiveVideoEncoder = encoder,
            HardwareFallbackApplied = fallback,
            VideoProcessing = new(SourceDeinterlacePolicy.SourceFieldRate, true, new(60000, 1001), 120, false)
        };
        var activeStreams = Substitute.For<IActiveStreamRegistry>();
        activeStreams.GetActiveStreams().Returns([stream, stream with { SessionId = "two" }]);
        var devices = Substitute.For<IPhysicalDeviceStatusService>();
        devices.Devices.Returns([]);
        var service = new StreamingFlowSnapshotService(settings, graph, [], activeStreams, devices);

        // Act
        var snapshot = service.GetSnapshot();

        // Assert
        var encodings = snapshot.Nodes.Single(node => node.Lane == StreamingFlowLane.VirtualProcessing).VideoEncodings;
        var stage = snapshot.ActivePaths[0].Stages.Single(stage => stage.Kind == StreamingFlowStageKind.OutputProcessing);
        Assert.All(snapshot.Nodes.Where(node => node.Lane == StreamingFlowLane.PhysicalProcessing), node => Assert.Empty(node.VideoEncodings));
        if (encoder is null)
        {
            Assert.Empty(encodings);
            Assert.Null(stage.VideoEncoding);
        }
        else
        {
            var actual = Assert.Single(encodings);
            Assert.Equal(encoder, actual.Encoder);
            Assert.Equal(fallback, actual.SoftwareFallbackApplied);
            Assert.Equal(HardwareVideoEncoder.Amf, actual.RequestedEncoder);
            Assert.Equal(actual, stage.VideoEncoding);
            Assert.Equal(stream.VideoProcessing, actual.Processing);
        }
    }

    /// <summary>Verifies shared consumers and physical/output transforms are projected onto configured devices.</summary>
    [Fact]
    public void GetSnapshot_SharedTranscodedPath_ProjectsDeviceAndRuntimeState()
    {
        // Arrange
        var physicalId = PhysicalDeviceId.New();
        var virtualId = VirtualDeviceId.New();
        var sourceChannel = new SourceChannelId(physicalId, "7.1");
        var configuration = Configuration(physicalId, virtualId);
        var settings = Substitute.For<ILineupSettingsService>();
        settings.Settings.Returns(new LineupSettings { Streaming = configuration });
        var graph = Substitute.For<IStreamingSessionGraph>();
        graph.GetDiagnostics().Returns(new StreamingSessionGraphDiagnostics(
            [
                new StreamNodeDiagnostics("acquisition", "source", 1, true)
                {
                    NodeId = "source",
                    PhysicalDeviceId = physicalId,
                    SourceChannelId = sourceChannel
                },
                new StreamNodeDiagnostics("transform", "source>source-normalization", 1, true)
                {
                    NodeId = "physical-transform",
                    ParentNodeId = "source",
                    Stage = "source-normalization"
                },
                new StreamNodeDiagnostics("transform", "source>source-normalization>virtual-mpegts", 2, true, false, 2)
                {
                    NodeId = "output",
                    ParentNodeId = "physical-transform",
                    Stage = "virtual-mpegts"
                }
            ],
            [new PhysicalCapacityDiagnostics(physicalId, 4, 1, 2)]));
        var activeStreams = Substitute.For<IActiveStreamRegistry>();
        activeStreams.GetActiveStreams().Returns(
        [
            ActiveStream("one", "192.0.2.10:5000", physicalId, virtualId, sourceChannel),
            ActiveStream("two", "192.0.2.11:5001", physicalId, virtualId, sourceChannel)
        ]);
        var cmaf = Substitute.For<ICmafOutputCoordinator>();
        cmaf.GetDiagnostics().Returns([]);
        cmaf.GetProducerDiagnostics().Returns(new StreamingProducerDiagnostics(0, 0, 0));
        var devices = Substitute.For<IPhysicalDeviceStatusService>();
        devices.Devices.Returns([]);
        var service = new StreamingFlowSnapshotService(settings, graph, [cmaf], activeStreams, devices);

        // Act
        var snapshot = service.GetSnapshot();

        // Assert
        Assert.Equal(2, snapshot.ActiveConsumerCount);
        Assert.Equal(2, snapshot.ActiveProducerCount);
        Assert.Equal(StreamingFlowState.Shared, snapshot.Nodes.Single(node => node.Id == $"physical:{physicalId}").State);
        Assert.Equal(StreamingFlowState.Transcoding, snapshot.Nodes.Single(node => node.Id == $"physical-processing:{physicalId}").State);
        Assert.Equal(StreamingFlowState.Shared, snapshot.Nodes.Single(node => node.Id == "multiplexer").State);
        Assert.Equal(StreamingFlowState.Shared, snapshot.Nodes.Single(node => node.Id == $"virtual:{virtualId}").State);
        Assert.Equal(StreamingFlowState.Transcoding, snapshot.Nodes.Single(node => node.Id == $"virtual-processing:{virtualId}").State);
        Assert.Equal(2, snapshot.ActivePaths.Count);
        Assert.Equal(["path:one", "path:two"], snapshot.ActivePaths.Select(path => path.Id).OrderBy(id => id, StringComparer.Ordinal));
        Assert.All(snapshot.ActivePaths, path =>
        {
            Assert.True(path.IsShared);
            Assert.Equal(2, path.SharedReceiverCount);
            Assert.Equal("7.1", path.Channel);
            Assert.Equal(6, path.Stages.Count);
            Assert.Equal(Enum.GetValues<StreamingFlowStageKind>(), path.Stages.Select(stage => stage.Kind));
            Assert.Equal("mpeg2video", path.Stages.Single(stage => stage.Kind == StreamingFlowStageKind.SourceProcessing).InputMedia!.Tracks[0].Codec);
            Assert.Equal("hevc", path.Stages.Single(stage => stage.Kind == StreamingFlowStageKind.SourceProcessing).OutputMedia!.Tracks[0].Codec);
            Assert.Equal("h264", path.Stages.Single(stage => stage.Kind == StreamingFlowStageKind.OutputProcessing).OutputMedia!.Tracks[0].Codec);
            var multiplexer = path.Stages.Single(stage => stage.Kind == StreamingFlowStageKind.Multiplexer);
            Assert.Equal("Reusable output 01234567...", multiplexer.Subtitle);
            Assert.Equal(
                "0123456789abcdef0123456789abcdef",
                multiplexer.Details.Single(detail => detail.Label == "Reusable output").Value);
        });
    }

    /// <summary>Verifies retained producers are shown as warm even when no logical consumers remain.</summary>
    [Fact]
    public void GetSnapshot_WarmProducerWithoutConsumers_ProjectsWarmState()
    {
        // Arrange
        var physicalId = PhysicalDeviceId.New();
        var virtualId = VirtualDeviceId.New();
        var sourceChannel = new SourceChannelId(physicalId, "9.1");
        var settings = Substitute.For<ILineupSettingsService>();
        settings.Settings.Returns(new LineupSettings { Streaming = Configuration(physicalId, virtualId) });
        var graph = Substitute.For<IStreamingSessionGraph>();
        graph.GetDiagnostics().Returns(new StreamingSessionGraphDiagnostics(
            [
                new StreamNodeDiagnostics("acquisition", "source", 1, true)
                {
                    NodeId = "source",
                    PhysicalDeviceId = physicalId,
                    SourceChannelId = sourceChannel
                },
                new StreamNodeDiagnostics("transform", "source>virtual-mpegts", 0, true, true, 1)
                {
                    NodeId = "warm-output",
                    ParentNodeId = "source",
                    Stage = "virtual-mpegts"
                }
            ],
            [new PhysicalCapacityDiagnostics(physicalId, 4, 1, 1)]));
        var activeStreams = Substitute.For<IActiveStreamRegistry>();
        activeStreams.GetActiveStreams().Returns([]);
        var cmaf = Substitute.For<ICmafOutputCoordinator>();
        cmaf.GetDiagnostics().Returns([
            new CmafOutputDiagnostics(StreamSessionId.New(), new StreamTransformKey("web-player-cmaf", "plan"), 0, true)
        ]);
        cmaf.GetProducerDiagnostics().Returns(new StreamingProducerDiagnostics(0, 1, 1));
        var devices = Substitute.For<IPhysicalDeviceStatusService>();
        devices.Devices.Returns([]);
        var service = new StreamingFlowSnapshotService(settings, graph, [cmaf], activeStreams, devices);

        // Act
        var snapshot = service.GetSnapshot();

        // Assert
        Assert.Equal(0, snapshot.ActiveProducerCount);
        Assert.Equal(2, snapshot.WarmProducerCount);
        Assert.Equal(StreamingFlowState.Warm, snapshot.Nodes.Single(node => node.Id == "multiplexer").State);
        Assert.Equal(StreamingFlowState.Warm, snapshot.Nodes.Single(node => node.Id == $"physical:{physicalId}").State);
        Assert.Empty(snapshot.ActivePaths);
    }

    private static StreamingConfiguration Configuration(PhysicalDeviceId physicalId, VirtualDeviceId virtualId) =>
        new()
        {
            PhysicalDefaults = new PhysicalTranscodeProfile { ConvertHevcToH264 = true },
            VirtualDefaults = new VirtualTranscodeProfile { Audio = OutputAudioPolicy.Ac3 },
            PhysicalDevices =
            [
                new PhysicalDeviceDefinition
                {
                    Id = physicalId,
                    Name = "Roof Antenna",
                    Adapter = new AdapterConfiguration("hdhomerun", JsonSerializer.SerializeToElement(new { }))
                }
            ],
            VirtualDevices =
            [
                new VirtualDeviceDefinition
                {
                    Id = virtualId,
                    Name = "Living Room DVR",
                    Adapter = new AdapterConfiguration("hdhomerun", JsonSerializer.SerializeToElement(new { }))
                }
            ]
        };

    private static ActiveStreamSnapshot ActiveStream(string sessionId, string endpoint, PhysicalDeviceId physicalId, VirtualDeviceId virtualId, SourceChannelId sourceChannel) =>
        new(
            sessionId,
            "7.1",
            HostedStreamFormat.MpegTs,
            DateTime.UtcNow,
            null,
            [new ActiveStreamTrack(MediaTrackType.Video, "hevc", "h264", null, null, 1920, 1080, null, null, null)])
        {
            ClientAddress = endpoint,
            PhysicalInput = Media("mpeg2video"),
            PhysicalOutput = Media("hevc"),
            VirtualOutput = Media("h264"),
            Flow = new ActiveStreamFlowContext
            {
                PhysicalDeviceId = physicalId,
                SourceChannelId = sourceChannel,
                VirtualDeviceId = virtualId,
                ProducerNodeId = "output",
                ReusableOutputId = new StreamSessionId(Guid.ParseExact("0123456789abcdef0123456789abcdef", "N"))
            }
        };

    private static ActiveStreamMediaSnapshot Media(string codec) =>
        new(null, [new ActiveStreamMediaTrack(0, MediaTrackType.Video, codec, null, 1920, 1080, null, null)]);
}
