using System.Text.Json;
using Lineup.Core.Common.Streaming;
using Lineup.Core.Extensibility.Streaming;
using Lineup.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Lineup.Core.Tests.Streaming;

/// <summary>Verifies catalog replacement retires graph ownership without affecting other tuners.</summary>
public sealed class PhysicalRuntimeReplacementTests
{
    /// <summary>Verifies a probe deadline remains a timeout even when final subscriber cleanup cancels its producer.</summary>
    [Fact]
    public async Task GetOrRefreshAcquisitionMetadataAsync_ProbeOnlyTimeout_PreservesTimeoutClassification()
    {
        // Arrange
        await using var fixture = new Fixture();
        await fixture.ApplyAsync("A");
        var descriptor = fixture.Descriptor(fixture.DeviceId);

        // Act
        var probe = fixture.Graph.GetOrRefreshAcquisitionMetadataAsync(
            descriptor, fixture.OpenAsync, "probe",
            async (_, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return 1;
            },
            new AcquisitionMetadataRefreshPolicy<int>
            {
                IsReusable = value => value > 0,
                Merge = Math.Max,
                RetryDelay = TimeSpan.Zero,
                OperationTimeout = TimeSpan.FromMilliseconds(50)
            }, fixture.Token);

        await fixture.Clock.WaitForTimerAsync(TimeSpan.FromMilliseconds(50), fixture.Token);
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(50));

