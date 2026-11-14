using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Lineup.App.Web.Tests.Components;

/// <summary>Verifies component cleanup completes before owned services are released and failures remain observable.</summary>
public sealed class BunitContextLifetimeTests
{
    /// <summary>Verifies asynchronous disposal owns pending component work and propagates component cleanup failures.</summary>
    /// <param name="fail">Whether component cleanup fails after its controlled release.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposeAsync_PendingComponentCleanup_WaitsBeforeReleasingServices(bool fail)
    {
        // Arrange
        var state = new CleanupState(fail);
        var context = new BunitContext();
        context.Services.AddSingleton(state);
        context.Services.AddSingleton<CleanupService>();
        context.Render<CleanupComponent>();
        var disposal = context.DisposeAsync().AsTask();
        bool waited;
        bool servicesRetained;
        Exception? error;

        // Act
        try
        {
            await state.Entered.Task.WaitAsync(BunitContext.WaitTimeout, TestContext.Current.CancellationToken);
            waited = !disposal.IsCompleted;
            servicesRetained = !state.ServicesDisposed;
        }
        finally
        {
            state.Release.TrySetResult();
            error = await Record.ExceptionAsync(() => disposal.WaitAsync(BunitContext.WaitTimeout, TestContext.Current.CancellationToken));
        }

        // Assert
        Assert.True(waited);
        Assert.True(servicesRetained);
        Assert.True(state.ComponentFinished);
        Assert.True(state.ServicesDisposed);
        if (fail)
        {
            Assert.Same(state.Failure, error);
        }
        else
        {
            Assert.Null(error);
        }
    }

    /// <summary>Provides a component whose cleanup is explicitly held until the test releases it.</summary>
    public sealed class CleanupComponent : ComponentBase, IAsyncDisposable
    {
        [Inject]
        private CleanupState State { get; set; } = null!;

        [Inject]
        private CleanupService Service { get; set; } = null!;

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            State.Entered.TrySetResult();
            await State.Release.Task;
            State.ComponentFinished = true;
            if (State.Fail)
            {
                throw State.Failure;
            }
        }
    }

    private sealed class CleanupState(bool fail)
    {
        /// <summary>Gets whether cleanup should fail after completing its owned work.</summary>
        internal bool Fail { get; } = fail;
        /// <summary>Gets the exact failure that cleanup must propagate.</summary>
        internal Exception Failure { get; } = new InvalidOperationException("Controlled component cleanup failure.");
        /// <summary>Gets the barrier indicating component cleanup has started.</summary>
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>Gets the controlled release for pending cleanup.</summary>
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>Gets or sets whether component cleanup has finished.</summary>
        internal bool ComponentFinished { get; set; }
        /// <summary>Gets or sets whether the owned service has been disposed.</summary>
        internal bool ServicesDisposed { get; set; }
    }

    private sealed class CleanupService(CleanupState state) : IDisposable
    {
        /// <inheritdoc />
        public void Dispose() => state.ServicesDisposed = true;
    }
}
