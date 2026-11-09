using System.Net.Sockets;
using Lineup.Plugin.SiliconDust.HDHomeRun.DiscoveryHost;
using Xunit;

namespace Lineup.Plugin.SiliconDust.HDHomeRun.VirtualTuner.Tests;

/// <summary>Verifies discovery task isolation and cleanup without binding network sockets.</summary>
public sealed class DiscoveryWorkerLifetimeTests
{
    /// <summary>Verifies SSDP initialization and running-loop failures leave UDP requests and lease renewals operational.</summary>
    [Theory]
    [InlineData(SocketError.AddressAlreadyInUse, false, false)]
    [InlineData(SocketError.AddressNotAvailable, false, false)]
    [InlineData(SocketError.NetworkUnreachable, false, false)]
    [InlineData(SocketError.NetworkUnreachable, true, false)]
    [InlineData(SocketError.NetworkUnreachable, true, true)]
    public async Task RunAsync_SsdpSocketFailure_PreservesDiscoveryAndRenewal(SocketError error, bool runningLoops, bool failReceive)
    {
        // Arrange
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromSeconds(5));
        var failed = new TaskCompletionSource<SocketException>(TaskCreationOptions.RunContinuationsAsynchronously);
        var renew = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var renewed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishLease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var discoveryStopped = false;
        var degraded = false;
        var failure = new SocketException((int)error);

        // Act
        var lifetime = DiscoveryWorkerLifetime.RunAsync(
            async (_, token) =>
            {
                await renew.Task.WaitAsync(token);
                renewed.TrySetResult(degraded);
                await finishLease.Task.WaitAsync(token);
            },
            async token =>
            {
                try
                {
                    await request.Task.WaitAsync(token);
                    response.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                finally
                {
                    discoveryStopped = true;
                }
            },
            token => runningLoops
                ? DiscoveryWorkerLifetime.RunSsdpLoopsAsync(
                    receiveToken => failReceive ? Task.FromException(failure) : Task.Delay(Timeout.InfiniteTimeSpan, receiveToken),
                    aliveToken => failReceive ? Task.Delay(Timeout.InfiniteTimeSpan, aliveToken) : Task.FromException(failure),
                    token)
                : throw failure,
            (exception, _) =>
            {
                degraded = true;
                failed.TrySetResult(exception);
                return Task.CompletedTask;
            },
            cancellation.Token);
        try
        {
            var reported = await failed.Task.WaitAsync(cancellation.Token);
            request.TrySetResult();
            renew.TrySetResult();
            await response.Task.WaitAsync(cancellation.Token);
            var renewedDegraded = await renewed.Task.WaitAsync(cancellation.Token);

            // Assert
            Assert.Same(failure, reported);
            Assert.True(renewedDegraded);
            Assert.False(lifetime.IsCompleted);
            Assert.False(discoveryStopped);
            finishLease.TrySetResult();
            await lifetime.WaitAsync(cancellation.Token);
            Assert.True(discoveryStopped);
        }
        finally
        {
            cancellation.Cancel();
            await lifetime;
        }
    }

    /// <summary>Verifies required-task failures and unexpected SSDP failures stop and observe every task.</summary>
    [Theory]
    [InlineData("lease", false)]
    [InlineData("udp", false)]
    [InlineData("ssdp", false)]
    [InlineData("lease", true)]
    [InlineData("udp", true)]
    public async Task RunAsync_FatalFailure_StopsAllTasks(string failingTask, bool socketFailure)
    {
        // Arrange
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromSeconds(5));
        var trigger = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = 0;
        var reports = 0;
        Exception failure = socketFailure
            ? new SocketException((int)SocketError.NetworkUnreachable)
            : new InvalidOperationException($"Fatal {failingTask} failure.");
        async Task Run(string name, CancellationToken token)
        {
            try
            {
                await trigger.Task.WaitAsync(token);
                if (name == failingTask)
                {
                    throw failure;
                }
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            finally
            {
                Interlocked.Increment(ref stopped);
            }
        }

        // Act
        var lifetime = DiscoveryWorkerLifetime.RunAsync(
            (_, token) => Run("lease", token),
            token => Run("udp", token),
            token => Run("ssdp", token),
            (_, _) =>
            {
                reports++;
                return Task.CompletedTask;
            },
            cancellation.Token);
        trigger.TrySetResult();
        Exception observed = socketFailure
            ? await Assert.ThrowsAsync<SocketException>(() => lifetime)
            : await Assert.ThrowsAsync<InvalidOperationException>(() => lifetime);

        // Assert
        Assert.Same(failure, observed);
        Assert.Equal(3, stopped);
        Assert.Equal(0, reports);
    }

    /// <summary>Verifies host cancellation cleans all healthy protocol tasks without reporting a socket failure.</summary>
    [Fact]
    public async Task RunAsync_HostCancellation_CleansAllTasks()
    {
        // Arrange
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var stopped = 0;
        async Task Run(CancellationToken token)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            finally
            {
                Interlocked.Increment(ref stopped);
            }
        }
        var lifetime = DiscoveryWorkerLifetime.RunAsync(
            (_, token) => Run(token),
            Run,
            Run,
            (_, _) => throw new InvalidOperationException("Cancellation must not report an SSDP socket failure."),
            cancellation.Token);

        // Act
        cancellation.Cancel();
        await lifetime.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(3, stopped);
    }

    /// <summary>Verifies a failed receive or alive loop cancels its sibling before SSDP cleanup completes.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunSsdpLoopsAsync_SocketFailure_CancelsAndObservesSibling(bool failReceive)
    {
        // Arrange
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromSeconds(5));
        var trigger = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new SocketException((int)SocketError.NetworkUnreachable);
        var stopped = 0;
        var siblingCanceled = false;
        async Task Run(bool fail, CancellationToken token)
        {
            try
            {
                await trigger.Task.WaitAsync(token);
                if (fail)
                {
                    throw failure;
                }
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            finally
            {
                if (!fail)
                {
                    siblingCanceled = token.IsCancellationRequested;
                }
                Interlocked.Increment(ref stopped);
            }
        }

        // Act
        var loops = DiscoveryWorkerLifetime.RunSsdpLoopsAsync(token => Run(failReceive, token), token => Run(!failReceive, token), cancellation.Token);
        trigger.TrySetResult();
        var observed = await Assert.ThrowsAsync<SocketException>(() => loops);

        // Assert
        Assert.Same(failure, observed);
        Assert.True(siblingCanceled);
        Assert.Equal(2, stopped);
    }
}
