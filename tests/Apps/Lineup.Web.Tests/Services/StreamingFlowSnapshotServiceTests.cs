using System.Text.Json;
using NSubstitute;
using Xunit;

namespace Lineup.Web.Tests.Services;

/// <summary>Verifies configured and runtime streaming-flow projection.</summary>
public sealed class StreamingFlowSnapshotServiceTests
{
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

    private static ActiveStreamSnapshot ActiveStream(
        string sessionId,
        string endpoint,
        PhysicalDeviceId physicalId,
        VirtualDeviceId virtualId,
        SourceChannelId sourceChannel) =>
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
