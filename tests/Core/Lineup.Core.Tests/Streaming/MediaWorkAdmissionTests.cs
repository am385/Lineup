using Lineup.Core.Common.Streaming;
using Xunit;

namespace Lineup.Core.Tests.Streaming;

/// <summary>Verifies deployment-wide media admission and ownership, independently of logical viewer limits.</summary>
public sealed class MediaWorkAdmissionTests
{
    /// <summary>Enforces producer and processing counts independently and recovers after idempotent terminal release.</summary>
    [Fact]
    public async Task AcquireAsync_Limits_AreIndependentAndReleasedExactlyOnce()
    {
        // Arrange
        var admission = new MediaWorkAdmission(new() { MaximumProducers = 1, MaximumProcessingOperations = 1 });
        var producer = await admission.AcquireAsync(MediaWorkKind.Producer, cancellationToken: TestContext.Current.CancellationToken);
        await using var processing = await admission.AcquireAsync(MediaWorkKind.Processing, cancellationToken: TestContext.Current.CancellationToken);

        // Act
        var producerFailure = await Assert.ThrowsAsync<MediaResourceLimitException>(() => admission.AcquireAsync(MediaWorkKind.Producer, cancellationToken: TestContext.Current.CancellationToken).AsTask());
        var processingFailure = await Assert.ThrowsAsync<MediaResourceLimitException>(() => admission.AcquireAsync(MediaWorkKind.Processing, cancellationToken: TestContext.Current.CancellationToken).AsTask());
        await producer.DisposeAsync();
        await producer.DisposeAsync();
        await using var replacement = await admission.AcquireAsync(MediaWorkKind.Producer, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("media_producer_limit", producerFailure.Code);
        Assert.Equal("media_processing_limit", processingFailure.Code);
        Assert.Equal(new MediaWorkDiagnostics(1, 1, 1, 1, 0), admission.GetDiagnostics());
    }

    /// <summary>Reservations remain charged while an idle owner's actual cleanup is incomplete, without serializing unrelated releases.</summary>
    [Fact]
    public async Task AcquireAsync_IdleReclamation_AwaitsCleanupBeforeReadmission()
    {
        // Arrange
        var admission = new MediaWorkAdmission(new() { MaximumProducers = 1 });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IAsyncDisposable? owner = null;
        owner = await admission.AcquireAsync(MediaWorkKind.Producer, async token =>
        {
            entered.TrySetResult();
            await finish.Task.WaitAsync(token);
            await owner!.DisposeAsync();
            return true;
        }, TestContext.Current.CancellationToken);

        // Act
        var opening = admission.AcquireAsync(MediaWorkKind.Producer, cancellationToken: TestContext.Current.CancellationToken).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var whileDraining = admission.GetDiagnostics();
        var premature = opening.IsCompleted;
        finish.TrySetResult();
        await using var replacement = await opening;

        // Assert
        Assert.False(premature);
        Assert.Equal(1, whileDraining.Producers);
        Assert.Equal(1, admission.GetDiagnostics().Producers);
    }

    /// <summary>Concurrent reservations never over-admit and zero limits preserve unlimited deployments.</summary>
    [Theory]
    [InlineData(0, 40)]
    [InlineData(3, 3)]
    public async Task AcquireAsync_ConcurrentPressure_NeverExceedsLimit(int limit, int expected)
    {
        // Arrange
        var admission = new MediaWorkAdmission(new() { MaximumProducers = limit });

        // Act
        var reservations = await Task.WhenAll(Enumerable.Range(0, 40).Select(async _ =>
        {
            try { return await admission.AcquireAsync(MediaWorkKind.Producer); }
            catch (MediaResourceLimitException) { return null; }
        }));
        var usage = admission.GetDiagnostics();
        foreach (var reservation in reservations.OfType<IAsyncDisposable>())
        {
            await reservation.DisposeAsync();
        }

        // Assert
        Assert.Equal(expected, reservations.Count(reservation => reservation is not null));
        Assert.Equal(expected, usage.Producers);
        Assert.Equal(0, admission.GetDiagnostics().Producers);
    }

    /// <summary>Free-space rejection checks the real workspace volume, including paths not created yet; zero disables checks.</summary>
    [Fact]
    public void CheckWorkspace_InsufficientSpace_ExplicitlyRejectsActualVolume()
    {
        // Arrange
        var workspace = Path.Combine(Path.GetTempPath(), "lineup-workspace-admission", Guid.NewGuid().ToString("N"));
        var admission = new MediaWorkAdmission(new() { MinimumMediaFreeSpaceMib = int.MaxValue });

        // Act
        var failure = Assert.Throws<MediaResourceLimitException>(() => admission.CheckWorkspace(workspace));
        new MediaWorkAdmission(new()).CheckWorkspace(workspace);

        // Assert
        Assert.Equal("media_storage_limit", failure.Code);
        Assert.False(Directory.Exists(workspace));
    }
}
