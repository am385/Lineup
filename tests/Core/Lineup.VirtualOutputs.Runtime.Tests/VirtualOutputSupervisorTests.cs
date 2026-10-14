using System.Text.Json;
using Lineup.Common.Streaming;
using Lineup.Core.Settings;
using Lineup.VirtualOutputs.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Lineup.VirtualOutputs.Runtime.Tests;

/// <summary>Verifies desired-versus-applied virtual-output worker supervision.</summary>
public sealed class VirtualOutputSupervisorTests
{
    /// <summary>Verifies a transient package activation failure is retried without a settings change.</summary>
    [Fact]
    public async Task StartAsync_ActivationFailure_RetriesAutomatically()
    {
        // Arrange
        var deviceId = VirtualDeviceId.New();
        var settings = Substitute.For<ILineupSettingsService>();
        settings.Settings.Returns(new LineupSettings
        {
            Streaming = new StreamingConfiguration
            {
                VirtualDevices =
                [
                    new VirtualDeviceDefinition
                    {
                        Id = deviceId,
                        Name = "Retry output",
                        Adapter = new AdapterConfiguration("retry-test", JsonSerializer.SerializeToElement(new { })),
                        Hosting = new VirtualOutputHostingDefinition
                        {
                            WorkerPolicyKey = "retry"
                        }
                    }
                ],
                VirtualOutputWorkerPolicies =
                [
                    new VirtualOutputWorkerPolicyDefinition
                    {
                        Key = "retry",
                        Name = "Retry",
                        MaximumRestarts = 1,
                        RestartWindowSeconds = 60
                    }
                ]
            }
        });
        var catalog = Substitute.For<IVirtualOutputWorkerCatalog>();
        var attempts = 0;
        catalog.ResolveAsync("retry-test", Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                Interlocked.Increment(ref attempts);
                return Task.FromException<VirtualOutputWorkerPackage>(new IOException("Package is temporarily unavailable."));
            });
        var status = new VirtualOutputRuntimeStatus();
        using var authority = new VirtualOutputSessionAuthority();
        var supervisor = new VirtualOutputSupervisor(
            settings,
            new VirtualOutputRuntimeOptions
            {
                WorkerPackageRoot = "unused",
                IpcEndpoint = new VirtualOutputIpcEndpoint
                {
                    Transport = VirtualOutputIpcTransport.NamedPipe,
                    Address = "lineup-retry-test"
                }
            },
            catalog,
            Substitute.For<IVirtualOutputWorkerLauncher>(),
            Substitute.For<IVirtualOutputCertificateResolver>(),
            authority,
            status,
            new VirtualOutputExternalEnrollmentStore(),
            NullLogger<VirtualOutputSupervisor>.Instance);

        // Act
        await supervisor.StartAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => Volatile.Read(ref attempts) >= 2, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await supervisor.StopAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, attempts);
        var snapshot = Assert.Single(status.GetSnapshots());
        Assert.Equal(VirtualOutputRuntimeState.Failed, snapshot.State);
        Assert.Equal(2, snapshot.RestartCount);
        Assert.Contains("exceeded its restart budget", snapshot.LastFailure, StringComparison.Ordinal);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(timeout);
        while (!condition())
        {
            await Task.Delay(10, timeoutCancellation.Token);
        }
    }
}
