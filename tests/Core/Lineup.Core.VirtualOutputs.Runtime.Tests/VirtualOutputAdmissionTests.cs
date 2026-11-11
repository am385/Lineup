using System.Text.Json;
using Lineup.Core.Common.Streaming;
using Lineup.Core.Extensibility.Streaming;
using Lineup.Core.Guides;
using Lineup.Core.Settings;
using Lineup.Core.Streaming;
using Lineup.Core.Streaming.FFmpeg;
using Lineup.Core.VirtualOutputs.Protocol;
using NSubstitute;
using Xunit;

namespace Lineup.Core.VirtualOutputs.Runtime.Tests;

/// <summary>Verifies broker admission precedes physical acquisition and failed startups do not leak reservations.</summary>
public sealed class VirtualOutputAdmissionTests
{
    /// <summary>Verifies full active and pending budgets reject both worker media stages without opening a stream.</summary>
    [Theory]
    [InlineData(false, VirtualOutputStreamStage.VirtualOutput)]
    [InlineData(true, VirtualOutputStreamStage.VirtualOutput)]
    [InlineData(false, VirtualOutputStreamStage.PhysicalOutput)]
    [InlineData(true, VirtualOutputStreamStage.PhysicalOutput)]
    public async Task OpenStreamAsync_FullBudget_DoesNotOpenSource(bool pending, VirtualOutputStreamStage stage)
    {
        // Arrange
        var registry = new ActiveStreamRegistry(resources: new() { MaximumPendingStarts = 1 });
        using var reservation = pending ? registry.TryBeginStartup(0, () => { }) : null;
        if (!pending)
        {
            registry.Register(new ActiveStreamSnapshot("existing", "1", HostedStreamFormat.MpegTs, DateTime.UtcNow, null, []));
        }
        var (broker, streams, request) = CreateBroker(registry, pending ? 0 : 1);
        request = request with { Stage = stage };

        // Act
        var error = await Assert.ThrowsAsync<VirtualOutputBrokerException>(() => broker.OpenStreamAsync(request, TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(VirtualOutputFailureCode.ResourceLimit, error.FailureCode);
        Assert.Empty(streams.ReceivedCalls());
        Assert.Equal(pending ? 0 : 1, registry.GetActiveStreams().Count);
    }

    /// <summary>Verifies an acquisition failure releases the pending reservation for the next caller.</summary>
    [Fact]
    public async Task OpenStreamAsync_AcquisitionFails_ReleasesStartup()
    {
        // Arrange
        var registry = new ActiveStreamRegistry(resources: new() { MaximumPendingStarts = 1 });
        var (broker, streams, request) = CreateBroker(registry, 1);
        streams.OpenAsync(Arg.Any<VirtualDeviceId>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns<Task<VirtualMpegTsOutput>>(_ => throw new KeyNotFoundException("Missing channel."));

        // Act
        var error = await Assert.ThrowsAsync<VirtualOutputBrokerException>(() => broker.OpenStreamAsync(request, TestContext.Current.CancellationToken));
        using var next = registry.TryBeginStartup(1, () => { });

        // Assert
        Assert.Equal(VirtualOutputFailureCode.ChannelNotFound, error.FailureCode);
        Assert.NotNull(next);
        Assert.Empty(registry.GetActiveStreams());
    }

    /// <summary>Verifies dedicated Web Player startup ownership survives the broker return and ends on readiness or stream disposal.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenStreamAsync_PhysicalOutput_HoldsStartupUntilReadyOrClosed(bool ready)
    {
        // Arrange
        var registry = new ActiveStreamRegistry(resources: new() { MaximumPendingStarts = 1 });
        var (broker, streams, request) = CreateBroker(registry, 0);
        var media = new MediaProbeResult([], null);
        streams.OpenPhysicalOutputAsync(Arg.Any<VirtualDeviceId>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new VirtualMpegTsOutput(new MemoryStream([1]), media, media, new VirtualTranscodeProfile(), new ActiveStreamFlowContext()));

        // Act
        await using var opened = await broker.OpenStreamAsync(request with { Stage = VirtualOutputStreamStage.PhysicalOutput }, TestContext.Current.CancellationToken);
        Assert.Null(registry.TryBeginStartup(0, () => { }));
        if (ready)
        {
            registry.CompleteStartup(opened.Response.StreamSessionId!.Value.ToString());
        }
        else
        {
            await opened.DisposeAsync();
        }

        // Assert
        using var next = registry.TryBeginStartup(0, () => { });
        Assert.NotNull(next);
        Assert.Equal(ready ? 1 : 0, registry.GetActiveStreams().Count);
    }

    /// <summary>Verifies retained sources survive logical viewer stops and release viewer/startup capacity independently.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenStreamAsync_ViewerStop_RespectsIndependentProducerOwnership(bool retained)
    {
        // Arrange
        var registry = new ActiveStreamRegistry(resources: new() { MaximumPendingStarts = 1 });
        var (broker, streams, request) = CreateBroker(registry, 1);
        var source = new MemoryStream([1]);
        var media = new MediaProbeResult([], null);
        streams.OpenPhysicalOutputAsync(Arg.Any<VirtualDeviceId>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new VirtualMpegTsOutput(source, media, media, new VirtualTranscodeProfile(), new ActiveStreamFlowContext { ProducerNodeId = "exact-runtime-generation" }));
        await using var opened = await broker.OpenStreamAsync(
            request with { Stage = VirtualOutputStreamStage.PhysicalOutput, RetainSourceForProducer = retained }, TestContext.Current.CancellationToken);

        // Act
        var stopped = registry.RequestStop(opened.Response.StreamSessionId!.Value.ToString());
        var sourceRetained = source.CanRead;
        if (retained)
        {
            opened.ReleaseViewer();
        }
        using var availableBeforeSourceDisposal = registry.TryBeginStartup(1, () => { });
        var sourceReadableAfterRelease = source.CanRead;
        await opened.DisposeAsync();
        availableBeforeSourceDisposal?.Dispose();
        using var next = registry.TryBeginStartup(1, () => { });

        // Assert
        Assert.True(stopped);
        Assert.Equal(retained, sourceRetained);
        Assert.Equal(retained, sourceReadableAfterRelease);
        Assert.Equal(retained, availableBeforeSourceDisposal is not null);
        Assert.True(opened.ViewerStopRequested);
        Assert.Equal("exact-runtime-generation", opened.Response.ProducerNodeId);
        Assert.NotNull(next);
        Assert.Empty(registry.GetActiveStreams());
        Assert.False(source.CanRead);
    }

    private static (VirtualOutputBroker Broker, IVirtualMpegTsOutputService Streams, VirtualOutputOpenStreamRequest Request) CreateBroker(ActiveStreamRegistry registry, int maximum)
    {
        var id = VirtualDeviceId.New();
        var authority = Substitute.For<IVirtualOutputSessionAuthority>();
        authority.Authorize(Arg.Any<VirtualOutputSessionContext>(), VirtualOutputCapability.OpenStream).Returns(new VirtualOutputAuthorizationResult { Authorized = true, VirtualDeviceId = id });
        var settings = Substitute.For<ILineupSettingsService>();
        settings.Settings.Returns(new LineupSettings
        {
            MaximumConcurrentStreams = maximum,
            Streaming = new StreamingConfiguration
            {
                VirtualDevices = [new VirtualDeviceDefinition { Id = id, Name = "Output", Adapter = new AdapterConfiguration("test", JsonSerializer.SerializeToElement(new { })) }]
            }
        });
        var streams = Substitute.For<IVirtualMpegTsOutputService>();
        var broker = new VirtualOutputBroker(authority, settings, Substitute.For<IStreamingAdapterRegistry>(), Substitute.For<IPhysicalDeviceCatalog>(),
            Substitute.For<IStreamingRouteResolver>(), Substitute.For<IGuideOrchestrator>(), streams, registry);
        return (broker, streams, new VirtualOutputOpenStreamRequest
        {
            Session = new VirtualOutputSessionContext { SessionId = VirtualOutputWorkerSessionId.New(), Generation = 1 },
            RequestId = VirtualOutputStreamRequestId.New(),
            ChannelKey = "1"
        });
    }
}
