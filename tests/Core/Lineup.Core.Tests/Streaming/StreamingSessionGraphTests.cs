using System.Threading.Channels;
using Lineup.Common.Streaming;
using Lineup.Extensibility.Streaming;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lineup.Core.Tests.Streaming;

/// <summary>
/// Verifies provider-neutral streaming session ownership and sharing.
/// </summary>
public sealed class StreamingSessionGraphTests
{
    /// <summary>Verifies live packets retain warm acquisitions and transform chains until the original idle deadline, without extending it.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task AcquirePipelineAsync_WarmTraffic_ExpiresAtOriginalDeadline(int stageCount)
    {
        // Arrange
        await using var fixture = new WarmPipelineFixture(stageCount);
        await using var lease = await fixture.AcquireAsync(TimeSpan.FromSeconds(30));
        await fixture.SendPacketAsync();
        await lease.DisposeAsync();

        // Act
        fixture.Time.Advance(TimeSpan.FromSeconds(10));
        await fixture.SendPacketAsync();

        // Assert
        fixture.AssertWarm(lease.ProducerNodeId);
        Assert.Equal(1, fixture.Time.CreatedTimers);

        // Act
        fixture.Time.Advance(TimeSpan.FromMilliseconds(19_999));
        await fixture.SendPacketAsync();

        // Assert
        fixture.AssertWarm(lease.ProducerNodeId);
        Assert.Equal(1, fixture.Time.CreatedTimers);

        // Act
        fixture.Time.Advance(TimeSpan.FromMilliseconds(1));
        await fixture.WaitForReleaseAsync();

        // Assert
        Assert.Empty(fixture.Graph.GetDiagnostics().Nodes);
        Assert.Empty(fixture.Graph.GetDiagnostics().Capacity);
    }

    /// <summary>Verifies reconnection cancels the prior warm deadline and the next real disconnect starts a fresh retention period.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task AcquirePipelineAsync_WarmTraffic_ReconnectReusesAndRenewsIdleDeadline(int stageCount)
    {
        // Arrange
        await using var fixture = new WarmPipelineFixture(stageCount);
        await using var first = await fixture.AcquireAsync(TimeSpan.FromSeconds(30));
        await first.DisposeAsync();
        fixture.Time.Advance(TimeSpan.FromSeconds(10));

        // Act
        await fixture.SendPacketAsync();
        await using var second = await fixture.AcquireAsync(TimeSpan.FromSeconds(30));
        fixture.Time.Advance(TimeSpan.FromSeconds(20));
        await fixture.SendPacketAsync();
        var payload = new byte[4];
        await second.Stream.ReadExactlyAsync(payload, fixture.CancellationToken);

        // Assert
        Assert.Equal(first.ProducerNodeId, second.ProducerNodeId);
        Assert.Equal(1, fixture.AcquisitionStarts);
        Assert.All(fixture.Transforms, transform => Assert.Equal(1, transform.StartCount));
        Assert.False(fixture.Graph.GetDiagnostics().Nodes.Single(node => node.NodeId == second.ProducerNodeId).IsWarm);
        Assert.Equal(new byte[] { 0x47, 1, 2, 3 }, payload);

        // Act
        await second.DisposeAsync();
        fixture.Time.Advance(TimeSpan.FromSeconds(29));
        await fixture.SendPacketAsync();

        // Assert
        fixture.AssertWarm(second.ProducerNodeId);
        Assert.Equal(2, fixture.Time.CreatedTimers);

        // Act
        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        await fixture.WaitForReleaseAsync();

        // Assert
        Assert.Empty(fixture.Graph.GetDiagnostics().Nodes);
        Assert.Empty(fixture.Graph.GetDiagnostics().Capacity);
    }

    /// <summary>Verifies shutdown still immediately releases warm pipelines that have continued receiving packets.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task StopAsync_WarmTraffic_ReleasesBeforeDeadline(int stageCount)
    {
        // Arrange
        await using var fixture = new WarmPipelineFixture(stageCount);
        await using var lease = await fixture.AcquireAsync(TimeSpan.FromSeconds(30));
        await lease.DisposeAsync();
        await fixture.SendPacketAsync();
        fixture.AssertWarm(lease.ProducerNodeId);

        // Act
        await fixture.Graph.StopAsync(fixture.CancellationToken);
        await fixture.WaitForReleaseAsync();

        // Assert
        Assert.Empty(fixture.Graph.GetDiagnostics().Nodes);
        Assert.Empty(fixture.Graph.GetDiagnostics().Capacity);
    }

    /// <summary>Verifies zero retention still immediately tears down unused acquisitions and transform chains.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task DisposeAsync_WarmTraffic_ZeroRetention_ReleasesImmediately(int stageCount)
    {
        // Arrange
        await using var fixture = new WarmPipelineFixture(stageCount);
        await using var lease = await fixture.AcquireAsync(TimeSpan.Zero);
        await fixture.SendPacketAsync();

        // Act
        await lease.DisposeAsync();
        await fixture.WaitForReleaseAsync();

        // Assert
        Assert.Equal(0, fixture.Time.CreatedTimers);
        Assert.Empty(fixture.Graph.GetDiagnostics().Nodes);
        Assert.Empty(fixture.Graph.GetDiagnostics().Capacity);
    }