        // Assert
        await Assert.ThrowsAsync<TimeoutException>(() => probe.WaitAsync(fixture.Token));
        Assert.Empty(fixture.Graph.GetDiagnostics().Nodes);
        Assert.Empty(fixture.Graph.GetDiagnostics().Capacity);
    }

    /// <summary>Verifies instance identity separates graph reuse while capacity remains aggregated by configured device.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task AcquireAsync_DifferentRuntimeInstances_NeverShareAnAcquisition(int capacity)
    {
        // Arrange
        await using var fixture = new Fixture();
        await fixture.ApplyAsync("A");
        var registry = new PhysicalCapacityLeaseRegistry();
        var graph = new StreamingSessionGraph(registry, NullLogger<StreamingSessionGraph>.Instance);
        var original = fixture.Descriptor(fixture.DeviceId) with { Capacity = new PhysicalTunerCapacity(capacity) };
        var replacement = original with { Runtime = Substitute.For<IPhysicalTunerDevice>() };
        await using var first = await graph.AcquireAsync(original, fixture.OpenAsync, fixture.Token);
        Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(original with { Runtime = null }));
        Assert.NotEqual(original.SourceIdentity, replacement.SourceIdentity);
        Assert.Equal(original.SourceIdentity, (original with { }).SourceIdentity);

        try
        {
            // Act
            if (capacity == 1)
            {
                var acquire = graph.AcquireAsync(replacement, fixture.OpenAsync, fixture.Token).AsTask();

                // Assert
                await Assert.ThrowsAsync<StreamCapacityExceededException>(() => acquire);
                Assert.Equal(1, Assert.Single(registry.GetDiagnostics()).ActiveAcquisitions);
                Assert.Equal(1, fixture.OpenCounts[fixture.DeviceId]);
            }
            else
            {
                await using var second = await graph.AcquireAsync(replacement, fixture.OpenAsync, fixture.Token);

                // Assert
                Assert.NotEqual(first.ProducerNodeId, second.ProducerNodeId);
                Assert.Equal(2, Assert.Single(registry.GetDiagnostics()).ActiveAcquisitions);
                Assert.Equal("A", await graph.GetOrCreateAcquisitionMetadataAsync(original, fixture.OpenAsync, "probe", (_, _) => Task.FromResult("A"), fixture.Token));
                Assert.Equal("B", await graph.GetOrCreateAcquisitionMetadataAsync(replacement, fixture.OpenAsync, "probe", (_, _) => Task.FromResult("B"), fixture.Token));
            }
        }
        finally
        {
            await graph.StopAsync(fixture.Token);
        }
    }

    /// <summary>Verifies labels, ordering and nonconnection settings preserve live sharing and current status names.</summary>
    [Fact]
    public async Task ApplyAsync_DisplayOnlyChanges_PreserveActiveGraph()
    {
        // Arrange
        await using var fixture = new Fixture();
        await fixture.ApplyAsync("A");
        var descriptor = fixture.Descriptor(fixture.DeviceId);
        await using var viewer = await fixture.AcquireAsync(descriptor);

        // Act
        var configuration = new StreamingConfiguration
        {
            PhysicalDevices = [fixture.Definition(fixture.DeviceId, "A") with { Name = "Renamed", Order = 20 }, fixture.Definition(fixture.UnrelatedId, "unrelated")],
            KeepWarmPeriodSeconds = 15
        };
        await fixture.Catalog.ApplyAsync(configuration, fixture.Token);
        await using var shared = await fixture.AcquireAsync(fixture.Descriptor(fixture.DeviceId));

        // Assert
        Assert.Equal(viewer.ProducerNodeId, shared.ProducerNodeId);
        Assert.Equal(1, fixture.OpenCounts[fixture.DeviceId]);
        Assert.Same(descriptor.Runtime, fixture.Catalog.GetRequired(fixture.DeviceId));
        Assert.True(fixture.Catalog.IsConfigurationCurrent(configuration));
    }

    /// <summary>Verifies active and warm consumers, derived stages and cached metadata cannot survive physical replacement.</summary>
    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    [InlineData(true, 2)]
    public async Task ApplyAsync_Replacement_RetiresOnlyAffectedRuntime(bool warm, int stageCount)
    {
        // Arrange
        await using var fixture = new Fixture();
        await fixture.ApplyAsync("A");
        var original = fixture.Descriptor(fixture.DeviceId);
        var unrelated = fixture.Descriptor(fixture.UnrelatedId);
        var stages = Enumerable.Range(0, stageCount)
            .Select(index => new StreamTransformStage(new StreamTransformKey($"stage-{index}", "copy"), new CopyTransform()))
            .ToArray();
        var viewers = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => fixture.AcquireAsync(original, stages)));
        await using var unaffected = await fixture.AcquireAsync(unrelated);
        Assert.Equal("A", await fixture.MetadataAsync(original, "A"));
        Assert.Equal(viewers[0].ProducerNodeId, viewers[1].ProducerNodeId);
        if (warm)
        {
            foreach (var viewer in viewers)
            {
                await viewer.DisposeAsync();
            }
            Assert.True(fixture.Graph.GetDiagnostics().Nodes.Single(node => node.NodeId == viewers[0].ProducerNodeId).IsWarm);
        }

        // Act
        await fixture.ApplyAsync("B");

        // Assert
        foreach (var viewer in viewers)
        {
            if (!warm)
            {
                Assert.Equal(0, await viewer.Stream.ReadAsync(new byte[1], fixture.Token));
            }
            await viewer.DisposeAsync();
        }
        Assert.Equal(1, fixture.OpenCounts[fixture.UnrelatedId]);
        Assert.Equal(unaffected.ProducerNodeId, Assert.Single(fixture.Graph.GetDiagnostics().Nodes).NodeId);
        Assert.Equal(1, Assert.Single(fixture.Graph.GetDiagnostics().Capacity).ActiveAcquisitions);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.AcquireAsync(original));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.MetadataAsync(original, "stale"));
        var replacement = fixture.Descriptor(fixture.DeviceId);
        var replacements = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => fixture.AcquireAsync(replacement, stages)));
        Assert.NotEqual(viewers[0].ProducerNodeId, replacements[0].ProducerNodeId);
        Assert.All(replacements, viewer => Assert.Equal(replacements[0].ProducerNodeId, viewer.ProducerNodeId));
        Assert.Equal("B", await fixture.MetadataAsync(replacement, "B"));
        Assert.Equal(2, fixture.OpenCounts[fixture.DeviceId]);
        foreach (var viewer in replacements)
        {
            await viewer.DisposeAsync();
        }
    }

    /// <summary>Verifies old capacity remains owned through blocked source cleanup and concurrent replacements serialize.</summary>
    [Fact]
    public async Task ApplyAsync_BlockedCleanup_FencesAdmissionAndRetainsCapacity()
    {
        // Arrange
        await using var fixture = new Fixture();
        await fixture.ApplyAsync("A");
        var original = fixture.Descriptor(fixture.DeviceId);
        fixture.BlockCleanup = true;
        await using var viewer = await fixture.AcquireAsync(original);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(fixture.Token);

        // Act
        var replacement = fixture.ApplyAsync("B", cancellation.Token);
        await fixture.CleanupStarted.Task.WaitAsync(fixture.Token);
        var next = fixture.ApplyAsync("C");
        try
        {
            // Assert
            Assert.False(replacement.IsCompleted);
            Assert.False(next.IsCompleted);
            Assert.Equal(0, await viewer.Stream.ReadAsync(new byte[1], fixture.Token));
            Assert.Equal(1, Assert.Single(fixture.Graph.GetDiagnostics().Capacity).ActiveAcquisitions);
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.AcquireAsync(original));
            await cancellation.CancelAsync();
            Assert.False(replacement.IsCompleted);
        }
        finally
        {
            fixture.ReleaseCleanup.TrySetResult();
        }
        await Task.WhenAll(replacement, next);
        Assert.Empty(fixture.Graph.GetDiagnostics().Nodes);
        Assert.Empty(fixture.Graph.GetDiagnostics().Capacity);
        await using var current = await fixture.AcquireAsync(fixture.Descriptor(fixture.DeviceId));
        Assert.Equal("C", await fixture.MetadataAsync(fixture.Descriptor(fixture.DeviceId), "C"));
        Assert.Equal(2, fixture.OpenCounts[fixture.DeviceId]);
    }

    /// <summary>Verifies concurrent pending opens share one source and all reject its late result after replacement begins.</summary>
    [Fact]
    public async Task ApplyAsync_PendingOpen_RejectsLateOldRuntime()
    {
        // Arrange
        await using var fixture = new Fixture();
        await fixture.ApplyAsync("A");
        var original = fixture.Descriptor(fixture.DeviceId);
        fixture.BlockOpen = true;
        var pending = Enumerable.Range(0, 3).Select(_ => fixture.AcquireAsync(original)).ToArray();
        await fixture.OpenStarted.Task.WaitAsync(fixture.Token);
        Assert.All(pending, request => Assert.False(request.IsCompleted));
        Assert.Equal(1, fixture.OpenCounts[fixture.DeviceId]);

        // Act
        var replacement = fixture.ApplyAsync("B");
        await fixture.RetirementStarted.Task.WaitAsync(fixture.Token);
        fixture.ReleaseOpen.TrySetResult();
        await replacement;

        // Assert
        foreach (var request in pending)
        {
            var failure = await Record.ExceptionAsync(async () =>
            {
                await using var late = await request;
            });
            Assert.True(failure is InvalidOperationException or OperationCanceledException, $"Expected a retired-runtime failure, received {failure}.");
        }
        Assert.Empty(fixture.Graph.GetDiagnostics().Nodes);
        Assert.Empty(fixture.Graph.GetDiagnostics().Capacity);
    }

    /// <summary>Verifies canceled pending opens and blocked cleanup cannot cross a replacement or disturb an unrelated tuner.</summary>
    [Fact]
    public async Task ApplyAsync_CanceledPendingOpenAndBlockedCleanup_RecoversOnlyAffectedRuntime()
    {
        // Arrange
        await using var fixture = new Fixture();
        await fixture.ApplyAsync("A");
        var original = fixture.Descriptor(fixture.DeviceId);
        var unrelatedRuntime = fixture.Catalog.GetRequired(fixture.UnrelatedId);
        await using var unaffected = await fixture.AcquireAsync(fixture.Descriptor(fixture.UnrelatedId));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(fixture.Token);
        fixture.BlockOpen = true;
        fixture.BlockCleanup = true;
        var opening = fixture.AcquireAsync(original, cancellationToken: cancellation.Token);
        await fixture.OpenStarted.Task.WaitAsync(fixture.Token);

        // Act
        await cancellation.CancelAsync();
        var replacement = fixture.ApplyAsync("B");
        fixture.ReleaseOpen.TrySetResult();
        try
        {
            await fixture.CleanupStarted.Task.WaitAsync(fixture.Token);

            // Assert
            Assert.False(replacement.IsCompleted);
            Assert.Equal(2, fixture.Graph.GetDiagnostics().Capacity.Sum(capacity => capacity.ActiveAcquisitions));
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.AcquireAsync(original));
        }
        finally
        {
            fixture.ReleaseCleanup.TrySetResult();
        }

        // Act
        await replacement;
        var failure = await Record.ExceptionAsync(async () =>
        {
            await using var retired = await opening;
        });
        await using var recovered = await fixture.AcquireAsync(fixture.Descriptor(fixture.DeviceId));

        // Assert
        Assert.IsAssignableFrom<OperationCanceledException>(failure);
        Assert.Same(unrelatedRuntime, fixture.Catalog.GetRequired(fixture.UnrelatedId));
        Assert.NotSame(original.Runtime, fixture.Catalog.GetRequired(fixture.DeviceId));
        Assert.Equal(2, fixture.OpenCounts[fixture.DeviceId]);
        Assert.Equal(1, fixture.OpenCounts[fixture.UnrelatedId]);
        Assert.Equal(2, fixture.Graph.GetDiagnostics().Capacity.Sum(capacity => capacity.ActiveAcquisitions));
        await fixture.Graph.StopAsync(fixture.Token);
        Assert.Empty(fixture.Graph.GetDiagnostics().Nodes);
        Assert.Empty(fixture.Graph.GetDiagnostics().Capacity);
    }

    /// <summary>Verifies replacement cancels an in-flight metadata operation and later generations never reuse its result.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplyAsync_PendingMetadata_CancelsProbeAndAllowsRepeatedReplacement(bool ignoreCancellation)
    {
        // Arrange
        await using var fixture = new Fixture();
        await fixture.ApplyAsync("A");
        var descriptor = fixture.Descriptor(fixture.DeviceId);
        await using var viewer = await fixture.AcquireAsync(descriptor);
        var probeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProbe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = fixture.Graph.GetOrCreateAcquisitionMetadataAsync(
            descriptor, fixture.OpenAsync, "probe",
            async (_, token) =>
            {
                probeStarted.TrySetResult();
                if (ignoreCancellation)
                {
                    await releaseProbe.Task.WaitAsync(fixture.Token);
                }
                else
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                return "retired";
            }, fixture.Token);
        await probeStarted.Task.WaitAsync(fixture.Token);

        // Act
        await fixture.ApplyAsync("B");
        releaseProbe.TrySetResult();

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.WaitAsync(fixture.Token));
        Assert.True(probe.IsCanceled, "The probe must observe producer cancellation, not merely the test watchdog.");
        foreach (var host in new[] { "B", "A", "C" })
        {
            await fixture.ApplyAsync(host);
            var current = fixture.Descriptor(fixture.DeviceId);
            await using var active = await fixture.AcquireAsync(current);
            Assert.Equal(host, await fixture.MetadataAsync(current, host));
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.AcquireAsync(descriptor));
        }
    }

    /// <summary>Verifies removed and disabled tuners lose their viewers and cannot be reopened through old routes.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplyAsync_RemovedOrDisabled_RetiresRuntime(bool disabled)
    {
        // Arrange
        await using var fixture = new Fixture();
        await fixture.ApplyAsync("A");
        var original = fixture.Descriptor(fixture.DeviceId);
        await using var viewer = await fixture.AcquireAsync(original);

        // Act
        await fixture.Catalog.ApplyAsync(new StreamingConfiguration
        {
            PhysicalDevices = disabled ? [fixture.Definition(fixture.DeviceId, "A") with { Enabled = false }] : []
        }, fixture.Token);

        // Assert
        Assert.Equal(0, await viewer.Stream.ReadAsync(new byte[1], fixture.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.AcquireAsync(original));
        Assert.Empty(fixture.Graph.GetDiagnostics().Nodes);
        Assert.Empty(fixture.Graph.GetDiagnostics().Capacity);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly CancellationTokenSource _watchdog = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        /// <summary>Builds the production catalog and graph composition with controlled adapters.</summary>
        internal Fixture()
        {
            _watchdog.CancelAfter(TimeSpan.FromSeconds(15));
            var driver = Substitute.For<IPhysicalTunerDriver>();
            driver.Kind.Returns("replacement-test");
            driver.Validate(Arg.Any<AdapterConfiguration>()).Returns([]);
            driver.CreateAsync(Arg.Any<PhysicalDeviceId>(), Arg.Any<string>(), Arg.Any<AdapterConfiguration>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                var runtime = Substitute.For<IPhysicalTunerDevice, IDisposable>();
                runtime.Snapshot.Returns(new PhysicalDeviceSnapshot
                {
                    Id = call.Arg<PhysicalDeviceId>(),
                    Name = call.Arg<string>(),
                    Kind = "replacement-test",
                    Capacity = new PhysicalTunerCapacity(1)
                });
                return ValueTask.FromResult(runtime);
            });
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<TimeProvider>(Clock);
            services.AddSingleton(driver);
            services.AddEpgCore();
            _services = services.BuildServiceProvider();
            _services.GetRequiredService<PhysicalDeviceRuntimeState>().Retiring += _ =>
            {
                RetirementStarted.TrySetResult();
                return Task.CompletedTask;
            };
            Graph = _services.GetRequiredService<IStreamingSessionGraph>();
            Catalog = _services.GetRequiredService<IPhysicalDeviceCatalog>();
        }

        /// <summary>Gets the reconfigured tuner identity.</summary>
        internal PhysicalDeviceId DeviceId { get; } = PhysicalDeviceId.New();
        /// <summary>Gets the controlled clock used by the production graph composition.</summary>
        internal ControlledTimeProvider Clock { get; } = new();
        /// <summary>Gets an unaffected tuner identity.</summary>
        internal PhysicalDeviceId UnrelatedId { get; } = PhysicalDeviceId.New();
        /// <summary>Gets the production graph.</summary>
        internal IStreamingSessionGraph Graph { get; }
        /// <summary>Gets the production catalog.</summary>
        internal IPhysicalDeviceCatalog Catalog { get; }
        /// <summary>Gets the test watchdog.</summary>
        internal CancellationToken Token => _watchdog.Token;
        /// <summary>Gets physical source creation counts.</summary>
        internal Dictionary<PhysicalDeviceId, int> OpenCounts { get; } = [];
        /// <summary>Gets or sets whether the next source delays cleanup.</summary>
        internal bool BlockCleanup { get; set; }
        /// <summary>Gets or sets whether the next source delays creation.</summary>
        internal bool BlockOpen { get; set; }
        /// <summary>Gets the cleanup-entry signal.</summary>
        internal TaskCompletionSource CleanupStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>Gets the cleanup release signal.</summary>
        internal TaskCompletionSource ReleaseCleanup { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>Gets the source-creation entry signal.</summary>
        internal TaskCompletionSource OpenStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>Gets the catalog's admission-fence signal before graph cleanup.</summary>
        internal TaskCompletionSource RetirementStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>Gets the source-creation release signal.</summary>
        internal TaskCompletionSource ReleaseOpen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Creates one connection definition.</summary>
        internal PhysicalDeviceDefinition Definition(PhysicalDeviceId id, string host) => new()
        {
            Id = id,
            Name = id.ToString(),
            Adapter = new AdapterConfiguration("replacement-test", JsonSerializer.SerializeToElement(new { host }))
        };

        /// <summary>Changes one connection while retaining an unrelated tuner.</summary>
        internal Task ApplyAsync(string host, CancellationToken? cancellationToken = null) => Catalog.ApplyAsync(new StreamingConfiguration
        {
            PhysicalDevices = [Definition(DeviceId, host), Definition(UnrelatedId, "unrelated")]
        }, cancellationToken ?? Token);

        /// <summary>Captures the current runtime in a source descriptor.</summary>
        internal StreamAcquisitionDescriptor Descriptor(PhysicalDeviceId id) => new(
            id, PhysicalAcquisitionKey.Create(new PhysicalStreamRequest { ChannelId = new SourceChannelId(id, "7.1") }), new PhysicalTunerCapacity(1))
        {
            Runtime = Catalog.GetRequired(id)
        };

        /// <summary>Opens a reusable, optionally transformed source.</summary>
        internal Task<IStreamSessionLease> AcquireAsync(StreamAcquisitionDescriptor descriptor, IReadOnlyList<StreamTransformStage>? stages = null, CancellationToken? cancellationToken = null) =>
            Graph.AcquirePipelineAsync(descriptor, token => OpenAsync(descriptor.DeviceId, token), stages ?? [], TimeSpan.FromMinutes(1), cancellationToken ?? Token).AsTask();

        /// <summary>Requests cached acquisition metadata.</summary>
        internal Task<string> MetadataAsync(StreamAcquisitionDescriptor descriptor, string value) =>
            Graph.GetOrCreateAcquisitionMetadataAsync(descriptor, OpenAsync, "probe", (_, _) => Task.FromResult(value), Token);

        /// <summary>Creates an owned source for the reconfigured tuner.</summary>
        internal ValueTask<IStreamSourceLease> OpenAsync(CancellationToken token) => OpenAsync(DeviceId, token);

        private async ValueTask<IStreamSourceLease> OpenAsync(PhysicalDeviceId id, CancellationToken token)
        {
            OpenCounts[id] = OpenCounts.GetValueOrDefault(id) + 1;
            var blockedCleanup = BlockCleanup;
            BlockCleanup = false;
            if (BlockOpen)
            {
                BlockOpen = false;
                OpenStarted.TrySetResult();
                await ReleaseOpen.Task.WaitAsync(Token);
            }
            return new SourceLease(new WaitingStream(), async () =>
            {
                if (blockedCleanup)
                {
                    CleanupStarted.TrySetResult();
                    await ReleaseCleanup.Task.WaitAsync(Token);
                }
            });
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            ReleaseCleanup.TrySetResult();
            ReleaseOpen.TrySetResult();
            await Graph.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await _services.DisposeAsync();
            _watchdog.Dispose();
        }
    }

    private sealed class CopyTransform : IStreamTransform
    {
        /// <inheritdoc />
        public ValueTask<IStreamSourceLease> StartAsync(Stream input, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IStreamSourceLease>(new SourceLease(input, () => ValueTask.CompletedTask));
    }

    private sealed class SourceLease(Stream stream, Func<ValueTask> cleanup) : IStreamSourceLease
    {
        /// <inheritdoc />
        public Stream Stream => stream;
        /// <inheritdoc />
        public string? ContentType => "video/mp2t";

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            await cleanup();
            await stream.DisposeAsync();
        }
    }

    private sealed class WaitingStream : Stream
    {
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

        /// <inheritdoc />
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
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
