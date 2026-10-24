using Bunit;
using Lineup.Web.Components.Streaming;
using Xunit;

namespace Lineup.Web.Tests.Components;

/// <summary>Verifies the accessible dashboard streaming-flow visualization and active paths.</summary>
public sealed class StreamingFlowDiagramTests
{
    /// <summary>Verifies a session path exposes compact metadata and contextual stage details.</summary>
    [Fact]
    public void Render_ActiveSharedPath_OpensAndReplacesStageDetails()
    {
        // Arrange
        using var context = new BunitContext();
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
                new("consumer:one", StreamingFlowLane.Consumer, "192.0.2.10:5000", "MpegTs · channel 7.1", StreamingFlowState.Active, [])
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

        // Act
        var component = context.Render<StreamingFlowPaths>(parameters => parameters
            .Add(item => item.Snapshot, snapshot)
            .Add(item => item.StopRequested, sessionId => stoppedSessionId = sessionId));

        // Assert
        var path = component.Find(".streaming-flow-path");
        Assert.Contains("192.0.2.10:5000", path.TextContent);
        Assert.Contains("session-one", path.TextContent);
        Assert.Contains("Shared · 2 sessions", path.TextContent);
        Assert.Empty(component.FindAll(".streaming-flow-path-details"));

        var sourceProcessing = component.Find("[data-flow-stage='SourceProcessing']");
        sourceProcessing.Click();

        var details = component.Find(".streaming-flow-path-details");
        Assert.Contains("Raw physical input", details.TextContent);
        Assert.Contains("mpeg2video", details.TextContent);
        Assert.Contains("Effective source components", details.TextContent);
        Assert.Contains("h264", details.TextContent);
        Assert.Equal("true", sourceProcessing.GetAttribute("aria-expanded"));

        component.Find("[data-flow-stage='OutputProcessing']").Click();

        details = Assert.Single(component.FindAll(".streaming-flow-path-details"));
        Assert.Equal("OutputProcessing", details.GetAttribute("data-selected-stage"));
        Assert.Contains("Source stream components", details.TextContent);
        Assert.Contains("Output stream components", details.TextContent);
        Assert.Contains("aac", details.TextContent);
        Assert.Equal("false", sourceProcessing.GetAttribute("aria-expanded"));

        component.Find("[data-flow-stage='OutputProcessing']").Click();
        component.Find("[aria-label='Stop stream session-one']").Click();

        Assert.Empty(component.FindAll(".streaming-flow-path-details"));
        Assert.Equal("session-one", stoppedSessionId);
    }

    /// <summary>Verifies topology details remain collapsed until selection and collapse when selected again.</summary>
    [Fact]
    public void Render_ConfiguredTopology_TogglesSelectedNodeDetails()
    {
        // Arrange
        using var context = new BunitContext();
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

        node.Click();

        Assert.Contains("Roof Antenna", component.Find("#streamingFlowDetails").TextContent);
        Assert.Equal("true", node.GetAttribute("aria-expanded"));

        node.Click();

        Assert.Empty(component.FindAll("#streamingFlowDetails"));
        Assert.Equal("false", node.GetAttribute("aria-expanded"));
    }

    /// <summary>Verifies an empty snapshot provides configuration guidance.</summary>
    [Fact]
    public void Render_EmptySnapshot_ShowsConfigurationGuidance()
    {
        // Arrange
        using var context = new BunitContext();

        // Act
        var component = context.Render<StreamingFlowDiagram>(parameters =>
            parameters.Add(item => item.Snapshot, new StreamingFlowSnapshot([], [], 0, 0, 0)));

        // Assert
        Assert.Contains("Configure physical and virtual devices", component.Markup);
        Assert.Empty(component.FindAll(".streaming-flow-node"));
    }

    private static StreamingFlowPathStage Stage(
        StreamingFlowStageKind kind,
        string label,
        string subtitle,
        ActiveStreamMediaSnapshot input,
        ActiveStreamMediaSnapshot output) =>
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
}
