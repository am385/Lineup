using System.Text.Json;
using Lineup.Core.Common.Streaming;
using Lineup.Core.Extensibility.Streaming;
using Lineup.Core.Settings;
using Lineup.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Lineup.Core.Tests.Streaming;

/// <summary>Verifies obsolete tune and refresh requests cannot supersede saved physical configuration.</summary>
public sealed class PhysicalConfigurationAuthorityTests
{
    /// <summary>Verifies a tune paused after capturing settings resolves the replacement without retiring its live stream.</summary>
    [Fact]
    public async Task ResolveVirtualAsync_SettingsSavedDuringTopologyRead_RestartsWithoutRollingBackLiveRuntime()
    {
        // Arrange
        await using var fixture = new Fixture();
        var original = await fixture.SaveAsync("A");
        await fixture.RefreshAsync(original);
        var pausedRead = new Pause();
        fixture.Factory.BeforeNextContext = pausedRead.WaitAsync;
        var pendingRoute = fixture.Resolver.ResolveVirtualAsync(fixture.VirtualId, "7.1", fixture.Token);
        await pausedRead.Entered.Task.WaitAsync(fixture.Token);

        // Act
        var replacement = await fixture.SaveAsync("B");
        await fixture.RefreshAsync(replacement);
        var current = fixture.Device;
        var currentRoute = await fixture.Resolver.ResolveVirtualAsync(fixture.VirtualId, "7.1", fixture.Token);
        Assert.NotNull(currentRoute);
        await using var viewer = await fixture.Graph.AcquireAsync(currentRoute.Acquisition, currentRoute.Candidates[0].OpenAsync, fixture.Token);
        await fixture.AssertReadableAsync(viewer, current);
        pausedRead.Release.TrySetResult();
        var resumedRoute = await pendingRoute.WaitAsync(fixture.Token);
        var obsoleteRefresh = await fixture.Lineups.RefreshAsync(original, fixture.Token);

        // Assert
        Assert.NotNull(resumedRoute);
        Assert.Same(current, resumedRoute.PhysicalDevice);
        Assert.Equal("B", resumedRoute.VirtualDevice.Name);
        Assert.Equal("B", resumedRoute.Candidates[0].SourceMetadata["host"]);
        Assert.False(Assert.Single(obsoleteRefresh).Succeeded);
        Assert.Same(current, fixture.Device);
        Assert.False(current.IsDisposed);
        Assert.False(current.LeaseDisposed);
        Assert.Equal(2, fixture.Created.Count);
        Assert.True(fixture.Catalog.IsConfigurationCurrent(replacement));
        Assert.False(fixture.Catalog.IsConfigurationCurrent(original));
        Assert.Equal("B", Assert.Single(await fixture.Store.ReadActiveAsync(fixture.Token)).Source.Metadata["host"]);
        await fixture.AssertReadableAsync(viewer, current);
    }

    /// <summary>Verifies desired settings are rechecked after asynchronous activation or lineup fetching, before either can publish old state.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefreshAsync_SettingsSavedDuringDeviceWork_DiscardsObsoletePublication(bool pauseLineup)
    {
        // Arrange
        await using var fixture = new Fixture();
        var original = await fixture.SaveAsync("A");
        var pausedWork = new Pause();
        if (pauseLineup)
        {
            fixture.BeforeLineup = pausedWork.WaitAsync;
        }
        else
        {
            fixture.BeforeCreate = pausedWork.WaitAsync;
        }
        var pendingRefresh = fixture.Lineups.RefreshAsync(original, fixture.Token);
        await pausedWork.Entered.Task.WaitAsync(fixture.Token);

        // Act
        var replacement = await fixture.SaveAsync("B");
        if (pauseLineup)
        {
            await fixture.RefreshAsync(replacement);
        }
        pausedWork.Release.TrySetResult();
        var obsolete = await pendingRefresh.WaitAsync(fixture.Token);
        if (!pauseLineup)
        {
            await fixture.RefreshAsync(replacement);
        }

        // Assert
        Assert.False(Assert.Single(obsolete).Succeeded);
        Assert.True(Assert.Single(fixture.Created, device => device.Host == "A").IsDisposed);
        Assert.Equal("B", fixture.Device.Host);
        Assert.False(fixture.Device.IsDisposed);
        Assert.True(fixture.Catalog.IsConfigurationCurrent(replacement));
        Assert.Equal("B", Assert.Single(await fixture.Store.ReadActiveAsync(fixture.Token)).Source.Metadata["host"]);
    }

