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
        // Act
        var second = Task.CompletedTask;
        bool secondWaited;
        try
        {
            await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            second = coordinator.ExecuteAsync(_ =>
            {
                secondEntered = true;
                return Task.CompletedTask;
            }, TestContext.Current.CancellationToken);
            secondWaited = !second.IsCompleted && !secondEntered;
        }
        finally
        {
            releaseFirst.TrySetResult();
            await Task.WhenAll(first, second);
        }

        // Assert
        Assert.True(secondWaited);
        Assert.True(secondEntered);
    }
}
