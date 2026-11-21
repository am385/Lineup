using System.Text.Json;
using NSubstitute;
using Xunit;

namespace Lineup.App.Web.Tests.Services;

/// <summary>Verifies configured and runtime streaming-flow projection.</summary>
public sealed class StreamingFlowSnapshotServiceTests
{
    /// <summary>Verifies all configured devices, no-op processors, and multiplexer connections survive an empty runtime graph.</summary>
    [Fact]
    public void GetSnapshot_IdleCardTopology_KeepsCompleteConfiguredPipeline()
    {
        // Arrange
        var physicalId = PhysicalDeviceId.New();
        var virtualId = VirtualDeviceId.New();
        var configuration = Configuration(physicalId, virtualId) with
        {
            PhysicalDefaults = new PhysicalTranscodeProfile(),
            VirtualDefaults = new VirtualTranscodeProfile()
        };
        var settings = Substitute.For<ILineupSettingsService>();
        settings.Settings.Returns(new LineupSettings { Streaming = configuration });
        var graph = Substitute.For<IStreamingSessionGraph>();
        graph.GetDiagnostics().Returns(new StreamingSessionGraphDiagnostics([], []));
        var streams = Substitute.For<IActiveStreamRegistry>();
        streams.GetActiveStreams().Returns([]);
        var devices = Substitute.For<IPhysicalDeviceStatusService>();
        devices.Devices.Returns([]);

        // Act
        var snapshot = new StreamingFlowSnapshotService(settings, graph, [], streams, devices).GetSnapshot();

        // Assert
        var topology = Assert.IsType<StreamingFlowTopology>(snapshot.Topology);
        Assert.Equal(4, topology.Nodes.Count);
        Assert.Empty(topology.Paths);
        Assert.Empty(topology.ReusableOutputs);
        Assert.All(topology.Nodes, node => Assert.Equal(StreamingFlowState.Idle, node.State));
        Assert.Equal("Pass-through", topology.Nodes.Single(node => node.Id == $"physical-processing:{physicalId}").Label);
        Assert.Equal("Pass-through", topology.Nodes.Single(node => node.Id == $"virtual-processing:{virtualId}").Label);
        Assert.Equal(2, topology.Edges.Count);
        Assert.Contains(topology.Edges, edge => edge.Source == $"physical:{physicalId}" && edge.Target == $"physical-processing:{physicalId}");
        Assert.Contains(topology.Edges, edge => edge.Source == $"virtual:{virtualId}" && edge.Target == $"virtual-processing:{virtualId}");
        Assert.DoesNotContain(topology.Nodes, node => node.Lane == StreamingFlowLane.Multiplexer);
        Assert.DoesNotContain(topology.Edges, edge => edge.Source == "multiplexer" || edge.Target == "multiplexer");
        Assert.All(topology.Edges, edge => Assert.Equal(StreamingFlowState.Idle, edge.State));
    }

    /// <summary>Verifies consumers expand the card graph through explicit copy stages without deleting idle outputs on removal.</summary>
    [Fact]
    public void GetSnapshot_CopyConsumers_ExpandsExactConnectionsAndRetainsIdleStages()
    {
        // Arrange
        var physicalId = PhysicalDeviceId.New();
        var virtualId = VirtualDeviceId.New();
        var idleVirtualId = VirtualDeviceId.New();
        var configuration = Configuration(physicalId, virtualId);
        configuration = configuration with
        {
            VirtualDevices = configuration.VirtualDevices.Concat(
                [new VirtualDeviceDefinition { Id = idleVirtualId, Name = "Idle DVR", Adapter = configuration.VirtualDevices[0].Adapter }]).ToArray()
        };
        var first = ActiveStream("first", "endpoint", physicalId, virtualId, new(physicalId, "7.1"));
        first = first with { ActiveVideoEncoder = "copy", Flow = first.Flow! with { ProducerNodeId = "source", ReusableOutputId = null } };
        var second = first with { SessionId = "second" };
        var settings = Substitute.For<ILineupSettingsService>();
        settings.Settings.Returns(new LineupSettings { Streaming = configuration });
        var graph = Substitute.For<IStreamingSessionGraph>();
        graph.GetDiagnostics().Returns(new StreamingSessionGraphDiagnostics(
            [new("acquisition", "source", 2, true) { NodeId = "source", PhysicalDeviceId = physicalId }], []));
        var streams = Substitute.For<IActiveStreamRegistry>();
        streams.GetActiveStreams().Returns([first]);
        var devices = Substitute.For<IPhysicalDeviceStatusService>();
        devices.Devices.Returns([]);
        var service = new StreamingFlowSnapshotService(settings, graph, [], streams, devices);

        // Act
        var one = service.GetSnapshot();
        streams.GetActiveStreams().Returns([first, second]);
        var two = service.GetSnapshot();
        streams.GetActiveStreams().Returns([]);
        var empty = service.GetSnapshot();

        // Assert
        Assert.Equal(7, one.Topology!.Nodes.Count);
        Assert.Equal(8, two.Topology!.Nodes.Count);
        Assert.Equal(6, empty.Topology!.Nodes.Count);
        Assert.Empty(two.Topology.ReusableOutputs);
        var sourceProcessor = Assert.Single(two.Topology.Nodes, node => node.Lane == StreamingFlowLane.PhysicalProcessing);
        Assert.Equal("source-processing:runtime:source", sourceProcessor.Id);
        var outputProcessor = Assert.Single(two.Topology.Nodes, node => node.Lane == StreamingFlowLane.VirtualProcessing && node.ActiveSessionCount > 0);
        var route = two.Topology.Paths.Single(path => path.Id == "path:first");
        Assert.Equal(
            [$"physical:{physicalId}", sourceProcessor.Id, $"virtual:{virtualId}", outputProcessor.Id, "consumer:first"],
            route.NodeIds);
        Assert.All(route.EdgeIds, id => Assert.Equal(StreamingFlowState.Active, two.Topology.Edges.Single(edge => edge.Id == id).State));
        Assert.DoesNotContain(route.EdgeIds, id => two.Topology.Edges.Single(edge => edge.Id == id).Target == "consumer:second");
        Assert.Equal("copy", Assert.Single(sourceProcessor.VideoEncodings).Encoder);
        Assert.False(outputProcessor.IsLogicalStage);
        Assert.Equal("copy", Assert.Single(outputProcessor.VideoEncodings).Encoder);
        Assert.Contains(two.Topology.Nodes, node => node.Id == $"virtual:{idleVirtualId}" && node.State == StreamingFlowState.Idle);
        Assert.Contains(two.Topology.Edges, edge => edge.Target == $"virtual-processing:{idleVirtualId}" && edge.State == StreamingFlowState.Idle);
        Assert.Empty(empty.Topology.Paths);
        Assert.Empty(empty.Topology.ReusableOutputs);
        Assert.DoesNotContain(two.Topology.Edges, edge => edge.Source == "multiplexer" || edge.Target == "multiplexer");
        Assert.All(empty.Topology.Edges, edge => Assert.Equal(StreamingFlowState.Idle, edge.State));
    }

