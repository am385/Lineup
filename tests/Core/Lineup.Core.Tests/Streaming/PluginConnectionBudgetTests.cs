using Lineup.Core.Settings;
using NSubstitute;
using Xunit;

namespace Lineup.Core.Tests.Streaming;

/// <summary>Verifies deployment-wide admission, configuration changes and exact lease ownership.</summary>
public sealed class PluginConnectionBudgetTests
{
    /// <summary>Verifies concurrent admissions from different workers cannot oversubscribe the deployment.</summary>
    [Fact]
    public async Task TryAcquire_ConcurrentWorkers_EnforcesOneBudget()
    {
        // Arrange
        var (_, budget) = CreateBudget(7);
        var workers = Enumerable.Range(0, 100).Select(_ => VirtualDeviceId.New()).ToArray();

        // Act
        var attempts = await Task.WhenAll(workers.Select(worker => Task.Run(() => budget.TryAcquire(worker), TestContext.Current.CancellationToken)));
        var accepted = attempts.OfType<IAsyncDisposable>().ToArray();

        // Assert
        Assert.Equal(7, accepted.Length);
        var snapshot = budget.GetSnapshot();
        Assert.Equal(7, snapshot.ActiveConnections);
        Assert.Equal(7, snapshot.WorkerConnections.Values.Sum());
        Assert.Equal(0, snapshot.ManagementHostedConnections);
        foreach (var lease in accepted)
        {
            await lease.DisposeAsync();
        }
        Assert.Equal(0, budget.GetSnapshot().ActiveConnections);
    }

    /// <summary>Verifies management-hosted plugin traffic and dedicated workers consume the same slots.</summary>
    [Fact]
    public async Task TryAcquire_ManagementAndWorker_ShareCapacity()
    {
        // Arrange
        var (_, budget) = CreateBudget(2);
        var worker = VirtualDeviceId.New();
        await using var management = budget.TryAcquire();
        await using var dedicated = budget.TryAcquire(worker);

        // Act
        var rejected = budget.TryAcquire(VirtualDeviceId.New());

        // Assert
        Assert.NotNull(management);
        Assert.NotNull(dedicated);
        Assert.Null(rejected);
        var snapshot = budget.GetSnapshot();
        Assert.Equal(2, snapshot.ActiveConnections);
        Assert.Equal(1, snapshot.ManagementHostedConnections);
        Assert.Equal(1, snapshot.WorkerConnections[worker]);
    }

    /// <summary>Verifies unlimited mode still tracks ownership and a reduced limit drains without evicting viewers.</summary>
    [Fact]
    public async Task TryAcquire_LimitChanges_PreservesExistingOwnership()
    {
        // Arrange
        var (settings, budget) = CreateBudget(0);
        await using var first = budget.TryAcquire();
        await using var second = budget.TryAcquire();
        await using var third = budget.TryAcquire();

        // Act
        settings.Streaming = settings.Streaming with { MaximumPluginConnections = 2 };

        // Assert
        Assert.True(budget.GetSnapshot().IsDraining);
        Assert.Null(budget.TryAcquire());
        await first!.DisposeAsync();
        Assert.False(budget.GetSnapshot().IsDraining);
        Assert.Null(budget.TryAcquire());
        await second!.DisposeAsync();
        await using var replacement = budget.TryAcquire();
        Assert.NotNull(replacement);
        Assert.Equal(2, budget.GetSnapshot().ActiveConnections);
        settings.Streaming = settings.Streaming with { MaximumPluginConnections = 3 };
        await using var raised = budget.TryAcquire();
        Assert.NotNull(raised);
        settings.Streaming = settings.Streaming with { MaximumPluginConnections = 0 };
        await using var unlimited = budget.TryAcquire();
        Assert.NotNull(unlimited);
        Assert.Equal(4, budget.GetSnapshot().ActiveConnections);
    }

    /// <summary>Verifies repeated release of an older connection cannot release a replacement slot.</summary>
    [Fact]
    public async Task DisposeAsync_OldLeaseCannotReleaseReplacement()
    {
        // Arrange
        var (_, budget) = CreateBudget(1);
        var old = Assert.IsAssignableFrom<IAsyncDisposable>(budget.TryAcquire(VirtualDeviceId.New()));
        await old.DisposeAsync();
        await using var current = budget.TryAcquire(VirtualDeviceId.New());

        // Act
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => old.DisposeAsync().AsTask()));

        // Assert
        Assert.NotNull(current);
        Assert.Equal(1, budget.GetSnapshot().ActiveConnections);
        Assert.Null(budget.TryAcquire());
    }

    /// <summary>Verifies invalid settings never silently disable the budget.</summary>
    [Fact]
    public void TryAcquire_NegativeLimit_ReportsInvalidConfiguration()
    {
        // Arrange
        var (_, budget) = CreateBudget(-1);

        // Act
        var error = Record.Exception(() => budget.TryAcquire());

        // Assert
        Assert.IsType<InvalidOperationException>(error);
        Assert.Contains("cannot be negative", error.Message, StringComparison.Ordinal);
    }

    private static (LineupSettings Settings, PluginConnectionBudget Budget) CreateBudget(int maximum)
    {
        var settings = new LineupSettings { Streaming = new StreamingConfiguration { MaximumPluginConnections = maximum } };
        var service = Substitute.For<ILineupSettingsService>();
        service.Settings.Returns(settings);
        return (settings, new PluginConnectionBudget(service));
    }
}