    /// <summary>Verifies a save cannot overtake an admitted asynchronous publication and subsequent obsolete callbacks never execute.</summary>
    [Fact]
    public async Task TryPublishAsync_SettingsSaveDuringPublication_SerializesSaveAndRejectsLateRefresh()
    {
        // Arrange
        await using var fixture = new Fixture();
        var original = await fixture.SaveAsync("A");
        await fixture.RefreshAsync(original);
        var publication = new Pause();
        var pendingPublication = fixture.Catalog.TryPublishAsync(original, publication.WaitAsync, fixture.Token);
        await publication.Entered.Task.WaitAsync(fixture.Token);
        var updateEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Act
        var save = fixture.Settings.UpdateAsync(settings =>
        {
            updateEntered.TrySetResult();
            settings.Streaming = fixture.Configuration("B");
        });
        var saveWasBlocked = !updateEntered.Task.IsCompleted && !save.IsCompleted;
        publication.Release.TrySetResult();
        var published = await pendingPublication.WaitAsync(fixture.Token);
        await save.WaitAsync(fixture.Token);
        await fixture.RefreshAsync(fixture.Settings.Settings.Streaming);
        var staleCallbackInvoked = false;
        var stalePublished = await fixture.Catalog.TryPublishAsync(original, _ =>
        {
            staleCallbackInvoked = true;
            return Task.CompletedTask;
        }, fixture.Token);

        // Assert
        Assert.True(saveWasBlocked);
        Assert.True(published);
        Assert.False(stalePublished);
        Assert.False(staleCallbackInvoked);
        Assert.Equal("B", fixture.Device.Host);
        Assert.Equal("B", Assert.Single(await fixture.Store.ReadActiveAsync(fixture.Token)).Source.Metadata["host"]);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory = Path.GetFullPath(Path.Combine("out", "tests", $"physical-authority-{Guid.NewGuid():N}"));
        private readonly ServiceProvider _services;
        private readonly CancellationTokenSource _watchdog = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        /// <summary>Builds real settings, catalog, topology and graph services with controlled physical devices.</summary>
        internal Fixture()
        {
            _watchdog.CancelAfter(TimeSpan.FromSeconds(15));
            Directory.CreateDirectory(_directory);
            Settings = new LineupSettingsService(NullLogger<LineupSettingsService>.Instance, Path.Combine(_directory, AppConstants.SettingsFileName));
            Factory = new ContextFactory(new DbContextOptionsBuilder<EpgDbContext>()
                .UseSqlite($"Data Source={Path.Combine(_directory, "topology.db")};Pooling=False").Options);
            var driver = Substitute.For<IPhysicalTunerDriver>();
            driver.Kind.Returns("authority-test");
            driver.Validate(Arg.Any<AdapterConfiguration>()).Returns([]);
            driver.CreateAsync(Arg.Any<PhysicalDeviceId>(), Arg.Any<string>(), Arg.Any<AdapterConfiguration>(), Arg.Any<CancellationToken>())
                .Returns(call => CreateDeviceAsync(call.Arg<AdapterConfiguration>(), call.Arg<CancellationToken>()));
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<ILineupSettingsService>(Settings);
            services.AddSingleton(driver);
            services.AddEpgCore();
            services.AddSingleton<IDbContextFactory<EpgDbContext>>(Factory);
            _services = services.BuildServiceProvider();
            Catalog = _services.GetRequiredService<IPhysicalDeviceCatalog>();
            Lineups = _services.GetRequiredService<IPhysicalLineupService>();
            Graph = _services.GetRequiredService<IStreamingSessionGraph>();
            Store = _services.GetRequiredService<StreamingTopologyStore>();
            Resolver = new StreamingRouteResolver(Settings, Catalog, Lineups, Store);
        }

        /// <summary>Gets the reconfigured physical identity.</summary>
        internal PhysicalDeviceId DeviceId { get; } = PhysicalDeviceId.New();
        /// <summary>Gets the stable virtual output identity.</summary>
        internal VirtualDeviceId VirtualId { get; } = VirtualDeviceId.New();
        /// <summary>Gets the real persisted settings authority.</summary>
        internal LineupSettingsService Settings { get; }
        /// <summary>Gets the controllable topology context factory.</summary>
        internal ContextFactory Factory { get; }
        /// <summary>Gets the production catalog.</summary>
        internal IPhysicalDeviceCatalog Catalog { get; }
        /// <summary>Gets the production lineup publisher.</summary>
        internal IPhysicalLineupService Lineups { get; }
        /// <summary>Gets the production stream graph.</summary>
        internal IStreamingSessionGraph Graph { get; }
        /// <summary>Gets the persisted topology.</summary>
        internal StreamingTopologyStore Store { get; }
        /// <summary>Gets the production route resolver.</summary>
        internal StreamingRouteResolver Resolver { get; }
        /// <summary>Gets the test watchdog.</summary>
        internal CancellationToken Token => _watchdog.Token;
        /// <summary>Gets the currently published runtime.</summary>
        internal Device Device => Assert.IsType<Device>(Catalog.GetRequired(DeviceId));
        /// <summary>Gets all created runtimes, including discarded activations.</summary>
        internal System.Collections.Concurrent.ConcurrentQueue<Device> Created { get; } = new();
        /// <summary>Gets or sets the original runtime's asynchronous creation hook.</summary>
        internal Func<CancellationToken, Task>? BeforeCreate { get; set; }
        /// <summary>Gets or sets the original runtime's asynchronous lineup hook.</summary>
        internal Func<CancellationToken, Task>? BeforeLineup { get; set; }

        private async ValueTask<IPhysicalTunerDevice> CreateDeviceAsync(AdapterConfiguration configuration, CancellationToken cancellationToken)
        {
            var host = configuration.Configuration.GetProperty("host").GetString()!;
            if (host == "A" && BeforeCreate is { } beforeCreate)
            {
                await beforeCreate(cancellationToken);
            }
            var device = new Device(DeviceId, host, async token =>
            {
                if (host == "A" && BeforeLineup is { } beforeLineup)
                {
                    await beforeLineup(token);
                }
            });
            Created.Enqueue(device);
            return device;
        }

        /// <summary>Creates a connection revision with stable physical and virtual identities.</summary>
        internal StreamingConfiguration Configuration(string host) => new()
        {
            PhysicalDevices = [new PhysicalDeviceDefinition
            {
                Id = DeviceId,
                Name = "Physical",
                Adapter = new AdapterConfiguration("authority-test", JsonSerializer.SerializeToElement(new { host }))
            }],
            VirtualDevices = [new VirtualDeviceDefinition
            {
                Id = VirtualId,
                Name = host,
                Adapter = new AdapterConfiguration("authority-output", JsonSerializer.SerializeToElement(new { }))
            }]
        };

        /// <summary>Persists and returns one desired connection revision.</summary>
        internal async Task<StreamingConfiguration> SaveAsync(string host)
        {
            await Settings.UpdateAsync(settings => settings.Streaming = Configuration(host));
            return Settings.Settings.Streaming;
        }

        /// <summary>Activates and publishes a revision, requiring successful lineup publication.</summary>
        internal async Task RefreshAsync(StreamingConfiguration configuration) =>
            Assert.All(await Lineups.RefreshAsync(configuration, Token), result => Assert.True(result.Succeeded, result.Error));

        /// <summary>Requires the existing graph consumer to receive another byte from its physical source.</summary>
        internal async Task AssertReadableAsync(IStreamSessionLease viewer, Device device)
        {
            device.Source.Pulse();
            Assert.Equal(1, await viewer.Stream.ReadAsync(new byte[1], Token));
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            await _watchdog.CancelAsync();
            await Graph.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await _services.DisposeAsync();
            foreach (var device in Created.Where(device => !device.IsDisposed))
            {
                device.Dispose();
            }
            _watchdog.Dispose();
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class ContextFactory(DbContextOptions<EpgDbContext> options) : IDbContextFactory<EpgDbContext>
    {
        /// <summary>Runs once before creating the next topology context.</summary>
        internal Func<CancellationToken, Task>? BeforeNextContext;

        /// <inheritdoc />
        public EpgDbContext CreateDbContext() => new(options);

        /// <inheritdoc />
        public async Task<EpgDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref BeforeNextContext, null) is { } before)
            {
                await before(cancellationToken);
            }
            return CreateDbContext();
        }
    }

    private sealed class Pause
    {
        /// <summary>Gets the operation-entry signal.</summary>
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>Gets the controlled continuation signal.</summary>
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Signals entry and pauses the operation until released.</summary>
        internal async Task WaitAsync(CancellationToken token)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(token);
        }
    }

    private sealed class Device(PhysicalDeviceId id, string host, Func<CancellationToken, Task> beforeLineup) : IPhysicalTunerDevice, IDisposable
    {
        /// <summary>Gets the connection revision owned by this runtime.</summary>
        internal string Host => host;
        /// <summary>Gets whether this runtime has been disposed.</summary>
        internal bool IsDisposed { get; private set; }
        /// <summary>Gets whether its graph-owned source lease has been disposed.</summary>
        internal bool LeaseDisposed { get; private set; }
        /// <summary>Gets its controlled live physical stream.</summary>
        internal PulseStream Source { get; } = new();

        /// <inheritdoc />
        public PhysicalDeviceSnapshot Snapshot { get; } = new() { Id = id, Name = "Physical", Kind = "authority-test", Capacity = new PhysicalTunerCapacity(1) };

        /// <inheritdoc />
        public async Task<IReadOnlyList<PhysicalSourceChannel>> RefreshLineupAsync(CancellationToken cancellationToken = default)
        {
            await beforeLineup(cancellationToken);
            return [new PhysicalSourceChannel
            {
                Id = new SourceChannelId(id, "7.1"),
                ProviderNumber = "7.1",
                ProviderName = host,
                Metadata = new Dictionary<string, string> { ["host"] = host }
            }];
        }

        /// <inheritdoc />
        public ValueTask<IPhysicalStreamLease> OpenStreamAsync(PhysicalStreamRequest request, CancellationToken cancellationToken = default)
        {
            Assert.False(IsDisposed);
            var lease = Substitute.For<IPhysicalStreamLease>();
            lease.Stream.Returns(Source);
            lease.DisposeAsync().Returns(_ =>
            {
                LeaseDisposed = true;
                return ValueTask.CompletedTask;
            });
            return ValueTask.FromResult(lease);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            IsDisposed = true;
            Source.Dispose();
        }
    }

    private sealed class PulseStream : Stream
    {
        private readonly SemaphoreSlim _bytes = new(0);

        /// <summary>Supplies one byte to the live producer.</summary>
        internal void Pulse() => _bytes.Release();

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
            await _bytes.WaitAsync(cancellationToken);
            buffer.Span[0] = 1;
            return 1;
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

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _bytes.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