    /// <summary>Verifies two channels have independent processors while two consumers reuse the same channel's processor.</summary>
    [Theory]
    [InlineData("h264_amf")]
    [InlineData("copy")]
    [InlineData(null)]
    public void GetSnapshot_ReusableChannelOutputs_ReportEncodersOnlyOnOutputProcessing(string? encoder)
    {
        // Arrange
        var physicalId = PhysicalDeviceId.New();
        var virtualId = VirtualDeviceId.New();
        var first = ActiveStream("first", "localhost", physicalId, virtualId, new(physicalId, "2.1")) with
        {
            Channel = "2.1",
            Format = HostedStreamFormat.Cmaf,
            ActiveVideoEncoder = encoder,
            ResourceIds = ["process-a"]
        };
        first = first with { Flow = first.Flow! with { ProducerNodeId = "source-a" } };
        var second = first with
        {
            SessionId = "second",
            Channel = "2.3",
            ResourceIds = ["process-b"],
            Flow = first.Flow with { ProducerNodeId = "source-b", ReusableOutputId = StreamSessionId.New() }
        };
        var settings = Substitute.For<ILineupSettingsService>();
        settings.Settings.Returns(new LineupSettings { Streaming = Configuration(physicalId, virtualId) });
        var graph = Substitute.For<IStreamingSessionGraph>();
        graph.GetDiagnostics().Returns(new StreamingSessionGraphDiagnostics(
        [
            new("acquisition", "source-a", 2, true) { NodeId = "source-a", PhysicalDeviceId = physicalId },
            new("acquisition", "source-b", 1, true) { NodeId = "source-b", PhysicalDeviceId = physicalId }
        ], []));
        var streams = Substitute.For<IActiveStreamRegistry>();
        streams.GetActiveStreams().Returns([first, first with { SessionId = "shared" }, second]);
        var devices = Substitute.For<IPhysicalDeviceStatusService>();
        devices.Devices.Returns([]);

        // Act
        var snapshot = new StreamingFlowSnapshotService(settings, graph, [], streams, devices).GetSnapshot();

        // Assert
        var topology = Assert.IsType<StreamingFlowTopology>(snapshot.Topology);
        var processor = topology.Nodes.Single(node => node.Id == $"output-processing:output:{first.Flow.ReusableOutputId}");
        var otherProcessor = topology.Nodes.Single(node => node.Id == $"output-processing:output:{second.Flow!.ReusableOutputId}");
        Assert.Equal(2, topology.Nodes.Count(node => node.Lane == StreamingFlowLane.VirtualProcessing));
        Assert.DoesNotContain(topology.Nodes, node => node.Id == $"virtual-processing:{virtualId}");
        Assert.False(processor.IsLogicalStage);
        Assert.Equal(encoder is null ? "Output processing" : encoder == "copy" ? "Copy / processing" : "Output Transcoder", processor.Label);
        Assert.Equal(["process-a"], processor.ResourceIds);
        Assert.Equal(["process-b"], otherProcessor.ResourceIds);
        Assert.Equal(2, processor.ActiveSessionCount);
        Assert.Equal(1, otherProcessor.ActiveSessionCount);
        Assert.Equal(["2.1"], processor.ActiveChannels);
        Assert.Equal(["2.3"], otherProcessor.ActiveChannels);
        Assert.DoesNotContain(processor.Details, detail => detail.Label is "Active consumers" or "Reusable outputs");
        Assert.DoesNotContain(otherProcessor.Details, detail => detail.Label is "Active consumers" or "Reusable outputs");
        if (encoder is null) Assert.Empty(processor.VideoEncodings);
        else Assert.Equal(encoder, Assert.Single(processor.VideoEncodings).Encoder);
        Assert.Empty(topology.ReusableOutputs);
        var sourceProcessors = topology.Nodes.Where(node => node.Lane == StreamingFlowLane.PhysicalProcessing).ToArray();
        Assert.Equal(2, sourceProcessors.Length);
        Assert.Equal(["2.1"], sourceProcessors.Single(node => node.Id == "source-processing:runtime:source-a").ActiveChannels);
        Assert.Equal(["2.3"], sourceProcessors.Single(node => node.Id == "source-processing:runtime:source-b").ActiveChannels);
        Assert.All(sourceProcessors, node => Assert.Equal("copy", Assert.Single(node.VideoEncodings).Encoder));
        Assert.Equal(encoder is not null && encoder != "copy" ? 2 : 0,
            topology.Nodes.Concat(topology.ReusableOutputs).Count(node => !node.IsLogicalStage && node.VideoEncodings.Any(encoding => encoding.Encoder != "copy")));
        Assert.Equal(["process-a"], snapshot.Graph!.Nodes.Single(node => node.Id == $"output:{first.Flow.ReusableOutputId}").ResourceIds);
        Assert.Equal(["process-b"], snapshot.Graph.Nodes.Single(node => node.Id == $"output:{second.Flow!.ReusableOutputId}").ResourceIds);
        var firstPath = topology.Paths.Single(path => path.Id == "path:first");
        var sharedPath = topology.Paths.Single(path => path.Id == "path:shared");
        var otherPath = topology.Paths.Single(path => path.Id == "path:second");
        Assert.Equal(firstPath.NodeIds.SkipLast(1), sharedPath.NodeIds.SkipLast(1));
        Assert.Equal(firstPath.EdgeIds.SkipLast(1), sharedPath.EdgeIds.SkipLast(1));
        Assert.Contains(processor.Id, firstPath.NodeIds);
        Assert.DoesNotContain(otherProcessor.Id, firstPath.NodeIds);
        Assert.Contains(otherProcessor.Id, otherPath.NodeIds);
        Assert.DoesNotContain(processor.Id, otherPath.NodeIds);
        Assert.Equal(2, topology.Edges.Count(edge => edge.Source == processor.Id));
        Assert.Single(topology.Edges, edge => edge.Source == otherProcessor.Id);
        Assert.Equal(["process-a", "process-b"], snapshot.Nodes.Single(node => node.Id == $"virtual-processing:{virtualId}").ResourceIds);
        Assert.All(snapshot.ActivePaths, path => Assert.Single(path.Stages.Single(stage => stage.Kind == StreamingFlowStageKind.OutputProcessing).ResourceIds));
    }

