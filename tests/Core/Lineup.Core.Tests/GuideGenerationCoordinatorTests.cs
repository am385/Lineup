using Xunit;

namespace Lineup.Core.Tests;

/// <summary>
/// Tests guide generation transition serialization.
/// </summary>
public class GuideGenerationCoordinatorTests
{
    /// <summary>
    /// Verifies concurrent transitions cannot interleave.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ConcurrentTransitions_SerializesCompleteTransitions()
    {
        // Arrange
        var coordinator = new GuideGenerationCoordinator();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = false;
        var first = coordinator.ExecuteAsync(async cancellationToken =>
        {
            firstEntered.TrySetResult();
            await releaseFirst.Task.WaitAsync(cancellationToken);
        }, TestContext.Current.CancellationToken);
        await firstEntered.Task.WaitAsync(TestContext.Current.CancellationToken);

        // Act
        var second = coordinator.ExecuteAsync(_ =>
        {
            secondEntered = true;
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        var secondWaited = !secondEntered;
        releaseFirst.TrySetResult();
        await Task.WhenAll(first, second);

        // Assert
        Assert.True(secondWaited);
        Assert.True(secondEntered);
    }
}
