using Bunit;
using Lineup.App.Web.Components.Dashboard;
using Lineup.App.Web.Components.Streaming;
using Lineup.App.Web.Services;
using Microsoft.JSInterop;
using System.Text.Json;
using Xunit;

namespace Lineup.App.Web.Tests.Components;

/// <summary>Verifies the accessible dashboard streaming-flow visualization and selected-node diagnostics.</summary>
public sealed class StreamingFlowDiagramTests
{
    /// <summary>Compact cards expose independent operations and selected details retain every audio rendition and same-codec encode.</summary>
    [Fact]
    public async Task Render_ProcessingOperations_KeepCardsCompactAndExposeTrackDetails()
    {
        // Arrange
        await using var context = new BunitContext();
        ActiveStreamTrack[] tracks =
        [
            new(MediaTrackType.Video, "h264", "copy", null, null, 1920, 1080, null, null, null),
            new(MediaTrackType.Audio, "ac3", "copy", null, null, null, null, 6, 6, 48000) { SourceIndex = 7, Language = "eng", OutputTitle = "Original" },
            new(MediaTrackType.Audio, "ac3", "ac3", null, null, null, null, 6, 2, 48000) { SourceIndex = 7, Language = "eng", OutputTitle = "Stereo" }
        ];
        var processing = new StreamProcessingSnapshot(StreamTransportOperation.Remux, tracks) { VideoKnown = true, AudioKnown = true };
        var stage = new StreamingFlowPathStage(StreamingFlowStageKind.OutputProcessing, "Remux", "", [])
        {
            Processing = processing,
            VideoEncoding = new("copy", null, false)
        };
        var node = new StreamingFlowNode("output", StreamingFlowLane.VirtualProcessing, "Output", "", StreamingFlowState.Active, [])
        {
            ProcessingOutcomes = [processing],
            VideoEncodings = [stage.VideoEncoding],
            ActiveSessionCount = 1
        };
        var path = new StreamingFlowPath("path", "session", "Viewer", HostedStreamFormat.Cmaf, "7.1", DateTime.UtcNow, [stage], false, false, 1);
        var snapshot = new StreamingFlowSnapshot([node], [path], 1, 1, 0)
        {
            Topology = new([node], [], [new("path", ["output"], [])], [])
        };
        var cut = context.Render<StreamingFlowDiagram>(parameters => parameters.Add(value => value.Snapshot, snapshot));
        var card = cut.Find("[data-flow-node-id='output']");

        // Act
        await card.ClickAsync(new());

        // Assert
        Assert.Equal(4, card.QuerySelectorAll("[data-processing-operation]").Length);
        Assert.Contains("Video Copy", card.TextContent);
        Assert.Contains("Audio Copy", card.TextContent);
        Assert.Contains("Audio Transcode", card.TextContent);
        Assert.Contains("Remux", card.TextContent);
        Assert.DoesNotContain("Stereo", card.TextContent);
        Assert.Empty(card.QuerySelectorAll(".video-encoder-badge"));
        Assert.Contains("Audio Transcode", card.GetAttribute("aria-label"));
        var details = cut.Find("[data-flow-stage='OutputProcessing']");
        Assert.Equal(3, details.QuerySelectorAll("[data-processing-track]").Length);
        Assert.Contains("Original", details.TextContent);
        Assert.Contains("Stereo", details.TextContent);
        Assert.Contains("eng", details.TextContent);
        Assert.Contains("ac3 → ac3", details.TextContent);
    }

    /// <summary>Unreported track presence remains unknown, whereas an explicit empty output mapping reports absence.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Render_ProcessingPresence_DistinguishesUnknownAbsentAndPassThrough(bool reported, bool known)
    {
        // Arrange
        await using var context = new BunitContext();
        var processing = new StreamProcessingSnapshot(StreamTransportOperation.PassThrough, []) { AudioKnown = known, VideoKnown = known };

        // Act
        var cut = context.Render<StreamProcessingBadges>(parameters => parameters.Add(value => value.Outcomes, reported ? [processing] : []));

        // Assert
        Assert.Contains(known ? "No Audio" : "Audio Unknown", cut.Markup);
        Assert.Contains(known ? "No Video" : "Video Unknown", cut.Markup);
        Assert.Contains(reported ? "Pass-through" : "Transport Unknown", cut.Markup);
        Assert.DoesNotContain("Audio Copy", cut.Markup);
        Assert.DoesNotContain("Video Copy", cut.Markup);
    }

    /// <summary>A retained producer exposes known operations and per-track details without inventing an active consumer.</summary>
    [Fact]
    public async Task Render_WarmProcessing_KeepsReportedAudioDetailsWithoutConsumers()
    {
        // Arrange
        await using var context = new BunitContext();
        var processing = new StreamProcessingSnapshot(StreamTransportOperation.Remux,
            [new(MediaTrackType.Audio, "ac4", "copy", null, null, null, null, 12, 12, 46034) { SourceIndex = 7, Language = "eng" }])
        {
            AudioKnown = true,
            VideoKnown = true
        };
        var node = new StreamingFlowNode("warm", StreamingFlowLane.VirtualProcessing, "Remux", "", StreamingFlowState.Warm, [])
        {
            ProcessingOutcomes = [processing]
        };
        var cut = context.Render<StreamingFlowDiagram>(parameters => parameters.Add(value => value.Snapshot, new StreamingFlowSnapshot([node], [], 0, 0, 1)));

        // Act
        await cut.Find("[data-flow-node-id='warm']").ClickAsync(new());

        // Assert
        Assert.Contains("Audio Copy", cut.Find("[data-flow-node-id='warm']").TextContent);
        Assert.Contains("No Video", cut.Find("[data-flow-node-id='warm']").TextContent);
        Assert.Empty(cut.FindAll(".video-encoder-unknown"));
        Assert.Single(cut.FindAll("#streamingFlowDetails [data-processing-track='7']"));
        Assert.Contains("ac4 → ac4", cut.Find("#streamingFlowDetails").TextContent);
        Assert.Empty(cut.FindAll("[data-flow-session-id]"));
    }

    /// <summary>An older JS completion cannot mark a newer snapshot or selection as already rendered.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Render_PendingGraphUpdate_AcknowledgesOnlySentState(bool changeSelection)
    {
        // Arrange
        await using var context = new BunitContext();
        var pending = context.JSInterop.SetupVoid("lineupFlowGraph.update", _ => true);
        StreamingFlowNode[] nodes =
        [
            new("physical:p", StreamingFlowLane.PhysicalSource, "Source", "", StreamingFlowState.Idle, []),
            new("virtual:v", StreamingFlowLane.VirtualOutput, "Output", "", StreamingFlowState.Idle, [])
        ];
        var first = new StreamingFlowSnapshot([], [], 0, 0, 0) { Topology = new(nodes, [], [], []) };
        var cut = context.Render<DelayedSecondFlowDiagram>(parameters => parameters.Add(value => value.Snapshot, first));
        var barrier = cut.Instance.Barrier;
        try
        {
            cut.WaitForAssertion(() => Assert.Single(context.JSInterop.Invocations["lineupFlowGraph.update"]), BunitContext.WaitTimeout);

            // Act
            if (changeSelection)
            {
                await cut.Find("[data-flow-node-id='physical:p']").ClickAsync(new());
            }
            else
            {
                cut.Render(parameters => parameters.Add(value => value.Snapshot,
                    first with { Topology = new(nodes, [new("edge:new", "physical:p", "virtual:v")], [], []) }));
            }
            await barrier.SecondEntered.Task.WaitAsync(BunitContext.WaitTimeout, Xunit.TestContext.Current.CancellationToken);
            pending.SetVoidResult();
            await barrier.FirstCompleted.Task.WaitAsync(BunitContext.WaitTimeout, Xunit.TestContext.Current.CancellationToken);
            barrier.ReleaseSecond.TrySetResult();
            await barrier.SecondCompleted.Task.WaitAsync(BunitContext.WaitTimeout, Xunit.TestContext.Current.CancellationToken);

            // Assert
            var calls = context.JSInterop.Invocations["lineupFlowGraph.update"].ToArray();
            Assert.Equal(2, calls.Length);
            var previous = JsonSerializer.SerializeToElement(calls[0].Arguments[1]);
            var latest = JsonSerializer.SerializeToElement(calls[1].Arguments[1]);
            Assert.Equal(JsonValueKind.Null, previous.GetProperty("SelectedNodeId").ValueKind);
            Assert.Empty(previous.GetProperty("Edges").EnumerateArray());
            if (changeSelection)
            {
                Assert.Equal("physical:p", latest.GetProperty("SelectedNodeId").GetString());
            }
            else
            {
                Assert.Equal("edge:new", Assert.Single(latest.GetProperty("Edges").EnumerateArray()).GetProperty("Id").GetString());
            }
        }
        finally
        {
            pending.SetVoidResult();
            await barrier.DrainAsync();
        }
    }