    /// <summary>Verifies reusable and session-owned telemetry are both retained on their virtual processing stage.</summary>
    [Fact]
    public void GetSnapshot_MixedProducerAttribution_KeepsAllOutputTelemetryOnProcessor()
    {
        // Arrange
        var physicalId = PhysicalDeviceId.New();
        var virtualId = VirtualDeviceId.New();
        var reusable = ActiveStream("reusable", "localhost", physicalId, virtualId, new(physicalId, "2.1")) with
        {
            ActiveVideoEncoder = "h264_amf",
            ResourceIds = ["reusable-process"]
        };
        var sessionOwned = reusable with
        {
            SessionId = "session-owned",
            ActiveVideoEncoder = "libx264",
            ResourceIds = ["session-process"],
            Flow = new() { PhysicalDeviceId = physicalId, VirtualDeviceId = virtualId }
        };
        var settings = Substitute.For<ILineupSettingsService>();
        settings.Settings.Returns(new LineupSettings { Streaming = Configuration(physicalId, virtualId) });
        var graph = Substitute.For<IStreamingSessionGraph>();
        graph.GetDiagnostics().Returns(new StreamingSessionGraphDiagnostics([], []));
        var streams = Substitute.For<IActiveStreamRegistry>();
        streams.GetActiveStreams().Returns([reusable, sessionOwned]);
        var devices = Substitute.For<IPhysicalDeviceStatusService>();
        devices.Devices.Returns([]);

        // Act
        var snapshot = new StreamingFlowSnapshotService(settings, graph, [], streams, devices).GetSnapshot();

        // Assert
        var topology = Assert.IsType<StreamingFlowTopology>(snapshot.Topology);
        Assert.Empty(topology.ReusableOutputs);
        var processor = topology.Nodes.Single(node => node.Id == $"output-processing:output:{reusable.Flow!.ReusableOutputId}");
        var sessionProcessor = topology.Nodes.Single(node => node.Id == "output-processing:session:session-owned");
        Assert.Equal(2, topology.Nodes.Count(node => node.Lane == StreamingFlowLane.VirtualProcessing));
        Assert.False(processor.IsLogicalStage);
        Assert.Equal("h264_amf", Assert.Single(processor.VideoEncodings).Encoder);
        Assert.Equal("libx264", Assert.Single(sessionProcessor.VideoEncodings).Encoder);
        Assert.Equal(["reusable-process"], processor.ResourceIds);
        Assert.Equal(["session-process"], sessionProcessor.ResourceIds);
    }

