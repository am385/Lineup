using System.Text.Json;
using Lineup.Common.Streaming;
using Lineup.Core.Storage;
using Lineup.Extensibility.Streaming;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Lineup.Core.Tests.Streaming;

/// <summary>Verifies every enabled device has a lineup refresh outcome, including devices that cannot activate.</summary>
public sealed class PhysicalLineupServiceTests
{
    private const string PrivateDetail = "Synthetic private adapter detail.";

    /// <summary>Verifies recoverable activation failures are explicit, healthy devices still commit, and saved lineups and identities survive.</summary>
    [Theory]
    [InlineData("argument", false)]
    [InlineData("argument", true)]
    [InlineData("invalid-operation", false)]
    [InlineData("invalid-operation", true)]
    [InlineData("io", false)]
    [InlineData("io", true)]
    [InlineData("http", false)]
    [InlineData("http", true)]
    [InlineData("unauthorized", false)]
    [InlineData("unauthorized", true)]
    [InlineData("validation", false)]
    [InlineData("validation", true)]
    [InlineData("unknown-driver", false)]
    [InlineData("unknown-driver", true)]
    public async Task RefreshAsync_ActivationFailure_ReturnsEveryEnabledOutcome(string failureMode, bool includeHealthy)
    {
        // Arrange
        using var fixture = new Fixture();
        var failed = Definition(failureMode, 20);
        var healthy = Definition("healthy", 10);
        var disabled = Definition("io", 0) with { Enabled = false };
        var deletedId = PhysicalDeviceId.New();
        var prior = await SeedIdentityAsync(fixture.Store, failed.Id);
        await fixture.Store.ReconcileAsync(disabled.Id, [Channel(disabled.Id, "7", "Disabled")], TestContext.Current.CancellationToken);
        await fixture.Store.ReconcileAsync(deletedId, [Channel(deletedId, "7", "Deleted")], TestContext.Current.CancellationToken);
        var configuration = new StreamingConfiguration { PhysicalDevices = includeHealthy ? [failed, disabled, healthy] : [failed, disabled] };

        // Act
        var results = await fixture.Service.RefreshAsync(configuration, TestContext.Current.CancellationToken);
        var stored = await fixture.Store.ReadActiveAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(includeHealthy ? new[] { healthy.Id, failed.Id } : [failed.Id], results.Select(result => result.DeviceId));
        var failedResult = Assert.Single(results, result => result.DeviceId == failed.Id);
        Assert.False(failedResult.Succeeded);
        Assert.Equal(0, failedResult.ChannelCount);
        Assert.Contains("activation failed", failedResult.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("preserved", failedResult.Error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(PrivateDetail, failedResult.Error!, StringComparison.Ordinal);
        Assert.Single(fixture.Logger.Warnings);
        Assert.DoesNotContain(disabled.Id, fixture.Driver.CreatedIds);
        Assert.DoesNotContain(fixture.Driver.RefreshedIds, id => id == failed.Id || id == disabled.Id);
        Assert.DoesNotContain(stored, channel => channel.Source.Id.PhysicalDeviceId == deletedId);
        Assert.Contains(stored, channel => channel.Source.Id.PhysicalDeviceId == disabled.Id);
        AssertPreservedIdentity(prior, Assert.Single(stored, channel => channel.Source.Id.PhysicalDeviceId == failed.Id));
        if (includeHealthy)
        {
            var success = Assert.Single(results, result => result.Succeeded);
            Assert.Equal(healthy.Id, success.DeviceId);
            Assert.Equal(2, success.ChannelCount);
            Assert.Null(success.Error);
            Assert.Equal(2, stored.Count(channel => channel.Source.Id.PhysicalDeviceId == healthy.Id));
            Assert.Equal([healthy.Id], fixture.Driver.RefreshedIds);
        }
        else
        {
            Assert.DoesNotContain(results, result => result.Succeeded);
            Assert.Empty(fixture.Driver.RefreshedIds);
        }
    }

    /// <summary>Verifies activation, refresh, and success outcomes are reported once in the catalog's configured order, including ID tie breaks.</summary>
    [Fact]
    public async Task RefreshAsync_MixedFailures_ReturnsOneOrderedOutcomePerEnabledDevice()
    {
        // Arrange
        using var fixture = new Fixture();
        var inactive = Definition("io", 1) with { Id = new PhysicalDeviceId(Guid.Parse("00000000-0000-0000-0000-000000000001")) };
        var refreshFailure = Definition("refresh-failure", 1) with { Id = new PhysicalDeviceId(Guid.Parse("00000000-0000-0000-0000-000000000002")) };
        var healthy = Definition("healthy", 2);
        await SeedIdentityAsync(fixture.Store, refreshFailure.Id);
        var configuration = new StreamingConfiguration { PhysicalDevices = [healthy, refreshFailure, inactive] };

        // Act
        var results = await fixture.Service.RefreshAsync(configuration, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal([inactive.Id, refreshFailure.Id, healthy.Id], results.Select(result => result.DeviceId));
        Assert.Equal([false, false, true], results.Select(result => result.Succeeded));
        Assert.Contains("activation failed", results[0].Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("refresh failed (IOException)", results[1].Error, StringComparison.Ordinal);
        Assert.DoesNotContain(PrivateDetail, results[1].Error!, StringComparison.Ordinal);
        Assert.Equal(2, results[2].ChannelCount);
        Assert.Equal(2, fixture.Logger.Warnings.Count);
        Assert.Equal([refreshFailure.Id, healthy.Id], fixture.Driver.RefreshedIds);
        var stored = await fixture.Store.ReadActiveAsync(TestContext.Current.CancellationToken);
        Assert.Equal("Saved identity", Assert.Single(stored, channel => channel.Source.Id.PhysicalDeviceId == refreshFailure.Id).EffectiveName);
    }

    /// <summary>Verifies an identical refresh can recover a failed activation and retain saved channel identity choices.</summary>
    [Fact]
    public async Task RefreshAsync_ActivationRetry_ReplacesFailureWithCommittedSuccess()
    {
        // Arrange
        using var fixture = new Fixture();
        var definition = Definition("fail-once", 0);
        var configuration = new StreamingConfiguration { PhysicalDevices = [definition] };
        await SeedIdentityAsync(fixture.Store, definition.Id);

        // Act
        var first = await fixture.Service.RefreshAsync(configuration, TestContext.Current.CancellationToken);
        var second = await fixture.Service.RefreshAsync(configuration, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(Assert.Single(first).Succeeded);
        var recovered = Assert.Single(second);
        Assert.True(recovered.Succeeded);
        Assert.Equal(definition.Id, recovered.DeviceId);
        Assert.Equal(2, recovered.ChannelCount);
        Assert.Null(recovered.Error);
        Assert.Equal([definition.Id, definition.Id], fixture.Driver.CreatedIds);
        Assert.Equal([definition.Id], fixture.Driver.RefreshedIds);
        var channel = Assert.Single(await fixture.Store.ReadActiveAsync(TestContext.Current.CancellationToken), item => item.Source.Id.ProviderKey == "7");
        Assert.Equal("Updated", channel.Source.ProviderName);
        Assert.Equal("107.1", channel.EffectiveNumber);
        Assert.Equal("Saved identity", channel.EffectiveName);
        Assert.False(channel.IsEnabled);
    }

    /// <summary>Verifies an activated device returning an empty lineup is a real success that deactivates its previous channels.</summary>
    [Fact]
    public async Task RefreshAsync_EmptySuccessfulLineup_CommitsZeroChannels()
    {
        // Arrange
        using var fixture = new Fixture();
        var definition = Definition("empty", 0);
        await SeedIdentityAsync(fixture.Store, definition.Id);
        var configuration = new StreamingConfiguration { PhysicalDevices = [definition] };

        // Act
        var result = Assert.Single(await fixture.Service.RefreshAsync(configuration, TestContext.Current.CancellationToken));

        // Assert
        Assert.True(result.Succeeded);
        Assert.Equal(0, result.ChannelCount);
        Assert.Null(result.Error);
        Assert.Empty(await fixture.Store.ReadActiveAsync(TestContext.Current.CancellationToken));
        Assert.Empty(fixture.Logger.Warnings);
    }

    /// <summary>Verifies disabled or absent devices produce no refresh results while deletion pruning still runs.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefreshAsync_NoEnabledDevices_ReturnsNoOutcomes(bool retainDisabled)
    {
        // Arrange
        using var fixture = new Fixture();
        var disabled = Definition("io", 0) with { Enabled = false };
        await SeedIdentityAsync(fixture.Store, disabled.Id);
        var configuration = new StreamingConfiguration { PhysicalDevices = retainDisabled ? [disabled] : [] };

        // Act
        var results = await fixture.Service.RefreshAsync(configuration, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(results);
        Assert.Empty(fixture.Driver.CreatedIds);
        Assert.Empty(fixture.Driver.RefreshedIds);
        Assert.Equal(retainDisabled ? 1 : 0, (await fixture.Store.ReadActiveAsync(TestContext.Current.CancellationToken)).Count);
        Assert.Empty(fixture.Logger.Warnings);
    }

    /// <summary>Verifies caller cancellation propagates from activation or lineup refresh and releases the coordinator gate for a later refresh.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefreshAsync_CallerCancellation_PropagatesAndAllowsRetry(bool cancelActivation)
    {
        // Arrange
        using var fixture = new Fixture();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var configuration = new StreamingConfiguration { PhysicalDevices = [Definition("healthy", 0)] };
        if (cancelActivation)
        {
            fixture.Driver.BeforeCreate = _ => cancellation.Cancel();
        }
        else
        {
            fixture.Driver.BeforeRefresh = _ => cancellation.Cancel();
        }

        // Act
        var exception = await Record.ExceptionAsync(() => fixture.Service.RefreshAsync(configuration, cancellation.Token));
        fixture.Driver.BeforeCreate = null;
        fixture.Driver.BeforeRefresh = null;
        var retry = await fixture.Service.RefreshAsync(configuration, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Assert
        var canceled = Assert.IsAssignableFrom<OperationCanceledException>(exception);
        Assert.Equal(cancellation.Token, canceled.CancellationToken);
        Assert.True(Assert.Single(retry).Succeeded);
        Assert.Empty(fixture.Logger.Warnings);
    }

    /// <summary>Verifies unexpected activation exceptions are not hidden behind fabricated per-device results.</summary>
    [Fact]
    public async Task RefreshAsync_UnexpectedActivationFailure_Propagates()
    {
        // Arrange
        using var fixture = new Fixture();
        var configuration = new StreamingConfiguration { PhysicalDevices = [Definition("unexpected", 0)] };

        // Act
        var exception = await Record.ExceptionAsync(() => fixture.Service.RefreshAsync(configuration, TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<NotSupportedException>(exception);
        Assert.Empty(fixture.Logger.Warnings);
    }

    private static PhysicalDeviceDefinition Definition(string mode, int order) => new()
    {
        Id = PhysicalDeviceId.New(),
        Name = $"Test {mode}",
        Order = order,
        Adapter = new AdapterConfiguration(mode == "unknown-driver" ? "missing" : FakeDriver.DriverKind, JsonSerializer.SerializeToElement(new { Mode = mode }))
    };

    private static PhysicalSourceChannel Channel(PhysicalDeviceId id, string key, string name) => new()
    {
        Id = new SourceChannelId(id, key),
        ProviderNumber = $"{key}.1",
        ProviderName = name
    };

    private static async Task<PhysicalChannelIdentity> SeedIdentityAsync(StreamingTopologyStore store, PhysicalDeviceId id)
    {
        var channelId = new SourceChannelId(id, "7");
        await store.ReconcileAsync(id, [Channel(id, "7", "Saved")], TestContext.Current.CancellationToken);
        await store.SetIdentityAsync(channelId, "107.1", "Saved identity", Guid.NewGuid(), TestContext.Current.CancellationToken);
        await store.SetEnabledAsync(channelId, false, TestContext.Current.CancellationToken);
        return Assert.Single(await store.ReadActiveAsync(TestContext.Current.CancellationToken), channel => channel.Source.Id == channelId);
    }

    private static void AssertPreservedIdentity(PhysicalChannelIdentity expected, PhysicalChannelIdentity actual)
    {
        Assert.Equal(expected.Source.Id, actual.Source.Id);
        Assert.Equal(expected.Source.ProviderNumber, actual.Source.ProviderNumber);
        Assert.Equal(expected.Source.ProviderName, actual.Source.ProviderName);
        Assert.Equal(expected.EffectiveNumber, actual.EffectiveNumber);
        Assert.Equal(expected.EffectiveName, actual.EffectiveName);
        Assert.Equal(expected.EquivalenceGroupId, actual.EquivalenceGroupId);
        Assert.Equal(expected.IsEnabled, actual.IsEnabled);
        Assert.Equal(expected.FirstSeenUtc, actual.FirstSeenUtc);
        Assert.Equal(expected.LastSeenUtc, actual.LastSeenUtc);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly SqliteConnection _connection = new("Data Source=:memory:");

        public Fixture()
        {
            _connection.Open();
            Store = new StreamingTopologyStore(new ContextFactory(new DbContextOptionsBuilder<EpgDbContext>().UseSqlite(_connection).Options));
            Service = new PhysicalLineupService(new PhysicalDeviceCatalog(new StreamingAdapterRegistry([Driver], [], [])), Store, Logger);
        }

        public FakeDriver Driver { get; } = new();
        public CapturedLogger Logger { get; } = new();
        public StreamingTopologyStore Store { get; }
        public PhysicalLineupService Service { get; }

        public void Dispose() => _connection.Dispose();
    }

    private sealed class ContextFactory(DbContextOptions<EpgDbContext> options) : IDbContextFactory<EpgDbContext>
    {
        public EpgDbContext CreateDbContext() => new(options);
    }

    private sealed class CapturedLogger : ILogger<PhysicalLineupService>
    {
        public List<string> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }

    private sealed class FakeDriver : IPhysicalTunerDriver
    {
        public const string DriverKind = "test-lineup";
        public string Kind => DriverKind;
        public string DisplayName => "Test lineup driver";
        public bool SupportsUserCreation => true;
        public DeviceCapabilities Capabilities { get; } = new();
        public List<PhysicalDeviceId> CreatedIds { get; } = [];
        public List<PhysicalDeviceId> RefreshedIds { get; } = [];
        public Action<CancellationToken>? BeforeCreate { get; set; }
        public Action<CancellationToken>? BeforeRefresh { get; set; }

        public AdapterConfiguration CreateDefaultConfiguration(PhysicalDeviceId deviceId) => new(DriverKind, JsonSerializer.SerializeToElement(new { Mode = "healthy" }));

        public IReadOnlyList<string> Validate(AdapterConfiguration configuration) => configuration.Configuration.GetProperty("Mode").GetString() == "validation" ? [PrivateDetail] : [];

        public ValueTask<IPhysicalTunerDevice> CreateAsync(PhysicalDeviceId deviceId, string name, AdapterConfiguration configuration, CancellationToken cancellationToken = default)
        {
            CreatedIds.Add(deviceId);
            BeforeCreate?.Invoke(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var mode = configuration.Configuration.GetProperty("Mode").GetString();
            var failure = mode switch
            {
                "argument" => new ArgumentException(PrivateDetail),
                "invalid-operation" => new InvalidOperationException(PrivateDetail),
                "io" => new IOException(PrivateDetail),
                "http" => new HttpRequestException(PrivateDetail),
                "unauthorized" => new UnauthorizedAccessException(PrivateDetail),
                "fail-once" when CreatedIds.Count(id => id == deviceId) == 1 => new IOException(PrivateDetail),
                "unexpected" => (Exception)new NotSupportedException(PrivateDetail),
                _ => null
            };
            return failure is not null
                ? ValueTask.FromException<IPhysicalTunerDevice>(failure)
                : ValueTask.FromResult<IPhysicalTunerDevice>(new FakeDevice(this, deviceId, name, mode));
        }
    }

    private sealed class FakeDevice(FakeDriver driver, PhysicalDeviceId id, string name, string? mode) : IPhysicalTunerDevice
    {
        public PhysicalDeviceSnapshot Snapshot { get; } = new() { Id = id, Kind = FakeDriver.DriverKind, Name = name, Capacity = new PhysicalTunerCapacity(1) };

        public Task<IReadOnlyList<PhysicalSourceChannel>> RefreshLineupAsync(CancellationToken cancellationToken = default)
        {
            driver.RefreshedIds.Add(id);
            driver.BeforeRefresh?.Invoke(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return mode == "refresh-failure"
                ? Task.FromException<IReadOnlyList<PhysicalSourceChannel>>(new IOException(PrivateDetail))
                : Task.FromResult<IReadOnlyList<PhysicalSourceChannel>>(mode == "empty" ? [] : [Channel(id, "7", "Updated"), Channel(id, "9", "Alternate")]);
        }

        public ValueTask<IPhysicalStreamLease> OpenStreamAsync(PhysicalStreamRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
