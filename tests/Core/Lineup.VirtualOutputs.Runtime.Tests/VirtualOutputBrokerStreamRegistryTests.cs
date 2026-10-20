using Lineup.Common.Streaming;
using Lineup.Core.Streaming;
using Lineup.Streaming.FFmpeg;
using Lineup.VirtualOutputs.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lineup.VirtualOutputs.Runtime.Tests;

/// <summary>Verifies deterministic and lease-backed cleanup of brokered worker streams.</summary>
public sealed class VirtualOutputBrokerStreamRegistryTests
{
    /// <summary>Verifies a correlated close request immediately unregisters and disposes its broker stream.</summary>
    [Fact]
    public async Task CloseAsync_CorrelatedRequest_ReleasesActiveStream()
    {
        // Arrange
        using var authority = new VirtualOutputSessionAuthority();
        var context = Register(authority, out var deviceId);
        var activeStreams = new ActiveStreamRegistry();
        var stream = new TrackingStream();
        var requestId = VirtualOutputStreamRequestId.New();
        var streamSessionId = StreamSessionId.New();
        activeStreams.Register(new ActiveStreamSnapshot(streamSessionId.ToString(), "48.3", HostedStreamFormat.MpegTs, DateTime.UtcNow, null, []));
        var brokered = CreateBrokeredStream(activeStreams, stream, streamSessionId);
        var registry = new VirtualOutputBrokerStreamRegistry(authority, NullLogger.Instance);
        var openRequest = CreateOpenRequest(context, requestId);
        Assert.True(registry.TryAdd(deviceId, openRequest, brokered));

        // Act
        var closed = await registry.CloseAsync(new VirtualOutputCloseStreamRequest
        {
            Session = context,
            RequestId = requestId,
            StreamSessionId = streamSessionId
        });

        // Assert
        Assert.True(closed);
        Assert.True(stream.IsDisposed);
        Assert.Empty(activeStreams.GetActiveStreams());
        Assert.Equal(0, registry.Count(deviceId));
    }

    /// <summary>Verifies lease revocation removes a broker stream even when its worker never sends a close request.</summary>
    [Fact]
    public async Task ExpireUnauthorizedAsync_RevokedWorker_ReleasesOrphanedStream()
    {
        // Arrange
        using var authority = new VirtualOutputSessionAuthority();
        var context = Register(authority, out var deviceId);
        var activeStreams = new ActiveStreamRegistry();
        var stream = new TrackingStream();
        var requestId = VirtualOutputStreamRequestId.New();
        var streamSessionId = StreamSessionId.New();
        activeStreams.Register(new ActiveStreamSnapshot(streamSessionId.ToString(), "48.3", HostedStreamFormat.MpegTs, DateTime.UtcNow, null, []));
        var registry = new VirtualOutputBrokerStreamRegistry(authority, NullLogger.Instance);
        Assert.True(registry.TryAdd(deviceId, CreateOpenRequest(context, requestId), CreateBrokeredStream(activeStreams, stream, streamSessionId)));
        Assert.True(authority.Revoke(deviceId, "Test worker disconnected."));

        // Act
        await registry.ExpireUnauthorizedAsync();

        // Assert
        Assert.True(stream.IsDisposed);
        Assert.Empty(activeStreams.GetActiveStreams());
        Assert.Equal(0, registry.Count(deviceId));
    }

    private static VirtualOutputSessionContext Register(VirtualOutputSessionAuthority authority, out VirtualDeviceId deviceId)
    {
        deviceId = VirtualDeviceId.New();
        var ticket = authority.CreateActivation(
            deviceId,
            "test-worker",
            new HashSet<VirtualOutputCapability> { VirtualOutputCapability.OpenStream },
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(1));
        var registration = authority.Register(new VirtualOutputWorkerRegistrationRequest
        {
            WorkerId = ticket.WorkerId,
            VirtualDeviceId = ticket.VirtualDeviceId,
            Generation = ticket.Generation,
            BootstrapToken = ticket.BootstrapToken,
            MinimumProtocolVersion = 1,
            MaximumProtocolVersion = 1
        });
        return new VirtualOutputSessionContext
        {
            SessionId = registration.SessionId!.Value,
            Generation = ticket.Generation
        };
    }

    private static VirtualOutputOpenStreamRequest CreateOpenRequest(VirtualOutputSessionContext context, VirtualOutputStreamRequestId requestId) =>
        new()
        {
            Session = context,
            RequestId = requestId,
            ChannelKey = "48.3",
            Stage = VirtualOutputStreamStage.PhysicalOutput
        };

    private static VirtualOutputBrokeredStream CreateBrokeredStream(IActiveStreamRegistry activeStreams, Stream stream, StreamSessionId streamSessionId)
    {
        var source = new MediaProbeResult(
            [new MediaTrackMetadata(0, MediaTrackType.Video, "h264", 4_000_000, 1280, 720, null, null)],
            4_000_000);
        return new VirtualOutputBrokeredStream(
            activeStreams,
            new VirtualMpegTsOutput(stream, source, source, new VirtualTranscodeProfile(), new ActiveStreamFlowContext()),
            new VirtualOutputOpenStreamResponse
            {
                Accepted = true,
                ContentType = "video/mp2t",
                StreamSessionId = streamSessionId,
                Source = source,
                Profile = new VirtualTranscodeProfile()
            });
    }

    private sealed class TrackingStream : MemoryStream
    {
        internal bool IsDisposed { get; private set; }

        public override ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return base.DisposeAsync();
        }
    }
}