    /// <summary>Verifies slow-consumer eviction retains its warm deadline through further traffic and disposal of an already-removed subscription.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcquirePipelineAsync_WarmTraffic_SlowConsumerEviction_PreservesDeadline(bool disposeEvictedLease)
    {
        // Arrange
        await using var fixture = new WarmPipelineFixture(0);
        await using var lease = await fixture.AcquireAsync(TimeSpan.FromSeconds(30));
        for (var packet = 0; packet < 2049; packet++)
        {
            await fixture.SendPacketAsync();
        }
        fixture.AssertWarm(lease.ProducerNodeId);

        // Act
        fixture.Time.Advance(TimeSpan.FromSeconds(10));
        if (disposeEvictedLease)
        {
            await lease.DisposeAsync();
        }
        await fixture.SendPacketAsync();

        // Assert
        fixture.AssertWarm(lease.ProducerNodeId);
        Assert.Equal(1, fixture.Time.CreatedTimers);

        // Act
        fixture.Time.Advance(TimeSpan.FromSeconds(20));
        await fixture.WaitForReleaseAsync();

        // Assert
        Assert.Empty(fixture.Graph.GetDiagnostics().Nodes);
        Assert.Empty(fixture.Graph.GetDiagnostics().Capacity);
    }

    /// <summary>
    /// Verifies canonical acquisition keys ignore tune-parameter insertion order.
    /// </summary>
    [Fact]
    public void PhysicalAcquisitionKey_Create_NormalizesTuneParameters()
    {
        // Arrange
        var deviceId = PhysicalDeviceId.New();
        var channelId = new SourceChannelId(deviceId, "atsc3:7.1");
        var first = new PhysicalStreamRequest
        {
            ChannelId = channelId,
            TuneParameters = new Dictionary<string, string> { ["transcode"] = "none", ["program"] = "3" }
        };
        var second = new PhysicalStreamRequest
        {
            ChannelId = channelId,
            TuneParameters = new Dictionary<string, string> { ["program"] = "3", ["transcode"] = "none" }
        };

        // Act
        var firstKey = PhysicalAcquisitionKey.Create(first);
        var secondKey = PhysicalAcquisitionKey.Create(second);

        // Assert
        Assert.Equal(firstKey, secondKey);
        Assert.Equal("program=3&transcode=none", firstKey.TuneKey);
    }

    /// <summary>
    /// Verifies two consumers share one physical acquisition and one identical transform.
    /// </summary>
    [Fact]
    public async Task AcquireTransformAsync_TwoConsumers_SharesAcquisitionAndTransform()
    {
        // Arrange
        var capacity = new PhysicalCapacityLeaseRegistry();
        var graph = new StreamingSessionGraph(capacity, NullLogger<StreamingSessionGraph>.Instance);
        var source = new ControlledSourceStream();
        var acquisitionStarts = 0;
        var transform = new PassthroughTransform();
        var descriptor = CreateDescriptor(PhysicalDeviceId.New(), "7.1", capacity: 1);

        // Act
        await using var first = await graph.AcquireTransformAsync(
            descriptor,
            new StreamTransformKey("physical", "deinterlace=field"),
            _ =>
            {
                Interlocked.Increment(ref acquisitionStarts);
                return ValueTask.FromResult<IStreamSourceLease>(new TestSourceLease(source));
            },
            transform,
            TestContext.Current.CancellationToken);
        await using var second = await graph.AcquireTransformAsync(
            descriptor,
            new StreamTransformKey("physical", "deinterlace=field"),
            _ => throw new InvalidOperationException("The shared acquisition factory must not run twice."),
            transform,
            TestContext.Current.CancellationToken);
        await source.WaitForReadAsync(TestContext.Current.CancellationToken);
        source.Write([1, 2, 3, 4]);
        var firstPayload = new byte[4];
        var secondPayload = new byte[4];
        await first.Stream.ReadExactlyAsync(firstPayload, TestContext.Current.CancellationToken);
        await second.Stream.ReadExactlyAsync(secondPayload, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, acquisitionStarts);
        Assert.Equal(1, transform.StartCount);
        Assert.Equal(firstPayload, secondPayload);
        var diagnostics = graph.GetDiagnostics();
        Assert.Equal(2, diagnostics.Nodes.Count);
        Assert.Equal(2, Assert.Single(diagnostics.Nodes, node => node.Kind == "transform").ReceiverCount);
        Assert.Equal(1, Assert.Single(diagnostics.Capacity).ActiveAcquisitions);
    }