    /// <summary>Verifies sharing a source and encoder preference does not fabricate an output identity for unreported processors.</summary>
    [Fact]
    public void GetSnapshot_UnreportedOutputIdentity_DoesNotMergeIndependentProcessing()
    {
        // Arrange
        var physicalId = PhysicalDeviceId.New();
        var virtualId = VirtualDeviceId.New();
        var first = ActiveStream("first", "localhost", physicalId, virtualId, new(physicalId, "2.1")) with
        {
            ActiveVideoEncoder = "h264_amf",
            ResourceIds = ["process-a"],
            Flow = new() { PhysicalDeviceId = physicalId, VirtualDeviceId = virtualId, ProducerNodeId = "source" }
        };
        var second = first with { SessionId = "second", ResourceIds = ["process-b"] };
        var settings = Substitute.For<ILineupSettingsService>();
        settings.Settings.Returns(new LineupSettings { Streaming = Configuration(physicalId, virtualId) });
        var graph = Substitute.For<IStreamingSessionGraph>();
        graph.GetDiagnostics().Returns(new StreamingSessionGraphDiagnostics(
            [new("acquisition", "source", 2, true) { NodeId = "source", PhysicalDeviceId = physicalId }], []));
        var streams = Substitute.For<IActiveStreamRegistry>();
        streams.GetActiveStreams().Returns([first, second]);
        var devices = Substitute.For<IPhysicalDeviceStatusService>();
        devices.Devices.Returns([]);

        // Act
        var snapshot = new StreamingFlowSnapshotService(settings, graph, [], streams, devices).GetSnapshot();

        // Assert
        var topology = Assert.IsType<StreamingFlowTopology>(snapshot.Topology);
        var processors = topology.Nodes.Where(node => node.Lane == StreamingFlowLane.VirtualProcessing).ToArray();
        Assert.Equal(2, processors.Length);
        Assert.Equal(["process-a"], processors.Single(node => node.Id == "output-processing:session:first").ResourceIds);
        Assert.Equal(["process-b"], processors.Single(node => node.Id == "output-processing:session:second").ResourceIds);
        Assert.All(processors, node => Assert.Equal(1, node.ActiveSessionCount));
        Assert.Contains("output-processing:session:first", topology.Paths.Single(path => path.Id == "path:first").NodeIds);
        Assert.DoesNotContain("output-processing:session:second", topology.Paths.Single(path => path.Id == "path:first").NodeIds);
    }

    /// <summary>Verifies later known session telemetry is not lost when a shared runtime producer has not reported its encoder yet.</summary>
    [Fact]
    public void GetSnapshot_PartialSharedTelemetry_KeepsKnownEncoderAndResource()
    {
        // Arrange
        var physicalId = PhysicalDeviceId.New();
        var virtualId = VirtualDeviceId.New();
        var first = ActiveStream("first", "localhost", physicalId, virtualId, new(physicalId, "2.1")) with
        {
            Flow = new() { PhysicalDeviceId = physicalId, VirtualDeviceId = virtualId, ProducerNodeId = "output" }
        };
        var second = first with { SessionId = "second", ActiveVideoEncoder = "h264_amf", ResourceIds = ["reported-process"] };
        var settings = Substitute.For<ILineupSettingsService>();
        settings.Settings.Returns(new LineupSettings { Streaming = Configuration(physicalId, virtualId) });
        var graph = Substitute.For<IStreamingSessionGraph>();
        graph.GetDiagnostics().Returns(new StreamingSessionGraphDiagnostics(
        [
            new("acquisition", "source", 1, true) { NodeId = "source", PhysicalDeviceId = physicalId },
            new("transform", "output", 2, true) { NodeId = "output", ParentNodeId = "source", Stage = "virtual-mpegts" }
        ], []));
        var streams = Substitute.For<IActiveStreamRegistry>();
        streams.GetActiveStreams().Returns([first, second]);
        var devices = Substitute.For<IPhysicalDeviceStatusService>();
        devices.Devices.Returns([]);

        // Act
        var snapshot = new StreamingFlowSnapshotService(settings, graph, [], streams, devices).GetSnapshot();

        // Assert
        var processor = Assert.Single(snapshot.Topology!.Nodes, node => node.Lane == StreamingFlowLane.VirtualProcessing);
        Assert.Equal("output-processing:runtime:output", processor.Id);
        Assert.Equal(2, processor.ActiveSessionCount);
        Assert.Equal("h264_amf", Assert.Single(processor.VideoEncodings).Encoder);
        Assert.Equal(["reported-process"], processor.ResourceIds);
        Assert.All(snapshot.Topology.Paths, path => Assert.Contains(processor.Id, path.NodeIds));
    }

