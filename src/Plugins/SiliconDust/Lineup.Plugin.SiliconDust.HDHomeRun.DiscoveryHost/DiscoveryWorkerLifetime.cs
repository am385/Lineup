using System.Net.Sockets;

namespace Lineup.Plugin.SiliconDust.HDHomeRun.DiscoveryHost;

/// <summary>Coordinates required worker tasks while isolating an unavailable SSDP listener.</summary>
public static class DiscoveryWorkerLifetime
{
    /// <summary>Runs discovery until lease renewal or UDP ends, reporting SSDP socket failures without stopping required tasks.</summary>
    public static async Task RunAsync(
        Func<Action, CancellationToken, Task> runLease,
        Func<CancellationToken, Task> runDiscovery,
        Func<CancellationToken, Task> runSsdp,
        Func<SocketException, CancellationToken, Task> reportSsdpFailure,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(runLease);
        ArgumentNullException.ThrowIfNull(runDiscovery);
        ArgumentNullException.ThrowIfNull(runSsdp);
        ArgumentNullException.ThrowIfNull(reportSsdpFailure);
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var leaseTask = RunLeaseAsync();
        var discoveryTask = RunDiscoveryAsync();
        var ssdpTask = RunOptionalSsdpAsync();
        try
        {
            var completed = await Task.WhenAny(leaseTask, discoveryTask, ssdpTask);
            if (completed == ssdpTask && ssdpTask.IsCompletedSuccessfully)
            {
                await Task.WhenAny(leaseTask, discoveryTask);
            }
        }
        finally
        {
            shutdown.Cancel();
            try
            {
                await Task.WhenAll(leaseTask, discoveryTask, ssdpTask);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
            }
        }

        async Task RunLeaseAsync() => await runLease(shutdown.Cancel, shutdown.Token);

        async Task RunDiscoveryAsync() => await runDiscovery(shutdown.Token);

        async Task RunOptionalSsdpAsync()
        {
            try
            {
                await runSsdp(shutdown.Token);
            }
            catch (SocketException exception) when (!shutdown.IsCancellationRequested)
            {
                await reportSsdpFailure(exception, shutdown.Token);
            }
        }
    }

    /// <summary>Cancels and observes both SSDP loops when either ends, before their shared socket is disposed.</summary>
    public static async Task RunSsdpLoopsAsync(Func<CancellationToken, Task> receive, Func<CancellationToken, Task> sendAlive, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(receive);
        ArgumentNullException.ThrowIfNull(sendAlive);
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var receiveTask = RunReceiveAsync();
        var aliveTask = RunAliveAsync();
        try
        {
            await Task.WhenAny(receiveTask, aliveTask);
        }
        finally
        {
            shutdown.Cancel();
            await Task.WhenAll(receiveTask, aliveTask);
        }

        async Task RunReceiveAsync() => await receive(shutdown.Token);

        async Task RunAliveAsync() => await sendAlive(shutdown.Token);
    }
}