    /// <summary>Verifies identical chained pipelines reuse every transform stage.</summary>
    [Fact]
    public async Task AcquirePipelineAsync_TwoConsumers_SharesEveryStage()
    {
        // Arrange
        var graph = new StreamingSessionGraph(new PhysicalCapacityLeaseRegistry(), NullLogger<StreamingSessionGraph>.Instance);
        var source = new ControlledSourceStream();
        var physical = new PassthroughTransform();
        var output = new PassthroughTransform();
        var descriptor = CreateDescriptor(PhysicalDeviceId.New(), "7.1", capacity: 1);
        StreamTransformStage[] stages =
        [
            new(new StreamTransformKey("physical", "ac4=ac3"), physical),
            new(new StreamTransformKey("mpegts", "copy"), output)
        ];

        // Act
        await using var first = await graph.AcquirePipelineAsync(
            descriptor,
            _ => ValueTask.FromResult<IStreamSourceLease>(new TestSourceLease(source)),
            stages,
            TimeSpan.Zero,
            TestContext.Current.CancellationToken);
        await using var second = await graph.AcquirePipelineAsync(
            descriptor,
            _ => throw new InvalidOperationException("The shared acquisition factory must not run twice."),
            stages,
            TimeSpan.Zero,
            TestContext.Current.CancellationToken);
        await source.WaitForReadAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, physical.StartCount);
        Assert.Equal(1, output.StartCount);
        var diagnostics = graph.GetDiagnostics();
        Assert.Single(diagnostics.Nodes, node => node.Kind == "acquisition");
        Assert.Equal(2, diagnostics.Nodes.Count(node => node.Kind == "transform"));
        Assert.Equal(2, diagnostics.Nodes.Single(node => node.Key.Contains(">mpegts:", StringComparison.Ordinal)).ReceiverCount);
        var acquisition = diagnostics.Nodes.Single(node => node.Kind == "acquisition");
        var physicalNode = diagnostics.Nodes.Single(node => node.Stage == "physical");
        var outputNode = diagnostics.Nodes.Single(node => node.Stage == "mpegts");
        Assert.Equal(descriptor.DeviceId, acquisition.PhysicalDeviceId);
        Assert.Equal(descriptor.AcquisitionKey.ChannelId, acquisition.SourceChannelId);
        Assert.Null(acquisition.ParentNodeId);
        Assert.Equal(acquisition.NodeId, physicalNode.ParentNodeId);
        Assert.Equal(physicalNode.NodeId, outputNode.ParentNodeId);
        Assert.Equal(outputNode.NodeId, first.ProducerNodeId);
        Assert.Equal(outputNode.NodeId, second.ProducerNodeId);
    }

    /// <summary>Verifies differing leaf outputs share their acquisition and physical transform prefix.</summary>
    [Fact]
    public async Task AcquirePipelineAsync_DifferentLeafPlans_SharesCompatiblePrefix()
    {
        // Arrange
        var graph = new StreamingSessionGraph(new PhysicalCapacityLeaseRegistry(), NullLogger<StreamingSessionGraph>.Instance);
        var source = new ControlledSourceStream();
        var physical = new PassthroughTransform();
        var firstOutput = new PassthroughTransform();
        var secondOutput = new PassthroughTransform();
        var descriptor = CreateDescriptor(PhysicalDeviceId.New(), "7.1", capacity: 1);
        var physicalStage = new StreamTransformStage(new StreamTransformKey("physical", "same"), physical);

        // Act
        await using var first = await graph.AcquirePipelineAsync(
            descriptor,
            _ => ValueTask.FromResult<IStreamSourceLease>(new TestSourceLease(source)),
            [physicalStage, new StreamTransformStage(new StreamTransformKey("mpegts", "ac3"), firstOutput)],
            TimeSpan.Zero,
            TestContext.Current.CancellationToken);
        await using var second = await graph.AcquirePipelineAsync(
            descriptor,
            _ => throw new InvalidOperationException("The shared acquisition factory must not run twice."),
            [physicalStage, new StreamTransformStage(new StreamTransformKey("mpegts", "eac3"), secondOutput)],
            TimeSpan.Zero,
            TestContext.Current.CancellationToken);
        await source.WaitForReadAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, physical.StartCount);
        Assert.Equal(1, firstOutput.StartCount);
        Assert.Equal(1, secondOutput.StartCount);
        Assert.Equal(4, graph.GetDiagnostics().Nodes.Count);
    }

    /// <summary>Verifies a warm leaf remains reusable and releases its parent immediately after expiry.</summary>
    [Fact]
    public async Task AcquirePipelineAsync_WarmLeaf_ReusesThenReleasesPipeline()
    {
        // Arrange
        var graph = new StreamingSessionGraph(new PhysicalCapacityLeaseRegistry(), NullLogger<StreamingSessionGraph>.Instance);
        var source = new ControlledSourceStream();
        var transform = new PassthroughTransform();
        var descriptor = CreateDescriptor(PhysicalDeviceId.New(), "7.1", capacity: 1);
        StreamTransformStage[] stages = [new(new StreamTransformKey("mpegts", "same"), transform)];
        var first = await graph.AcquirePipelineAsync(
            descriptor,
            _ => ValueTask.FromResult<IStreamSourceLease>(new TestSourceLease(source)),
            stages,
            TimeSpan.FromMilliseconds(200),
            TestContext.Current.CancellationToken);
        await source.WaitForReadAsync(TestContext.Current.CancellationToken);

        // Act
        await first.DisposeAsync();
        Assert.True(graph.GetDiagnostics().Nodes.Single(node => node.Kind == "transform").IsWarm);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        await using var second = await graph.AcquirePipelineAsync(
            descriptor,
            _ => throw new InvalidOperationException("The warm acquisition must be reused."),
            stages,
            TimeSpan.FromMilliseconds(200),
            TestContext.Current.CancellationToken);
        await second.DisposeAsync();
        await source.WaitForDisposalAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => graph.GetDiagnostics().Nodes.Count == 0, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, transform.StartCount);
        Assert.Empty(graph.GetDiagnostics().Nodes);
        Assert.Empty(graph.GetDiagnostics().Capacity);
    }

    /// <summary>Verifies concurrent metadata requests run one factory for the active acquisition generation.</summary>
    [Fact]
    public async Task GetOrCreateAcquisitionMetadataAsync_ConcurrentRequests_SharesFactory()
    {
        // Arrange
        var graph = new StreamingSessionGraph(new PhysicalCapacityLeaseRegistry(), NullLogger<StreamingSessionGraph>.Instance);
        var source = new ControlledSourceStream();
        var descriptor = CreateDescriptor(PhysicalDeviceId.New(), "7.1", capacity: 1);
        await using var continuity = await graph.AcquireAsync(
            descriptor,
            _ => ValueTask.FromResult<IStreamSourceLease>(new TestSourceLease(source)),
            TestContext.Current.CancellationToken);
        var starts = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int> CreateMetadata(Stream _, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref starts);
            return CompleteAsync(cancellationToken);
        }
        async Task<int> CompleteAsync(CancellationToken cancellationToken)
        {
            await release.Task.WaitAsync(cancellationToken);
            return 42;
        }

        // Act
        var first = graph.GetOrCreateAcquisitionMetadataAsync(descriptor, _ => throw new InvalidOperationException(), "probe", CreateMetadata, TestContext.Current.CancellationToken);
        var second = graph.GetOrCreateAcquisitionMetadataAsync(descriptor, _ => throw new InvalidOperationException(), "probe", CreateMetadata, TestContext.Current.CancellationToken);
        release.SetResult();
        var results = await Task.WhenAll(first, second);

        // Assert
        Assert.Equal([42, 42], results);
        Assert.Equal(1, starts);
    }

    /// <summary>Verifies provisional metadata is refreshed once, merged, and then reused after becoming complete.</summary>
    [Fact]
    public async Task GetOrRefreshAcquisitionMetadataAsync_ProvisionalResult_RefreshesAndCachesMergedResult()
    {
        // Arrange
        var graph = new StreamingSessionGraph(new PhysicalCapacityLeaseRegistry(), NullLogger<StreamingSessionGraph>.Instance);
        var source = new ControlledSourceStream();
        var descriptor = CreateDescriptor(PhysicalDeviceId.New(), "7.1", capacity: 1);
        await using var continuity = await graph.AcquireAsync(
            descriptor,
            _ => ValueTask.FromResult<IStreamSourceLease>(new TestSourceLease(source)),
            TestContext.Current.CancellationToken);
        var starts = 0;
        Task<IReadOnlyList<string>> CreateMetadata(Stream stream, CancellationToken cancellationToken)
        {
            _ = stream;
            _ = cancellationToken;
            var attempt = Interlocked.Increment(ref starts);
            return Task.FromResult<IReadOnlyList<string>>(attempt == 1 ? ["video"] : ["video", "audio"]);
        }
        var policy = new AcquisitionMetadataRefreshPolicy<IReadOnlyList<string>>
        {
            IsReusable = metadata => metadata.Contains("audio", StringComparer.Ordinal),
            Merge = (prior, refreshed) => prior.Concat(refreshed).Distinct(StringComparer.Ordinal).ToArray(),
            RetryDelay = TimeSpan.Zero
        };

        // Act
        var first = await graph.GetOrRefreshAcquisitionMetadataAsync(
            descriptor,
            _ => throw new InvalidOperationException(),
            "probe",
            CreateMetadata,
            policy,
            TestContext.Current.CancellationToken);
        var second = await graph.GetOrRefreshAcquisitionMetadataAsync(
            descriptor,
            _ => throw new InvalidOperationException(),
            "probe",
            CreateMetadata,
            policy,
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(["video", "audio"], first);
        Assert.Equal(first, second);
        Assert.Equal(2, starts);
    }

    /// <summary>Verifies metadata that remains provisional is evicted so a later independent request probes again.</summary>
    [Fact]
    public async Task GetOrRefreshAcquisitionMetadataAsync_StillProvisional_EvictsForNextRequest()
    {
        // Arrange
        var graph = new StreamingSessionGraph(new PhysicalCapacityLeaseRegistry(), NullLogger<StreamingSessionGraph>.Instance);
        var source = new ControlledSourceStream();
        var descriptor = CreateDescriptor(PhysicalDeviceId.New(), "7.1", capacity: 1);
        await using var continuity = await graph.AcquireAsync(
            descriptor,
            _ => ValueTask.FromResult<IStreamSourceLease>(new TestSourceLease(source)),
            TestContext.Current.CancellationToken);
        var starts = 0;
        Task<int> CreateMetadata(Stream stream, CancellationToken cancellationToken)
        {
            _ = stream;
            _ = cancellationToken;
            Interlocked.Increment(ref starts);
            return Task.FromResult(0);
        }
        var policy = new AcquisitionMetadataRefreshPolicy<int>
        {
            IsReusable = metadata => metadata > 0,
            Merge = Math.Max,
            RetryDelay = TimeSpan.Zero
        };

        // Act
        var first = await graph.GetOrRefreshAcquisitionMetadataAsync(
            descriptor,
            _ => throw new InvalidOperationException(),
            "probe",
            CreateMetadata,
            policy,
            TestContext.Current.CancellationToken);
        var second = await graph.GetOrRefreshAcquisitionMetadataAsync(
            descriptor,
            _ => throw new InvalidOperationException(),
            "probe",
            CreateMetadata,
            policy,
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0, first);
        Assert.Equal(0, second);
        Assert.Equal(4, starts);
    }

    /// <summary>Verifies concurrent callers share one bounded refresh even when the refreshed metadata remains provisional.</summary>
    [Fact]
    public async Task GetOrRefreshAcquisitionMetadataAsync_ConcurrentProvisionalCallers_ShareSingleRefresh()
    {
        // Arrange
        var graph = new StreamingSessionGraph(new PhysicalCapacityLeaseRegistry(), NullLogger<StreamingSessionGraph>.Instance);
        var source = new ControlledSourceStream();
        var descriptor = CreateDescriptor(PhysicalDeviceId.New(), "7.1", capacity: 1);
        await using var continuity = await graph.AcquireAsync(
            descriptor,
            _ => ValueTask.FromResult<IStreamSourceLease>(new TestSourceLease(source)),
            TestContext.Current.CancellationToken);
        var starts = 0;
        var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<int> CreateMetadata(Stream stream, CancellationToken cancellationToken)
        {
            _ = stream;
            var attempt = Interlocked.Increment(ref starts);
            if (attempt == 2)
            {
                refreshStarted.SetResult();
                await releaseRefresh.Task.WaitAsync(cancellationToken);
            }
            return 0;
        }
        var policy = new AcquisitionMetadataRefreshPolicy<int>
        {
            IsReusable = metadata => metadata > 0,
            Merge = Math.Max,
            RetryDelay = TimeSpan.Zero
        };

        // Act
        var first = graph.GetOrRefreshAcquisitionMetadataAsync(
            descriptor,
            _ => throw new InvalidOperationException(),
            "probe",
            CreateMetadata,
            policy,
            TestContext.Current.CancellationToken);
        var second = graph.GetOrRefreshAcquisitionMetadataAsync(
            descriptor,
            _ => throw new InvalidOperationException(),
            "probe",
            CreateMetadata,
            policy,
            TestContext.Current.CancellationToken);
        await refreshStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        releaseRefresh.SetResult();
        var results = await Task.WhenAll(first, second);

        // Assert
        Assert.Equal([0, 0], results);
        Assert.Equal(2, starts);
    }

    /// <summary>Verifies one caller can use provisional metadata while a shared refresh continues for a later caller.</summary>
    [Fact]
    public async Task GetOrRefreshAcquisitionMetadataAsync_RefreshWaitExpires_ReturnsProvisionalAndRetainsRefresh()
    {
        // Arrange
        var graph = new StreamingSessionGraph(new PhysicalCapacityLeaseRegistry(), NullLogger<StreamingSessionGraph>.Instance);
        var source = new ControlledSourceStream();
        var descriptor = CreateDescriptor(PhysicalDeviceId.New(), "7.1", capacity: 1);
        await using var continuity = await graph.AcquireAsync(
            descriptor,
            _ => ValueTask.FromResult<IStreamSourceLease>(new TestSourceLease(source)),
            TestContext.Current.CancellationToken);
        var starts = 0;
        var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<int> CreateMetadata(Stream stream, CancellationToken cancellationToken)
        {
            _ = stream;
            var attempt = Interlocked.Increment(ref starts);
            if (attempt == 2)
            {
                refreshStarted.SetResult();
                await releaseRefresh.Task.WaitAsync(cancellationToken);
            }
            return attempt == 1 ? 0 : 1;
        }
        var policy = new AcquisitionMetadataRefreshPolicy<int>
        {
            IsReusable = metadata => metadata > 0,
            Merge = Math.Max,
            RetryDelay = TimeSpan.Zero,
            OperationTimeout = TimeSpan.FromSeconds(5),
            RefreshWaitTimeout = TimeSpan.FromMilliseconds(50)
        };

        // Act
        var firstTask = graph.GetOrRefreshAcquisitionMetadataAsync(
            descriptor,
            _ => throw new InvalidOperationException(),
            "probe",
            CreateMetadata,
            policy,
            TestContext.Current.CancellationToken);
        await refreshStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        var first = await firstTask;
        releaseRefresh.SetResult();
        var second = await graph.GetOrRefreshAcquisitionMetadataAsync(
            descriptor,
            _ => throw new InvalidOperationException(),
            "probe",
            CreateMetadata,
            policy,
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0, first);
        Assert.Equal(1, second);
        Assert.Equal(2, starts);
    }

    /// <summary>Verifies a shared metadata operation cannot outlive its independent operation budget.</summary>
    [Fact]
    public async Task GetOrRefreshAcquisitionMetadataAsync_OperationTimeout_CancelsAndEvictsMetadata()
    {
        // Arrange
        var graph = new StreamingSessionGraph(new PhysicalCapacityLeaseRegistry(), NullLogger<StreamingSessionGraph>.Instance);
        var source = new ControlledSourceStream();
        var descriptor = CreateDescriptor(PhysicalDeviceId.New(), "7.1", capacity: 1);
        await using var continuity = await graph.AcquireAsync(
            descriptor,
            _ => ValueTask.FromResult<IStreamSourceLease>(new TestSourceLease(source)),
            TestContext.Current.CancellationToken);
        var starts = 0;
        async Task<int> CreateMetadata(Stream stream, CancellationToken cancellationToken)
        {
            _ = stream;
            Interlocked.Increment(ref starts);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 1;
        }
        var policy = new AcquisitionMetadataRefreshPolicy<int>
        {
            IsReusable = metadata => metadata > 0,
            Merge = Math.Max,
            RetryDelay = TimeSpan.Zero,
            OperationTimeout = TimeSpan.FromMilliseconds(50)
        };

        // Act
        var first = graph.GetOrRefreshAcquisitionMetadataAsync(
            descriptor,
            _ => throw new InvalidOperationException(),
            "probe",
            CreateMetadata,
            policy,
            TestContext.Current.CancellationToken);
        var second = graph.GetOrRefreshAcquisitionMetadataAsync(
            descriptor,
            _ => throw new InvalidOperationException(),
            "probe",
            CreateMetadata,
            policy,
            TestContext.Current.CancellationToken);

        // Assert
        await Assert.ThrowsAsync<TimeoutException>(() => first);
        await Assert.ThrowsAsync<TimeoutException>(() => second);
        Assert.Equal(1, starts);
    }

    /// <summary>
    /// Verifies a distinct acquisition is rejected when finite capacity is occupied.
    /// </summary>
    [Fact]
    public async Task AcquireAsync_DistinctSource_EnforcesCapacity()
    {
        // Arrange
        var capacity = new PhysicalCapacityLeaseRegistry();
        var graph = new StreamingSessionGraph(capacity, NullLogger<StreamingSessionGraph>.Instance);
        var deviceId = PhysicalDeviceId.New();
        var firstSource = new ControlledSourceStream();
        await using var first = await graph.AcquireAsync(
            CreateDescriptor(deviceId, "7.1", capacity: 1),
            _ => ValueTask.FromResult<IStreamSourceLease>(new TestSourceLease(firstSource)),
            TestContext.Current.CancellationToken);
        await firstSource.WaitForReadAsync(TestContext.Current.CancellationToken);

        // Act
        var exception = await Record.ExceptionAsync(async () =>
            await graph.AcquireAsync(
                CreateDescriptor(deviceId, "9.1", capacity: 1),
                _ => ValueTask.FromResult<IStreamSourceLease>(new TestSourceLease(new ControlledSourceStream())),
                TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<StreamCapacityExceededException>(exception);
        Assert.Equal(1, Assert.Single(graph.GetDiagnostics().Capacity).ActiveAcquisitions);
    }

    /// <summary>
    /// Verifies disposing the last consumer cancels the source and releases graph state.
    /// </summary>
    [Fact]
    public async Task DisposeAsync_LastConsumer_CancelsAndCleansUp()
    {
        // Arrange
        var graph = new StreamingSessionGraph(new PhysicalCapacityLeaseRegistry(), NullLogger<StreamingSessionGraph>.Instance);
        var source = new ControlledSourceStream();
        var lease = await graph.AcquireAsync(
            CreateDescriptor(PhysicalDeviceId.New(), "7.1", capacity: 1),
            _ => ValueTask.FromResult<IStreamSourceLease>(new TestSourceLease(source)),
            TestContext.Current.CancellationToken);
        await source.WaitForReadAsync(TestContext.Current.CancellationToken);

        // Act
        await lease.DisposeAsync();
        await source.WaitForDisposalAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(graph.GetDiagnostics().Nodes);
        Assert.Empty(graph.GetDiagnostics().Capacity);
    }

    /// <summary>
    /// Verifies transform startup failure releases the transform input, physical source, and capacity.
    /// </summary>
    [Fact]
    public async Task AcquireTransformAsync_TransformFailure_CleansUpReverseOrder()
    {
        // Arrange
        var cleanup = new List<string>();
        var graph = new StreamingSessionGraph(new PhysicalCapacityLeaseRegistry(), NullLogger<StreamingSessionGraph>.Instance);
        var descriptor = CreateDescriptor(PhysicalDeviceId.New(), "7.1", capacity: 1);

        // Act
        var exception = await Record.ExceptionAsync(async () =>
            await graph.AcquireTransformAsync(
                descriptor,
                new StreamTransformKey("virtual", "failure"),
                _ => ValueTask.FromResult<IStreamSourceLease>(new TestSourceLease(new ControlledSourceStream(), () => cleanup.Add("acquisition"))),
                new FailingTransform(cleanup),
                TestContext.Current.CancellationToken));
        await WaitUntilAsync(() => graph.GetDiagnostics().Nodes.Count == 0, TestContext.Current.CancellationToken);

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Equal(["transform", "acquisition"], cleanup);
        Assert.Empty(graph.GetDiagnostics().Capacity);
    }

    /// <summary>Verifies a transform cleanup failure cannot retain its parent acquisition or tuner capacity.</summary>
    [Fact]
    public async Task DisposeAsync_TransformCleanupFailure_ReleasesParentOwnership()
    {
        // Arrange
        var graph = new StreamingSessionGraph(new PhysicalCapacityLeaseRegistry(), NullLogger<StreamingSessionGraph>.Instance);
        var source = new ControlledSourceStream();
        var descriptor = CreateDescriptor(PhysicalDeviceId.New(), "7.1", capacity: 1);
        var lease = await graph.AcquireTransformAsync(
            descriptor,
            new StreamTransformKey("virtual", "throwing-cleanup"),
            _ => ValueTask.FromResult<IStreamSourceLease>(new TestSourceLease(source)),
            new ThrowingDisposeTransform(),
            TestContext.Current.CancellationToken);
        await source.WaitForReadAsync(TestContext.Current.CancellationToken);

        // Act
        await lease.DisposeAsync();
        await source.WaitForDisposalAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => graph.GetDiagnostics().Nodes.Count == 0, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(graph.GetDiagnostics().Nodes);
        Assert.Empty(graph.GetDiagnostics().Capacity);
    }

    private static StreamAcquisitionDescriptor CreateDescriptor(PhysicalDeviceId deviceId, string channel, int capacity) =>
        new(
            deviceId,
            new PhysicalAcquisitionKey(new SourceChannelId(deviceId, channel), "transcode=none"),
            new PhysicalTunerCapacity(capacity));

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            await Task.Delay(1, cancellationToken);
        }
    }

    private sealed class WarmPipelineFixture : IAsyncDisposable
    {
        private readonly CancellationTokenSource _watchdog = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        private readonly ControlledSourceStream _source = new();
        private readonly ReadTrackingStream _reader;
        private readonly StreamAcquisitionDescriptor _descriptor = CreateDescriptor(PhysicalDeviceId.New(), "7.1", capacity: 1);
        private int _acquisitionStarts;

        public WarmPipelineFixture(int stageCount)
        {
            _watchdog.CancelAfter(TimeSpan.FromSeconds(10));
            _reader = new ReadTrackingStream(_source);
            Transforms = Enumerable.Range(0, stageCount).Select(_ => new ObservedTransform()).ToArray();
            Graph = new StreamingSessionGraph(new PhysicalCapacityLeaseRegistry(), NullLogger<StreamingSessionGraph>.Instance, Time);
        }

        public ManualTimeProvider Time { get; } = new();
        public StreamingSessionGraph Graph { get; }
        public ObservedTransform[] Transforms { get; }
        public int AcquisitionStarts => _acquisitionStarts;
        public CancellationToken CancellationToken => _watchdog.Token;

        public async Task<IStreamSessionLease> AcquireAsync(TimeSpan keepWarmPeriod)
        {
            var stages = Transforms.Select((transform, index) => new StreamTransformStage(new StreamTransformKey($"stage-{index}", "copy"), transform)).ToArray();
            var lease = await Graph.AcquirePipelineAsync(
                _descriptor,
                _ =>
                {
                    Interlocked.Increment(ref _acquisitionStarts);
                    return ValueTask.FromResult<IStreamSourceLease>(new TestSourceLease(_reader));
                },
                stages,
                keepWarmPeriod,
                CancellationToken);
            await Task.WhenAll(GetReaders().Select(reader => reader.WaitForReadCountAsync(1, CancellationToken)));
            return lease;
        }

        public async Task SendPacketAsync()
        {
            var readers = GetReaders();
            var pending = readers.Select(reader => reader.WaitForReadCountAsync(reader.ReadCount + 1, CancellationToken)).ToArray();
            _source.Write([0x47, 1, 2, 3]);
            await Task.WhenAll(pending);
        }

        public void AssertWarm(string producerNodeId)
        {
            var diagnostics = Graph.GetDiagnostics();
            Assert.Equal(Transforms.Length + 1, diagnostics.Nodes.Count);
            var leaf = Assert.Single(diagnostics.Nodes, node => node.NodeId == producerNodeId);
            Assert.True(leaf.IsWarm);
            Assert.True(leaf.IsRunning);
            Assert.Equal(0, leaf.ReceiverCount);
            Assert.Equal(1, Assert.Single(diagnostics.Capacity).ActiveAcquisitions);
        }

        public async Task WaitForReleaseAsync()
        {
            await _source.WaitForDisposalAsync(CancellationToken);
            await WaitUntilAsync(() => Graph.GetDiagnostics().Nodes.Count == 0, CancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Graph.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                await _watchdog.CancelAsync();
                _watchdog.Dispose();
            }
        }

        private ReadTrackingStream[] GetReaders() =>
            [_reader, .. Transforms.Select(transform => Assert.IsType<ReadTrackingStream>(transform.Output))];
    }

    private sealed class ObservedTransform : IStreamTransform
    {
        public int StartCount { get; private set; }
        public ReadTrackingStream? Output { get; private set; }

        public ValueTask<IStreamSourceLease> StartAsync(Stream input, CancellationToken cancellationToken)
        {
            StartCount++;
            Output = new ReadTrackingStream(input);
            return ValueTask.FromResult<IStreamSourceLease>(new TestSourceLease(Output));
        }
    }

    private sealed class ReadTrackingStream(Stream input) : Stream
    {
        private readonly Channel<int> _reads = Channel.CreateUnbounded<int>();
        private int _readCount;

        public int ReadCount => Volatile.Read(ref _readCount);
        public override bool CanRead => input.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public async Task WaitForReadCountAsync(int count, CancellationToken cancellationToken)
        {
            while (ReadCount < count)
            {
                if (!await _reads.Reader.WaitToReadAsync(cancellationToken))
                {
                    throw new IOException("The shared producer stopped before it could finish broadcasting the packet.");
                }
                while (_reads.Reader.TryRead(out _))
                {
                }
            }
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _reads.Writer.TryWrite(Interlocked.Increment(ref _readCount));
            return input.ReadAsync(buffer, cancellationToken);
        }

        public override async ValueTask DisposeAsync()
        {
            try
            {
                await input.DisposeAsync();
            }
            finally
            {
                _reads.Writer.TryComplete();
            }
            GC.SuppressFinalize(this);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];
        private TimeSpan _elapsed;
        private int _createdTimers;

        public int CreatedTimers => Volatile.Read(ref _createdTimers);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Interlocked.Increment(ref _createdTimers);
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }

        public void Advance(TimeSpan duration)
        {
            ManualTimer[] elapsed;
            lock (_timers)
            {
                _elapsed += duration;
                elapsed = _timers.Where(timer => timer.DueAt <= _elapsed).ToArray();
                foreach (var timer in elapsed)
                {
                    _timers.Remove(timer);
                }
            }
            foreach (var timer in elapsed)
            {
                timer.Callback(timer.State);
            }
        }

        private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
        {
            public TimerCallback Callback { get; } = callback;
            public object? State { get; } = state;
            public TimeSpan DueAt { get; private set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                Assert.Equal(Timeout.InfiniteTimeSpan, period);
                lock (owner._timers)
                {
                    owner._timers.Remove(this);
                    if (dueTime != Timeout.InfiniteTimeSpan)
                    {
                        DueAt = owner._elapsed + dueTime;
                        owner._timers.Add(this);
                    }
                }
                return true;
            }

            public void Dispose()
            {
                lock (owner._timers)
                {
                    owner._timers.Remove(this);
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class PassthroughTransform : IStreamTransform
    {
        private int _startCount;

        public int StartCount => _startCount;

        public ValueTask<IStreamSourceLease> StartAsync(Stream input, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _startCount);
            return ValueTask.FromResult<IStreamSourceLease>(new TestSourceLease(input));
        }
    }

    private sealed class FailingTransform(List<string> cleanup) : IStreamTransform
    {
        public ValueTask<IStreamSourceLease> StartAsync(Stream input, CancellationToken cancellationToken)
        {
            cleanup.Add("transform");
            throw new InvalidOperationException("Transform startup failed.");
        }
    }

    private sealed class ThrowingDisposeTransform : IStreamTransform
    {
        public ValueTask<IStreamSourceLease> StartAsync(Stream input, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IStreamSourceLease>(new ThrowingSourceLease(input));
    }

    private sealed class ThrowingSourceLease(Stream stream) : IStreamSourceLease
    {
        public Stream Stream { get; } = stream;

        public string? ContentType => "video/mp2t";

        public ValueTask DisposeAsync() => ValueTask.FromException(new InvalidOperationException("Transform cleanup failed."));
    }

    private sealed class TestSourceLease(Stream stream, Action? onDispose = null) : IStreamSourceLease
    {
        public Stream Stream { get; } = stream;

        public string? ContentType => "video/mp2t";

        public async ValueTask DisposeAsync()
        {
            await Stream.DisposeAsync();
            onDispose?.Invoke();
        }
    }

    private sealed class ControlledSourceStream : Stream
    {
        private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>();
        private readonly TaskCompletionSource _readStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private byte[]? _current;
        private int _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public void Write(byte[] payload) => _chunks.Writer.TryWrite(payload);

        public Task WaitForReadAsync(CancellationToken cancellationToken) => _readStarted.Task.WaitAsync(cancellationToken);

        public Task WaitForDisposalAsync(CancellationToken cancellationToken) => _disposed.Task.WaitAsync(cancellationToken);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _readStarted.TrySetResult();
            while (_current is null || _offset == _current.Length)
            {
                _current = await _chunks.Reader.ReadAsync(cancellationToken);
                _offset = 0;
            }
            var count = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _disposed.TrySetResult();
                _chunks.Writer.TryComplete();
            }
            base.Dispose(disposing);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