    /// <summary>Verifies a reusable processor is not duplicated when multiple virtual devices share its actual identity.</summary>
    [Fact]
    public void GetSnapshot_SharedOutputAcrossVirtualDevices_UsesOneProcessor()
    {
        // Arrange
        var physicalId = PhysicalDeviceId.New();
        var firstVirtualId = VirtualDeviceId.New();
        var secondVirtualId = VirtualDeviceId.New();
        var configuration = Configuration(physicalId, firstVirtualId);
        configuration = configuration with
        {
            VirtualDevices = [configuration.VirtualDevices[0], configuration.VirtualDevices[0] with { Id = secondVirtualId, Name = "Other DVR" }]
        };
        var first = ActiveStream("first", "localhost", physicalId, firstVirtualId, new(physicalId, "2.1")) with
        {
            ActiveVideoEncoder = "h264_amf",
            ResourceIds = ["shared-process"]
        };
        var second = first with { SessionId = "second", Flow = first.Flow! with { VirtualDeviceId = secondVirtualId } };
        var settings = Substitute.For<ILineupSettingsService>();
        settings.Settings.Returns(new LineupSettings { Streaming = configuration });
        var graph = Substitute.For<IStreamingSessionGraph>();
        graph.GetDiagnostics().Returns(new StreamingSessionGraphDiagnostics([], []));
        var streams = Substitute.For<IActiveStreamRegistry>();
        streams.GetActiveStreams().Returns([first, second]);
        var devices = Substitute.For<IPhysicalDeviceStatusService>();
        devices.Devices.Returns([]);

        // Act
        var snapshot = new StreamingFlowSnapshotService(settings, graph, [], streams, devices).GetSnapshot();

        // Assert
        var topology = Assert.IsType<StreamingFlowTopology>(snapshot.Topology);
        var processor = Assert.Single(topology.Nodes, node => node.Lane == StreamingFlowLane.VirtualProcessing);
        Assert.Equal(2, processor.ActiveSessionCount);
        Assert.Equal(["shared-process"], processor.ResourceIds);
        Assert.Contains("Living Room DVR", processor.Subtitle);
        Assert.Contains("Other DVR", processor.Subtitle);
        Assert.Contains(topology.Edges, edge => edge.Source == $"virtual:{firstVirtualId}" && edge.Target == processor.Id);
        Assert.Contains(topology.Edges, edge => edge.Source == $"virtual:{secondVirtualId}" && edge.Target == processor.Id);
        Assert.All(topology.Paths, path => Assert.Contains(processor.Id, path.NodeIds));
    }

    /// <summary>Verifies a retained processing chain keeps separate reusable identities, resources, and truthful virtual attribution.</summary>
    [Fact]
    public async Task GetSnapshot_WarmOutputChain_KeepsDistinctProcessorsAndUnassignedUpstreamInventory()
    {
        // Arrange
        await using var coordinator = new CmafOutputCoordinator();
        var owner = Substitute.For<IAsyncDisposable>();
        var physicalId = PhysicalDeviceId.New();
        var virtualId = VirtualDeviceId.New();
        var context = new StreamingProducerContext(new() { ProducerNodeId = "mpegts", PhysicalDeviceId = physicalId, VirtualDeviceId = virtualId }, "2.1")
        {
            VideoEncoding = new("h264_amf", HardwareVideoEncoder.Amf, false),
            ResourceIds = ["cmaf-process"]
        };
        var lease = await coordinator.AcquireAsync(new("test", "chain"), StreamSessionId.New(),
            _ => ValueTask.FromResult(new CmafOutputRegistration(owner, new object()) { ObserveContext = () => context }),
            TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);
        var outputId = lease.OutputId;
        var settings = Substitute.For<ILineupSettingsService>();
        settings.Settings.Returns(new LineupSettings { Streaming = Configuration(physicalId, virtualId) });
        var graph = Substitute.For<IStreamingSessionGraph>();
        graph.GetDiagnostics().Returns(new StreamingSessionGraphDiagnostics(
        [
            new("acquisition", "source", 1, true) { NodeId = "source", PhysicalDeviceId = physicalId },
            new("transform", "normalized", 1, true)
            {
                NodeId = "normalized", ParentNodeId = "source", Stage = "source-normalization",
                VideoEncoding = new("h264_amf", HardwareVideoEncoder.Amf, false), ResourceIds = ["source-process"]
            },
            new("transform", "mpegts", 0, true, true)
            {
                NodeId = "mpegts", ParentNodeId = "normalized", Stage = "virtual-mpegts",
                VideoEncoding = new("copy", null, false), ResourceIds = ["mpegts-process"]
            }
        ], []));
        var streams = Substitute.For<IActiveStreamRegistry>();
        streams.GetActiveStreams().Returns([]);
        var devices = Substitute.For<IPhysicalDeviceStatusService>();
        devices.Devices.Returns([]);
        var service = new StreamingFlowSnapshotService(settings, graph, [coordinator], streams, devices);

        // Act
        await lease.DisposeAsync();
        var warm = service.GetSnapshot();
        await coordinator.ReleaseOutputAsync(outputId);
        var detached = service.GetSnapshot();

        // Assert
        var topology = Assert.IsType<StreamingFlowTopology>(warm.Topology);
        var upstreamId = "output-processing:runtime:mpegts";
        var cmafId = $"output-processing:output:{outputId}";
        Assert.Equal(2, topology.Nodes.Count(node => node.Lane == StreamingFlowLane.VirtualProcessing));
        var sourceProcessor = Assert.Single(topology.Nodes, node => node.Lane == StreamingFlowLane.PhysicalProcessing);
        Assert.Equal("source-processing:runtime:normalized", sourceProcessor.Id);
        Assert.Equal(["source-process"], sourceProcessor.ResourceIds);
        Assert.Equal(["mpegts-process"], topology.Nodes.Single(node => node.Id == upstreamId).ResourceIds);
        Assert.Equal(["cmaf-process"], topology.Nodes.Single(node => node.Id == cmafId).ResourceIds);
        Assert.Contains(topology.Edges, edge => edge.Source == $"virtual:{virtualId}" && edge.Target == upstreamId && edge.State == StreamingFlowState.Warm);
        Assert.Contains(topology.Edges, edge => edge.Source == upstreamId && edge.Target == cmafId && edge.State == StreamingFlowState.Warm);
        Assert.DoesNotContain(topology.Edges, edge => edge.Source == "runtime:mpegts" && edge.Target == upstreamId);
        Assert.Empty(topology.Paths);
        var detachedTopology = Assert.IsType<StreamingFlowTopology>(detached.Topology);
        Assert.DoesNotContain(detachedTopology.Nodes, node => node.Id == cmafId);
        Assert.Contains(detachedTopology.Nodes, node => node.Id == $"virtual-processing:{virtualId}" && node.State == StreamingFlowState.Idle);
        Assert.Equal("Output attribution not reported", detachedTopology.Nodes.Single(node => node.Id == upstreamId).Subtitle);
        Assert.Contains(detachedTopology.Edges, edge => edge.Source == sourceProcessor.Id && edge.Target == upstreamId && edge.State == StreamingFlowState.Warm);
    }