    /// <summary>Interval changes during observer setup still install their own browser refresh cadence.</summary>
    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 2)]
    public async Task Render_PendingObserver_AcknowledgesOnlySentInterval(int originalInterval, int updatedInterval)
    {
        // Arrange
        await using var context = new BunitContext();
        var pending = context.JSInterop.SetupVoid("lineupFlowGraph.observe", _ => true);
        var cut = context.Render<DelayedSecondStreamsPanel>(parameters => parameters
            .Add(value => value.Expanded, true)
            .Add(value => value.RefreshIntervalSeconds, originalInterval));
        var barrier = cut.Instance.Barrier;
        try
        {
            cut.WaitForAssertion(() => Assert.Single(context.JSInterop.Invocations["lineupFlowGraph.observe"]), BunitContext.WaitTimeout);

            // Act
            cut.Render(parameters => parameters.Add(value => value.RefreshIntervalSeconds, updatedInterval));
            await barrier.SecondEntered.Task.WaitAsync(BunitContext.WaitTimeout, Xunit.TestContext.Current.CancellationToken);
            pending.SetVoidResult();
            await barrier.FirstCompleted.Task.WaitAsync(BunitContext.WaitTimeout, Xunit.TestContext.Current.CancellationToken);
            barrier.ReleaseSecond.TrySetResult();
            await barrier.SecondCompleted.Task.WaitAsync(BunitContext.WaitTimeout, Xunit.TestContext.Current.CancellationToken);

            // Assert
            var calls = context.JSInterop.Invocations["lineupFlowGraph.observe"].ToArray();
            Assert.Equal(2, calls.Length);
            Assert.Equal(originalInterval, Assert.IsType<int>(calls[0].Arguments[3]));
            Assert.Equal(updatedInterval, Assert.IsType<int>(calls[1].Arguments[3]));
        }
        finally
        {
            pending.SetVoidResult();
            await barrier.DrainAsync();
        }
    }

    /// <summary>Verifies every rendered lane follows connected branches and shared consumers are no longer interleaved.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Render_CrossedBranches_OrdersEveryLaneAndPreservesSelectionOnRefresh(bool sharedVirtual)
    {
        // Arrange
        await using var context = new BunitContext();
        (string Id, StreamingFlowLane Lane)[] stages =
        [
            ("physical:a", StreamingFlowLane.PhysicalSource), ("physical:b", StreamingFlowLane.PhysicalSource),
            ("source:z", StreamingFlowLane.PhysicalProcessing), ("source:a", StreamingFlowLane.PhysicalProcessing),
            ("virtual:z", StreamingFlowLane.VirtualOutput), ("virtual:a", StreamingFlowLane.VirtualOutput),
            ("output:z", StreamingFlowLane.VirtualProcessing), ("output:a", StreamingFlowLane.VirtualProcessing),
            ("consumer:a", StreamingFlowLane.Consumer), ("consumer:b", StreamingFlowLane.Consumer), ("consumer:c", StreamingFlowLane.Consumer),
            ("physical:idle", StreamingFlowLane.PhysicalSource), ("source:idle", StreamingFlowLane.PhysicalProcessing),
            ("unassigned", StreamingFlowLane.VirtualProcessing)
        ];
        var nodes = stages.Select(stage => new StreamingFlowNode(stage.Id, stage.Lane, stage.Id, "", StreamingFlowState.Active, [])).ToArray();
        var firstVirtual = sharedVirtual ? "virtual:a" : "virtual:z";
        (string Source, string Target)[] pairs =
        [
            ("physical:a", "source:z"), ("physical:b", "source:a"),
            ("source:z", firstVirtual), ("source:a", "virtual:a"),
            (firstVirtual, "output:z"), ("virtual:a", "output:a"),
            ("output:z", "consumer:a"), ("output:a", "consumer:b"), ("output:z", "consumer:c"),
            ("physical:idle", "source:idle")
        ];
        var edges = pairs.Select((pair, index) => new StreamingFlowEdge($"edge:{index}", pair.Source, pair.Target)).ToArray();
        var snapshot = new StreamingFlowSnapshot([], [], 3, 2, 0) { Topology = new(nodes, edges, [], []) };
        var cut = context.Render<StreamingFlowDiagram>(parameters => parameters.Add(component => component.Snapshot, snapshot));
        string[] ReadOrder() => cut.FindAll("[data-flow-node-id]").Select(card => card.GetAttribute("data-flow-node-id")!).ToArray();
        var originalOrder = ReadOrder();

        // Act
        await cut.Find("[data-flow-node-id='consumer:c']").ClickAsync(new());
        cut.Render(parameters => parameters.Add(component => component.Snapshot, snapshot with
        {
            Topology = new(nodes.Reverse().Select(node => node with { ActiveSessionCount = 4, Details = [new("Telemetry", "Updated")] }).ToArray(),
                edges.Reverse().ToArray(), [], [])
        }));

        // Assert
        Assert.Equal(0, CountCrossings(originalOrder.Select(id => nodes.Single(node => node.Id == id)).ToArray(), edges));
        Assert.Equal(originalOrder, ReadOrder());
        Assert.Equal(nodes.Select(node => node.Id).Order(StringComparer.Ordinal), originalOrder.Order(StringComparer.Ordinal));
        var consumers = cut.FindAll("[data-flow-lane='Consumer'] [data-flow-node-id]").Select(card => card.GetAttribute("data-flow-node-id")).ToArray();
        Assert.Equal(1, Math.Abs(Array.IndexOf(consumers, "consumer:a") - Array.IndexOf(consumers, "consumer:c")));
        Assert.Equal("true", cut.Find("[data-flow-node-id='consumer:c']").GetAttribute("aria-pressed"));
        Assert.Contains("Updated", cut.Find("#streamingFlowDetails").TextContent);
    }

    /// <summary>Verifies retained processing chains remain contiguous and upstream-first while crossings are reduced.</summary>
    [Fact]
    public void Order_MultistageWarmProducers_KeepsEachChainTogether()
    {
        // Arrange
        var nodes = new[]
        {
            new StreamingFlowNode("virtual:a", StreamingFlowLane.VirtualOutput, "A", "", StreamingFlowState.Warm, []),
            new StreamingFlowNode("virtual:b", StreamingFlowLane.VirtualOutput, "B", "", StreamingFlowState.Warm, []),
            new StreamingFlowNode("z:mpegts-a", StreamingFlowLane.VirtualProcessing, "MPEG-TS", "", StreamingFlowState.Warm, []),
            new StreamingFlowNode("a:cmaf-a", StreamingFlowLane.VirtualProcessing, "CMAF", "", StreamingFlowState.Warm, []),
            new StreamingFlowNode("z:mpegts-b", StreamingFlowLane.VirtualProcessing, "MPEG-TS", "", StreamingFlowState.Warm, []),
            new StreamingFlowNode("a:cmaf-b", StreamingFlowLane.VirtualProcessing, "CMAF", "", StreamingFlowState.Warm, []),
            new StreamingFlowNode("consumer:a", StreamingFlowLane.Consumer, "A", "", StreamingFlowState.Active, []),
            new StreamingFlowNode("consumer:b", StreamingFlowLane.Consumer, "B", "", StreamingFlowState.Active, []),
            new StreamingFlowNode("consumer:c", StreamingFlowLane.Consumer, "C", "", StreamingFlowState.Active, [])
        };
        StreamingFlowEdge[] edges =
        [
            new("a", "virtual:a", "z:mpegts-a"), new("b", "virtual:b", "z:mpegts-b"),
            new("c", "z:mpegts-a", "a:cmaf-a"), new("d", "z:mpegts-b", "a:cmaf-b"),
            new("e", "a:cmaf-a", "consumer:b"), new("f", "a:cmaf-b", "consumer:a"), new("g", "z:mpegts-a", "consumer:c")
        ];

        // Act
        var ordered = StreamingFlowLayout.Order(nodes, edges);

        // Assert
        var processors = ordered.Where(node => node.Lane == StreamingFlowLane.VirtualProcessing).Select(node => node.Id).ToArray();
        Assert.Equal(["z:mpegts-a", "a:cmaf-a", "z:mpegts-b", "a:cmaf-b"], processors);
        Assert.Equal(0, CountCrossings(ordered, edges));
        Assert.All(ordered.Where(node => node.Lane != StreamingFlowLane.Consumer), node => Assert.Equal(StreamingFlowState.Warm, node.State));
    }

    /// <summary>Verifies a shared destination is positioned between connected branches rather than sorted by identity.</summary>
    [Fact]
    public void Order_MultipleParents_RemovesAvoidableConvergingCrossing()
    {
        // Arrange
        StreamingFlowNode[] nodes =
        [
            new("source:a", StreamingFlowLane.PhysicalSource, "A", "", StreamingFlowState.Active, []),
            new("source:b", StreamingFlowLane.PhysicalSource, "B", "", StreamingFlowState.Active, []),
            new("virtual:a", StreamingFlowLane.VirtualOutput, "Shared", "", StreamingFlowState.Shared, []),
            new("virtual:b", StreamingFlowLane.VirtualOutput, "One source", "", StreamingFlowState.Active, [])
        ];
        StreamingFlowEdge[] edges =
        [
            new("a", "source:a", "virtual:a"), new("b", "source:a", "virtual:b"), new("c", "source:b", "virtual:a")
        ];

        // Act
        var ordered = StreamingFlowLayout.Order(nodes, edges);

        // Assert
        Assert.Equal(1, CountCrossings(nodes, edges));
        Assert.Equal(0, CountCrossings(ordered, edges));
        Assert.Equal(["virtual:b", "virtual:a"], ordered.Where(node => node.Lane == StreamingFlowLane.VirtualOutput).Select(node => node.Id));
    }

    private static int CountCrossings(IReadOnlyList<StreamingFlowNode> nodes, IReadOnlyList<StreamingFlowEdge> edges)
    {
        var positions = nodes.GroupBy(node => node.Lane).SelectMany(lane => lane.Select((node, rank) => (node.Id, node.Lane, Rank: rank)))
            .ToDictionary(item => item.Id, StringComparer.Ordinal);
        return edges.SelectMany((edge, index) => edges.Skip(index + 1).Select(other => (edge, other)))
            .Count(pair => positions[pair.edge.Source].Lane != positions[pair.edge.Target].Lane &&
                positions[pair.edge.Source].Lane == positions[pair.other.Source].Lane && positions[pair.edge.Target].Lane == positions[pair.other.Target].Lane &&
                (positions[pair.edge.Source].Rank - positions[pair.other.Source].Rank) * (positions[pair.edge.Target].Rank - positions[pair.other.Target].Rank) < 0);
    }

    /// <summary>Verifies graph and keyboard selection share current details without resetting on telemetry refresh.</summary>
    [Fact]
    public async Task Render_RuntimeGraph_UpdatesSelectedDetailsAndClearsRemovedNodes()
    {
        // Arrange
        await using var context = new BunitContext();
        var node = new StreamingFlowNode("runtime:source", StreamingFlowLane.PhysicalSource, "Antenna", "7.1", StreamingFlowState.Active, [new("Receivers", "1")]);
        var snapshot = new StreamingFlowSnapshot([], [], 1, 1, 0) { Graph = new([node], [], [], []) };
        var cut = context.Render<StreamingFlowDiagram>(parameters => parameters.Add(component => component.Snapshot, snapshot));

        // Act
        await cut.Find("[data-flow-node-id]").ClickAsync(new());
        var updated = node with { Details = [new("Receivers", "2")] };
        cut.Render(parameters => parameters.Add(component => component.Snapshot, snapshot with { Graph = new([updated], [], [], []) }));

        // Assert
        Assert.Contains("Receivers", cut.Find("#streamingFlowDetails").TextContent);
        Assert.Equal("2", cut.Find("#streamingFlowDetails dd").TextContent);
        Assert.Equal("true", cut.Find("[data-flow-node-id]").GetAttribute("aria-pressed"));
        var updates = context.JSInterop.Invocations["lineupFlowGraph.update"].Count;
        cut.Render();
        Assert.Equal(updates, context.JSInterop.Invocations["lineupFlowGraph.update"].Count);
        cut.Render(parameters => parameters.Add(component => component.Snapshot, snapshot with { Graph = new([], [], [], []) }));
        Assert.Empty(cut.FindAll("#streamingFlowDetails"));
    }

    /// <summary>Verifies node selection is the only highlight control and clearing selection restores the whole graph.</summary>
    [Fact]
    public async Task Render_Topology_KeepsIdleDevicesAndTogglesNodeSelection()
    {
        // Arrange
        await using var context = new BunitContext();
        var consumer = new StreamingFlowNode("consumer:one", StreamingFlowLane.Consumer, "Endpoint", "7.1", StreamingFlowState.Active, []);
        var idle = new StreamingFlowNode("physical:idle", StreamingFlowLane.PhysicalSource, "Idle antenna", "HDHomeRun", StreamingFlowState.Idle, []);
        var path = new StreamingFlowPath("path:one", "one", "Endpoint", HostedStreamFormat.Cmaf, "7.1", DateTime.UtcNow, [], false, false, 1);
        var snapshot = new StreamingFlowSnapshot([], [path], 1, 1, 0) { Topology = new([consumer, idle], [], [new(path.Id, [consumer.Id], [])], []) };
        var cut = context.Render<StreamingFlowDiagram>(parameters => parameters.Add(component => component.Snapshot, snapshot));
        Assert.Equal(2, cut.FindAll("[data-flow-node-id]").Count);

        // Act
        await cut.Find("[data-flow-node-id='consumer:one']").ClickAsync(new());

        // Assert
        Assert.Equal(2, cut.FindAll("[data-flow-node-id]").Count);
        var model = JsonSerializer.SerializeToElement(context.JSInterop.Invocations["lineupFlowGraph.update"].Last().Arguments[1]);
        Assert.False(model.TryGetProperty("SelectedPathId", out _));
        Assert.Equal(consumer.Id, model.GetProperty("SelectedNodeId").GetString());
        Assert.Empty(cut.FindAll("select"));
        await cut.Find("[data-flow-node-id='consumer:one']").ClickAsync(new());
        model = JsonSerializer.SerializeToElement(context.JSInterop.Invocations["lineupFlowGraph.update"].Last().Arguments[1]);
        Assert.Equal(JsonValueKind.Null, model.GetProperty("SelectedNodeId").ValueKind);
        Assert.Empty(cut.FindAll("#streamingFlowDetails, .streaming-flow-node.selected"));
        await cut.Find("[data-flow-node-id='physical:idle']").ClickAsync(new());
        cut.Render(parameters => parameters.Add(component => component.Snapshot,
            snapshot with { ActivePaths = [], Topology = new([idle], [], [], []) }));
        Assert.Contains("Idle antenna", cut.Find("#streamingFlowDetails").TextContent);
        Assert.Single(cut.FindAll("[data-flow-node-id]"));
        Assert.Empty(cut.FindAll("input[type=checkbox]"));
    }

    /// <summary>Verifies producer selection reports every current route and remains selected as shared consumers change.</summary>
    [Fact]
    public async Task Render_SharedProducer_UpdatesRoutesWithoutSelectingOneConsumer()
    {
        // Arrange
        await using var context = new BunitContext();
        var producer = new StreamingFlowNode("source:shared", StreamingFlowLane.PhysicalProcessing, "Pass-through", "2.2", StreamingFlowState.Shared, []);
        var first = new StreamingFlowNode("consumer:first", StreamingFlowLane.Consumer, "First viewer", "2.2", StreamingFlowState.Active, []);
        var second = first with { Id = "consumer:second", Label = "Second viewer" };
        var snapshot = new StreamingFlowSnapshot([], [], 2, 1, 0)
        {
            Topology = new([producer, first, second], [],
                [new("path:first", [producer.Id, first.Id], []), new("path:second", [producer.Id, second.Id], [])], [])
        };
        var cut = context.Render<StreamingFlowDiagram>(parameters => parameters.Add(component => component.Snapshot, snapshot));

        // Act
        await cut.Find("[data-flow-node-id='source:shared']").ClickAsync(new());
        var initial = JsonSerializer.SerializeToElement(context.JSInterop.Invocations["lineupFlowGraph.update"].Last().Arguments[1]);
        cut.Render(parameters => parameters.Add(component => component.Snapshot,
            snapshot with { Topology = new([producer, second], [], [new("path:second", [producer.Id, second.Id], [])], []) }));

        // Assert
        Assert.Equal(producer.Id, initial.GetProperty("SelectedNodeId").GetString());
        Assert.Equal(2, initial.GetProperty("Paths").GetArrayLength());
        var updated = JsonSerializer.SerializeToElement(context.JSInterop.Invocations["lineupFlowGraph.update"].Last().Arguments[1]);
        Assert.Equal(producer.Id, updated.GetProperty("SelectedNodeId").GetString());
        Assert.Equal("path:second", updated.GetProperty("Paths")[0].GetProperty("Id").GetString());
        Assert.Equal("true", cut.Find("[data-flow-node-id='source:shared']").GetAttribute("aria-pressed"));
        Assert.Empty(cut.FindAll("select"));
    }

    /// <summary>Verifies the five combined lane boxes retain explicit idle pass-through stages without duplicate inventory.</summary>
    [Fact]
    public async Task Render_IdleTopology_PreservesOriginalCardsAndEmptyReusableOutputCollection()
    {
        // Arrange
        await using var context = new BunitContext();
        StreamingFlowNode[] nodes =
        [
            new("physical:one", StreamingFlowLane.PhysicalSource, "Antenna", "HDHomeRun", StreamingFlowState.Idle, []),
            new("physical-processing:one", StreamingFlowLane.PhysicalProcessing, "Pass-through", "Antenna", StreamingFlowState.Idle, []),
            new("virtual:one", StreamingFlowLane.VirtualOutput, "DVR", "HDHomeRun", StreamingFlowState.Idle, []),
            new("virtual-processing:one", StreamingFlowLane.VirtualProcessing, "Pass-through", "DVR", StreamingFlowState.Idle, [])
        ];
        var snapshot = new StreamingFlowSnapshot(nodes, [], 0, 0, 0) { Topology = new(nodes, [], [], []) };

        // Act
        var cut = context.Render<StreamingFlowDiagram>(parameters => parameters.Add(component => component.Snapshot, snapshot));
        await cut.Find("[data-flow-node-id='physical-processing:one']").ClickAsync(new());

        // Assert
        Assert.Equal(5, cut.FindAll(".streaming-flow-lane").Count);
        Assert.Equal(4, cut.FindAll(".streaming-flow-node").Count);
        Assert.Empty(cut.FindAll("[data-flow-node-id='multiplexer']"));
        Assert.Empty(cut.FindAll("[data-flow-lane='Multiplexer'], .streaming-flow-inventory"));
        Assert.Contains("Source Processing Multiplexer", cut.Find("[data-flow-lane='PhysicalProcessing'] h6").TextContent);
        Assert.Contains("Output Processing Multiplexer", cut.Find("[data-flow-lane='VirtualProcessing'] h6").TextContent);
        Assert.Equal(2, cut.FindAll(".streaming-flow-node-title").Count(element => element.TextContent == "Pass-through"));
        Assert.Contains("Pass-through", cut.Find("#streamingFlowDetails").TextContent);
        Assert.Empty(cut.FindAll(".video-encoder-unknown"));
        Assert.Empty(cut.FindAll("canvas, details"));
    }

    /// <summary>Verifies a retained producer is selectable once inside its output processing multiplexer.</summary>
    [Fact]
    public async Task Render_ReusableOutput_SelectsDetailsInsideTheMultiplexerLane()
    {
        // Arrange
        await using var context = new BunitContext();
        var producer = new StreamingFlowNode("output-processing:output:one", StreamingFlowLane.VirtualProcessing, "Output processing", "one", StreamingFlowState.Warm,
            [new("Reusable output", "one"), new("Direct viewers", "0")]);
        var snapshot = new StreamingFlowSnapshot([], [], 0, 0, 1) { Topology = new([producer], [], [], []) };
        var cut = context.Render<StreamingFlowDiagram>(parameters => parameters.Add(component => component.Snapshot, snapshot));

        // Act
        await cut.Find("[data-flow-node-id='output-processing:output:one']").ClickAsync(new());

        // Assert
        Assert.Single(cut.FindAll("[data-flow-node-id='output-processing:output:one']"));
        Assert.Single(cut.FindAll("[data-flow-lane='VirtualProcessing'] button"));
        Assert.Empty(cut.FindAll("[data-flow-node-id='multiplexer']"));
        Assert.Contains("Direct viewers", cut.Find("#streamingFlowDetails").TextContent);
        Assert.Equal("true", cut.Find("[data-flow-node-id='output-processing:output:one']").GetAttribute("aria-pressed"));
        Assert.Empty(cut.FindAll(".streaming-flow-inventory"));
    }

    /// <summary>Verifies source copies and output encoders appear once on their own reusable producer cards.</summary>
    [Theory]
    [InlineData("h264_amf")]
    [InlineData(null)]
    public async Task Render_ReusableOutputProcessing_ShowsEncoderOnlyOnProcessingStage(string? encoder)
    {
        // Arrange
        await using var context = new BunitContext();
        var processor = new StreamingFlowNode("output-processing:output:first", StreamingFlowLane.VirtualProcessing, "Output Transcoder", "Web Player", StreamingFlowState.Shared,
            [new("Producer identity", "output:first")])
        {
            VideoEncodings = encoder is null ? [] : [new(encoder, HardwareVideoEncoder.Amf, false)],
            ResourceIds = ["process-a"],
            ActiveSessionCount = 2,
            ActiveChannels = ["2.1"]
        };
        var otherProcessor = processor with
        {
            Id = "output-processing:output:second",
            ResourceIds = ["process-b"],
            ActiveSessionCount = 1,
            ActiveChannels = ["2.3"],
            Details = [new("Producer identity", "output:second")]
        };
        var first = new StreamingFlowNode("source-processing:runtime:first", StreamingFlowLane.PhysicalProcessing, "Pass-through", "2.1", StreamingFlowState.Shared,
            [new("Producer identity", "runtime:first")])
        {
            VideoEncodings = [new("copy", null, false)],
            ActiveSessionCount = 2
        };
        var second = first with { Id = "source-processing:runtime:second", Subtitle = "2.3", ActiveSessionCount = 1, Details = [new("Producer identity", "runtime:second")] };
        var snapshot = new StreamingFlowSnapshot([], [], 3, 2, 0) { Topology = new([first, second, processor, otherProcessor], [], [], []) };
        var cut = context.Render<StreamingFlowDiagram>(parameters => parameters.Add(component => component.Snapshot, snapshot));

        // Act
        await cut.Find("[data-flow-node-id='output-processing:output:second']").ClickAsync(new());

        // Assert
        var card = cut.Find("[data-flow-node-id='output-processing:output:second']");
        Assert.Single(card.QuerySelectorAll(".video-encoder-badge, .video-encoder-unknown"));
        Assert.Equal(2, cut.FindAll("[data-flow-lane='VirtualProcessing'] [data-flow-node-id]").Count);
        Assert.Contains("output:second", cut.Find("#streamingFlowDetails").TextContent);
        Assert.DoesNotContain("output:first", cut.Find("#streamingFlowDetails").TextContent);
        Assert.Contains("2.3", card.TextContent);
        Assert.Equal(encoder is null ? 2 : 4, cut.FindAll(".video-encoder-badge").Count);
        Assert.Equal(encoder is null ? 2 : 0, cut.FindAll(".video-encoder-unknown").Count);
        Assert.Empty(cut.FindAll("[data-flow-lane='Multiplexer'], [data-flow-node-id='output:second']"));
        Assert.Equal(2, cut.Find("[data-flow-lane='PhysicalProcessing']").QuerySelectorAll(".video-encoder-badge").Length);
        await cut.Find("[data-flow-node-id='source-processing:runtime:second']").ClickAsync(new());
        Assert.Contains("runtime:second", cut.Find("#streamingFlowDetails").TextContent);
    }

    /// <summary>Verifies a graph failure remains explicit and does not remove accessible node selection.</summary>
    [Fact]
    public async Task Render_GraphInitializationFailure_PreservesAccessibleDetails()
    {
        // Arrange
        await using var context = new BunitContext();
        context.JSInterop.SetupVoid("lineupFlowGraph.update", _ => true).SetException(new JSException("Graph bundle unavailable"));
        var node = new StreamingFlowNode("source", StreamingFlowLane.PhysicalSource, "Antenna", "HDHomeRun", StreamingFlowState.Active, []);
        var snapshot = new StreamingFlowSnapshot([node], [], 0, 0, 0);

        // Act
        var cut = context.Render<StreamingFlowDiagram>(parameters => parameters.Add(component => component.Snapshot, snapshot));

        // Assert
        cut.WaitForAssertion(() => Assert.Contains("could not be displayed", cut.Find("[role=alert]").TextContent), BunitContext.WaitTimeout);
        await cut.Find("[data-flow-node-id='source']").ClickAsync(new());
        Assert.Contains("Antenna", cut.Find("#streamingFlowDetails").TextContent);
    }

    /// <summary>Verifies server-side foreground callbacks cannot bypass manual-only or collapsed panel state.</summary>
    [Fact]
    public async Task RefreshVisibleStreams_RespectsManualOnlyAndCollapsedState()
    {
        // Arrange
        await using var context = new BunitContext();
        var calls = 0;
        var cut = context.Render<DashboardActiveStreamsPanel>(parameters => parameters
            .Add(component => component.Summary, "Streams")
            .Add(component => component.Expanded, true)
            .Add(component => component.RefreshRequested, () => calls++));

        // Act
        await cut.InvokeAsync(cut.Instance.RefreshVisibleStreams);
        cut.Render(parameters => parameters.Add(component => component.RefreshIntervalSeconds, 1).Add(component => component.Expanded, false));
        await cut.InvokeAsync(cut.Instance.RefreshVisibleStreams);
        var pausedCalls = calls;
        cut.Render(parameters => parameters.Add(component => component.Expanded, true));
        await cut.InvokeAsync(cut.Instance.RefreshVisibleStreams);

        // Assert
        Assert.Equal(0, pausedCalls);
        Assert.Equal(1, calls);
    }

    /// <summary>Verifies effective automatic deinterlacing and uncalculated cadence are visible in the shared diagnostic badge.</summary>
    [Fact]
    public async Task Render_QsvProcessing_ReportsAutomaticModeAndUnknownRate()
    {
        // Arrange
        await using var context = new BunitContext();
        var encoding = new VideoEncodingSnapshot("h264_qsv", HardwareVideoEncoder.Qsv, false)
        {
            Processing = new(SourceDeinterlacePolicy.SourceFieldRate, true, null, 120, true)
        };

        // Act
        var cut = context.Render<VideoEncoderBadge>(parameters => parameters.Add(component => component.Encoding, encoding));

        // Assert
        Assert.Equal("SourceFieldRate", cut.Find(".video-deinterlace").GetAttribute("data-deinterlace"));
        Assert.Equal("true", cut.Find(".video-deinterlace").GetAttribute("data-automatic-deinterlace"));
        Assert.Contains("Automatic QSV deinterlacing", cut.Find(".video-deinterlace").TextContent);
        Assert.Contains("120 frames; frame rate unavailable", cut.Find(".video-gop-rate-unknown").TextContent);
    }

    /// <summary>Verifies all processing badges share the wrapping and gap layout used by session identity badges.</summary>
    [Theory]
    [InlineData(StreamingFlowStageKind.SourceProcessing)]
    [InlineData(StreamingFlowStageKind.OutputProcessing)]
    public async Task Render_ProcessingDetails_BadgesUseSessionBadgeSpacingAndWrapping(StreamingFlowStageKind kind)
    {
        // Arrange
        await using var context = new BunitContext();
        var stage = new StreamingFlowPathStage(kind, "Processing", "Web Player", [])
        {
            VideoEncoding = new("libx264", HardwareVideoEncoder.Amf, true)
            {
                Processing = new(SourceDeinterlacePolicy.SourceFrameRate, false, null, 120, true)
            }
        };

        // Act
        var component = context.Render<StreamingFlowStageDetails>(parameters => parameters.Add(value => value.Stage, stage));

        // Assert
        var badges = component.Find("[data-flow-stage] > .d-flex.flex-wrap.align-items-center.gap-2");
        Assert.Equal(6, badges.QuerySelectorAll(":scope > .badge").Length);
        Assert.NotNull(badges.QuerySelector(".video-encoder-badge"));
        Assert.NotNull(badges.QuerySelector(".video-encoder-fallback"));
        Assert.NotNull(badges.QuerySelector(".video-deinterlace"));
        Assert.NotNull(badges.QuerySelector(".video-gop-rate-unknown"));
        Assert.Contains("Audio Unknown", badges.TextContent);
        Assert.Contains("Transport Unknown", badges.TextContent);
    }

    /// <summary>Verifies actual GPU, software fallback and copy outcomes are visible without expanding a dashboard path or node.</summary>
    [Theory]
    [InlineData("h264_amf", false, "GPU encoding", false)]
    [InlineData("libx264", true, "Software encoding", true)]
    [InlineData("copy", false, "Video copied", false)]
    [InlineData("unknown_encoder", false, "Video encoder:", false)]
    public async Task Render_RuntimeEncoder_ShowsOutcomeOnTopology(string encoder, bool fallback, string label, bool showsFallback)
    {
        // Arrange
        await using var context = new BunitContext();
        var encoding = new VideoEncodingSnapshot(encoder, HardwareVideoEncoder.Amf, fallback);
        var sourceEncoding = new VideoEncodingSnapshot("h264_qsv", HardwareVideoEncoder.Qsv, false);
        var snapshot = new StreamingFlowSnapshot(
            [new StreamingFlowNode("source", StreamingFlowLane.PhysicalProcessing, "Source Transcoder", "HDHomeRun", StreamingFlowState.Transcoding, [])
                { VideoEncodings = [sourceEncoding] },
             new StreamingFlowNode("output", StreamingFlowLane.VirtualProcessing, "Output Transcoder", "Web Player", StreamingFlowState.Transcoding, [])
                { VideoEncodings = [encoding] }],
            [new StreamingFlowPath("path", "session", "localhost", HostedStreamFormat.Cmaf, "2.1", DateTime.UtcNow,
                [new StreamingFlowPathStage(StreamingFlowStageKind.SourceProcessing, "Source Transcoder", "HDHomeRun", []) { VideoEncoding = sourceEncoding },
                 new StreamingFlowPathStage(StreamingFlowStageKind.OutputProcessing, "CMAF transcode", "Web Player", []) { VideoEncoding = encoding }], false, false, 1)],
            1, 1, 0);
        snapshot = snapshot with { Topology = new(snapshot.Nodes, [], [new("path", ["source", "output"], [])], []) };

        // Act
        var topology = context.Render<StreamingFlowDiagram>(parameters => parameters.Add(component => component.Snapshot, snapshot));

        // Assert
        Assert.Contains(label, topology.Find("[data-flow-node-id='output'] .video-encoder-badge").TextContent);
        Assert.Contains(encoder, topology.Find("[data-flow-node-id='output'] .video-encoder-badge").TextContent);
        Assert.Contains("GPU encoding", topology.Find("[data-flow-node-id='source']").TextContent);
        Assert.Contains(encoder, topology.Find("[data-flow-node-id='output']").GetAttribute("aria-label"));
        Assert.Equal("Requested encoder: Amf", topology.Find("[data-flow-node-id='output'] .video-encoder-badge").GetAttribute("title"));
        Assert.Equal(showsFallback, topology.FindAll(".video-encoder-fallback").Count == 1);
        Assert.Empty(topology.FindAll("#streamingFlowDetails"));
        await topology.Find("[data-flow-node-id='source']").ClickAsync(new());
        Assert.Contains("h264_qsv", topology.Find("[data-flow-stage='SourceProcessing'] .video-encoder-badge").TextContent);
        await topology.Find("[data-flow-node-id='output']").ClickAsync(new());
        Assert.Contains(label, topology.Find("[data-flow-stage='OutputProcessing'] .video-encoder-badge").TextContent);
        Assert.Equal("Requested encoder: Amf", topology.Find("[data-flow-stage='OutputProcessing'] .video-encoder-badge").GetAttribute("title"));
        Assert.Equal(showsFallback, topology.FindAll("[data-flow-stage='OutputProcessing'] .video-encoder-fallback").Count == 1);
    }

    /// <summary>Verifies missing producer telemetry is explicit on active processing cards instead of suggesting a configured GPU is in use.</summary>
    [Theory]
    [InlineData(StreamingFlowLane.PhysicalProcessing, StreamingFlowStageKind.SourceProcessing)]
    [InlineData(StreamingFlowLane.VirtualProcessing, StreamingFlowStageKind.OutputProcessing)]
    public async Task Render_ActiveProcessingWithoutTelemetry_ShowsNotReported(StreamingFlowLane lane, StreamingFlowStageKind kind)
    {
        // Arrange
        await using var context = new BunitContext();
        var snapshot = new StreamingFlowSnapshot(
            [new StreamingFlowNode("processing", lane, "Transcoder", "Device", StreamingFlowState.Active, []) { ActiveSessionCount = 1 }],
            [new StreamingFlowPath("path", "session", "localhost", HostedStreamFormat.Cmaf, "2.1", DateTime.UtcNow,
                [new StreamingFlowPathStage(kind, "Transcoder", "Device", [])], false, false, 1)], 1, 1, 0);
        snapshot = snapshot with { Topology = new(snapshot.Nodes, [], [new("path", ["processing"], [])], []) };

        // Act
        var topology = context.Render<StreamingFlowDiagram>(parameters => parameters.Add(component => component.Snapshot, snapshot));

        // Assert
        Assert.Equal("Video encoder not reported", topology.Find(".video-encoder-unknown").TextContent);
        Assert.Contains("Video encoder not reported", topology.Find("[data-flow-node-id='processing']").GetAttribute("aria-label"));
        Assert.Empty(topology.FindAll(".video-encoder-badge"));
        await topology.Find("[data-flow-node-id='processing']").ClickAsync(new());
        Assert.Equal("Video encoder not reported", topology.Find("[data-flow-stage] .video-encoder-unknown").TextContent);
        Assert.Equal(2, topology.FindAll("[data-flow-stage] .streaming-flow-stage-media .text-body-secondary").Count);
    }

    /// <summary>Verifies graph details replace Active Paths without losing media, session metadata or session-specific stop routing.</summary>
    [Fact]
    public async Task Render_ActiveSharedGraph_ConsolidatesStageDetailsAndSessionStop()
    {
        // Arrange
        await using var context = new BunitContext();
        var startedAtUtc = DateTime.UtcNow.AddMinutes(-3);
        string? stoppedSessionId = null;
        var physical = new StreamingFlowNode(
            "physical:one",
            StreamingFlowLane.PhysicalSource,
            "Roof Antenna",
            "hdhomerun",
            StreamingFlowState.Active,
            [new StreamingFlowDetail("Adapter", "hdhomerun")])
        {
            ActiveChannels = ["7.1"],
            ActiveSessionCount = 2
        };
        var multiplexer = new StreamingFlowNode(
            "multiplexer",
            StreamingFlowLane.Multiplexer,
            "Shared Multiplexer",
            "Reference-counted stream graph",
            StreamingFlowState.Shared,
            [new StreamingFlowDetail("Logical consumers", "2")])
        {
            ActiveChannels = ["7.1"],
            ActiveSessionCount = 2
        };
        var rawMedia = Media("mpeg2video", "ac3");
        var effectiveMedia = Media("h264", "ac3");
        var outputMedia = Media("h264", "aac");
        var snapshot = new StreamingFlowSnapshot(
            [
                physical,
                new("physical-processing:one", StreamingFlowLane.PhysicalProcessing, "Source Transcoder", "Roof Antenna", StreamingFlowState.Transcoding, []),
                multiplexer,
                new("virtual:one", StreamingFlowLane.VirtualOutput, "Living Room DVR", "hdhomerun", StreamingFlowState.Shared, []),
                new("virtual-processing:one", StreamingFlowLane.VirtualProcessing, "Output Transcoder", "Living Room DVR", StreamingFlowState.Transcoding, []),
                new("consumer:session-one", StreamingFlowLane.Consumer, "192.0.2.10:5000", "MpegTs · channel 7.1", StreamingFlowState.Active, [])
            ],
            [
                new StreamingFlowPath(
                    "path:one",
                    "session-one",
                    "192.0.2.10:5000",
                    HostedStreamFormat.MpegTs,
                    "7.1",
                    startedAtUtc,
                    [
                        Stage(StreamingFlowStageKind.PhysicalSource, "Roof Antenna", "HDHomeRun", rawMedia, rawMedia),
                        Stage(StreamingFlowStageKind.SourceProcessing, "Source Transcoder", "Roof Antenna", rawMedia, effectiveMedia),
                        Stage(StreamingFlowStageKind.Multiplexer, "Shared Multiplexer", "Reusable output", effectiveMedia, effectiveMedia),
                        Stage(StreamingFlowStageKind.VirtualOutput, "Living Room DVR", "HDHomeRun", effectiveMedia, effectiveMedia),
                        Stage(StreamingFlowStageKind.OutputProcessing, "MPEG-TS transcode", "Living Room DVR", effectiveMedia, outputMedia),
                        Stage(StreamingFlowStageKind.Consumer, "192.0.2.10:5000", "MPEG-TS", outputMedia, outputMedia)
                    ],
                    false,
                    true,
                    2)
            ],
            2,
            2,
            0);
        snapshot = snapshot with
        {
            Topology = new(snapshot.Nodes, [],
                [new("path:one", ["physical:one", "physical-processing:one", "virtual:one", "virtual-processing:one", "consumer:session-one"], [])], [])
        };

        // Act
        var component = context.Render<DashboardActiveStreamsPanel>(parameters => parameters
            .Add(item => item.Summary, "One session")
            .Add(item => item.Expanded, true)
            .Add(item => item.Snapshot, snapshot)
            .Add(item => item.StopRequested, sessionId => stoppedSessionId = sessionId));

        // Assert
        Assert.Empty(component.FindAll("#activeStreamsSection > .card"));
        Assert.Single(component.FindAll("#activeStreamsSection > #streamingFlowDiagram"));
        Assert.DoesNotContain("Active Paths", component.Markup);
        Assert.Empty(component.FindAll("#streamingFlowDetails, [aria-label^='Stop stream']"));
        var sourceProcessing = component.Find("[data-flow-node-id='physical-processing:one']");
        await sourceProcessing.ClickAsync(new());

        var details = component.Find("#streamingFlowDetails");
        Assert.Contains("Raw physical input", details.TextContent);
        Assert.Contains("mpeg2video", details.TextContent);
        Assert.Contains("Effective source components", details.TextContent);
        Assert.Contains("h264", details.TextContent);
        Assert.Contains("192.0.2.10:5000", details.TextContent);
        Assert.Contains("session-one", details.TextContent);
        Assert.Contains("Shared · 2 sessions", details.TextContent);
        Assert.Contains("1920x1080", details.TextContent);
        Assert.Contains("4 Mbps", details.TextContent);
        Assert.Contains("6 ch", details.TextContent);
        Assert.Equal("Stop stream session-one", Assert.Single(component.FindAll("[aria-label^='Stop stream']")).GetAttribute("aria-label"));
        Assert.Equal("true", sourceProcessing.GetAttribute("aria-expanded"));

        await component.Find("[data-flow-node-id='virtual-processing:one']").ClickAsync(new());

        details = Assert.Single(component.FindAll("#streamingFlowDetails"));
        Assert.Single(details.QuerySelectorAll("[data-flow-stage='OutputProcessing']"));
        Assert.Contains("Source stream components", details.TextContent);
        Assert.Contains("Output stream components", details.TextContent);
        Assert.Contains("aac", details.TextContent);
        Assert.Contains("Shared Multiplexer", details.TextContent);
        Assert.Equal("false", sourceProcessing.GetAttribute("aria-expanded"));

        await component.Find("[data-flow-node-id='consumer:session-one']").ClickAsync(new());
        Assert.Contains("Delivered stream components", component.Find("#streamingFlowDetails").TextContent);
        Assert.Single(component.FindAll("[aria-label^='Stop stream']"));
        component.Render(parameters => parameters.Add(item => item.StoppingSessionIds, new HashSet<string>(StringComparer.Ordinal) { "session-one" }));
        Assert.True(component.Find("[aria-label='Stop stream session-one']").HasAttribute("disabled"));
        component.Render(parameters => parameters.Add(item => item.StoppingSessionIds, new HashSet<string>(StringComparer.Ordinal)));
        await component.Find("[aria-label='Stop stream session-one']").ClickAsync(new());

        Assert.Equal("session-one", stoppedSessionId);
        component.Render(parameters => parameters.Add(item => item.Snapshot, snapshot with { Nodes = [], ActivePaths = [], Topology = new([], [], [], []) }));
        Assert.Empty(component.FindAll("#streamingFlowDetails, [aria-label^='Stop stream']"));
    }

    /// <summary>Verifies the consumer panel shows one canonical identity and metadata set while preserving controls and unique diagnostics.</summary>
    [Fact]
    public async Task Render_ConsumerDetails_CondensesRepeatedIdentityWithoutLosingStopMediaOrAdditionalMetadata()
    {
        // Arrange
        await using var context = new BunitContext();
        const string endpoint = "[::1]:57865";
        const string clientId = "client-identity";
        const string sessionId = "selected-session";
        var node = new StreamingFlowNode($"consumer:{sessionId}", StreamingFlowLane.Consumer, endpoint, "Cmaf · channel 2.1", StreamingFlowState.Active,
            [
                new("Session", sessionId), new("Endpoint", endpoint), new("Client ID", clientId), new("Channel", "2.1"), new("Format", "Cmaf"),
                new("Active", "stale-node-duration"), new("Node-only detail", "node-value")
            ])
        {
            ActiveChannels = ["2.1"],
            ActiveSessionCount = 1
        };
        var stage = Stage(StreamingFlowStageKind.Consumer, endpoint, "CMAF · channel 2.1", Media("h264", "aac"), Media("h264", "aac")) with
        {
            Details =
            [
                new("Session", sessionId), new("Client address", endpoint), new("Client ID", clientId), new("Format", "CMAF"),
                new("Active", "stale-stage-duration"), new("Stage-only detail", "stage-value")
            ]
        };
        var path = new StreamingFlowPath($"path:{sessionId}", sessionId, endpoint, HostedStreamFormat.Cmaf, "2.1", DateTime.UtcNow.AddMinutes(-5), [stage], false, true, 2);
        var snapshot = new StreamingFlowSnapshot([node], [path], 1, 1, 0);
        string? stopped = null;
        var component = context.Render<StreamingFlowDiagram>(parameters => parameters
            .Add(item => item.Snapshot, snapshot)
            .Add(item => item.Resources, new DashboardResourceSnapshot(true, [], []))
            .Add(item => item.StopRequested, session => stopped = session));

        // Act
        await component.Find($"[data-flow-node-id='{node.Id}']").ClickAsync(new());

        // Assert
        var details = component.Find("#streamingFlowDetails");
        Assert.Single(details.QuerySelectorAll("h6"), heading => heading.TextContent == endpoint);
        Assert.Single(details.QuerySelectorAll("dt"), label => label.TextContent == "Session");
        Assert.Single(details.QuerySelectorAll("dd"), value => value.TextContent.Trim() == sessionId);
        Assert.Single(details.QuerySelectorAll("dt"), label => label.TextContent == "Active");
        Assert.Single(details.QuerySelectorAll("dt"), label => label.TextContent == "Client ID");
        Assert.Single(details.QuerySelectorAll("dd"), value => value.TextContent.Trim() == clientId);
        Assert.DoesNotContain(details.QuerySelectorAll("dt"), label => label.TextContent is "Endpoint" or "Client address" or "Format" or "Channel" or "Active sessions" or "Active channels");
        Assert.DoesNotContain("Cmaf", details.TextContent);
        Assert.DoesNotContain("stale-node-duration", details.TextContent);
        Assert.DoesNotContain("stale-stage-duration", details.TextContent);
        Assert.Contains("node-value", details.TextContent);
        Assert.Contains("stage-value", details.TextContent);
        Assert.Contains("CMAF", details.TextContent);
        Assert.Contains("Channel 2.1", details.TextContent);
        Assert.Contains("Shared · 2 sessions", details.TextContent);
        Assert.Contains("Delivered stream components", details.TextContent);
        Assert.Contains("1920x1080", details.TextContent);
        Assert.Contains("h264", details.TextContent);
        Assert.Contains("aac", details.TextContent);
        Assert.Single(details.QuerySelectorAll("[data-resource-stage]"));
        Assert.Contains("outside Lineup", details.TextContent);
        await component.Find($"[aria-label='Stop stream {sessionId}']").ClickAsync(new());
        Assert.Equal(sessionId, stopped);
    }

    /// <summary>Verifies shared producer metadata is rendered once without hiding different policies or media, including after a session leaves.</summary>
    [Fact]
    public async Task Render_SharedDetails_CondensesCommonFieldsAndKeepsSessionDifferencesScoped()
    {
        // Arrange
        await using var context = new BunitContext();
        var producer = new StreamingFlowNode("producer", StreamingFlowLane.VirtualProcessing, "Output Transcoder", "Web Player", StreamingFlowState.Shared,
            [new("Processing", "Virtual-device output transcode"), new("Effective policy", "first-policy"), new("Producer identity", "shared-producer")])
        {
            ActiveSessionCount = 2,
            ActiveChannels = ["2.1"],
            ResourceIds = ["process"]
        };
        var paths = new[] { "first", "second" }.Select(session => new StreamingFlowPath(
            $"path:{session}", session, $"endpoint-{session}", HostedStreamFormat.Cmaf, "2.1", DateTime.UtcNow,
            [
                Stage(StreamingFlowStageKind.OutputProcessing, "Repeated stage heading", "Web Player", Media("mpeg2video", "ac3"),
                    Media(session == "first" ? "h264" : "hevc", session == "first" ? "aac" : "opus")) with
                {
                    Details = [new("Processing", "Virtual-device output transcode"), new("Effective policy", $"{session}-policy")]
                }
            ], false, true, 2)).ToArray();
        var snapshot = new StreamingFlowSnapshot([], paths, 2, 1, 0)
        {
            Topology = new([producer], [], paths.Select(path => new StreamingFlowGraphPath(path.Id, [producer.Id], [])).ToArray(), [])
        };
        var component = context.Render<StreamingFlowDiagram>(parameters => parameters
            .Add(item => item.Snapshot, snapshot)
            .Add(item => item.Resources, new DashboardResourceSnapshot(true, [new("process", "output", 123, 50, 1024, DateTimeOffset.UtcNow, null)], [])));

        // Act
        await component.Find("[data-flow-node-id='producer']").ClickAsync(new());

        // Assert
        var details = component.Find("#streamingFlowDetails");
        Assert.Single(details.QuerySelectorAll("dt"), label => label.TextContent == "Processing");
        Assert.Single(details.QuerySelectorAll("dt"), label => label.TextContent == "Producer identity");
        Assert.Single(details.QuerySelectorAll("[data-resource-id]"));
        Assert.DoesNotContain("Repeated stage heading", details.TextContent);
        foreach (var session in new[] { "first", "second" })
        {
            var group = details.QuerySelector($"[data-flow-session-id='{session}']")!;
            Assert.Single(group.QuerySelectorAll("dt"), label => label.TextContent == "Session");
            Assert.Single(group.QuerySelectorAll("dt"), label => label.TextContent == "Active");
            Assert.Single(group.QuerySelectorAll("dt"), label => label.TextContent == "Effective policy");
            Assert.Contains($"{session}-policy", group.TextContent);
            Assert.DoesNotContain(group.QuerySelectorAll("dt"), label => label.TextContent == "Processing");
        }
        Assert.Contains("h264", details.QuerySelector("[data-flow-session-id='first']")!.TextContent);
        Assert.Contains("opus", details.QuerySelector("[data-flow-session-id='second']")!.TextContent);

        component.Render(parameters => parameters.Add(item => item.Snapshot, snapshot with
        {
            ActivePaths = [paths[1]],
            Topology = snapshot.Topology with { Paths = [snapshot.Topology.Paths[1]] }
        }));
        details = component.Find("#streamingFlowDetails");
        Assert.Single(details.QuerySelectorAll("dt"), label => label.TextContent == "Session");
        Assert.Single(details.QuerySelectorAll("dt"), label => label.TextContent == "Processing");
        Assert.Single(details.QuerySelectorAll("dt"), label => label.TextContent == "Effective policy");
        Assert.Contains("second-policy", details.TextContent);
        Assert.DoesNotContain("first-policy", details.TextContent);
        Assert.Single(details.QuerySelectorAll("[data-resource-id]"));
        Assert.Contains("hevc", details.TextContent);
        Assert.Contains("opus", details.TextContent);
    }

    /// <summary>Verifies shared-node diagnostics retain each exact session without following unrelated branches through shared devices.</summary>
    [Fact]
    public async Task Render_SharedNodes_ShowOnlyExactRouteDiagnosticsAndRefreshSelectedMedia()
    {
        // Arrange
        await using var context = new BunitContext();
        (string Id, StreamingFlowLane Lane)[] definitions =
        [
            ("physical", StreamingFlowLane.PhysicalSource), ("source:a", StreamingFlowLane.PhysicalProcessing), ("source:b", StreamingFlowLane.PhysicalProcessing),
            ("virtual", StreamingFlowLane.VirtualOutput), ("output:a", StreamingFlowLane.VirtualProcessing), ("output:b", StreamingFlowLane.VirtualProcessing),
            ("consumer:one", StreamingFlowLane.Consumer), ("consumer:two", StreamingFlowLane.Consumer), ("consumer:three", StreamingFlowLane.Consumer)
        ];
        var nodes = definitions.Select(item => new StreamingFlowNode(item.Id, item.Lane, item.Id, "", StreamingFlowState.Shared, [])).ToArray();
        var paths = new[] { "one", "two", "three" }.Select(session => new StreamingFlowPath(
            $"path:{session}", session, $"endpoint-{session}", HostedStreamFormat.Cmaf, session == "three" ? "2.3" : "2.1", DateTime.UtcNow,
            [
                Stage(StreamingFlowStageKind.PhysicalSource, "Antenna", "Raw", Media("mpeg2video", "ac3"), Media("mpeg2video", "ac3")),
                Stage(StreamingFlowStageKind.SourceProcessing, $"source-stage-{session}", "Effective", Media("mpeg2video", "ac3"), Media("h264", "ac3")),
                Stage(StreamingFlowStageKind.VirtualOutput, $"virtual-stage-{session}", "Subscribed", Media("h264", "ac3"), Media("h264", "ac3")),
                Stage(StreamingFlowStageKind.OutputProcessing, $"output-stage-{session}", "Delivered", Media("h264", "ac3"), Media("h264", "aac"))
            ], false, session != "three", session == "three" ? 1 : 2)).ToArray();
        var routes = paths.Select(path =>
        {
            var producer = path.SessionId == "three" ? "b" : "a";
            return new StreamingFlowGraphPath(path.Id, ["physical", $"source:{producer}", "virtual", $"output:{producer}", $"consumer:{path.SessionId}"], []);
        }).ToArray();
        var snapshot = new StreamingFlowSnapshot([], paths, 3, 2, 0) { Topology = new(nodes, [], routes, []) };
        var stopped = new List<string>();
        var component = context.Render<StreamingFlowDiagram>(parameters => parameters
            .Add(item => item.Snapshot, snapshot)
            .Add(item => item.StoppingSessionIds, new HashSet<string>(StringComparer.Ordinal) { "one" })
            .Add(item => item.StopRequested, session => stopped.Add(session)));

        // Act
        await component.Find("[data-flow-node-id='source:a']").ClickAsync(new());

        // Assert
        Assert.Equal(["one", "two"], component.FindAll("[data-flow-session-id]").Select(item => item.GetAttribute("data-flow-session-id")));
        Assert.Contains("source-stage-one", component.Find("#streamingFlowDetails").TextContent);
        Assert.Contains("source-stage-two", component.Find("#streamingFlowDetails").TextContent);
        Assert.DoesNotContain("source-stage-three", component.Find("#streamingFlowDetails").TextContent);
        Assert.Equal(2, component.FindAll("[aria-label^='Stop stream']").Count);
        Assert.True(component.Find("[data-flow-session-id='one'] [aria-label='Stop stream one']").HasAttribute("disabled"));
        Assert.False(component.Find("[data-flow-session-id='two'] [aria-label='Stop stream two']").HasAttribute("disabled"));
        await component.Find("[data-flow-session-id='two'] [aria-label='Stop stream two']").ClickAsync(new());
        Assert.Equal(["two"], stopped);

        await component.Find("[data-flow-node-id='output:b']").ClickAsync(new());
        Assert.Equal("three", Assert.Single(component.FindAll("[data-flow-session-id]")).GetAttribute("data-flow-session-id"));
        Assert.Contains("output-stage-three", component.Find("#streamingFlowDetails").TextContent);
        Assert.DoesNotContain("output-stage-one", component.Find("#streamingFlowDetails").TextContent);
        Assert.Equal("Stop stream three", Assert.Single(component.FindAll("[aria-label^='Stop stream']")).GetAttribute("aria-label"));
        await component.Find("[aria-label='Stop stream three']").ClickAsync(new());
        Assert.Equal(["two", "three"], stopped);
        await component.Find("[data-flow-node-id='virtual']").ClickAsync(new());
        Assert.Equal(3, component.FindAll("[data-flow-stage='VirtualOutput']").Count);
        Assert.Equal(3, component.FindAll("[data-flow-session-id] [aria-label^='Stop stream']").Count);
        Assert.Contains("Subscribed stream components", component.Find("#streamingFlowDetails").TextContent);
        await component.Find("[data-flow-node-id='physical']").ClickAsync(new());
        Assert.Equal(3, component.FindAll("[data-flow-stage='PhysicalSource']").Count);
        Assert.Equal(3, component.FindAll("[data-flow-session-id] [aria-label^='Stop stream']").Count);
        Assert.Contains("Raw physical stream components", component.Find("#streamingFlowDetails").TextContent);

        await component.Find("[data-flow-node-id='output:a']").ClickAsync(new());
        component.Render(parameters => parameters.Add(item => item.Snapshot, snapshot with
        {
            ActivePaths = [paths[1] with { Stages = [Stage(StreamingFlowStageKind.OutputProcessing, "updated-output", "Updated", null, Media("hevc", "opus"))] }],
            Topology = snapshot.Topology with { Paths = [routes[1]] }
        }));
        Assert.Equal("two", Assert.Single(component.FindAll("[data-flow-session-id]")).GetAttribute("data-flow-session-id"));
        Assert.Contains("updated-output", component.Find("#streamingFlowDetails").TextContent);
        Assert.Contains("hevc", component.Find("#streamingFlowDetails").TextContent);
        Assert.Contains("opus", component.Find("#streamingFlowDetails").TextContent);
        Assert.DoesNotContain("endpoint-one", component.Find("#streamingFlowDetails").TextContent);
        Assert.Equal("Stop stream two", Assert.Single(component.FindAll("[aria-label^='Stop stream']")).GetAttribute("aria-label"));
        Assert.False(component.Find("[aria-label='Stop stream two']").HasAttribute("disabled"));
        Assert.Empty(component.FindAll("[data-flow-stage-boundary]"));
    }

    /// <summary>Verifies Stop All targets only the displayed sessions, skips pending stops and disappears for single-session or idle details.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Render_StopAll_StopsOnlyDisplayedSessions(bool firstAlreadyStopping)
    {
        // Arrange
        await using var context = new BunitContext();
        var snapshot = SharedStopSnapshot();
        var stopped = new List<string>();
        var component = context.Render<StreamingFlowDiagram>(parameters => parameters
            .Add(item => item.Snapshot, snapshot)
            .Add(item => item.StoppingSessionIds, new HashSet<string>(firstAlreadyStopping ? ["one"] : [], StringComparer.Ordinal))
            .Add(item => item.StopRequested, session => stopped.Add(session)));
        await component.Find("[data-flow-node-id='shared']").ClickAsync(new());
        var stopAll = component.Find("[aria-label='Stop all streams for Shared output']");

        // Act
        await stopAll.ClickAsync(new());

        // Assert
        Assert.Equal("Stop All", stopAll.TextContent.Trim());
        Assert.NotNull(stopAll.ParentElement!.QuerySelector(".badge.text-bg-success"));
        Assert.Equal(firstAlreadyStopping ? ["two"] : ["one", "two"], stopped);
        Assert.Equal(2, component.FindAll("[data-flow-session-id] [aria-label^='Stop stream']").Count);
        await component.Find("[data-flow-node-id='other']").ClickAsync(new());
        Assert.Empty(component.FindAll("[aria-label^='Stop all streams']"));
        Assert.Equal("Stop stream other", Assert.Single(component.FindAll("[aria-label^='Stop stream']")).GetAttribute("aria-label"));
        component.Render(parameters => parameters.Add(item => item.Snapshot, snapshot with { ActivePaths = [] }));
        Assert.Empty(component.FindAll("[aria-label^='Stop all streams'], [aria-label^='Stop stream']"));
    }

    /// <summary>Verifies queued stops retain their original scope across refreshes and selection changes while unrelated and new sessions remain usable.</summary>
    [Fact]
    public async Task Render_StopAll_InFlight_DisablesTargetsWithoutIncludingNewSessions()
    {
        // Arrange
        await using var context = new BunitContext();
        var snapshot = SharedStopSnapshot();
        Assert.NotNull(snapshot.Topology);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new List<string>();
        var component = context.Render<StreamingFlowDiagram>(parameters => parameters
            .Add(item => item.Snapshot, snapshot)
            .Add(item => item.StopRequested, async session =>
            {
                stopped.Add(session);
                if (session == "one")
                {
                    entered.SetResult();
                    await release.Task;
                }
            }));
        await component.Find("[data-flow-node-id='shared']").ClickAsync(new());
        Task stopAll = Task.CompletedTask;
        try
        {
            // Act
            stopAll = component.Find("[aria-label='Stop all streams for Shared output']").ClickAsync(new());
            await entered.Task.WaitAsync(LineupWebBunitContext.WaitTimeout, Xunit.TestContext.Current.CancellationToken);

            // Assert
            component.WaitForAssertion(() =>
            {
                Assert.True(component.Find("[aria-label='Stop all streams for Shared output']").HasAttribute("disabled"));
                Assert.All(component.FindAll("[aria-label^='Stop stream']"), button => Assert.True(button.HasAttribute("disabled")));
            }, timeout: LineupWebBunitContext.WaitTimeout);

            // Act
            var arrival = snapshot.ActivePaths[0] with { Id = "path:new", SessionId = "new", Endpoint = "endpoint-new" };
            component.Render(parameters => parameters.Add(item => item.Snapshot, snapshot with
            {
                ActivePaths = [snapshot.ActivePaths[1], snapshot.ActivePaths[2], arrival],
                Topology = snapshot.Topology with { Paths = [snapshot.Topology.Paths[1], snapshot.Topology.Paths[2], new(arrival.Id, ["shared"], [])] }
            }));

            // Assert
            Assert.True(component.Find("[aria-label='Stop stream two']").HasAttribute("disabled"));
            Assert.False(component.Find("[aria-label='Stop stream new']").HasAttribute("disabled"));

            // Act
            await component.Find("[data-flow-node-id='other']").ClickAsync(new());

            // Assert
            Assert.False(component.Find("[aria-label='Stop stream other']").HasAttribute("disabled"));

            // Act
            release.SetResult();
            await stopAll;
            await component.Find("[data-flow-node-id='shared']").ClickAsync(new());

            // Assert
            Assert.Equal(["one", "two"], stopped);
            Assert.All(component.FindAll("[aria-label^='Stop stream']"), button => Assert.False(button.HasAttribute("disabled")));
            Assert.False(component.Find("[aria-label='Stop all streams for Shared output']").HasAttribute("disabled"));
        }
        finally
        {
            release.TrySetResult();
            await stopAll;
        }
    }

    /// <summary>Verifies selecting one consumer never exposes another consumer's stop target or pending state.</summary>
    [Fact]
    public async Task Render_ConsumerSelection_StopsOnlySelectedSessionAndClearsActionWhenSessionDisappears()
    {
        // Arrange
        await using var context = new BunitContext();
        var nodes = new[] { "one", "two" }.Select(session => new StreamingFlowNode(
            $"consumer:{session}", StreamingFlowLane.Consumer, session, "", StreamingFlowState.Active, [])).ToArray();
        var paths = new[] { "one", "two" }.Select(session => new StreamingFlowPath(
            $"path:{session}", session, session, HostedStreamFormat.Cmaf, "2.1", DateTime.UtcNow, [], false, true, 2)).ToArray();
        var snapshot = new StreamingFlowSnapshot(nodes, paths, 2, 1, 0);
        var stopped = new List<string>();
        var component = context.Render<StreamingFlowDiagram>(parameters => parameters
            .Add(item => item.Snapshot, snapshot)
            .Add(item => item.StoppingSessionIds, new HashSet<string>(StringComparer.Ordinal) { "one" })
            .Add(item => item.StopRequested, session => stopped.Add(session)));

        // Act
        await component.Find("[data-flow-node-id='consumer:one']").ClickAsync(new());

        // Assert
        Assert.True(component.Find("[aria-label='Stop stream one']").HasAttribute("disabled"));
        await component.Find("[data-flow-node-id='consumer:two']").ClickAsync(new());
        Assert.False(component.Find("[aria-label='Stop stream two']").HasAttribute("disabled"));
        Assert.Single(component.FindAll("[aria-label^='Stop stream']"));
        await component.Find("[aria-label='Stop stream two']").ClickAsync(new());
        Assert.Equal(["two"], stopped);
        component.Render(parameters => parameters.Add(item => item.Snapshot, snapshot with { ActivePaths = [paths[0]] }));
        Assert.Empty(component.FindAll("[aria-label^='Stop stream'], [data-flow-session-id]"));
        await component.Find("[data-flow-node-id='consumer:two']").ClickAsync(new());
        Assert.Empty(component.FindAll("#streamingFlowDetails"));
    }

    /// <summary>Verifies chain diagnostics are explicitly session-wide while resource observations remain producer-specific, including retained producers.</summary>
    [Fact]
    public async Task Render_ProcessingChain_LabelsBoundaryDiagnosticsWithoutBorrowingOtherProducerResources()
    {
        // Arrange
        await using var context = new BunitContext();
        var intermediate = new StreamingFlowNode("intermediate", StreamingFlowLane.VirtualProcessing, "MPEG-TS transform", "", StreamingFlowState.Shared,
            [new("Processing", "Intermediate producer policy")])
        {
            ResourceIds = ["process-intermediate"],
            VideoEncodings = [new("h264_qsv", HardwareVideoEncoder.Qsv, false)]
        };
        var terminal = new StreamingFlowNode("terminal", StreamingFlowLane.VirtualProcessing, "Browser transform", "", StreamingFlowState.Shared, [])
        {
            ResourceIds = ["process-terminal"],
            VideoEncodings = [new("libx264", HardwareVideoEncoder.Amf, true)]
        };
        var stage = Stage(StreamingFlowStageKind.OutputProcessing, "Complete session stage", "", Media("mpeg2video", "ac3"), Media("h264", "aac")) with
        {
            ResourceIds = ["process-terminal"],
            VideoEncoding = terminal.VideoEncodings[0],
            Details = [new("Processing", "Complete boundary policy")]
        };
        var path = new StreamingFlowPath("path:one", "one", "browser", HostedStreamFormat.Cmaf, "2.1", DateTime.UtcNow, [stage], false, true, 2);
        var snapshot = new StreamingFlowSnapshot([], [path], 1, 2, 0)
        {
            Topology = new([intermediate, terminal], [new("chain", "intermediate", "terminal") { State = StreamingFlowState.Shared }],
                [new(path.Id, ["intermediate", "terminal"], ["chain"])], [])
        };
        var resources = new DashboardResourceSnapshot(true,
            [new("process-intermediate", "source", 101, 25, 1024, DateTimeOffset.UtcNow, null), new("process-terminal", "output", 202, 75, 2048, DateTimeOffset.UtcNow, null)], []);
        var component = context.Render<StreamingFlowDiagram>(parameters => parameters.Add(item => item.Snapshot, snapshot).Add(item => item.Resources, resources));

        // Act
        await component.Find("[data-flow-node-id='intermediate']").ClickAsync(new());

        // Assert
        Assert.Single(component.FindAll("[data-flow-stage-boundary]"));
        Assert.Equal("process-intermediate", Assert.Single(component.FindAll("[data-resource-id]")).GetAttribute("data-resource-id"));
        Assert.Contains("25%", component.Find("#streamingFlowDetails").TextContent);
        Assert.Contains("Intermediate producer policy", component.Find("#streamingFlowDetails").TextContent);
        Assert.DoesNotContain("Intermediate producer policy", component.Find("[data-flow-session-id='one']").TextContent);
        Assert.Contains("Complete boundary policy", component.Find("[data-flow-session-id='one']").TextContent);
        Assert.Contains("h264_qsv", component.Find("[data-flow-node-id='intermediate']").TextContent);
        Assert.Contains("mpeg2video", component.Find("[data-flow-stage]").TextContent);
        Assert.Contains("libx264", component.Find("[data-flow-stage]").TextContent);
        await component.Find("[data-flow-node-id='terminal']").ClickAsync(new());
        Assert.Equal("process-terminal", Assert.Single(component.FindAll("[data-resource-id]")).GetAttribute("data-resource-id"));
        component.Render(parameters => parameters.Add(item => item.Snapshot, snapshot with { ActivePaths = [], Topology = snapshot.Topology with { Paths = [] } }));
        Assert.Empty(component.FindAll("[data-flow-session-id]"));
        Assert.Equal("process-terminal", Assert.Single(component.FindAll("[data-resource-id]")).GetAttribute("data-resource-id"));
        Assert.Empty(component.FindAll("[aria-label^='Stop stream']"));
    }

    /// <summary>Verifies topology details remain collapsed until selection and collapse when selected again.</summary>
    [Fact]
    public async Task Render_ConfiguredTopology_TogglesSelectedNodeDetails()
    {
        // Arrange
        await using var context = new BunitContext();
        var snapshot = new StreamingFlowSnapshot(
            [
                new StreamingFlowNode(
                    "physical:one",
                    StreamingFlowLane.PhysicalSource,
                    "Roof Antenna",
                    "hdhomerun",
                    StreamingFlowState.Idle,
                    [new StreamingFlowDetail("Adapter", "hdhomerun")])
            ],
            [],
            0,
            0,
            0);

        // Act
        var component = context.Render<StreamingFlowDiagram>(parameters => parameters.Add(item => item.Snapshot, snapshot));

        // Assert
        var node = component.Find("[data-flow-node-id='physical:one']");
        Assert.Empty(component.FindAll("#streamingFlowDetails"));
        Assert.Equal("false", node.GetAttribute("aria-expanded"));

        await node.ClickAsync(new());

        Assert.Contains("Roof Antenna", component.Find("#streamingFlowDetails").TextContent);
        Assert.Single(component.FindAll("#streamingFlowDetails dt"), label => label.TextContent == "Adapter");
        Assert.Equal("true", node.GetAttribute("aria-expanded"));

        await node.ClickAsync(new());

        Assert.Empty(component.FindAll("#streamingFlowDetails"));
        Assert.Equal("false", node.GetAttribute("aria-expanded"));
    }

    /// <summary>Verifies an empty snapshot provides configuration guidance.</summary>
    [Fact]
    public async Task Render_EmptySnapshot_ShowsConfigurationGuidance()
    {
        // Arrange
        await using var context = new BunitContext();

        // Act
        var component = context.Render<StreamingFlowDiagram>(parameters =>
            parameters.Add(item => item.Snapshot, new StreamingFlowSnapshot([], [], 0, 0, 0)));

        // Assert
        Assert.Contains("Configure physical and virtual devices", component.Markup);
        Assert.Empty(component.FindAll(".streaming-flow-node"));
    }

    private static StreamingFlowSnapshot SharedStopSnapshot()
    {
        var paths = new[] { "one", "two", "other" }.Select(session => new StreamingFlowPath(
            $"path:{session}", session, $"endpoint-{session}", HostedStreamFormat.Cmaf, "2.1", DateTime.UtcNow, [], false, session != "other", session == "other" ? 1 : 2)).ToArray();
        return new([], paths, 3, 2, 0)
        {
            Topology = new(
                [
                    new("shared", StreamingFlowLane.VirtualOutput, "Shared output", "Web Player", StreamingFlowState.Shared, []) { ActiveSessionCount = 2 },
                    new("other", StreamingFlowLane.VirtualOutput, "Other output", "Web Player", StreamingFlowState.Active, []) { ActiveSessionCount = 1 }
                ], [],
                paths.Select(path => new StreamingFlowGraphPath(path.Id, [path.SessionId == "other" ? "other" : "shared"], [])).ToArray(), [])
        };
    }

    private static StreamingFlowPathStage Stage(StreamingFlowStageKind kind, string label, string subtitle, ActiveStreamMediaSnapshot? input, ActiveStreamMediaSnapshot? output) =>
        new(kind, label, subtitle, [new StreamingFlowDetail("Stage", label)])
        {
            InputMedia = input,
            OutputMedia = output
        };

    private static ActiveStreamMediaSnapshot Media(string videoCodec, string audioCodec) =>
        new(
            4_000_000,
            [
                new ActiveStreamMediaTrack(0, MediaTrackType.Video, videoCodec, 3_500_000, 1920, 1080, null, null),
                new ActiveStreamMediaTrack(1, MediaTrackType.Audio, audioCodec, 192_000, null, null, 6, 48_000)
            ]);

    private sealed class AfterRenderBarrier
    {
        private int _calls;

        /// <summary>Gets the first production callback's completion.</summary>
        public TaskCompletionSource FirstCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Gets the newer render's entry into its controlled callback.</summary>
        public TaskCompletionSource SecondEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Gets the gate admitting the newer production callback.</summary>
        public TaskCompletionSource ReleaseSecond { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Gets the newer production callback's completion.</summary>
        public TaskCompletionSource SecondCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Runs production callbacks with the newer browser acknowledgment deliberately held pending.</summary>
        public async Task RunAsync(Func<Task> callback)
        {
            var call = ++_calls;
            if (call == 2)
            {
                // Model a pending browser render-batch acknowledgment while the older JS call completes.
                SecondEntered.TrySetResult();
                await ReleaseSecond.Task;
            }
            try
            {
                await callback();
            }
            finally
            {
                if (call == 1) FirstCompleted.TrySetResult();
                if (call == 2) SecondCompleted.TrySetResult();
            }
        }

        /// <summary>Releases controlled work and observes every admitted callback before teardown.</summary>
        public async Task DrainAsync()
        {
            ReleaseSecond.TrySetResult();
            await FirstCompleted.Task.WaitAsync(BunitContext.WaitTimeout);
            if (SecondEntered.Task.IsCompleted)
            {
                await SecondCompleted.Task.WaitAsync(BunitContext.WaitTimeout);
            }
        }
    }

    private sealed class DelayedSecondFlowDiagram : StreamingFlowDiagram
    {
        /// <summary>Gets the controlled post-render acknowledgment ordering.</summary>
        public AfterRenderBarrier Barrier { get; } = new();

        /// <inheritdoc />
        protected override Task OnAfterRenderAsync(bool firstRender) => Barrier.RunAsync(() => base.OnAfterRenderAsync(firstRender));
    }

    private sealed class DelayedSecondStreamsPanel : DashboardActiveStreamsPanel
    {
        /// <summary>Gets the controlled post-render acknowledgment ordering.</summary>
        public AfterRenderBarrier Barrier { get; } = new();

        /// <inheritdoc />
        protected override Task OnAfterRenderAsync(bool firstRender) => Barrier.RunAsync(() => base.OnAfterRenderAsync(firstRender));
    }
}
