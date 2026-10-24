using System.Reflection;
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
    /// <summary>Verifies initial and subsequent reconciliation preserve the independently owned discovery worker.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Reconcile_DiscoveryWorker_PreservesSharedStatusAndAuthorization(bool beforeInitialReconcile)
    {
        // Arrange
        await using var fixture = new OwnershipFixture();
        if (!beforeInitialReconcile)
        {
            await fixture.ReconcileAsync();
        }
        var discovery = fixture.RegisterDiscovery();

        // Act
        await fixture.ReconcileAsync();
        await fixture.ReconcileAsync();

        // Assert
        Assert.True(fixture.Authority.Authorize(discovery.Context, VirtualOutputCapability.ReportHealth).Authorized);
        Assert.Same(discovery.Snapshot, Assert.Single(fixture.Status.GetSnapshots()));
        Assert.Equal(0, fixture.ActivationAttempts);
    }

    /// <summary>Verifies obsolete configured outputs still lose their own credentials, enrollments, processes, and status.</summary>
    [Theory]
    [InlineData(VirtualOutputExecutionMode.ManagedProcess, "remove")]
    [InlineData(VirtualOutputExecutionMode.ManagedProcess, "disable")]
    [InlineData(VirtualOutputExecutionMode.ManagedProcess, "unhost")]
    [InlineData(VirtualOutputExecutionMode.ExternalProcess, "remove")]
    [InlineData(VirtualOutputExecutionMode.ExternalProcess, "disable")]
    [InlineData(VirtualOutputExecutionMode.ExternalProcess, "unhost")]
    public async Task Reconcile_ObsoleteOwnedOutput_CleansOnlyOwnedResources(VirtualOutputExecutionMode mode, string change)
    {
        // Arrange
        await using var fixture = new OwnershipFixture();
        var device = fixture.CreateDevice(mode);
        fixture.Configuration = fixture.Configuration with { VirtualDevices = [device] };
        await fixture.ReconcileAsync();
        var bootstrap = fixture.GetBootstrap(mode, device.Id);
        var worker = fixture.Register(bootstrap);
        var discovery = fixture.RegisterDiscovery();
        fixture.Configuration = fixture.Configuration with
        {
            VirtualDevices = change switch
            {
                "remove" => [],
                "disable" => [device with { Enabled = false }],
                _ => [device with { Hosting = null }]
            }
        };

        // Act
        await fixture.ReconcileAsync();

        // Assert
        Assert.False(fixture.Authority.Authorize(worker, VirtualOutputCapability.ReportHealth).Authorized);
        Assert.Null(fixture.Enrollments.Take(device.Id));
        Assert.DoesNotContain(fixture.Status.GetSnapshots(), snapshot => snapshot.VirtualDeviceId == device.Id);
        Assert.All(fixture.Processes, process => Assert.True(process.Completion.IsCompleted));
        Assert.True(fixture.Authority.Authorize(discovery.Context, VirtualOutputCapability.ReportHealth).Authorized);
        Assert.Same(discovery.Snapshot, Assert.Single(fixture.Status.GetSnapshots()));
    }

    /// <summary>Verifies removal resets failures even when activation never produced an active worker.</summary>
    [Theory]
    [InlineData("quarantined")]
    [InlineData("retrying")]
    [InlineData("missing-policy")]
    public async Task Reconcile_FailedOwnedOutput_RemovesStatusAndAllowsReactivation(string failure)
    {
        // Arrange
        await using var fixture = new OwnershipFixture();
        var device = fixture.CreateDevice(VirtualOutputExecutionMode.ExternalProcess);
        var policy = Assert.Single(fixture.Configuration.VirtualOutputWorkerPolicies) with { MaximumRestarts = failure == "retrying" ? 1 : 0 };
        fixture.Configuration = fixture.Configuration with
        {
            VirtualDevices = [device],
            VirtualOutputWorkerPolicies = failure == "missing-policy" ? [] : [policy]
        };
        fixture.FailActivation = true;
        await fixture.ReconcileAsync();
        Assert.Equal(VirtualOutputRuntimeState.Failed, Assert.Single(fixture.Status.GetSnapshots()).State);
        fixture.Configuration = fixture.Configuration with { VirtualDevices = [] };

        // Act
        await fixture.ReconcileAsync();
        var afterRemoval = fixture.Status.GetSnapshots();
        fixture.FailActivation = false;
        fixture.Configuration = fixture.Configuration with { VirtualDevices = [device], VirtualOutputWorkerPolicies = [policy] };
        await fixture.ReconcileAsync();

        // Assert
        Assert.Empty(afterRemoval);
        var snapshot = Assert.Single(fixture.Status.GetSnapshots());
        Assert.Equal(device.Id, snapshot.VirtualDeviceId);
        Assert.Equal(VirtualOutputRuntimeState.Starting, snapshot.State);
        Assert.NotNull(fixture.Enrollments.Take(device.Id));
        Assert.Equal(failure == "missing-policy" ? 1 : 2, fixture.ActivationAttempts);
    }

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

    private sealed class OwnershipFixture : IAsyncDisposable
    {
        private readonly VirtualOutputSupervisor _supervisor;
        private VirtualOutputWorkerBootstrap? _bootstrap;

        internal OwnershipFixture()
        {
            var settings = Substitute.For<ILineupSettingsService>();
            settings.Settings.Returns(_ => new LineupSettings { Streaming = Configuration });
            var catalog = Substitute.For<IVirtualOutputWorkerCatalog>();
            catalog.ResolveAsync("ownership-test", Arg.Any<CancellationToken>()).Returns(_ =>
            {
                ActivationAttempts++;
                return FailActivation
                    ? Task.FromException<VirtualOutputWorkerPackage>(new IOException("Synthetic activation failure."))
                    : Task.FromResult(new VirtualOutputWorkerPackage
                    {
                        PackageRoot = "unused",
                        Manifest = new VirtualOutputWorkerManifest
                        {
                            WorkerId = "ownership-test",
                            Version = "1.0.0",
                            Publisher = "Test",
                            ExecutablePath = "unused",
                            ExecutableSha256 = new string('0', 64),
                            MinimumLineupVersion = "1.0.0",
                            SettingsDescriptor = new Lineup.Common.Configuration.AdapterSettingsDescriptor { AdapterKind = "ownership-test" }
                        }
                    });
            });
            var launcher = Substitute.For<IVirtualOutputWorkerLauncher>();
            launcher.StartAsync(Arg.Any<VirtualOutputWorkerPackage>(), Arg.Any<VirtualOutputWorkerBootstrap>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                _bootstrap = call.Arg<VirtualOutputWorkerBootstrap>();
                var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                var process = Substitute.For<IVirtualOutputWorkerProcess>();
                process.Completion.Returns(completion.Task);
                process.StopAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(_ =>
                {
                    completion.TrySetResult(0);
                    return Task.CompletedTask;
                });
                process.DisposeAsync().Returns(_ =>
                {
                    completion.TrySetResult(0);
                    return ValueTask.CompletedTask;
                });
                Processes.Add(process);
                return process;
            });
            _supervisor = new VirtualOutputSupervisor(
                settings,
                new VirtualOutputRuntimeOptions
                {
                    WorkerPackageRoot = "unused",
                    IpcEndpoint = new VirtualOutputIpcEndpoint { Transport = VirtualOutputIpcTransport.NamedPipe, Address = "unused" }
                },
                catalog,
                launcher,
                Substitute.For<IVirtualOutputCertificateResolver>(),
                Authority,
                Status,
                Enrollments,
                NullLogger<VirtualOutputSupervisor>.Instance);
        }

        internal StreamingConfiguration Configuration { get; set; } = new()
        {
            VirtualOutputWorkerPolicies = [new VirtualOutputWorkerPolicyDefinition { Key = "ownership", Name = "Ownership", MaximumRestarts = 0, DrainTimeoutSeconds = 0 }]
        };

        internal VirtualOutputSessionAuthority Authority { get; } = new();

        internal VirtualOutputRuntimeStatus Status { get; } = new();

        internal VirtualOutputExternalEnrollmentStore Enrollments { get; } = new();

        internal List<IVirtualOutputWorkerProcess> Processes { get; } = [];

        internal bool FailActivation { get; set; }

        internal int ActivationAttempts { get; private set; }

        internal VirtualDeviceDefinition CreateDevice(VirtualOutputExecutionMode mode) => new()
        {
            Id = VirtualDeviceId.New(),
            Name = "Owned output",
            Adapter = new AdapterConfiguration("ownership-test", JsonSerializer.SerializeToElement(new { })),
            Hosting = new VirtualOutputHostingDefinition { ExecutionMode = mode, WorkerPolicyKey = Assert.Single(Configuration.VirtualOutputWorkerPolicies).Key }
        };

        internal VirtualOutputWorkerBootstrap GetBootstrap(VirtualOutputExecutionMode mode, VirtualDeviceId deviceId)
        {
            if (mode == VirtualOutputExecutionMode.ManagedProcess)
            {
                return Assert.IsType<VirtualOutputWorkerBootstrap>(_bootstrap);
            }
            var enrollment = Enrollments.Take(deviceId);
            Assert.NotNull(enrollment);
            return enrollment.Bootstrap;
        }

        internal (VirtualOutputSessionContext Context, VirtualOutputRuntimeSnapshot Snapshot) RegisterDiscovery()
        {
            var deviceId = new VirtualDeviceId(new Guid("d1d41379-85f9-4834-970d-3ba27ea7c104"));
            var ticket = Authority.CreateActivation(deviceId, "shared-discovery", new HashSet<VirtualOutputCapability> { VirtualOutputCapability.ReportHealth },
                TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5));
            var registration = Authority.Register(new VirtualOutputWorkerRegistrationRequest
            {
                VirtualDeviceId = deviceId,
                WorkerId = ticket.WorkerId,
                Generation = ticket.Generation,
                BootstrapToken = ticket.BootstrapToken,
                MinimumProtocolVersion = VirtualOutputProtocol.MinimumVersion,
                MaximumProtocolVersion = VirtualOutputProtocol.MaximumVersion
            });
            Assert.True(registration.Accepted);
            var snapshot = new VirtualOutputRuntimeSnapshot { VirtualDeviceId = deviceId, State = VirtualOutputRuntimeState.Starting, Generation = ticket.Generation };
            Status.Set(snapshot);
            return (new VirtualOutputSessionContext { SessionId = registration.SessionId!.Value, Generation = ticket.Generation }, snapshot);
        }

        internal VirtualOutputSessionContext Register(VirtualOutputWorkerBootstrap bootstrap)
        {
            var registration = Authority.Register(new VirtualOutputWorkerRegistrationRequest
            {
                VirtualDeviceId = bootstrap.VirtualDevice.Id,
                WorkerId = bootstrap.WorkerId,
                Generation = bootstrap.Generation,
                BootstrapToken = bootstrap.BootstrapToken,
                MinimumProtocolVersion = VirtualOutputProtocol.MinimumVersion,
                MaximumProtocolVersion = VirtualOutputProtocol.MaximumVersion
            });
            Assert.True(registration.Accepted);
            return new VirtualOutputSessionContext { SessionId = registration.SessionId!.Value, Generation = bootstrap.Generation };
        }

        internal async Task ReconcileAsync()
        {
            var gate = Assert.IsType<SemaphoreSlim>(typeof(VirtualOutputSupervisor).GetField("_stateGate", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(_supervisor));
            var reconcile = typeof(VirtualOutputSupervisor).GetMethod("ReconcileAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(reconcile);
            await gate.WaitAsync(TestContext.Current.CancellationToken);
            try
            {
                await Assert.IsAssignableFrom<Task>(reconcile.Invoke(_supervisor, [TestContext.Current.CancellationToken]));
            }
            finally
            {
                gate.Release();
            }
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            Configuration = Configuration with { VirtualDevices = [] };
            await ReconcileAsync();
            await _supervisor.StopAsync(CancellationToken.None);
            _supervisor.Dispose();
            Authority.Dispose();
        }
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