    /// <summary>Verifies runtime ancestry shares real prefixes without joining unrelated channels on one configured device.</summary>
    [Fact]
    public void GetSnapshot_RuntimeGraph_PreservesExactSharedAndIndependentPaths()
    {
        // Arrange
        var physical = PhysicalDeviceId.New();
        var virtualId = VirtualDeviceId.New();
        var first = ActiveStream("first", "same endpoint", physical, virtualId, new(physical, "7.1"));
        first = first with { Flow = first.Flow! with { ProducerNodeId = "output-a", ReusableOutputId = null } };
        var second = first with { SessionId = "second" };
        var other = first with { SessionId = "other", Channel = "9.1", Flow = first.Flow with { ProducerNodeId = "output-b" } };
        var settings = Substitute.For<ILineupSettingsService>();
        settings.Settings.Returns(new LineupSettings { Streaming = Configuration(physical, virtualId) });
        var graph = Substitute.For<IStreamingSessionGraph>();
        graph.GetDiagnostics().Returns(new StreamingSessionGraphDiagnostics(
        [
            new("acquisition", "a", 1, true) { NodeId = "a", PhysicalDeviceId = physical },
            new("transform", "normalize", 1, true) { NodeId = "normalize", ParentNodeId = "a", Stage = "source-normalization" },
            new("transform", "output-a", 2, true) { NodeId = "output-a", ParentNodeId = "normalize", Stage = "virtual-mpegts", ResourceIds = ["process-a"] },
            new("acquisition", "b", 1, true) { NodeId = "b", PhysicalDeviceId = physical },
            new("transform", "output-b", 1, true) { NodeId = "output-b", ParentNodeId = "b", Stage = "virtual-mpegts", ResourceIds = ["process-b"] }
        ], []));
        var streams = Substitute.For<IActiveStreamRegistry>();
        streams.GetActiveStreams().Returns([first, second, other]);
        var devices = Substitute.For<IPhysicalDeviceStatusService>();
        devices.Devices.Returns([]);
        var service = new StreamingFlowSnapshotService(settings, graph, [], streams, devices);

        // Act
        var snapshot = service.GetSnapshot();
        var next = service.GetSnapshot();

        // Assert
        var topology = Assert.IsType<StreamingFlowGraph>(snapshot.Graph);
        var firstPath = topology.Paths.Single(path => path.Id == "path:first");
        var secondPath = topology.Paths.Single(path => path.Id == "path:second");
        var otherPath = topology.Paths.Single(path => path.Id == "path:other");
        Assert.Equal(firstPath.NodeIds.SkipLast(1), secondPath.NodeIds.SkipLast(1));
        Assert.Empty(firstPath.NodeIds.Intersect(otherPath.NodeIds, StringComparer.Ordinal));
        Assert.Equal(["runtime:a", "runtime:normalize", "runtime:output-a"], firstPath.NodeIds.Take(3));
        Assert.Equal(2, topology.Nodes.Single(node => node.Id == "runtime:output-a").ActiveSessionCount);
        Assert.Equal(["process-a"], topology.Nodes.Single(node => node.Id == "runtime:output-a").ResourceIds);
        Assert.Equal(["process-b"], topology.Nodes.Single(node => node.Id == "runtime:output-b").ResourceIds);
        Assert.DoesNotContain(topology.Nodes, node => node.Id == "multiplexer");
        Assert.All(topology.Paths, path =>
        {
            Assert.Equal(path.NodeIds.Count - 1, path.EdgeIds.Count);
            for (var index = 0; index < path.EdgeIds.Count; index++)
            {
                var edge = topology.Edges.Single(edge => edge.Id == path.EdgeIds[index]);
                Assert.Equal(path.NodeIds[index], edge.Source);
                Assert.Equal(path.NodeIds[index + 1], edge.Target);
            }
        });
        Assert.Equal(topology.Nodes.Select(node => node.Id), next.Graph!.Nodes.Select(node => node.Id));
        Assert.DoesNotContain(snapshot.Topology!.Nodes, node => node.Id == "multiplexer");
        Assert.DoesNotContain(snapshot.Topology.Edges, edge => edge.Source == "multiplexer" || edge.Target == "multiplexer");
        Assert.Contains(snapshot.Topology.Edges, edge => edge.Source == $"physical:{physical}" && edge.Target == "source-processing:runtime:normalize");
        Assert.Contains(snapshot.Topology.Edges, edge => edge.Source == "source-processing:runtime:normalize" && edge.Target == $"virtual:{virtualId}");
        Assert.Contains(snapshot.Topology.Edges, edge => edge.Source == $"virtual:{virtualId}" && edge.Target == "output-processing:runtime:output-a");
        var sourceProcessors = snapshot.Topology.Nodes.Where(node => node.Lane == StreamingFlowLane.PhysicalProcessing).ToArray();
        Assert.Equal(2, sourceProcessors.Length);
        Assert.Equal(2, sourceProcessors.Single(node => node.Id == "source-processing:runtime:normalize").ActiveSessionCount);
        Assert.Equal(1, sourceProcessors.Single(node => node.Id == "source-processing:runtime:b").ActiveSessionCount);
        Assert.DoesNotContain(snapshot.Topology.Nodes, node => node.Id == $"physical-processing:{physical}");
        Assert.Empty(snapshot.Topology.ReusableOutputs);
        Assert.All(snapshot.Topology.Paths, path =>
        {
            Assert.Equal(5, path.NodeIds.Count);
            Assert.Equal(path.NodeIds.Count - 1, path.EdgeIds.Count);
            Assert.All(path.NodeIds, id => Assert.Contains(snapshot.Topology.Nodes, node => node.Id == id));
        });
        Assert.DoesNotContain(snapshot.Topology.Paths.Single(path => path.Id == "path:first").NodeIds, id => id == "runtime:output-b");
    }

