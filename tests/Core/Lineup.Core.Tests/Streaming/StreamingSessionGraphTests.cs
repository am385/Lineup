using System.Threading.Channels;
using Lineup.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lineup.Core.Tests.Streaming;

/// <summary>
/// Verifies provider-neutral streaming session ownership and sharing.
/// </summary>
public sealed class StreamingSessionGraphTests
{
    /// <summary>Verifies short reads allocate near their payload size and full reads do not require a second payload copy.</summary>
    [Theory]
    [InlineData(1316, 256)]
    [InlineData(65536, 32)]
    public async Task AcquireAsync_ReadAllocations_StayWithinPayloadBudget(int chunkSize, int chunkCount)
    {
        // Arrange
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        watchdog.CancelAfter(TimeSpan.FromSeconds(10));
        var graph = new StreamingSessionGraph(new PhysicalCapacityLeaseRegistry(), NullLogger<StreamingSessionGraph>.Instance);
        var source = new AllocationTrackingStream(chunkSize, chunkCount);
        await using var lease = await graph.AcquireAsync(
            CreateDescriptor(PhysicalDeviceId.New(), "7.1", 1), _ => ValueTask.FromResult<IStreamSourceLease>(new TestSourceLease(source)), watchdog.Token);

        try
        {
            // Act
            source.ReleaseFirstRead();
            var allocatedBytes = await source.AllocatedBytes.Task.WaitAsync(watchdog.Token);
            var destination = new byte[65536];
            for (var index = 0; index < chunkCount; index++)
            {
                var count = await lease.Stream.ReadAsync(destination, watchdog.Token);
                Assert.Equal(chunkSize, count);
                Assert.True(destination.AsSpan(0, count).IndexOfAnyExcept((byte)index) < 0);
            }
            var eof = await lease.Stream.ReadAsync(destination, watchdog.Token);

            // Assert
            Assert.Equal(0, eof);
            var allocationLimit = (long)chunkSize * chunkCount + chunkCount * 2048L + 2 * 65536;
            Assert.InRange(allocatedBytes, 1, allocationLimit);
        }
        finally
        {
            source.ReleaseFirstRead();
            await graph.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Verifies later short/full reads and EOF cannot overwrite bytes retained by delayed or partially advanced subscribers.</summary>
    [Fact]
    public async Task AcquireAsync_MixedReadSizes_PreservesDelayedSubscribersThroughEof()
    {
        // Arrange
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        watchdog.CancelAfter(TimeSpan.FromSeconds(10));
        var graph = new StreamingSessionGraph(new PhysicalCapacityLeaseRegistry(), NullLogger<StreamingSessionGraph>.Instance);
        var source = new ControlledSourceStream();
        var reads = new ReadTrackingStream(source);
        var descriptor = CreateDescriptor(PhysicalDeviceId.New(), "7.1", 1);
        byte[][] chunks = [new byte[1316], new byte[65536], new byte[7], new byte[65536], new byte[3]];
        for (var index = 0; index < chunks.Length; index++)
        {
            chunks[index].AsSpan().Fill((byte)(index + 1));
        }
        var expected = chunks.SelectMany(chunk => chunk).ToArray();
        await using var first = await graph.AcquireAsync(descriptor, _ => ValueTask.FromResult<IStreamSourceLease>(new TestSourceLease(reads)), watchdog.Token);
        await using var delayed = await graph.AcquireAsync(descriptor, _ => throw new InvalidOperationException("The source must remain shared."), watchdog.Token);

        try
        {
            // Act
            source.Write(chunks[0]);
            await reads.WaitForReadCountAsync(2, watchdog.Token);
            var firstPayload = new byte[expected.Length];
            await first.Stream.ReadExactlyAsync(firstPayload.AsMemory(0, 13), watchdog.Token);
            foreach (var chunk in chunks.Skip(1))
            {
                source.Write(chunk);
            }
            await reads.WaitForReadCountAsync(chunks.Length + 1, watchdog.Token);
            source.Complete();
            await source.WaitForDisposalAsync(watchdog.Token);
            await first.Stream.ReadExactlyAsync(firstPayload.AsMemory(13), watchdog.Token);
            var delayedPayload = new byte[expected.Length];
            await delayed.Stream.ReadExactlyAsync(delayedPayload, watchdog.Token);

            // Assert
            Assert.Equal(expected, firstPayload);
            Assert.Equal(expected, delayedPayload);
            Assert.Equal(0, await first.Stream.ReadAsync(new byte[1], watchdog.Token));
            Assert.Equal(0, await delayed.Stream.ReadAsync(new byte[1], watchdog.Token));
        }
        finally
        {
            await graph.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Verifies runtime encoder outcomes survive graph ownership wrappers and remain distinct for each shared pipeline stage.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetDiagnostics_PipelineEncoders_RetainsStageSpecificOutcomes(bool fallback)
    {
        // Arrange
        var graph = new StreamingSessionGraph(new PhysicalCapacityLeaseRegistry(), NullLogger<StreamingSessionGraph>.Instance);
        var descriptor = CreateDescriptor(PhysicalDeviceId.New(), "7.1", 1);
        var physicalEncoding = new VideoEncodingSnapshot(fallback ? "libx264" : "h264_amf", HardwareVideoEncoder.Amf, fallback);
        var outputEncoding = new VideoEncodingSnapshot("copy", HardwareVideoEncoder.Nvenc, false);
        StreamAcquisitionFactory open = _ => ValueTask.FromResult<IStreamSourceLease>(new TestSourceLease(new ControlledSourceStream()));
        StreamTransformStage[] stages =
        [
            new(new("source-normalization", "physical"), new PassthroughTransform { VideoEncoding = physicalEncoding }),
            new(new("virtual-mpegts", "output"), new PassthroughTransform { VideoEncoding = outputEncoding })
        ];

        // Act
        await using var first = await graph.AcquirePipelineAsync(descriptor, open, stages, TimeSpan.Zero, TestContext.Current.CancellationToken);
        await using var second = await graph.AcquirePipelineAsync(descriptor, open, stages, TimeSpan.Zero, TestContext.Current.CancellationToken);
        var diagnostics = graph.GetDiagnostics();

        // Assert
        Assert.Equal(3, diagnostics.Nodes.Count);
        Assert.Null(diagnostics.Nodes.Single(node => node.Kind == "acquisition").VideoEncoding);
        Assert.Equal(physicalEncoding, diagnostics.Nodes.Single(node => node.Stage == "source-normalization").VideoEncoding);
        Assert.Equal(outputEncoding, diagnostics.Nodes.Single(node => node.Stage == "virtual-mpegts").VideoEncoding);
        Assert.Equal(first.ProducerNodeId, second.ProducerNodeId);
        await second.DisposeAsync();
        await first.DisposeAsync();
        await graph.StopAsync(TestContext.Current.CancellationToken);
        Assert.Empty(graph.GetDiagnostics().Nodes);
    }

    /// <summary>Equivalent acquisitions join at saturation, but a new retained transform cannot bypass the producer budget.</summary>
    [Fact]
    public async Task AcquirePipelineAsync_ProducerLimit_AllowsOnlyExistingProducerJoins()
    {
        // Arrange
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        watchdog.CancelAfter(TimeSpan.FromSeconds(10));
        var resources = new StreamingResourceOptions { MaximumProducers = 1 };
        var admission = new MediaWorkAdmission(resources);
        var graph = new StreamingSessionGraph(new PhysicalCapacityLeaseRegistry(), NullLogger<StreamingSessionGraph>.Instance, TimeProvider.System, null, resources, admission);
        var descriptor = CreateDescriptor(PhysicalDeviceId.New(), "7.1", 1);
        StreamAcquisitionFactory open = _ => ValueTask.FromResult<IStreamSourceLease>(new TestSourceLease(new ControlledSourceStream()));
        await using var first = await graph.AcquireAsync(descriptor, open, watchdog.Token);
        await using var joined = await graph.AcquireAsync(descriptor, open, watchdog.Token);

        // Act
        var failure = await Assert.ThrowsAsync<MediaResourceLimitException>(() => graph.AcquirePipelineAsync(descriptor, open,
            [new(new StreamTransformKey("mpegts", "new-output"), new PassthroughTransform())], TimeSpan.Zero, watchdog.Token).AsTask());

        // Assert
        Assert.Equal("media_producer_limit", failure.Code);
        Assert.Equal(first.ProducerNodeId, joined.ProducerNodeId);
        Assert.Equal(1, admission.GetDiagnostics().Producers);
        await joined.DisposeAsync();
        await first.DisposeAsync();
        await graph.StopAsync(watchdog.Token);
        Assert.Equal(0, admission.GetDiagnostics().Producers);
    }

    /// <summary>Capacity pressure retires an idle transform chain and awaits physical cleanup before tuning a distinct source.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcquireAsync_OneTuner_ReclaimsWarmOwnerBeforeOpeningReplacement(bool transform)
    {
        // Arrange
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        watchdog.CancelAfter(TimeSpan.FromSeconds(10));
        var admission = new MediaWorkAdmission(new());
        var graph = new StreamingSessionGraph(new PhysicalCapacityLeaseRegistry(), NullLogger<StreamingSessionGraph>.Instance, TimeProvider.System, null, mediaWork: admission);
        var descriptor = CreateDescriptor(PhysicalDeviceId.New(), "7.1", 1);
        var source = new ControlledSourceStream();
        var disposed = false;
        var first = await graph.AcquirePipelineAsync(descriptor,
            _ => ValueTask.FromResult<IStreamSourceLease>(new TestSourceLease(source, () => disposed = true)),
            transform ? [new(new StreamTransformKey("mpegts", "warm"), new PassthroughTransform())] : [],
            TimeSpan.FromMinutes(1), watchdog.Token);
        await first.DisposeAsync();
        var openedBeforeCleanup = false;

        // Act
        await using var replacement = await graph.AcquireAsync(CreateDescriptor(descriptor.DeviceId, "9.1", 1), _ =>
        {
            openedBeforeCleanup = !disposed;
            return ValueTask.FromResult<IStreamSourceLease>(new TestSourceLease(new ControlledSourceStream()));
        }, watchdog.Token);

        // Assert
        Assert.True(disposed);
        Assert.False(openedBeforeCleanup);
        Assert.Single(graph.GetDiagnostics().Capacity);
        Assert.Equal(1, admission.GetDiagnostics().Producers);
        await replacement.DisposeAsync();
        await graph.StopAsync(watchdog.Token);
        Assert.Equal(0, admission.GetDiagnostics().Producers);
    }

    /// <summary>An idle leaf cannot release a physical acquisition while an independently active sibling still needs it.</summary>
    [Fact]
    public async Task AcquireAsync_ActiveSharedPrefix_CannotBePreempted()
    {
        // Arrange
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        watchdog.CancelAfter(TimeSpan.FromSeconds(10));
        var graph = new StreamingSessionGraph(new PhysicalCapacityLeaseRegistry(), NullLogger<StreamingSessionGraph>.Instance);
        var descriptor = CreateDescriptor(PhysicalDeviceId.New(), "7.1", 1);
        var source = new ControlledSourceStream();
        StreamAcquisitionFactory open = _ => ValueTask.FromResult<IStreamSourceLease>(new TestSourceLease(source));
        await using var active = await graph.AcquireAsync(descriptor, open, watchdog.Token);
        var warm = await graph.AcquirePipelineAsync(descriptor, open,
            [new(new StreamTransformKey("mpegts", "idle-leaf"), new PassthroughTransform())], TimeSpan.FromMinutes(1), watchdog.Token);
        await warm.DisposeAsync();

        // Act
        await Assert.ThrowsAsync<StreamCapacityExceededException>(() => graph.AcquireAsync(
            CreateDescriptor(descriptor.DeviceId, "9.1", 1), _ => throw new InvalidOperationException("The tuner is still active."), watchdog.Token).AsTask());
        source.Write([7, 9]);
        var bytes = new byte[2];
        var count = await active.Stream.ReadAsync(bytes, watchdog.Token);

        // Assert
        Assert.Equal(2, count);
        Assert.Equal(new byte[] { 7, 9 }, bytes);
        Assert.Single(graph.GetDiagnostics().Nodes);
        await active.DisposeAsync();
        await graph.StopAsync(watchdog.Token);
    }

    /// <summary>Excludes transport abort exceptions caused by intentional shutdown from unexpected source-loss diagnostics.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopAsync_CanceledTransportRead_DoesNotReportSourceLoss(bool disposed)
    {
        // Arrange
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        watchdog.CancelAfter(TimeSpan.FromSeconds(10));
        var graph = new StreamingSessionGraph(new PhysicalCapacityLeaseRegistry(), NullLogger<StreamingSessionGraph>.Instance);
        var source = new CancellationFailureStream(disposed);
        await using var lease = await graph.AcquireAsync(CreateDescriptor(PhysicalDeviceId.New(), "7.1", 1),
            _ => ValueTask.FromResult<IStreamSourceLease>(new TestSourceLease(source)), watchdog.Token);
        await source.ReadStarted.Task.WaitAsync(watchdog.Token);

        // Act
        await graph.StopAsync(watchdog.Token);

        // Assert
        Assert.Empty(graph.GetDiagnostics().RecentTerminations);
        Assert.Empty(graph.GetDiagnostics().Nodes);
    }

    /// <summary>Bounds terminal history and excludes explicit viewer disposal and graph shutdown from source-loss records.</summary>
    [Fact]
    public async Task GetDiagnostics_TerminalHistory_IsBoundedAndIgnoresExplicitStops()
    {
        // Arrange
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        watchdog.CancelAfter(TimeSpan.FromSeconds(10));
        var graph = new StreamingSessionGraph(new PhysicalCapacityLeaseRegistry(), NullLogger<StreamingSessionGraph>.Instance);
        var descriptor = CreateDescriptor(PhysicalDeviceId.New(), "7.1", 1);

        // Act
        for (var index = 0; index < 65; index++)
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var lease = await graph.AcquireAsync(descriptor,
                _ => ValueTask.FromResult<IStreamSourceLease>(new TestSourceLease(new TerminalStream(release.Task, false))), watchdog.Token);
            release.SetResult();
            Assert.Equal(0, await lease.Stream.ReadAsync(new byte[1], watchdog.Token));
            await WaitUntilAsync(() => graph.GetDiagnostics().Nodes.Count == 0, watchdog.Token);
        }
        await using var active = await graph.AcquireAsync(descriptor,
            _ => ValueTask.FromResult<IStreamSourceLease>(new TestSourceLease(new ControlledSourceStream())), watchdog.Token);
        await active.DisposeAsync();
        await graph.StopAsync(watchdog.Token);

        // Assert
        Assert.Equal(64, graph.GetDiagnostics().RecentTerminations.Count);
        Assert.All(graph.GetDiagnostics().RecentTerminations, termination => Assert.Equal("source_eof", termination.Reason));
        Assert.Empty(graph.GetDiagnostics().Nodes);
    }

    /// <summary>Reports midstream EOF/failure without splicing, then permits a fresh independently routed acquisition.</summary>
    [Theory]
    [InlineData(false, "source_eof")]
    [InlineData(true, "source_failure")]
    public async Task AcquireAsync_SourceTerminates_RecordsReasonAndAllowsReconnect(bool fail, string expected)
    {
        // Arrange
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        watchdog.CancelAfter(TimeSpan.FromSeconds(10));
        using var operation = Lineup.Core.Extensibility.Diagnostics.LineupTelemetry.Begin("recovery_test", cancellationToken: watchdog.Token);
        var correlation = System.Diagnostics.Activity.Current!.TraceId.ToString();
        var graph = new StreamingSessionGraph(new PhysicalCapacityLeaseRegistry(), NullLogger<StreamingSessionGraph>.Instance);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var descriptor = CreateDescriptor(PhysicalDeviceId.New(), "7.1", 1);
        await using var first = await graph.AcquireAsync(descriptor,
            _ => ValueTask.FromResult<IStreamSourceLease>(new TestSourceLease(new TerminalStream(release.Task, fail))), watchdog.Token);
        await using var second = await graph.AcquireAsync(descriptor, _ => throw new InvalidOperationException("The source must remain shared."), watchdog.Token);
        await first.DisposeAsync();

        // Act
        release.SetResult();
        var read = second.Stream.ReadAsync(new byte[1], watchdog.Token).AsTask();
        if (fail)
        {
            await Assert.ThrowsAsync<IOException>(() => read);
        }
        else
        {
            Assert.Equal(0, await read);
        }
        await WaitUntilAsync(() => graph.GetDiagnostics().RecentTerminations.Count == 1, watchdog.Token);
        var replacement = new ControlledSourceStream();
        var reopened = false;
        await using var reconnected = await graph.AcquireAsync(descriptor,
            _ =>
            {
                reopened = true;
                return ValueTask.FromResult<IStreamSourceLease>(new TestSourceLease(replacement));
            }, watchdog.Token);

        // Assert
        var termination = Assert.Single(graph.GetDiagnostics().RecentTerminations);
        Assert.Equal(expected, termination.Reason);
        Assert.Equal(correlation, termination.CorrelationId);
        Assert.Equal(second.ProducerNodeId, termination.NodeId);
        Assert.True(reopened);
        Assert.NotEqual(second.SessionId, reconnected.SessionId);
        await reconnected.DisposeAsync();
        await graph.StopAsync(watchdog.Token);
        Assert.Single(graph.GetDiagnostics().RecentTerminations);
    }

    /// <summary>Verifies transform inputs receive startup grace; ordinary viewers retain five-second eviction.</summary>
    [Fact]
    public async Task AcquirePipelineAsync_StartupPause_PreservesTransformInputButNotStalledViewer()
    {
        // Arrange
        var clock = new ManualTimeProvider();
        var graph = new StreamingSessionGraph(new PhysicalCapacityLeaseRegistry(), NullLogger<StreamingSessionGraph>.Instance, clock);
        var source = new ControlledSourceStream();
        var reader = new ReadTrackingStream(source);
        var descriptor = CreateDescriptor(PhysicalDeviceId.New(), "7.1", capacity: 1);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transform = new PausedStartupTransform(release.Task);
        StreamAcquisitionFactory factory = _ => ValueTask.FromResult<IStreamSourceLease>(new TestSourceLease(reader));
        var pending = graph.AcquirePipelineAsync(descriptor, factory,
            [new(new StreamTransformKey("startup", "copy"), transform)], TimeSpan.Zero, TestContext.Current.CancellationToken).AsTask();

        try
        {
            await transform.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
            await using var stalledViewer = await graph.AcquireAsync(descriptor, factory, TestContext.Current.CancellationToken);
            source.Write([0x47, 1, 2, 3]);
            await reader.WaitForReadCountAsync(2, TestContext.Current.CancellationToken);

            // Act
            clock.Advance(TimeSpan.FromMilliseconds(34999));
            release.TrySetResult();
            await using var output = await pending;
            var bytes = new byte[4];
            await output.Stream.ReadExactlyAsync(bytes, TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal(new byte[] { 0x47, 1, 2, 3 }, bytes);
            Assert.Equal(1, graph.GetDiagnostics().BufferEvictions);
            await Assert.ThrowsAsync<IOException>(() => stalledViewer.Stream.ReadAsync(new byte[1], TestContext.Current.CancellationToken).AsTask());
        }
        finally
        {
            release.TrySetResult();
            await graph.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Verifies hosted processing inputs use an absolute startup deadline and retain ordinary viewer and steady-state eviction.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcquirePipelineAsync_ProcessingInput_PreservesBoundedStartupAndSteadyState(bool expireDuringStartup)
    {
        // Arrange
        var clock = new ManualTimeProvider();
        var graph = new StreamingSessionGraph(new PhysicalCapacityLeaseRegistry(), NullLogger<StreamingSessionGraph>.Instance, clock);
        var source = new ControlledSourceStream();
        var reader = new ReadTrackingStream(source);
        var descriptor = CreateDescriptor(PhysicalDeviceId.New(), "7.1", capacity: 1);
        StreamAcquisitionFactory factory = _ => ValueTask.FromResult<IStreamSourceLease>(new TestSourceLease(reader));
        await using var processing = await graph.AcquirePipelineAsync(descriptor, factory, [], TimeSpan.Zero, TestContext.Current.CancellationToken, processingInput: true);
        await using var viewer = await graph.AcquireAsync(descriptor, factory, TestContext.Current.CancellationToken);

        try
        {
            source.Write([0x47, 1, 2, 3]);
            await reader.WaitForReadCountAsync(2, TestContext.Current.CancellationToken);

            // Act
            clock.Advance(TimeSpan.FromMilliseconds(34999));

            // Assert
            Assert.Equal(1, graph.GetDiagnostics().BufferEvictions);
            await Assert.ThrowsAsync<IOException>(() => viewer.Stream.ReadAsync(new byte[1], TestContext.Current.CancellationToken).AsTask());
            if (!expireDuringStartup)
            {
                var bytes = new byte[4];
                await processing.Stream.ReadExactlyAsync(bytes, TestContext.Current.CancellationToken);
                Assert.Equal(new byte[] { 0x47, 1, 2, 3 }, bytes);
            }

            // Act
            clock.Advance(TimeSpan.FromMilliseconds(1));
            if (!expireDuringStartup)
            {
                source.Write([0x47, 4, 5, 6]);
                await reader.WaitForReadCountAsync(3, TestContext.Current.CancellationToken);
                clock.Advance(TimeSpan.FromSeconds(5));
            }

            // Assert
            Assert.Equal(2, graph.GetDiagnostics().BufferEvictions);
            Assert.Equal(0, graph.GetDiagnostics().BufferedBytes);
            await Assert.ThrowsAsync<IOException>(() => processing.Stream.ReadAsync(new byte[1], TestContext.Current.CancellationToken).AsTask());
        }
        finally
        {
            await graph.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Verifies an external processing input can compact short startup reads without changing bytes or exempting normal viewers.</summary>
    [Fact]
    public async Task AcquirePipelineAsync_ProcessingInput_CompactsFragmentedStartup()
    {
        // Arrange
        var clock = new ManualTimeProvider();
        var graph = new StreamingSessionGraph(new PhysicalCapacityLeaseRegistry(), NullLogger<StreamingSessionGraph>.Instance, clock);
        var source = new ControlledSourceStream();
        var reader = new ReadTrackingStream(source);
        var descriptor = CreateDescriptor(PhysicalDeviceId.New(), "7.1", capacity: 1);
        StreamAcquisitionFactory factory = _ => ValueTask.FromResult<IStreamSourceLease>(new TestSourceLease(reader));
        await using var processing = await graph.AcquirePipelineAsync(descriptor, factory, [], TimeSpan.Zero, TestContext.Current.CancellationToken, processingInput: true);
        await using var healthy = await graph.AcquireAsync(descriptor, factory, TestContext.Current.CancellationToken);
        var expected = new byte[2049 * 188];
        var received = new byte[188];

        try
        {
            // Act
            for (var index = 0; index < 2049; index++)
            {
                var payload = Enumerable.Repeat((byte)(index % 251), 188).ToArray();
                payload.CopyTo(expected, index * 188);
                source.Write(payload);
                await healthy.Stream.ReadExactlyAsync(received, TestContext.Current.CancellationToken);
                Assert.Equal(payload, received);
            }
            clock.Advance(TimeSpan.FromSeconds(6));
            var actual = new byte[expected.Length];
            await processing.Stream.ReadExactlyAsync(actual, TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal(expected, actual);
            Assert.Equal(0, graph.GetDiagnostics().BufferEvictions);
            Assert.Equal(0, graph.GetDiagnostics().BufferedBytes);
        }
        finally
        {
            await graph.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Verifies metadata probes survive bounded startup backpressure without extending stalled viewer lifetime.</summary>
    [Fact]
    public async Task GetOrCreateAcquisitionMetadataAsync_StartupPause_PreservesProbeInputButNotStalledViewer()
    {
        // Arrange
        var clock = new ManualTimeProvider();
        var graph = new StreamingSessionGraph(new PhysicalCapacityLeaseRegistry(), NullLogger<StreamingSessionGraph>.Instance, clock);
        var source = new ControlledSourceStream();
        var reader = new ReadTrackingStream(source);
        var descriptor = CreateDescriptor(PhysicalDeviceId.New(), "7.1", capacity: 1);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        StreamAcquisitionFactory factory = _ => ValueTask.FromResult<IStreamSourceLease>(new TestSourceLease(reader));
        var pending = graph.GetOrCreateAcquisitionMetadataAsync(descriptor, factory, "probe", async (input, token) =>
        {
            started.SetResult();
            await release.Task.WaitAsync(token);
            var bytes = new byte[4];
            await input.ReadExactlyAsync(bytes, token);
            return bytes;
        }, TestContext.Current.CancellationToken);

        try
        {
            await started.Task.WaitAsync(TestContext.Current.CancellationToken);
            await using var stalledViewer = await graph.AcquireAsync(descriptor, factory, TestContext.Current.CancellationToken);
            source.Write([0x47, 1, 2, 3]);
            await reader.WaitForReadCountAsync(2, TestContext.Current.CancellationToken);

            // Act
            clock.Advance(TimeSpan.FromMilliseconds(24999));
            release.TrySetResult();
            var result = await pending;

            // Assert
            Assert.Equal(new byte[] { 0x47, 1, 2, 3 }, result);
            Assert.Equal(1, graph.GetDiagnostics().BufferEvictions);
            await Assert.ThrowsAsync<IOException>(() => stalledViewer.Stream.ReadAsync(new byte[1], TestContext.Current.CancellationToken).AsTask());
        }
        finally
        {
            release.TrySetResult();
            await graph.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Verifies shared allocations remain accounted until every reader releases its reference, including partially consumed heads.</summary>
    [Fact]
    public async Task BufferBudget_SharedPayload_CountsAllocationOnce()
    {
        // Arrange
        using var budget = new StreamBufferBudget(new(), TimeProvider.System);
        var first = budget.Subscribe(_ => { });
        var second = budget.Subscribe(_ => { });
        var payload = new byte[64 * 1024];

        // Act
        budget.Publish(payload, [first, second]);
        await first.ReadAsync(new byte[1], TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(payload.Length, budget.RetainedBytes);

        // Act
        first.Close();
        await second.ReadAsync(new byte[payload.Length], TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0, budget.RetainedBytes);
        Assert.Equal(0, budget.Evictions);
    }

    /// <summary>Verifies a byte-limited stalled viewer does not interrupt a consuming viewer sharing its producer.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(20)]
    public async Task BufferBudget_SlowReader_EnforcesBytesWithoutBlockingHealthyReader(int startupGraceSeconds)
    {
        // Arrange
        using var budget = new StreamBufferBudget(new() { SubscriberBufferMib = 1, GraphBufferMib = 4 }, TimeProvider.System);
        var evicted = new TaskCompletionSource<StreamBufferBudget.Eviction>(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = budget.Subscribe(snapshot => evicted.TrySetResult(snapshot), TimeSpan.FromSeconds(startupGraceSeconds));
        var healthy = budget.Subscribe(_ => { });
        var destination = new byte[64 * 1024];

        // Act
        for (var index = 0; index < 17; index++)
        {
            budget.Publish(new byte[64 * 1024], [slow, healthy]);
            Assert.Equal(destination.Length, await healthy.ReadAsync(destination, TestContext.Current.CancellationToken));
            Assert.InRange(budget.RetainedBytes, 0, 1024 * 1024);
        }

        // Assert
        Assert.Equal(1, budget.Evictions);
        Assert.Equal(0, budget.RetainedBytes);
        var snapshot = await evicted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal("bytes", snapshot.Reason);
        Assert.Equal(1024 * 1024, snapshot.QueuedBytes);
        Assert.Equal(16, snapshot.QueuedEntries);
        Assert.Equal(1024 * 1024, snapshot.SubscriberLimit);
        await Assert.ThrowsAsync<IOException>(() => slow.ReadAsync(destination, TestContext.Current.CancellationToken).AsTask());
    }

    /// <summary>Verifies graph-wide exhaustion evicts the largest backlog rather than rejecting unrelated healthy traffic.</summary>
    [Fact]
    public async Task BufferBudget_GlobalLimit_EvictsBacklogAndAcceptsHealthyTraffic()
    {
        // Arrange
        using var budget = new StreamBufferBudget(new() { GraphBufferMib = 1 }, TimeProvider.System);
        var evicted = new TaskCompletionSource<StreamBufferBudget.Eviction>(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = budget.Subscribe(snapshot => evicted.TrySetResult(snapshot));
        var healthy = budget.Subscribe(_ => { });
        for (var index = 0; index < 16; index++)
        {
            budget.Publish(new byte[64 * 1024], [slow]);
        }

        // Act
        budget.Publish(new byte[64 * 1024], [healthy]);

        // Assert
        Assert.Equal(1, budget.Evictions);
        var snapshot = await evicted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal("aggregate", snapshot.Reason);
        Assert.Equal(1024 * 1024, snapshot.GraphBytes);
        Assert.Equal(1024 * 1024, snapshot.GraphLimit);
        Assert.Equal(64 * 1024, budget.RetainedBytes);
        Assert.Equal(64 * 1024, await healthy.ReadAsync(new byte[64 * 1024], TestContext.Current.CancellationToken));
        Assert.Equal(0, budget.RetainedBytes);
        await Assert.ThrowsAsync<IOException>(() => slow.ReadAsync(new byte[1], TestContext.Current.CancellationToken).AsTask());
    }

    /// <summary>Verifies idle producers cannot leave unread media retained beyond the five-second age limit.</summary>
    [Fact]
    public async Task BufferBudget_MediaAge_ExpiresWithoutFurtherPublication()
    {
        // Arrange
        var clock = new ManualTimeProvider();
        using var budget = new StreamBufferBudget(new(), clock);
        var evicted = new TaskCompletionSource<StreamBufferBudget.Eviction>(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = budget.Subscribe(snapshot => evicted.TrySetResult(snapshot));
        budget.Publish(new byte[64 * 1024], [slow]);

        // Act
        clock.Advance(TimeSpan.FromMilliseconds(4999));

        // Assert
        Assert.Equal(64 * 1024, budget.RetainedBytes);
        Assert.Equal(0, budget.Evictions);

        // Act
        clock.Advance(TimeSpan.FromMilliseconds(1));

        // Assert
        Assert.Equal(0, budget.RetainedBytes);
        Assert.Equal(1, budget.Evictions);
        var snapshot = await evicted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal("age", snapshot.Reason);
        Assert.Equal(TimeSpan.FromSeconds(5), snapshot.OldestAge);
        Assert.Equal(64 * 1024, snapshot.QueuedBytes);
        await Assert.ThrowsAsync<IOException>(() => slow.ReadAsync(new byte[1], TestContext.Current.CancellationToken).AsTask());
    }

    /// <summary>Verifies startup age grace ends at a fixed deadline even if the source becomes idle.</summary>
    [Fact]
    public async Task BufferBudget_StartupGrace_ExpiresAtAbsoluteDeadline()
    {
        // Arrange
        var clock = new ManualTimeProvider();
        using var budget = new StreamBufferBudget(new(), clock);
        var slow = budget.Subscribe(_ => { }, TimeSpan.FromSeconds(20));
        budget.Publish(new byte[188], [slow]);

        // Act
        clock.Advance(TimeSpan.FromMilliseconds(19999));

        // Assert
        Assert.Equal(188, budget.RetainedBytes);
        Assert.Equal(0, budget.Evictions);

        // Act
        clock.Advance(TimeSpan.FromMilliseconds(1));

        // Assert
        Assert.Equal(0, budget.RetainedBytes);
        Assert.Equal(1, budget.Evictions);
        await Assert.ThrowsAsync<IOException>(() => slow.ReadAsync(new byte[1], TestContext.Current.CancellationToken).AsTask());
    }

    /// <summary>Verifies draining a startup queue does not renew grace or weaken the steady-state age limit.</summary>
    [Fact]
    public async Task BufferBudget_StartupGrace_DrainDoesNotRenewSteadyStateDeadline()
    {
        // Arrange
        var clock = new ManualTimeProvider();
        using var budget = new StreamBufferBudget(new(), clock);
        var reader = budget.Subscribe(_ => { }, TimeSpan.FromSeconds(20));
        budget.Publish(new byte[188], [reader]);
        await reader.ReadAsync(new byte[188], TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(19));
        budget.Publish(new byte[188], [reader]);

        // Act
        clock.Advance(TimeSpan.FromMilliseconds(4999));

        // Assert
        Assert.Equal(188, budget.RetainedBytes);
        Assert.Equal(0, budget.Evictions);

        // Act
        clock.Advance(TimeSpan.FromMilliseconds(1));

        // Assert
        Assert.Equal(0, budget.RetainedBytes);
        Assert.Equal(1, budget.Evictions);
        await Assert.ThrowsAsync<IOException>(() => reader.ReadAsync(new byte[1], TestContext.Current.CancellationToken).AsTask());
    }

    /// <summary>Verifies startup grace never bypasses the fixed queue-entry limit.</summary>
    [Fact]
    public async Task BufferBudget_StartupGrace_StillEnforcesEntryLimit()
    {
        // Arrange
        using var budget = new StreamBufferBudget(new() { SubscriberBufferMib = 256, GraphBufferMib = 512 }, TimeProvider.System);
        var evicted = new TaskCompletionSource<StreamBufferBudget.Eviction>(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = budget.Subscribe(snapshot => evicted.TrySetResult(snapshot), TimeSpan.FromSeconds(20));

        // Act
        for (var index = 0; index < 2049; index++)
        {
            budget.Publish(new byte[64 * 1024], [slow]);
        }

        // Assert
        var snapshot = await evicted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal("entries", snapshot.Reason);
        Assert.Equal(2048, snapshot.QueuedEntries);
        Assert.Equal(128 * 1024 * 1024, snapshot.QueuedBytes);
        Assert.Equal(0, budget.RetainedBytes);
        Assert.Equal(1, budget.Evictions);
    }

    /// <summary>Verifies startup fragmentation preserves ordered bytes, shared ownership and the original grace deadline.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BufferBudget_StartupFragments_PreservesDataAndDeadline(bool expire)
    {
        // Arrange
        var clock = new ManualTimeProvider();
        using var budget = new StreamBufferBudget(new(), clock);
        var slow = budget.Subscribe(_ => { }, TimeSpan.FromSeconds(35));
        var healthy = budget.Subscribe(_ => { });
        var expected = new byte[2049 * 188];
        var received = new byte[188];

        // Act
        for (var index = 0; index < 2049; index++)
        {
            if (index == 2048)
            {
                clock.Advance(TimeSpan.FromMilliseconds(34999));
            }
            var payload = Enumerable.Repeat((byte)(index % 251), 188).ToArray();
            payload.CopyTo(expected, index * 188);
            budget.Publish(payload, [slow, healthy]);
            Assert.Equal(188, await healthy.ReadAsync(received, TestContext.Current.CancellationToken));
            Assert.Equal(payload, received);
            if (index == 0)
            {
                Assert.Equal(1, await slow.ReadAsync(new byte[1], TestContext.Current.CancellationToken));
            }
        }

        // Assert
        Assert.Equal(0, budget.Evictions);
        Assert.InRange(slow.Queue.Count, 1, 2048);
        Assert.Equal(expected.Length, budget.RetainedBytes);
        if (expire)
        {
            while (slow.Queue.Peek().Chunk.Payload.Length == 188)
            {
                await slow.ReadAsync(received, TestContext.Current.CancellationToken);
            }
            clock.Advance(TimeSpan.FromMilliseconds(1));
            await Assert.ThrowsAsync<IOException>(() => slow.ReadAsync(new byte[1], TestContext.Current.CancellationToken).AsTask());
            Assert.Equal(1, budget.Evictions);
        }
        else
        {
            var actual = new byte[expected.Length - 1];
            var offset = 0;
            while (offset < actual.Length)
            {
                offset += await slow.ReadAsync(actual.AsMemory(offset), TestContext.Current.CancellationToken);
            }
            Assert.Equal(expected[1..], actual);
        }
        Assert.Equal(0, budget.RetainedBytes);
    }

    /// <summary>Verifies large tail chunks do not prevent eligible startup compaction and ordered delivery of earlier fragments.</summary>
    [Fact]
    public async Task BufferBudget_StartupFragments_LargeTailPreservesEarlierCompactibleData()
    {
        // Arrange
        using var budget = new StreamBufferBudget(new(), new ManualTimeProvider());
        var slow = budget.Subscribe(_ => { }, TimeSpan.FromSeconds(35));
        var expected = new byte[2047 * 188 + 2 * 64 * 1024];
        var offset = 0;
        for (var index = 0; index < 2048; index++)
        {
            var payload = Enumerable.Repeat((byte)(index % 251), index >= 2046 ? 64 * 1024 : 188).ToArray();
            payload.CopyTo(expected, offset);
            offset += payload.Length;
            budget.Publish(payload, [slow]);
        }

        // Act
        budget.Publish(new byte[188], [slow]);

        // Assert
        Assert.Equal(0, budget.Evictions);
        var actual = new byte[expected.Length];
        offset = 0;
        while (offset < actual.Length)
        {
            offset += await slow.ReadAsync(actual.AsMemory(offset), TestContext.Current.CancellationToken);
        }
        Assert.Equal(expected, actual);
        Assert.Equal(0, budget.RetainedBytes);
    }

    /// <summary>Verifies startup compaction preserves another reader's original allocations and releases both representations.</summary>
    [Fact]
    public async Task BufferBudget_StartupCompaction_PreservesOtherReaderOwnership()
    {
        // Arrange
        using var budget = new StreamBufferBudget(new(), new ManualTimeProvider());
        var compacted = budget.Subscribe(_ => { }, TimeSpan.FromSeconds(35));
        var original = budget.Subscribe(_ => { }, TimeSpan.FromSeconds(35));
        var expected = new byte[2049 * 188];

        // Act
        for (var index = 0; index < 2049; index++)
        {
            var payload = Enumerable.Repeat((byte)(index % 251), 188).ToArray();
            payload.CopyTo(expected, index * 188);
            budget.Publish(payload, index == 2048 ? [compacted] : [compacted, original]);
        }
        var actual = new byte[expected.Length];
        var offset = 0;
        while (offset < actual.Length)
        {
            offset += await compacted.ReadAsync(actual.AsMemory(offset), TestContext.Current.CancellationToken);
        }

        // Assert
        Assert.Equal(0, budget.Evictions);
        Assert.Equal(expected, actual);
        Assert.Equal(2048 * 188, budget.RetainedBytes);
        actual = new byte[2048 * 188];
        offset = 0;
        while (offset < actual.Length)
        {
            offset += await original.ReadAsync(actual.AsMemory(offset), TestContext.Current.CancellationToken);
        }
        Assert.Equal(expected[..actual.Length], actual);
        Assert.Equal(0, budget.RetainedBytes);
    }

    /// <summary>Verifies fragmented queues retain entry eviction after startup and cannot allocate compaction outside the aggregate budget.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BufferBudget_CompactionNotEligible_PreservesEntryAndAggregateLimits(bool exhaustAggregate)
    {
        // Arrange
        var clock = new ManualTimeProvider();
        using var budget = new StreamBufferBudget(new() { SubscriberBufferMib = 2, GraphBufferMib = 1 }, clock);
        var slow = budget.Subscribe(_ => { }, TimeSpan.FromSeconds(35));
        var other = budget.Subscribe(_ => { }, TimeSpan.FromSeconds(35));
        var padding = new byte[1024 * 1024 - 2049 * 188];
        if (!exhaustAggregate)
        {
            budget.Publish(new byte[188], [slow]);
            await slow.ReadAsync(new byte[188], TestContext.Current.CancellationToken);
            clock.Advance(TimeSpan.FromSeconds(36));
        }
        for (var index = 0; index < 2048; index++)
        {
            budget.Publish(new byte[188], [slow]);
        }
        if (exhaustAggregate)
        {
            budget.Publish(padding, [other]);
        }

        // Act
        budget.Publish(new byte[188], [slow]);

        // Assert
        Assert.Equal(1, budget.Evictions);
        await Assert.ThrowsAsync<IOException>(() => slow.ReadAsync(new byte[1], TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(exhaustAggregate ? padding.Length : 0, budget.RetainedBytes);
        other.Close();
        Assert.Equal(0, budget.RetainedBytes);
    }

    /// <summary>Verifies ordinary EOF preserves remaining payload and explicit disposal releases it without waiting for expiration.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BufferBudget_SourceCompletes_ReleasesOwnedPayload(bool dispose)
    {
        // Arrange
        using var budget = new StreamBufferBudget(new(), TimeProvider.System);
        var subscription = budget.Subscribe(_ => { });
        budget.Publish(new byte[64 * 1024], [subscription]);

        // Act
        budget.Complete(subscription);
        if (dispose)
        {
            subscription.Close();
        }
        else
        {
            Assert.Equal(64 * 1024, await subscription.ReadAsync(new byte[64 * 1024], TestContext.Current.CancellationToken));
        }

        // Assert
        Assert.Equal(0, budget.RetainedBytes);
        Assert.Equal(0, await subscription.ReadAsync(new byte[1], TestContext.Current.CancellationToken));
    }

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
        Assert.Equal(1, fixture.Time.CreatedWarmTimers);

        // Act
        fixture.Time.Advance(TimeSpan.FromMilliseconds(19_999));
        await fixture.SendPacketAsync();

        // Assert
        fixture.AssertWarm(lease.ProducerNodeId);
        Assert.Equal(1, fixture.Time.CreatedWarmTimers);

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
        Assert.Equal(2, fixture.Time.CreatedWarmTimers);

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
        Assert.Equal(0, fixture.Time.CreatedWarmTimers);
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
        await WaitUntilAsync(() => fixture.Graph.GetDiagnostics().Nodes.Single(node => node.NodeId == lease.ProducerNodeId).IsWarm, fixture.CancellationToken);
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
        Assert.Equal(1, fixture.Time.CreatedWarmTimers);

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
        var clock = new ControlledTimeProvider();
        var graph = new StreamingSessionGraph(new PhysicalCapacityLeaseRegistry(), NullLogger<StreamingSessionGraph>.Instance, clock);
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
        await clock.WaitForTimerAsync(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromMilliseconds(50));
        await using var second = await graph.AcquirePipelineAsync(
            descriptor,
            _ => throw new InvalidOperationException("The warm acquisition must be reused."),
            stages,
            TimeSpan.FromMilliseconds(200),
            TestContext.Current.CancellationToken);
        await second.DisposeAsync();
        await clock.WaitForTimerAsync(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromMilliseconds(200));
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
    /// <param name="retryMilliseconds">The controlled delay before refreshing provisional metadata.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public async Task GetOrRefreshAcquisitionMetadataAsync_RefreshWaitExpires_ReturnsProvisionalAndRetainsRefresh(int retryMilliseconds)
    {
        // Arrange
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var clock = new ControlledTimeProvider();
        var graph = new StreamingSessionGraph(new PhysicalCapacityLeaseRegistry(), NullLogger<StreamingSessionGraph>.Instance, clock);
        var source = new ControlledSourceStream();
        var descriptor = CreateDescriptor(PhysicalDeviceId.New(), "7.1", capacity: 1);
        await using var continuity = await graph.AcquireAsync(
            descriptor,
            _ => ValueTask.FromResult<IStreamSourceLease>(new TestSourceLease(source)),
            TestContext.Current.CancellationToken);
        var starts = 0;
        var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<int> CreateMetadata(Stream stream, CancellationToken cancellationToken)
        {
            _ = stream;
            var attempt = Interlocked.Increment(ref starts);
            if (attempt == 2)
            {
                refreshStarted.SetResult();
                try
                {
                    await releaseRefresh.Task.WaitAsync(cancellationToken);
                }
                finally
                {
                    refreshCompleted.TrySetResult();
                }
            }
            return attempt == 1 ? 0 : 1;
        }
        var policy = new AcquisitionMetadataRefreshPolicy<int>
        {
            IsReusable = metadata => metadata > 0,
            Merge = Math.Max,
            RetryDelay = TimeSpan.FromMilliseconds(retryMilliseconds),
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
            cancellation.Token);
        var observed = Record.ExceptionAsync(async () => await firstTask).AsTask();
        try
        {
            if (retryMilliseconds > 0)
            {
                await clock.WaitForTimerAsync(policy.RetryDelay, TestContext.Current.CancellationToken);
                Assert.False(refreshStarted.Task.IsCompleted);
                clock.Advance(policy.RetryDelay);
            }
            await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            await clock.WaitForTimerAsync(policy.RefreshWaitTimeout, TestContext.Current.CancellationToken);
            clock.Advance(policy.RefreshWaitTimeout);
            var first = await firstTask.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.False(refreshCompleted.Task.IsCompleted);
            releaseRefresh.TrySetResult();
            var second = await graph.GetOrRefreshAcquisitionMetadataAsync(
                descriptor,
                _ => throw new InvalidOperationException(),
                "probe",
                CreateMetadata,
                policy,
                TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal(0, first);
            Assert.Equal(1, second);
            Assert.Equal(2, starts);
        }
        finally
        {
            releaseRefresh.TrySetResult();
            cancellation.Cancel();
            await observed.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            if (refreshStarted.Task.IsCompleted)
            {
                await refreshCompleted.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            }
        }
    }

    /// <summary>Verifies a shared metadata operation cannot outlive its independent operation budget.</summary>
    [Fact]
    public async Task GetOrRefreshAcquisitionMetadataAsync_OperationTimeout_CancelsAndEvictsMetadata()
    {
        // Arrange
        var clock = new ControlledTimeProvider();
        var graph = new StreamingSessionGraph(new PhysicalCapacityLeaseRegistry(), NullLogger<StreamingSessionGraph>.Instance, clock);
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
            if (Interlocked.Increment(ref starts) == 1)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
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

        try
        {
            await clock.WaitForTimerAsync(policy.OperationTimeout, TestContext.Current.CancellationToken);
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
            clock.Advance(policy.OperationTimeout);

            // Assert
            await Assert.ThrowsAsync<TimeoutException>(() => first.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<TimeoutException>(() => second.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
            Assert.Equal(1, starts);
            var replacement = await graph.GetOrRefreshAcquisitionMetadataAsync(
                descriptor, _ => throw new InvalidOperationException(), "probe", CreateMetadata, policy, TestContext.Current.CancellationToken);
            Assert.Equal(1, replacement);
            Assert.Equal(2, starts);
        }
        finally
        {
            clock.Advance(policy.OperationTimeout);
            await Assert.ThrowsAsync<TimeoutException>(() => first.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<TimeoutException>(() => second.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
            Assert.True(first.IsCompleted);
            Assert.True(second.IsCompleted);
        }
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

    /// <summary>Verifies reconnects wait for physical cleanup without overlapping opens or holding the graph lifecycle lock.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task AcquirePipelineAsync_DrainingSource_WaitsThenSharesReplacement(int stageCount)
    {
        // Arrange
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromSeconds(10));
        var graph = new StreamingSessionGraph(new PhysicalCapacityLeaseRegistry(), NullLogger<StreamingSessionGraph>.Instance);
        var descriptor = CreateDescriptor(PhysicalDeviceId.New(), "7.1", capacity: 1);
        var cleanupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var starts = 0;
        var active = 0;
        var maximumActive = 0;
        var replacementSource = new ControlledSourceStream();
        StreamTransformStage[] stages = Enumerable.Range(0, stageCount)
            .Select(index => new StreamTransformStage(new StreamTransformKey($"stage-{index}", "copy"), new PassthroughTransform()))
            .ToArray();
        ValueTask<IStreamSourceLease> Open(CancellationToken _)
        {
            var first = ++starts == 1;
            var currentActive = ++active;
            maximumActive = Math.Max(maximumActive, currentActive);
            return ValueTask.FromResult<IStreamSourceLease>(new TestSourceLease(
                first ? new ControlledSourceStream() : replacementSource,
                () => active--,
                first ? async () =>
                {
                    cleanupStarted.TrySetResult();
                    await releaseCleanup.Task;
                }
            : null));
        }
        var first = await graph.AcquirePipelineAsync(descriptor, Open, stages, TimeSpan.Zero, cancellation.Token);

        // Act
        var disposal = first.DisposeAsync().AsTask();
        try
        {
            await cleanupStarted.Task.WaitAsync(cancellation.Token);
            var reconnect = graph.AcquirePipelineAsync(descriptor, Open, stages, TimeSpan.Zero, cancellation.Token).AsTask();
            var sharing = graph.AcquirePipelineAsync(descriptor, Open, stages, TimeSpan.Zero, cancellation.Token).AsTask();
            using var pressureDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
            pressureDeadline.CancelAfter(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await graph.AcquireAsync(
                    CreateDescriptor(descriptor.DeviceId, "9.1", capacity: 1),
                    _ => throw new InvalidOperationException("A draining source still occupies physical capacity."),
                    pressureDeadline.Token));
            await using (var independent = await graph.AcquireAsync(
                CreateDescriptor(PhysicalDeviceId.New(), "10.1", capacity: 1),
                _ => ValueTask.FromResult<IStreamSourceLease>(new TestSourceLease(new ControlledSourceStream())),
                cancellation.Token))
            {
                Assert.NotNull(independent.Stream);
            }

            // Assert
            Assert.True(pressureDeadline.IsCancellationRequested);
            Assert.False(reconnect.IsCompleted);
            Assert.False(sharing.IsCompleted);
            Assert.Equal(1, starts);
            Assert.Equal(1, active);
            Assert.Equal(1, Assert.Single(graph.GetDiagnostics().Capacity).ActiveAcquisitions);
            Assert.Equal(1, Assert.Single(graph.GetDiagnostics().Capacity).Receivers);
            releaseCleanup.TrySetResult();
            await disposal.WaitAsync(cancellation.Token);
            await using var second = await reconnect.WaitAsync(cancellation.Token);
            await using var third = await sharing.WaitAsync(cancellation.Token);
            await replacementSource.WaitForReadAsync(cancellation.Token);
            replacementSource.Write([1, 2, 3]);
            var secondPayload = new byte[3];
            var thirdPayload = new byte[3];
            await second.Stream.ReadExactlyAsync(secondPayload, cancellation.Token);
            await third.Stream.ReadExactlyAsync(thirdPayload, cancellation.Token);
            Assert.Equal(secondPayload, thirdPayload);
            Assert.Equal(2, starts);
            Assert.Equal(1, maximumActive);
            Assert.Equal(second.ProducerNodeId, third.ProducerNodeId);
            Assert.Equal(1, Assert.Single(graph.GetDiagnostics().Capacity).ActiveAcquisitions);
        }
        finally
        {
            releaseCleanup.TrySetResult();
            await disposal;
            await graph.StopAsync(TestContext.Current.CancellationToken);
        }
        Assert.Equal(0, active);
        Assert.Empty(graph.GetDiagnostics().Nodes);
        Assert.Empty(graph.GetDiagnostics().Capacity);
    }

    /// <summary>Verifies pipeline and metadata waits preserve cancellation and cannot restart a shutting-down graph.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task AcquireAsync_DrainingSource_CancellationOrShutdownPreventsReplacement(bool metadata, bool stopGraph)
    {
        // Arrange
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromSeconds(10));
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        var graph = new StreamingSessionGraph(new PhysicalCapacityLeaseRegistry(), NullLogger<StreamingSessionGraph>.Instance);
        var descriptor = CreateDescriptor(PhysicalDeviceId.New(), "7.1", capacity: 1);
        var cleanupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var starts = 0;
        ValueTask<IStreamSourceLease> Open(CancellationToken _)
        {
            starts++;
            return ValueTask.FromResult<IStreamSourceLease>(new TestSourceLease(new ControlledSourceStream(), beforeDispose: async () =>
            {
                cleanupStarted.TrySetResult();
                await releaseCleanup.Task;
            }));
        }
        var first = await graph.AcquireAsync(descriptor, Open, cancellation.Token);

        // Act
        var disposal = first.DisposeAsync().AsTask();
        try
        {
            await cleanupStarted.Task.WaitAsync(cancellation.Token);
            Task waiting = metadata
                ? graph.GetOrCreateAcquisitionMetadataAsync(descriptor, Open, "probe", (_, _) => Task.FromResult(42), caller.Token)
                : graph.AcquireAsync(descriptor, Open, caller.Token).AsTask();

            // Assert
            Assert.False(waiting.IsCompleted);
            Assert.Equal(1, starts);
            if (stopGraph)
            {
                var shutdown = graph.StopAsync(cancellation.Token);
                Assert.False(shutdown.IsCompleted);
                releaseCleanup.TrySetResult();
                await Assert.ThrowsAsync<ObjectDisposedException>(() => waiting);
                await shutdown.WaitAsync(cancellation.Token);
            }
            else
            {
                caller.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
                Assert.False(disposal.IsCompleted);
            }
            Assert.Equal(1, starts);
        }
        finally
        {
            releaseCleanup.TrySetResult();
            await disposal;
            await graph.StopAsync(TestContext.Current.CancellationToken);
        }
        Assert.Empty(graph.GetDiagnostics().Capacity);
        Assert.Empty(graph.GetDiagnostics().Nodes);
    }

    /// <summary>Verifies metadata acquisition waits for a draining generation and probes only its replacement.</summary>
    [Fact]
    public async Task GetOrCreateAcquisitionMetadataAsync_DrainingSource_WaitsForCleanup()
    {
        // Arrange
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromSeconds(10));
        var graph = new StreamingSessionGraph(new PhysicalCapacityLeaseRegistry(), NullLogger<StreamingSessionGraph>.Instance);
        var descriptor = CreateDescriptor(PhysicalDeviceId.New(), "7.1", capacity: 1);
        var cleanupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var starts = 0;
        ValueTask<IStreamSourceLease> Open(CancellationToken _)
        {
            var first = ++starts == 1;
            return ValueTask.FromResult<IStreamSourceLease>(new TestSourceLease(new ControlledSourceStream(), beforeDispose: first ? async () =>
            {
                cleanupStarted.TrySetResult();
                await releaseCleanup.Task;
            }
            : null));
        }
        var first = await graph.AcquireAsync(descriptor, Open, cancellation.Token);

        // Act
        var disposal = first.DisposeAsync().AsTask();
        try
        {
            await cleanupStarted.Task.WaitAsync(cancellation.Token);
            var probe = graph.GetOrCreateAcquisitionMetadataAsync(descriptor, Open, "probe", (_, _) => Task.FromResult(42), cancellation.Token);

            // Assert
            Assert.False(probe.IsCompleted);
            Assert.Equal(1, starts);
            releaseCleanup.TrySetResult();
            Assert.Equal(42, await probe.WaitAsync(cancellation.Token));
            Assert.Equal(2, starts);
        }
        finally
        {
            releaseCleanup.TrySetResult();
            await disposal;
            await graph.StopAsync(TestContext.Current.CancellationToken);
        }
        Assert.Empty(graph.GetDiagnostics().Capacity);
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

    /// <summary>Verifies cancellation bounds shutdown waiting without skipping independent acquisition stops.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task StopAsync_BlockedTransform_StopsAcquisitionsDespiteCanceledWait(bool cancelWait, bool cancelBeforeStop)
    {
        // Arrange
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        watchdog.CancelAfter(TimeSpan.FromSeconds(10));
        using var shutdownCancellation = new CancellationTokenSource();
        var graph = new StreamingSessionGraph(new PhysicalCapacityLeaseRegistry(), NullLogger<StreamingSessionGraph>.Instance);
        var transformCleanupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTransformCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transformedSource = new ControlledSourceStream();
        var rawSource = new ControlledSourceStream();
        var rawDeviceId = PhysicalDeviceId.New();
        await using var transformed = await graph.AcquireTransformAsync(
            CreateDescriptor(PhysicalDeviceId.New(), "7.1", capacity: 1),
            new StreamTransformKey("virtual", "blocked-cleanup"),
            _ => ValueTask.FromResult<IStreamSourceLease>(new TestSourceLease(transformedSource)),
            new BlockingCleanupTransform(transformCleanupStarted, releaseTransformCleanup.Task),
            watchdog.Token);
        await using var raw = await graph.AcquireAsync(
            CreateDescriptor(rawDeviceId, "9.1", capacity: 1),
            _ => ValueTask.FromResult<IStreamSourceLease>(new TestSourceLease(rawSource)),
            watchdog.Token);
        await Task.WhenAll(transformedSource.WaitForReadAsync(watchdog.Token), rawSource.WaitForReadAsync(watchdog.Token));
        try
        {
            // Act
            if (cancelBeforeStop)
            {
                shutdownCancellation.Cancel();
            }
            var stopping = graph.StopAsync(shutdownCancellation.Token);
            await transformCleanupStarted.Task.WaitAsync(watchdog.Token);
            if (cancelWait)
            {
                shutdownCancellation.Cancel();
            }
            else
            {
                Assert.False(stopping.IsCompleted);
                Assert.Contains(graph.GetDiagnostics().Nodes, node => node.PhysicalDeviceId == rawDeviceId && node.ReceiverCount == 1);
                releaseTransformCleanup.TrySetResult();
            }
            var failure = await Record.ExceptionAsync(() => stopping.WaitAsync(watchdog.Token));

            // Assert
            if (cancelWait)
            {
                Assert.IsAssignableFrom<OperationCanceledException>(failure);
                Assert.DoesNotContain(graph.GetDiagnostics().Nodes, node => node.PhysicalDeviceId == rawDeviceId && node.ReceiverCount != 0);
                await rawSource.WaitForDisposalAsync(watchdog.Token);
            }
            else
            {
                Assert.Null(failure);
            }
            releaseTransformCleanup.TrySetResult();
            await graph.StopAsync(watchdog.Token);
            await graph.StopAsync(watchdog.Token);
            Assert.Empty(graph.GetDiagnostics().Nodes);
            Assert.Empty(graph.GetDiagnostics().Capacity);
        }
        finally
        {
            releaseTransformCleanup.TrySetResult();
            await graph.StopAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
    }

    private sealed class CancellationFailureStream(bool disposed) : MemoryStream
    {
        /// <summary>Gets confirmation that a transport read is pending before shutdown.</summary>
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <inheritdoc />
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellationToken.Register(() => stopped.TrySetResult());
            ReadStarted.TrySetResult();
            await stopped.Task;
            throw disposed ? new ObjectDisposedException("transport") : new IOException("Transport aborted during shutdown.");
        }
    }

    private sealed class TerminalStream(Task release, bool fail) : MemoryStream
    {
        /// <inheritdoc />
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await release.WaitAsync(cancellationToken);
            return fail ? throw new IOException("Controlled source failure.") : 0;
        }
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

        /// <summary>Initializes the warm pipeline fixture.</summary>
        /// <param name="stageCount">The stage count value.</param>
        public WarmPipelineFixture(int stageCount)
        {
            _watchdog.CancelAfter(TimeSpan.FromSeconds(10));
            _reader = new ReadTrackingStream(_source);
            Transforms = Enumerable.Range(0, stageCount).Select(_ => new ObservedTransform()).ToArray();
            Graph = new StreamingSessionGraph(new PhysicalCapacityLeaseRegistry(), NullLogger<StreamingSessionGraph>.Instance, Time);
        }

        /// <summary>Gets the time.</summary>
        public ManualTimeProvider Time { get; } = new();
        /// <summary>Gets the graph.</summary>
        public StreamingSessionGraph Graph { get; }
        /// <summary>Gets the transforms.</summary>
        public ObservedTransform[] Transforms { get; }
        /// <summary>Gets the acquisition starts.</summary>
        public int AcquisitionStarts => _acquisitionStarts;
        /// <summary>Gets the cancellation token.</summary>
        public CancellationToken CancellationToken => _watchdog.Token;

        /// <summary>Executes acquire asynchronously.</summary>
        /// <param name="keepWarmPeriod">The keep warm period value.</param>
        /// <returns>The operation's result.</returns>
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

        /// <summary>Executes send packet asynchronously.</summary>
        /// <returns>A task that completes when the operation has finished.</returns>
        public async Task SendPacketAsync()
        {
            var readers = GetReaders();
            var pending = readers.Select(reader => reader.WaitForReadCountAsync(reader.ReadCount + 1, CancellationToken)).ToArray();
            _source.Write([0x47, 1, 2, 3]);
            await Task.WhenAll(pending);
        }

        /// <summary>Executes assert warm.</summary>
        /// <param name="producerNodeId">The producer node ID value.</param>
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

        /// <summary>Waits for for release asynchronously.</summary>
        /// <returns>A task that completes when the operation has finished.</returns>
        public async Task WaitForReleaseAsync()
        {
            await _source.WaitForDisposalAsync(CancellationToken);
            await WaitUntilAsync(() => Graph.GetDiagnostics().Nodes.Count == 0, CancellationToken);
        }

        /// <inheritdoc />
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
        /// <summary>Gets or sets the start count.</summary>
        public int StartCount { get; private set; }
        /// <summary>Gets or sets the output.</summary>
        public ReadTrackingStream? Output { get; private set; }

        /// <summary>Starts the observed transform asynchronously.</summary>
        /// <param name="input">The input value.</param>
        /// <param name="cancellationToken">Token used to cancel the operation.</param>
        /// <returns>The operation's result.</returns>
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

        /// <summary>Gets the read count.</summary>
        public int ReadCount => Volatile.Read(ref _readCount);
        /// <inheritdoc />
        public override bool CanRead => input.CanRead;
        /// <inheritdoc />
        public override bool CanSeek => false;
        /// <inheritdoc />
        public override bool CanWrite => false;
        /// <inheritdoc />
        public override long Length => throw new NotSupportedException();
        /// <inheritdoc />
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        /// <summary>Waits for for read count asynchronously.</summary>
        /// <param name="count">The count value.</param>
        /// <param name="cancellationToken">Token used to cancel the operation.</param>
        /// <returns>A task that completes when the operation has finished.</returns>
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

        /// <inheritdoc />
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _reads.Writer.TryWrite(Interlocked.Increment(ref _readCount));
            return input.ReadAsync(buffer, cancellationToken);
        }

        /// <inheritdoc />
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

        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        /// <inheritdoc />
        public override void Flush() => throw new NotSupportedException();
        /// <inheritdoc />
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        /// <inheritdoc />
        public override void SetLength(long value) => throw new NotSupportedException();
        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];
        private TimeSpan _elapsed;
        private int _createdTimers;

        /// <summary>Gets the created warm timers.</summary>
        public int CreatedWarmTimers => Volatile.Read(ref _createdTimers);

        /// <inheritdoc />
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        /// <inheritdoc />
        public override long GetTimestamp() => _elapsed.Ticks;

        /// <inheritdoc />
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime != Timeout.InfiniteTimeSpan)
            {
                Interlocked.Increment(ref _createdTimers);
            }
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }

        /// <summary>Executes advance.</summary>
        /// <param name="duration">The duration value.</param>
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
            /// <summary>Gets the callback.</summary>
            public TimerCallback Callback { get; } = callback;
            /// <summary>Gets the state.</summary>
            public object? State { get; } = state;
            /// <summary>Gets or sets the due at.</summary>
            public TimeSpan DueAt { get; private set; }

            /// <inheritdoc />
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

            /// <inheritdoc />
            public void Dispose()
            {
                lock (owner._timers)
                {
                    owner._timers.Remove(this);
                }
            }

            /// <inheritdoc />
            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class PausedStartupTransform(Task release) : IStreamTransform
    {
        /// <summary>Signals that the transform owns its input subscription.</summary>
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <inheritdoc />
        public async ValueTask<IStreamSourceLease> StartAsync(Stream input, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await release.WaitAsync(cancellationToken);
            return new TestSourceLease(input);
        }
    }

    private sealed class PassthroughTransform : IStreamTransform
    {
        private int _startCount;

        /// <summary>Gets the test producer's reported encoder.</summary>
        public VideoEncodingSnapshot? VideoEncoding { get; init; }

        /// <summary>Gets the start count.</summary>
        public int StartCount => _startCount;

        /// <summary>Starts the passthrough transform asynchronously.</summary>
        /// <param name="input">The input value.</param>
        /// <param name="cancellationToken">Token used to cancel the operation.</param>
        /// <returns>The operation's result.</returns>
        public ValueTask<IStreamSourceLease> StartAsync(Stream input, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _startCount);
            return ValueTask.FromResult<IStreamSourceLease>(new TestSourceLease(input) { VideoEncoding = VideoEncoding });
        }
    }

    private sealed class FailingTransform(List<string> cleanup) : IStreamTransform
    {
        /// <summary>Starts the failing transform asynchronously.</summary>
        /// <param name="input">The input value.</param>
        /// <param name="cancellationToken">Token used to cancel the operation.</param>
        /// <returns>The operation's result.</returns>
        public ValueTask<IStreamSourceLease> StartAsync(Stream input, CancellationToken cancellationToken)
        {
            cleanup.Add("transform");
            throw new InvalidOperationException("Transform startup failed.");
        }
    }

    private sealed class BlockingCleanupTransform(TaskCompletionSource cleanupStarted, Task releaseCleanup) : IStreamTransform
    {
        /// <summary>Starts the blocking cleanup transform asynchronously.</summary>
        /// <param name="input">The input value.</param>
        /// <param name="cancellationToken">Token used to cancel the operation.</param>
        /// <returns>The operation's result.</returns>
        public ValueTask<IStreamSourceLease> StartAsync(Stream input, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IStreamSourceLease>(new TestSourceLease(input, beforeDispose: async () =>
            {
                cleanupStarted.TrySetResult();
                await releaseCleanup;
            }));
    }

    private sealed class ThrowingDisposeTransform : IStreamTransform
    {
        /// <summary>Starts the throwing dispose transform asynchronously.</summary>
        /// <param name="input">The input value.</param>
        /// <param name="cancellationToken">Token used to cancel the operation.</param>
        /// <returns>The operation's result.</returns>
        public ValueTask<IStreamSourceLease> StartAsync(Stream input, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IStreamSourceLease>(new ThrowingSourceLease(input));
    }

    private sealed class ThrowingSourceLease(Stream stream) : IStreamSourceLease
    {
        /// <summary>Gets the stream.</summary>
        public Stream Stream { get; } = stream;

        /// <summary>Gets the content type.</summary>
        public string? ContentType => "video/mp2t";

        /// <inheritdoc />
        public ValueTask DisposeAsync() => ValueTask.FromException(new InvalidOperationException("Transform cleanup failed."));
    }

    private sealed class TestSourceLease(Stream stream, Action? onDispose = null, Func<ValueTask>? beforeDispose = null) : IStreamSourceLease
    {
        /// <inheritdoc />
        public VideoEncodingSnapshot? VideoEncoding { get; init; }

        /// <summary>Gets the stream.</summary>
        public Stream Stream { get; } = stream;

        /// <summary>Gets the content type.</summary>
        public string? ContentType => "video/mp2t";

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (beforeDispose is not null)
            {
                await beforeDispose();
            }
            await Stream.DisposeAsync();
            onDispose?.Invoke();
        }
    }

    private sealed class AllocationTrackingStream(int chunkSize, int chunkCount) : MemoryStream
    {
        private readonly TaskCompletionSource<int> _firstRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long _allocatedBefore;
        private int _threadId;
        private int _reads;

        /// <summary>Gets producer-thread allocations between the second read and EOF, excluding consumer/test allocations.</summary>
        public TaskCompletionSource<long> AllocatedBytes { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Releases the initial read after graph admission so all remaining reads complete synchronously on one producer thread.</summary>
        public void ReleaseFirstRead() => _firstRead.TrySetResult(chunkSize);

        /// <inheritdoc />
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_reads == 1)
            {
                _threadId = Environment.CurrentManagedThreadId;
                _allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            }
            if (_reads == chunkCount)
            {
                if (_threadId != Environment.CurrentManagedThreadId)
                {
                    throw new InvalidOperationException("Allocation measurement requires uninterrupted synchronous producer reads.");
                }
                AllocatedBytes.TrySetResult(GC.GetAllocatedBytesForCurrentThread() - _allocatedBefore);
                return ValueTask.FromResult(0);
            }
            buffer.Span[..chunkSize].Fill((byte)_reads++);
            return _reads == 1 ? new(_firstRead.Task.WaitAsync(cancellationToken)) : ValueTask.FromResult(chunkSize);
        }
    }

    private sealed class ControlledSourceStream : Stream
    {
        private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>();
        private readonly TaskCompletionSource _readStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private byte[]? _current;
        private int _offset;

        /// <inheritdoc />
        public override bool CanRead => true;
        /// <inheritdoc />
        public override bool CanSeek => false;
        /// <inheritdoc />
        public override bool CanWrite => false;
        /// <inheritdoc />
        public override long Length => throw new NotSupportedException();
        /// <inheritdoc />
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        /// <summary>Writes the controlled source stream.</summary>
        /// <param name="payload">The payload value.</param>
        public void Write(byte[] payload) => _chunks.Writer.TryWrite(payload);

        /// <summary>Completes the source after all queued chunks have been read.</summary>
        public void Complete() => _chunks.Writer.TryComplete();

        /// <summary>Waits for for read asynchronously.</summary>
        /// <param name="cancellationToken">Token used to cancel the operation.</param>
        /// <returns>A task that completes when the operation has finished.</returns>
        public Task WaitForReadAsync(CancellationToken cancellationToken) => _readStarted.Task.WaitAsync(cancellationToken);

        /// <summary>Waits for for disposal asynchronously.</summary>
        /// <param name="cancellationToken">Token used to cancel the operation.</param>
        /// <returns>A task that completes when the operation has finished.</returns>
        public Task WaitForDisposalAsync(CancellationToken cancellationToken) => _disposed.Task.WaitAsync(cancellationToken);

        /// <inheritdoc />
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _readStarted.TrySetResult();
            while (_current is null || _offset == _current.Length)
            {
                if (!await _chunks.Reader.WaitToReadAsync(cancellationToken))
                {
                    return 0;
                }
                if (!_chunks.Reader.TryRead(out _current))
                {
                    continue;
                }
                _offset = 0;
            }
            var count = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _disposed.TrySetResult();
                _chunks.Writer.TryComplete();
            }
            base.Dispose(disposing);
        }

        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        /// <inheritdoc />
        public override void Flush() => throw new NotSupportedException();
        /// <inheritdoc />
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        /// <inheritdoc />
        public override void SetLength(long value) => throw new NotSupportedException();
        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