    /// <summary>Verifies warm output inventory survives viewer removal and is removed with its producer.</summary>
    [Fact]
    public async Task GetSnapshot_WarmOutput_KeepsExactProducerIdentityAndResources()
    {
        // Arrange
        await using var coordinator = new CmafOutputCoordinator();
        var owner = Substitute.For<IAsyncDisposable>();
        var physicalId = PhysicalDeviceId.New();
        var virtualId = VirtualDeviceId.New();
        var context = new StreamingProducerContext(new() { ProducerNodeId = "source", PhysicalDeviceId = physicalId, VirtualDeviceId = virtualId }, "7.1")
        {
            ResourceIds = ["encoder"]
        };
        var lease = await coordinator.AcquireAsync(new("test", "plan"), StreamSessionId.New(),
            _ => ValueTask.FromResult(new CmafOutputRegistration(owner, new object()) { ObserveContext = () => context }),
            TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);
        var id = lease.OutputId;
        var settings = Substitute.For<ILineupSettingsService>();
        settings.Settings.Returns(new LineupSettings { Streaming = Configuration(physicalId, virtualId) });
        var graph = Substitute.For<IStreamingSessionGraph>();
        graph.GetDiagnostics().Returns(new StreamingSessionGraphDiagnostics([new("acquisition", "source", 1, true) { NodeId = "source", PhysicalDeviceId = physicalId }], []));
        var streams = Substitute.For<IActiveStreamRegistry>();
        streams.GetActiveStreams().Returns([]);
        var devices = Substitute.For<IPhysicalDeviceStatusService>();
        devices.Devices.Returns([]);
        var service = new StreamingFlowSnapshotService(settings, graph, [coordinator], streams, devices);

        // Act
        await lease.DisposeAsync();
        var warm = service.GetSnapshot();
        await coordinator.ReleaseOutputAsync(id);
        var stopped = service.GetSnapshot();

        // Assert
        var node = Assert.Single(warm.Graph!.Nodes, node => node.Id == $"output:{id}");
        Assert.Equal(StreamingFlowState.Warm, node.State);
        Assert.Equal(["encoder"], node.ResourceIds);
        Assert.Contains(warm.Graph.Edges, edge => edge.Source == "runtime:source" && edge.Target == node.Id);
        Assert.Empty(warm.Graph.Paths);
        Assert.Empty(warm.Topology!.ReusableOutputs);
        Assert.Contains(warm.Topology.Edges, edge => edge.Source == $"physical:{physicalId}" && edge.Target == "source-processing:runtime:source" && edge.State == StreamingFlowState.Warm);
        Assert.DoesNotContain(warm.Topology.Edges, edge => edge.Source == "multiplexer" || edge.Target == "multiplexer");
        Assert.All(warm.Topology.Nodes, node => Assert.Equal(StreamingFlowState.Warm, node.State));
        var processor = Assert.Single(warm.Topology.Nodes, node => node.Lane == StreamingFlowLane.VirtualProcessing);
        Assert.Equal($"output-processing:output:{id}", processor.Id);
        Assert.False(processor.IsLogicalStage);
        Assert.Equal(["encoder"], processor.ResourceIds);
        Assert.Contains(warm.Topology.Edges, edge => edge.Source == "source-processing:runtime:source" && edge.Target == $"virtual:{virtualId}" && edge.State == StreamingFlowState.Warm);
        Assert.Contains(warm.Topology.Edges, edge => edge.Source == $"virtual:{virtualId}" && edge.Target == processor.Id && edge.State == StreamingFlowState.Warm);
        Assert.DoesNotContain(stopped.Graph!.Nodes, node => node.Id == $"output:{id}");
        Assert.Empty(stopped.Topology!.ReusableOutputs);
        Assert.DoesNotContain(stopped.Topology.Nodes, node => node.Id == processor.Id);
        Assert.Contains(stopped.Topology.Nodes, node => node.Id == $"virtual-processing:{virtualId}" && node.State == StreamingFlowState.Idle);
        await owner.Received(1).DisposeAsync();
    }

    /// <summary>Verifies missing producer attribution is explicit and synthetic sessions are never shared just by label.</summary>
    [Fact]
    public void GetSnapshot_SyntheticAndMissingSources_DoNotFabricateSharedProducers()
    {
        // Arrange
        var physical = PhysicalDeviceId.New();
        var virtualId = VirtualDeviceId.New();
        var first = ActiveStream("first", "endpoint", physical, virtualId, new(physical, "7.1")) with
        {
            Flow = new() { SyntheticSource = "Protected content slate", VirtualDeviceId = virtualId }
        };
        var second = first with { SessionId = "second" };
        var missing = first with { SessionId = "missing", Flow = new() { ProducerNodeId = "not-reported", VirtualDeviceId = virtualId } };
        var unattributedOutput = first with
        {
            SessionId = "unattributed-output",
            Flow = new() { ReusableOutputId = StreamSessionId.New(), VirtualDeviceId = virtualId }
        };
        var settings = Substitute.For<ILineupSettingsService>();
        settings.Settings.Returns(new LineupSettings { Streaming = Configuration(physical, virtualId) });
        var graph = Substitute.For<IStreamingSessionGraph>();
        graph.GetDiagnostics().Returns(new StreamingSessionGraphDiagnostics([], []));
        var streams = Substitute.For<IActiveStreamRegistry>();
        streams.GetActiveStreams().Returns([first, second, missing, unattributedOutput]);
        var devices = Substitute.For<IPhysicalDeviceStatusService>();
        devices.Devices.Returns([]);

        // Act
        var snapshot = new StreamingFlowSnapshotService(settings, graph, [], streams, devices).GetSnapshot();

        // Assert
        Assert.Empty(snapshot.Graph!.Paths[0].NodeIds.Intersect(snapshot.Graph.Paths[1].NodeIds, StringComparer.Ordinal));
        Assert.Equal(2, snapshot.Graph.Nodes.Count(node => node.Label == "Protected content slate"));
        Assert.Equal(StreamingFlowState.Unavailable, snapshot.Graph.Nodes.Single(node => node.Id == "runtime:not-reported").State);
        Assert.Contains(snapshot.Graph.ConfiguredNodes, node => node.Id == $"physical:{physical}");
        Assert.Contains(snapshot.Topology!.Nodes, node => node.Id == "session-source:unattributed-output" && node.State == StreamingFlowState.Unavailable);
        var topologyIds = snapshot.Topology.Nodes.Concat(snapshot.Topology.ReusableOutputs).Select(node => node.Id).ToArray();
        Assert.Equal(topologyIds.Length, topologyIds.Distinct(StringComparer.Ordinal).Count());
        Assert.All(snapshot.Topology.Edges, edge =>
        {
            Assert.Contains(edge.Source, topologyIds);
            Assert.Contains(edge.Target, topologyIds);
        });
        Assert.All(snapshot.Graph.Edges, edge =>
        {
            Assert.Contains(snapshot.Graph.Nodes, node => node.Id == edge.Source);
            Assert.Contains(snapshot.Graph.Nodes, node => node.Id == edge.Target);
        });
    }

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
        var sourceResource = Guid.NewGuid().ToString("N");
        var outputResource = Guid.NewGuid().ToString("N");
        var workerResource = Guid.NewGuid().ToString("N");
        var captionResource = Guid.NewGuid().ToString("N");
        var first = ActiveStream("first", "localhost", physicalId, virtualId, new(physicalId, "7.1"));
        var second = ActiveStream("second", "localhost", physicalId, virtualId, new(physicalId, "9.1"));
        first = first with { Flow = first.Flow! with { ProducerNodeId = "output-a" } };
        second = second with { Flow = second.Flow! with { ProducerNodeId = "output-b" }, ResourceIds = [workerResource, captionResource] };
        var settings = Substitute.For<ILineupSettingsService>();
        settings.Settings.Returns(new LineupSettings { Streaming = Configuration(physicalId, virtualId) });
        var graph = Substitute.For<IStreamingSessionGraph>();
        graph.GetDiagnostics().Returns(new StreamingSessionGraphDiagnostics(
        [
            new("acquisition", "a", 1, true) { NodeId = "a", PhysicalDeviceId = physicalId, SourceChannelId = new(physicalId, "7.1") },
            new("transform", "physical-a", 1, true)
            {
                NodeId = "physical-a", ParentNodeId = "a", Stage = "source-normalization", VideoEncoding = sourceEncoding, ResourceIds = [sourceResource]
            },
            new("transform", "output-a", 1, true)
            {
                NodeId = "output-a", ParentNodeId = "physical-a", Stage = "virtual-mpegts", VideoEncoding = copyEncoding, ResourceIds = [outputResource]
            },
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
        Assert.Equal([sourceResource], firstPath.Stages.Single(stage => stage.Kind == StreamingFlowStageKind.SourceProcessing).ResourceIds);
        Assert.Equal([outputResource], firstPath.Stages.Single(stage => stage.Kind == StreamingFlowStageKind.OutputProcessing).ResourceIds);
        Assert.Equal([workerResource, captionResource], secondPath.Stages.Single(stage => stage.Kind == StreamingFlowStageKind.OutputProcessing).ResourceIds);
        var sourceCopy = secondPath.Stages.Single(stage => stage.Kind == StreamingFlowStageKind.SourceProcessing);
        Assert.Equal("copy", sourceCopy.VideoEncoding!.Encoder);
        Assert.Equal("Pass-through", sourceCopy.Label);
        Assert.Empty(sourceCopy.ResourceIds);
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
