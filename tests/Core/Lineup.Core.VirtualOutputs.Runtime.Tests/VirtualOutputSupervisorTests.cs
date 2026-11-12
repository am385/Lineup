using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Lineup.Core.Common.Streaming;
using Lineup.Core.Settings;
using Lineup.Core.VirtualOutputs.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.Extensions;
using Xunit;

namespace Lineup.Core.VirtualOutputs.Runtime.Tests;

/// <summary>Verifies desired-versus-applied virtual-output worker supervision.</summary>
public sealed class VirtualOutputSupervisorTests
{
    /// <summary>Verifies explicit recovery revalidates one unchanged quarantined output without disturbing its healthy sibling.</summary>
    /// <param name="mode">The failed output's ownership mode.</param>
    [Theory]
    [InlineData(VirtualOutputExecutionMode.ManagedProcess)]
    [InlineData(VirtualOutputExecutionMode.ExternalProcess)]
    public async Task RetryAsync_QuarantinedWorker_PreservesHealthySibling(VirtualOutputExecutionMode mode)
    {
        // Arrange
        await using var fixture = new OwnershipFixture();
        var healthy = fixture.CreateDevice(VirtualOutputExecutionMode.ManagedProcess);
        fixture.Configuration = fixture.Configuration with { VirtualDevices = [healthy] };
        await fixture.ReconcileAsync();
        var healthySession = fixture.Register(fixture.GetBootstrap(VirtualOutputExecutionMode.ManagedProcess, healthy.Id));
        fixture.Status.Update(healthy.Id, current => current! with { State = VirtualOutputRuntimeState.Healthy });
        var healthyProcess = fixture.GetProcess(healthy.Id)!;
        var failed = fixture.CreateDevice(mode);
        fixture.Configuration = fixture.Configuration with { VirtualDevices = [healthy, failed] };
        fixture.FailActivation = true;
        await fixture.ReconcileAsync();
        var previous = Assert.Single(fixture.Status.GetSnapshots(), snapshot => snapshot.VirtualDeviceId == failed.Id);
        fixture.FailActivation = false;

        // Act
        var recovery = await fixture.RetryAsync(failed.Id);
        await fixture.ReconcileAsync();

        // Assert
        Assert.True(recovery.Accepted);
        Assert.Equal(VirtualOutputRuntimeState.Starting, recovery.Snapshot!.State);
        Assert.Equal(previous.LastFailure, recovery.Snapshot.PreviousFailure);
        Assert.NotNull(recovery.Snapshot.LastRecoveryAttemptUtc);
        Assert.Equal(0, recovery.Snapshot.RestartCount);
        Assert.Equal(3, fixture.ActivationAttempts);
        Assert.True(fixture.Authority.Authorize(healthySession, VirtualOutputCapability.ReportHealth).Authorized);
        Assert.False(healthyProcess.Completion.IsCompleted);
        await healthyProcess.DidNotReceive().StopAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        await healthyProcess.DidNotReceive().DisposeAsync();
        Assert.Equal(VirtualOutputRuntimeState.Healthy, Assert.Single(fixture.Status.GetSnapshots(), snapshot => snapshot.VirtualDeviceId == healthy.Id).State);
        Assert.NotNull(fixture.GetBootstrap(mode, failed.Id));
    }

    /// <summary>Verifies repeated failure after an operator retry re-enters the original budget and retains recovery diagnostics.</summary>
    [Fact]
    public async Task RetryAsync_PersistentFailure_RequarantinesWithoutBackgroundLoop()
    {
        // Arrange
        await using var fixture = new OwnershipFixture { FailActivation = true };
        var device = fixture.CreateDevice(VirtualOutputExecutionMode.ManagedProcess);
        fixture.Configuration = fixture.Configuration with { VirtualDevices = [device] };
        await fixture.ReconcileAsync();
        var previous = Assert.Single(fixture.Status.GetSnapshots()).LastFailure;

        // Act
        var result = await fixture.RetryAsync(device.Id);
        await fixture.ReconcileAsync();
        await fixture.ReconcileAsync();

        // Assert
        Assert.True(result.Accepted);
        Assert.Equal(2, fixture.ActivationAttempts);
        Assert.Equal(VirtualOutputRuntimeState.Failed, result.Snapshot!.State);
        Assert.Equal(1, result.Snapshot.RestartCount);
        Assert.Contains("exceeded its restart budget", result.Snapshot.LastFailure, StringComparison.Ordinal);
        Assert.Equal(previous, result.Snapshot.PreviousFailure);
        Assert.NotNull(result.Snapshot.LastRecoveryAttemptUtc);
    }

    /// <summary>Verifies retry cannot weaken resource enforcement or launch an unsupported externally supervised policy.</summary>
    [Fact]
    public async Task RetryAsync_UnsupportedResourcePolicy_DoesNotLaunch()
    {
        // Arrange
        await using var fixture = new OwnershipFixture();
        var device = fixture.CreateDevice(VirtualOutputExecutionMode.ExternalProcess);
        fixture.Configuration = fixture.Configuration with
        {
            VirtualDevices = [device],
            VirtualOutputWorkerPolicies = [Assert.Single(fixture.Configuration.VirtualOutputWorkerPolicies) with { MaximumCpuPercent = 25 }]
        };
        await fixture.ReconcileAsync();

        // Act
        var result = await fixture.RetryAsync(device.Id);

        // Assert
        Assert.True(result.Accepted);
        Assert.Equal(0, fixture.ActivationAttempts);
        Assert.Equal(VirtualOutputRuntimeState.Failed, result.Snapshot!.State);
        Assert.Equal(0, result.Snapshot.RestartCount);
        Assert.Contains("externally launched", result.Snapshot.LastFailure, StringComparison.Ordinal);
        Assert.NotNull(result.Snapshot.PreviousFailure);
    }

    /// <summary>Verifies recovery rejects running workers and outputs that no longer have enabled saved hosting.</summary>
    /// <param name="state">The unsafe or unavailable recovery target.</param>
    /// <param name="mode">The output's process-ownership mode.</param>
    [Theory]
    [InlineData("healthy", VirtualOutputExecutionMode.ManagedProcess)]
    [InlineData("active-failed", VirtualOutputExecutionMode.ManagedProcess)]
    [InlineData("disabled", VirtualOutputExecutionMode.ManagedProcess)]
    [InlineData("management", VirtualOutputExecutionMode.ManagedProcess)]
    [InlineData("missing", VirtualOutputExecutionMode.ManagedProcess)]
    [InlineData("healthy", VirtualOutputExecutionMode.ExternalProcess)]
    [InlineData("active-failed", VirtualOutputExecutionMode.ExternalProcess)]
    [InlineData("disabled", VirtualOutputExecutionMode.ExternalProcess)]
    [InlineData("management", VirtualOutputExecutionMode.ExternalProcess)]
    [InlineData("missing", VirtualOutputExecutionMode.ExternalProcess)]
    public async Task RetryAsync_IneligibleOutput_DoesNotChangeOwnership(string state, VirtualOutputExecutionMode mode)
    {
        // Arrange
        await using var fixture = new OwnershipFixture();
        var device = fixture.CreateDevice(mode);
        fixture.Configuration = fixture.Configuration with { VirtualDevices = [device] };
        await fixture.ReconcileAsync();
        var session = fixture.Register(fixture.GetBootstrap(mode, device.Id));
        fixture.Status.Update(device.Id, current => current! with { State = state == "healthy" ? VirtualOutputRuntimeState.Healthy : VirtualOutputRuntimeState.Failed });
        fixture.Configuration = fixture.Configuration with
        {
            VirtualDevices = [device with { Enabled = state != "disabled", Hosting = state == "management" ? null : device.Hosting }]
        };
        var target = state == "missing" ? VirtualDeviceId.New() : device.Id;

        // Act
        var result = await fixture.RetryAsync(target);

        // Assert
        Assert.False(result.Accepted);
        Assert.Equal(1, fixture.ActivationAttempts);
        Assert.True(fixture.Authority.Authorize(session, VirtualOutputCapability.ReportHealth).Authorized);
        if (mode == VirtualOutputExecutionMode.ManagedProcess)
        {
            Assert.False(fixture.GetProcess(device.Id)!.Completion.IsCompleted);
        }
        else
        {
            Assert.Empty(fixture.Processes);
        }
    }

    /// <summary>Verifies cancellation before acceptance cannot reset a failed output's restart budget.</summary>
    [Fact]
    public async Task RetryAsync_CanceledBeforeAcceptance_PreservesFailure()
    {
        // Arrange
        await using var fixture = new OwnershipFixture { FailActivation = true };
        var device = fixture.CreateDevice(VirtualOutputExecutionMode.ManagedProcess);
        fixture.Configuration = fixture.Configuration with { VirtualDevices = [device] };
        await fixture.ReconcileAsync();
        var before = Assert.Single(fixture.Status.GetSnapshots());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        // Act
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.RetryAsync(device.Id, cancellation.Token));

        // Assert
        Assert.Same(before, Assert.Single(fixture.Status.GetSnapshots()));
        Assert.Equal(1, fixture.ActivationAttempts);
    }

    /// <summary>Verifies closing the requesting page cannot strand an already accepted worker activation.</summary>
    [Fact]
    public async Task RetryAsync_CallerCancelsAfterAcceptance_CompletesUnderHostOwnership()
    {
        // Arrange
        await using var fixture = new OwnershipFixture { FailActivation = true };
        var device = fixture.CreateDevice(VirtualOutputExecutionMode.ManagedProcess);
        fixture.Configuration = fixture.Configuration with { VirtualDevices = [device] };
        await fixture.ReconcileAsync();
        fixture.FailActivation = false;
        fixture.BlockLaunch = true;
        using var cancellation = new CancellationTokenSource();
        var recovery = fixture.RetryAsync(device.Id, cancellation.Token);
        try
        {
            await WaitUntilAsync(() => fixture.GetProcess(device.Id) is not null, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

            // Act
            cancellation.Cancel();
            fixture.ReleaseLaunches();
            var result = await recovery.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

            // Assert
            Assert.True(result.Accepted);
            Assert.Equal(VirtualOutputRuntimeState.Starting, result.Snapshot!.State);
            Assert.False(fixture.GetProcess(device.Id)!.Completion.IsCompleted);
            Assert.Equal(2, fixture.ActivationAttempts);
        }
        finally
        {
            fixture.ReleaseLaunches();
        }
    }

    /// <summary>Verifies external re-enrollment cannot replace a generation while an output lifecycle transition owns it.</summary>
    [Fact]
    public async Task ExternalEnrollment_ConcurrentTransition_DefersRenewal()
    {
        // Arrange
        var time = new ManualTimeProvider();
        await using var fixture = new OwnershipFixture(time);
        var device = fixture.CreateDevice(VirtualOutputExecutionMode.ExternalProcess);
        fixture.Configuration = fixture.Configuration with { VirtualDevices = [device] };
        await fixture.ReconcileAsync();
        var old = fixture.GetBootstrap(VirtualOutputExecutionMode.ExternalProcess, device.Id);
        fixture.Register(old);
        time.Advance(TimeSpan.FromSeconds(16));
        VirtualOutputExternalEnrollment? duringTransition = null;

        // Act
        await fixture.TransitionAsync(device.Id, _ =>
        {
            duringTransition = fixture.Enrollments.Take(device.Id);
            return Task.CompletedTask;
        });
        var renewed = fixture.GetBootstrap(VirtualOutputExecutionMode.ExternalProcess, device.Id);
        var session = fixture.Register(renewed);

        // Assert
        Assert.Null(duringTransition);
        Assert.True(renewed.Generation > old.Generation);
        Assert.True(fixture.Authority.Authorize(session, VirtualOutputCapability.ReportHealth).Authorized);
        Assert.Empty(fixture.Processes);
    }

    /// <summary>Verifies unrelated lease renewals and dead-worker detection continue while another output has a stalled drain.</summary>
    [Fact]
    public async Task Reconcile_StalledDrain_DoesNotBlockSiblingLiveness()
    {
        // Arrange
        var time = new ManualTimeProvider();
        await using var fixture = new OwnershipFixture(time) { RegisterDuringLaunch = true };
        var draining = fixture.CreateDevice(VirtualOutputExecutionMode.ManagedProcess);
        var healthy = fixture.CreateDevice(VirtualOutputExecutionMode.ManagedProcess);
        var expired = fixture.CreateDevice(VirtualOutputExecutionMode.ManagedProcess);
        fixture.Configuration = fixture.Configuration with
        {
            VirtualDevices = [draining, healthy, expired],
            VirtualOutputWorkerPolicies = [Assert.Single(fixture.Configuration.VirtualOutputWorkerPolicies) with { DrainTimeoutSeconds = 60 }]
        };
        await fixture.StartAsync();
        try
        {
            await WaitUntilAsync(() => fixture.Status.GetSnapshots().Count(snapshot => snapshot.State == VirtualOutputRuntimeState.Healthy) == 3,
                TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            fixture.Configuration = fixture.Configuration with { VirtualDevices = [healthy, expired] };
            fixture.SignalSettingsChanged();
            await WaitUntilAsync(() => fixture.Status.GetSnapshots().Any(snapshot => snapshot.VirtualDeviceId == draining.Id && snapshot.State == VirtualOutputRuntimeState.Draining),
                TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

            // Act
            fixture.Authority.Revoke(expired.Id, "Synthetic sibling lease expiry.");
            await time.WaitForTimerAsync(TimeSpan.FromSeconds(1));
            time.Advance(TimeSpan.FromSeconds(1));
            var renewal = fixture.Authority.Renew(new VirtualOutputLeaseRenewalRequest { Session = fixture.GetSession(healthy.Id), Ready = true });
            await WaitUntilAsync(() => fixture.Status.GetSnapshots().Any(snapshot => snapshot.VirtualDeviceId == expired.Id && snapshot.State == VirtualOutputRuntimeState.Failed),
                TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

            // Assert
            Assert.True(renewal.Accepted);
            Assert.False(fixture.GetProcess(draining.Id)!.Completion.IsCompleted);
            Assert.False(fixture.GetProcess(healthy.Id)!.Completion.IsCompleted);
            Assert.Equal(VirtualOutputRuntimeState.Healthy, Assert.Single(fixture.Status.GetSnapshots(), snapshot => snapshot.VirtualDeviceId == healthy.Id).State);
        }
        finally
        {
            foreach (var process in fixture.Processes)
            {
                fixture.ExitProcess(process);
            }
            await fixture.StopAsync();
        }
    }

    /// <summary>Verifies one shutdown grace deadline cleans all sixteen workers even when every per-worker drain is longer.</summary>
    [Fact]
    public async Task StopAsync_SharedGraceBudget_CleansEveryWorker()
    {
        // Arrange
        var time = new ManualTimeProvider();
        await using var fixture = new OwnershipFixture(time) { RegisterDuringLaunch = true };
        fixture.Configuration = fixture.Configuration with
        {
            VirtualDevices = Enumerable.Range(0, 16).Select(_ => fixture.CreateDevice(VirtualOutputExecutionMode.ManagedProcess)).ToArray(),
            VirtualOutputWorkerPolicies = [Assert.Single(fixture.Configuration.VirtualOutputWorkerPolicies) with { DrainTimeoutSeconds = 60 }]
        };
        await fixture.StartAsync();
        try
        {
            await WaitUntilAsync(() => fixture.Status.GetSnapshots().Count(snapshot => snapshot.State == VirtualOutputRuntimeState.Healthy) == 16,
                TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

            // Act
            var stopping = fixture.StopAsync();
            await time.WaitForTimerAsync(TimeSpan.FromSeconds(30));
            await WaitUntilAsync(() => fixture.Status.GetSnapshots().Count(snapshot => snapshot.State == VirtualOutputRuntimeState.Draining) == 16,
                TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            time.Advance(TimeSpan.FromSeconds(29));
            var endedEarly = stopping.IsCompleted;
            time.Advance(TimeSpan.FromSeconds(1));
            await stopping.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

            // Assert
            Assert.False(endedEarly);
            Assert.All(fixture.Processes, process => Assert.True(process.Completion.IsCompletedSuccessfully));
            Assert.All(fixture.Configuration.VirtualDevices, device =>
                Assert.False(fixture.Authority.HasActiveSession(device.Id, fixture.GetBootstrap(VirtualOutputExecutionMode.ManagedProcess, device.Id).Generation)));
        }
        finally
        {
            foreach (var process in fixture.Processes)
            {
                fixture.ExitProcess(process);
            }
            await fixture.StopAsync();
        }
    }

    /// <summary>Verifies an expired host deadline forces cleanup without returning while an owned child's disposal is still pending.</summary>
    /// <param name="exitedBeforeStop">Whether the worker has already exited and its exit observer owns cleanup.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopAsync_HostDeadlineExpired_AwaitsOwnedCleanup(bool exitedBeforeStop)
    {
        // Arrange
        await using var fixture = new OwnershipFixture { RegisterDuringLaunch = true };
        fixture.Configuration = fixture.Configuration with
        {
            VirtualDevices = Enumerable.Range(0, 3).Select(_ => fixture.CreateDevice(VirtualOutputExecutionMode.ManagedProcess)).ToArray(),
            VirtualOutputWorkerPolicies = [Assert.Single(fixture.Configuration.VirtualOutputWorkerPolicies) with { DrainTimeoutSeconds = 60 }]
        };
        await fixture.StartAsync();
        await WaitUntilAsync(() => fixture.Status.GetSnapshots().Count(snapshot => snapshot.State == VirtualOutputRuntimeState.Healthy) == 3,
            TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var target = fixture.Processes[0];
        target.Configure().DisposeAsync().Returns(_ =>
        {
            entered.TrySetResult();
            return new ValueTask(release.Task);
        });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var returnedBeforeCleanup = false;

        // Act
        Task stopping;
        try
        {
            if (exitedBeforeStop)
            {
                fixture.ExitProcess(target);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            }
            stopping = fixture.StopAsync(cancellation.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            returnedBeforeCleanup = stopping.IsCompleted;
        }
        finally
        {
            release.TrySetResult();
            foreach (var process in fixture.Processes)
            {
                fixture.ExitProcess(process);
            }
        }
        await stopping.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        // Assert
        Assert.False(returnedBeforeCleanup);
        Assert.All(fixture.Processes, process => Assert.True(process.Completion.IsCompletedSuccessfully));
    }

    /// <summary>Verifies stalled activations cannot exceed the concurrency budget and queued outputs are not launched during shutdown.</summary>
    [Fact]
    public async Task Reconcile_StalledStarts_EnforcesConcurrencyAndCancelsQueuedWork()
    {
        // Arrange
        await using var fixture = new OwnershipFixture { BlockLaunch = true };
        fixture.Configuration = fixture.Configuration with
        {
            VirtualDevices = Enumerable.Range(0, 16).Select(_ => fixture.CreateDevice(VirtualOutputExecutionMode.ManagedProcess)).ToArray()
        };
        await fixture.StartAsync();
        await WaitUntilAsync(() => fixture.Processes.Count == 4, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        // Act
        await fixture.StopAsync().WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(4, fixture.ActivationAttempts);
        Assert.Equal(4, fixture.Processes.Count);
        Assert.All(fixture.Processes, process => Assert.True(process.Completion.IsCompletedSuccessfully));
    }

    /// <summary>Verifies queued transitions do not launch outputs removed from saved settings while earlier starts are stalled.</summary>
    [Fact]
    public async Task Reconcile_QueuedOutputs_UsesLatestSavedConfiguration()
    {
        // Arrange
        await using var fixture = new OwnershipFixture { BlockLaunch = true };
        fixture.Configuration = fixture.Configuration with
        {
            VirtualDevices = Enumerable.Range(0, 16).Select(_ => fixture.CreateDevice(VirtualOutputExecutionMode.ManagedProcess)).ToArray()
        };
        await fixture.StartAsync();
        try
        {
            await WaitUntilAsync(() => fixture.Configuration.VirtualDevices.Count(device => fixture.GetProcess(device.Id) is not null) == 4,
                TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

            // Act
            fixture.Configuration = fixture.Configuration with
            {
                VirtualDevices = fixture.Configuration.VirtualDevices.Where(device => fixture.GetProcess(device.Id) is not null).ToArray()
            };
            fixture.BlockLaunch = false;
            fixture.ReleaseLaunches();
            await fixture.ReconcileAsync();

            // Assert
            Assert.Equal(4, fixture.ActivationAttempts);
            Assert.Equal(4, fixture.Processes.Count);
            Assert.Equal(4, fixture.Status.GetSnapshots().Count);
        }
        finally
        {
            fixture.ReleaseLaunches();
        }
    }

    /// <summary>Verifies unsupported enforcement is rejected before activation, does not consume restart attempts, and recovers after correction.</summary>
    [Fact]
    public async Task Reconcile_UnenforceableExternalPolicy_RequiresOperatorCorrectionWithoutRestartLoop()
    {
        // Arrange
        await using var fixture = new OwnershipFixture();
        var device = fixture.CreateDevice(VirtualOutputExecutionMode.ExternalProcess);
        var policy = Assert.Single(fixture.Configuration.VirtualOutputWorkerPolicies);
        fixture.Configuration = fixture.Configuration with { VirtualDevices = [device], VirtualOutputWorkerPolicies = [policy with { MaximumCpuPercent = 25 }] };

        // Act
        await fixture.ReconcileAsync();
        await fixture.ReconcileAsync();

        // Assert
        Assert.Empty(fixture.Processes);
        Assert.Equal(0, fixture.ActivationAttempts);
        var failed = Assert.Single(fixture.Status.GetSnapshots());
        Assert.Equal(VirtualOutputRuntimeState.Failed, failed.State);
        Assert.Equal(0, failed.RestartCount);
        Assert.Contains("externally launched", failed.LastFailure);
        Assert.DoesNotContain("Retrying", failed.LastFailure);

        // Act
        fixture.Configuration = fixture.Configuration with { VirtualOutputWorkerPolicies = [policy] };
        await fixture.ReconcileAsync();

        // Assert
        Assert.Equal(1, fixture.ActivationAttempts);
        Assert.NotNull(fixture.GetBootstrap(VirtualOutputExecutionMode.ExternalProcess, device.Id));
    }

    /// <summary>Verifies malformed certificate metadata is contained per output, retires obsolete ownership and recovers after correction.</summary>
    [Theory]
    [InlineData(VirtualOutputExecutionMode.ManagedProcess, false)]
    [InlineData(VirtualOutputExecutionMode.ManagedProcess, true)]
    [InlineData(VirtualOutputExecutionMode.ExternalProcess, false)]
    [InlineData(VirtualOutputExecutionMode.ExternalProcess, true)]
    public async Task Reconcile_InvalidCertificateFingerprint_IsolatesOutputAndRecovers(VirtualOutputExecutionMode mode, bool replace)
    {
        // Arrange
        var directory = Directory.CreateTempSubdirectory("lineup-fingerprint-");
        try
        {
            var certificate = Path.Combine(directory.FullName, "listener.pfx");
            await File.WriteAllBytesAsync(certificate, [1, 2, 3, 4], TestContext.Current.CancellationToken);
            await using var fixture = new OwnershipFixture(certificateResolver: new FileVirtualOutputCertificateResolver());
            var device = fixture.CreateDevice(mode);
            device = device with
            {
                Hosting = device.Hosting! with
                {
                    Bindings = [new VirtualOutputBindingDefinition { Transport = VirtualOutputTransport.Https, Addresses = ["127.0.0.1"], Port = 8443, SecurityPolicyKey = "tls" }]
                }
            };
            var healthy = fixture.CreateDevice(mode);
            var security = new VirtualOutputSecurityPolicyDefinition { Key = "tls", Name = "TLS", CertificateReference = certificate };
            fixture.Configuration = fixture.Configuration with { VirtualDevices = [device, healthy], VirtualOutputSecurityPolicies = [security] };
            VirtualOutputSessionContext? healthySession = null;
            IVirtualOutputWorkerProcess? replacedProcess = null;
            if (replace)
            {
                await fixture.ReconcileAsync();
                healthySession = fixture.Register(fixture.GetBootstrap(mode, healthy.Id));
                fixture.Status.Update(healthy.Id, current => current! with { State = VirtualOutputRuntimeState.Healthy });
                replacedProcess = fixture.GetProcess(device.Id);
            }
            fixture.Configuration = fixture.Configuration with { VirtualOutputSecurityPolicies = [security with { CertificateReference = "invalid\0certificate" }] };

            // Act
            await fixture.StartAsync();
            await WaitUntilAsync(() => fixture.Status.GetSnapshots().Any(snapshot => snapshot.VirtualDeviceId == device.Id && snapshot.State == VirtualOutputRuntimeState.Failed)
                && fixture.Status.GetSnapshots().Any(snapshot => snapshot.VirtualDeviceId == healthy.Id && (mode != VirtualOutputExecutionMode.ManagedProcess || snapshot.ProcessId is not null)),
                TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            if (!replace)
            {
                healthySession = fixture.Register(fixture.GetBootstrap(mode, healthy.Id));
                fixture.Status.Update(healthy.Id, current => current! with { State = VirtualOutputRuntimeState.Healthy });
            }
            var failed = Assert.Single(fixture.Status.GetSnapshots(), snapshot => snapshot.VirtualDeviceId == device.Id);
            fixture.Configuration = fixture.Configuration with { VirtualOutputSecurityPolicies = [security] };
            fixture.SignalSettingsChanged();
            await WaitUntilAsync(() => fixture.Status.GetSnapshots().Any(snapshot => snapshot.VirtualDeviceId == device.Id && snapshot.State == VirtualOutputRuntimeState.Starting
                && (mode != VirtualOutputExecutionMode.ManagedProcess || snapshot.ProcessId is not null)),
                TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

            // Assert
            Assert.True(fixture.IsRunning);
            Assert.Equal(VirtualOutputRuntimeState.Failed, failed.State);
            Assert.False(string.IsNullOrWhiteSpace(failed.LastFailure));
            Assert.Contains(fixture.Logger.ReceivedCalls(), call => call.GetMethodInfo().Name == nameof(ILogger.Log) && Equals(call.GetArguments()[0], LogLevel.Error)
                && call.GetArguments()[2]?.ToString()?.Contains(device.Id.ToString(), StringComparison.Ordinal) == true);
            Assert.True(fixture.Authority.Authorize(Assert.IsType<VirtualOutputSessionContext>(healthySession), VirtualOutputCapability.ReportHealth).Authorized);
            Assert.Equal(VirtualOutputRuntimeState.Healthy, Assert.Single(fixture.Status.GetSnapshots(), snapshot => snapshot.VirtualDeviceId == healthy.Id).State);
            Assert.Equal(VirtualOutputRuntimeState.Starting, Assert.Single(fixture.Status.GetSnapshots(), snapshot => snapshot.VirtualDeviceId == device.Id).State);
            var corrected = fixture.GetBootstrap(mode, device.Id);
            var material = Assert.Single(corrected.CertificateMaterials);
            Assert.Equal(certificate, material.Reference);
            Assert.Equal([1, 2, 3, 4], material.Pkcs12);
            if (replacedProcess is not null)
            {
                Assert.True(replacedProcess.Completion.IsCompleted);
                await replacedProcess.Received().DisposeAsync();
            }
            if (mode == VirtualOutputExecutionMode.ExternalProcess)
            {
                Assert.Empty(fixture.Processes);
            }
            else
            {
                var sibling = fixture.GetProcess(healthy.Id)!;
                await sibling.DidNotReceive().StopAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
                await sibling.DidNotReceive().DisposeAsync();
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>Verifies lease expiry updates external availability without an enrollment read and preserves renewed generations and siblings.</summary>
    [Theory]
    [InlineData(VirtualOutputRuntimeState.Healthy, false)]
    [InlineData(VirtualOutputRuntimeState.Healthy, true)]
    [InlineData(VirtualOutputRuntimeState.Degraded, false)]
    [InlineData(VirtualOutputRuntimeState.Starting, false)]
    public async Task StartAsync_ExternalLeaseExpires_UpdatesStatusAndPreservesRenewedGeneration(VirtualOutputRuntimeState state, bool observeLiveLease)
    {
        // Arrange
        var time = new ManualTimeProvider();
        await using var fixture = new OwnershipFixture(time);
        var device = fixture.CreateDevice(VirtualOutputExecutionMode.ExternalProcess);
        var healthy = fixture.CreateDevice(VirtualOutputExecutionMode.ExternalProcess);
        fixture.Configuration = fixture.Configuration with { VirtualDevices = [device, healthy] };
        await fixture.StartAsync();
        await time.WaitForTimerAsync(TimeSpan.FromSeconds(1));
        var bootstrap = fixture.GetBootstrap(VirtualOutputExecutionMode.ExternalProcess, device.Id);
        var context = fixture.Register(bootstrap);
        var healthyContext = fixture.Register(fixture.GetBootstrap(VirtualOutputExecutionMode.ExternalProcess, healthy.Id));
        fixture.Status.Update(device.Id, current => current! with { State = state, ProtocolVersion = VirtualOutputProtocol.MaximumVersion });
        fixture.Status.Update(healthy.Id, current => current! with { State = VirtualOutputRuntimeState.Healthy, ProtocolVersion = VirtualOutputProtocol.MaximumVersion });
        if (observeLiveLease)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            await time.WaitForTimerAsync(TimeSpan.FromSeconds(1));
        }
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.True(fixture.Authority.Renew(new VirtualOutputLeaseRenewalRequest { Session = healthyContext, Ready = true }).Accepted);
        await time.WaitForTimerAsync(TimeSpan.FromSeconds(1));

        // Act
        time.Advance(TimeSpan.FromSeconds(6));
        await time.WaitForTimerAsync(TimeSpan.FromSeconds(1));
        var expired = Assert.Single(fixture.Status.GetSnapshots(), snapshot => snapshot.VirtualDeviceId == device.Id);
        Assert.Equal(VirtualOutputRuntimeState.Failed, expired.State);
        var renewed = Assert.IsType<VirtualOutputExternalEnrollment>(fixture.Enrollments.Take(device.Id));
        var renewedContext = fixture.Register(renewed.Bootstrap);
        fixture.Status.Update(device.Id, current => current! with { State = VirtualOutputRuntimeState.Healthy, ProtocolVersion = VirtualOutputProtocol.MaximumVersion });
        time.Advance(TimeSpan.FromSeconds(1));
        await time.WaitForTimerAsync(TimeSpan.FromSeconds(1));

        // Assert
        Assert.Contains("lease has expired", expired.LastFailure, StringComparison.Ordinal);
        Assert.Equal(0, expired.RestartCount);
        Assert.True(renewed.Bootstrap.Generation > bootstrap.Generation);
        Assert.False(fixture.Authority.Renew(new VirtualOutputLeaseRenewalRequest { Session = context, Ready = true }).Accepted);
        Assert.True(fixture.Authority.Authorize(renewedContext, VirtualOutputCapability.ReportHealth).Authorized);
        Assert.Equal(VirtualOutputRuntimeState.Healthy, Assert.Single(fixture.Status.GetSnapshots(), snapshot => snapshot.VirtualDeviceId == device.Id).State);
        Assert.Equal(VirtualOutputRuntimeState.Healthy, Assert.Single(fixture.Status.GetSnapshots(), snapshot => snapshot.VirtualDeviceId == healthy.Id).State);
        Assert.True(fixture.Authority.Authorize(healthyContext, VirtualOutputCapability.ReportHealth).Authorized);
        Assert.Empty(fixture.Processes);
        Assert.Equal(2, fixture.ActivationAttempts);
    }

    /// <summary>Verifies missed registration is recovered without settings changes and cannot exceed the restart budget.</summary>
    [Fact]
    public async Task StartAsync_UnregisteredWorker_StopsRetriesAndQuarantines()
    {
        // Arrange
        var time = new ManualTimeProvider();
        await using var fixture = new OwnershipFixture(time, TimeSpan.FromSeconds(10));
        var device = fixture.CreateDevice(VirtualOutputExecutionMode.ManagedProcess);
        fixture.Configuration = fixture.Configuration with
        {
            VirtualDevices = [device],
            VirtualOutputWorkerPolicies = [Assert.Single(fixture.Configuration.VirtualOutputWorkerPolicies) with { MaximumRestarts = 1, DrainTimeoutSeconds = 60 }]
        };
        await fixture.StartAsync();
        await time.WaitForTimerAsync(TimeSpan.FromSeconds(1));
        var first = Assert.Single(fixture.Processes);

        // Act
        time.Advance(TimeSpan.FromSeconds(10));
        await time.WaitForTimerAsync(TimeSpan.FromSeconds(1));
        var atDeadline = Assert.Single(fixture.Status.GetSnapshots());
        var aliveAtDeadline = !first.Completion.IsCompleted;
        time.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => first.Completion.IsCompleted && fixture.Status.GetSnapshots().Single().RestartCount == 1,
            TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        var retrying = Assert.Single(fixture.Status.GetSnapshots());
        await time.WaitForTimerAsync(TimeSpan.FromSeconds(1));
        time.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => fixture.Processes.Count == 2 && fixture.Status.GetSnapshots().Single().State == VirtualOutputRuntimeState.Starting,
            TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await time.WaitForTimerAsync(TimeSpan.FromSeconds(1));
        var second = fixture.Processes[1];
        time.Advance(TimeSpan.FromSeconds(11));
        await WaitUntilAsync(() => second.Completion.IsCompleted && fixture.Status.GetSnapshots().Single().RestartCount == 2,
            TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await time.WaitForTimerAsync(TimeSpan.FromSeconds(1));
        time.Advance(TimeSpan.FromSeconds(30));
        await time.WaitForTimerAsync(TimeSpan.FromSeconds(1));

        // Assert
        Assert.True(aliveAtDeadline);
        Assert.Equal(VirtualOutputRuntimeState.Starting, atDeadline.State);
        Assert.Equal(VirtualOutputRuntimeState.Failed, retrying.State);
        Assert.Contains("did not register", retrying.LastFailure, StringComparison.Ordinal);
        Assert.Contains("Retrying in 1 seconds", retrying.LastFailure, StringComparison.Ordinal);
        var quarantined = Assert.Single(fixture.Status.GetSnapshots());
        Assert.Equal(VirtualOutputRuntimeState.Failed, quarantined.State);
        Assert.Equal(2, quarantined.RestartCount);
        Assert.Contains("exceeded its restart budget", quarantined.LastFailure, StringComparison.Ordinal);
        Assert.Equal(2, fixture.ActivationAttempts);
        await first.Received(1).StopAsync(TimeSpan.Zero, Arg.Any<CancellationToken>());
        await second.Received(1).StopAsync(TimeSpan.Zero, Arg.Any<CancellationToken>());
    }

    /// <summary>Verifies an expired registered worker is stopped even if the supervisor did not observe its live lease.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StartAsync_ExpiredWorkerLease_RevokesStopsAndFails(bool observeLiveLease)
    {
        // Arrange
        var time = new ManualTimeProvider();
        await using var fixture = new OwnershipFixture(time);
        var device = fixture.CreateDevice(VirtualOutputExecutionMode.ManagedProcess);
        fixture.Configuration = fixture.Configuration with { VirtualDevices = [device] };
        await fixture.StartAsync();
        await time.WaitForTimerAsync(TimeSpan.FromSeconds(1));
        var process = Assert.Single(fixture.Processes);
        var context = fixture.Register(fixture.GetBootstrap(VirtualOutputExecutionMode.ManagedProcess, device.Id));
        fixture.Status.Update(device.Id, current => current! with
        {
            State = VirtualOutputRuntimeState.Healthy,
            ProtocolVersion = VirtualOutputProtocol.MaximumVersion
        });
        if (observeLiveLease)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            await time.WaitForTimerAsync(TimeSpan.FromSeconds(1));
        }

        // Act
        time.Advance(TimeSpan.FromSeconds(16));
        await WaitUntilAsync(() => process.Completion.IsCompleted && fixture.Status.GetSnapshots().Single().State == VirtualOutputRuntimeState.Failed,
            TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        // Assert
        var snapshot = Assert.Single(fixture.Status.GetSnapshots());
        Assert.Equal(1, snapshot.RestartCount);
        Assert.Contains("lease has expired", snapshot.LastFailure, StringComparison.Ordinal);
        Assert.False(fixture.Authority.Authorize(context, VirtualOutputCapability.ReportHealth).Authorized);
        Assert.Single(fixture.Processes);
        await process.Received(1).StopAsync(TimeSpan.Zero, Arg.Any<CancellationToken>());
        await process.Received().DisposeAsync();
    }

    /// <summary>Verifies healthy renewal preserves a managed worker after its bootstrap deadline has passed.</summary>
    [Fact]
    public async Task StartAsync_RenewingWorker_PreservesProcessBeyondBootstrapDeadline()
    {
        // Arrange
        var time = new ManualTimeProvider();
        await using var fixture = new OwnershipFixture(time);
        var device = fixture.CreateDevice(VirtualOutputExecutionMode.ManagedProcess);
        fixture.Configuration = fixture.Configuration with { VirtualDevices = [device] };
        await fixture.StartAsync();
        await time.WaitForTimerAsync(TimeSpan.FromSeconds(1));
        var process = Assert.Single(fixture.Processes);
        var context = fixture.Register(fixture.GetBootstrap(VirtualOutputExecutionMode.ManagedProcess, device.Id));
        fixture.Status.Update(device.Id, current => current! with { State = VirtualOutputRuntimeState.Healthy, ProtocolVersion = VirtualOutputProtocol.MaximumVersion });

        // Act
        for (var renewal = 0; renewal < 7; renewal++)
        {
            time.Advance(TimeSpan.FromSeconds(10));
            Assert.True(fixture.Authority.Renew(new VirtualOutputLeaseRenewalRequest { Session = context, Ready = true }).Accepted);
            await time.WaitForTimerAsync(TimeSpan.FromSeconds(1));
        }

        // Assert
        Assert.Equal(1, fixture.ActivationAttempts);
        Assert.False(process.Completion.IsCompleted);
        Assert.Equal(VirtualOutputRuntimeState.Healthy, Assert.Single(fixture.Status.GetSnapshots()).State);
        Assert.Equal(0, Assert.Single(fixture.Status.GetSnapshots()).RestartCount);
        await process.DidNotReceive().StopAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        await process.DidNotReceive().DisposeAsync();
    }

    /// <summary>Verifies worker registration status reported during launch is not overwritten by process ownership.</summary>
    [Fact]
    public async Task StartAsync_RegistersDuringLaunch_PreservesHealthyStatus()
    {
        // Arrange
        await using var fixture = new OwnershipFixture { RegisterDuringLaunch = true };
        var device = fixture.CreateDevice(VirtualOutputExecutionMode.ManagedProcess);
        fixture.Configuration = fixture.Configuration with { VirtualDevices = [device] };

        // Act
        await fixture.ReconcileAsync();

        // Assert
        var snapshot = Assert.Single(fixture.Status.GetSnapshots());
        Assert.Equal(VirtualOutputRuntimeState.Healthy, snapshot.State);
        Assert.Equal(VirtualOutputProtocol.MaximumVersion, snapshot.ProtocolVersion);
        Assert.NotNull(snapshot.ProcessId);
    }

    /// <summary>Verifies stalled bootstrap delivery is canceled by its deadline and shutdown still cleans the launched child.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartAsync_BlockedBootstrap_DeadlineOrShutdownCancelsLaunch(bool stopHost)
    {
        // Arrange
        var time = new ManualTimeProvider();
        await using var fixture = new OwnershipFixture(time, TimeSpan.FromSeconds(10)) { BlockLaunch = true };
        var device = fixture.CreateDevice(VirtualOutputExecutionMode.ManagedProcess);
        fixture.Configuration = fixture.Configuration with { VirtualDevices = [device] };
        await fixture.StartAsync();
        await time.WaitForTimerAsync(TimeSpan.FromSeconds(10));
        var process = Assert.Single(fixture.Processes);
        var bootstrap = fixture.GetBootstrap(VirtualOutputExecutionMode.ManagedProcess, device.Id);

        // Act
        if (stopHost)
        {
            await fixture.StopAsync().WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        }
        else
        {
            time.Advance(TimeSpan.FromSeconds(10));
            await WaitUntilAsync(() => process.Completion.IsCompleted && fixture.Status.GetSnapshots().Single().State == VirtualOutputRuntimeState.Failed,
                TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        }

        // Assert
        Assert.True(process.Completion.IsCompleted);
        await process.Received().DisposeAsync();
        Assert.False(fixture.Authority.Register(new VirtualOutputWorkerRegistrationRequest
        {
            VirtualDeviceId = device.Id,
            WorkerId = bootstrap.WorkerId,
            Generation = bootstrap.Generation,
            BootstrapToken = bootstrap.BootstrapToken,
            MinimumProtocolVersion = VirtualOutputProtocol.MinimumVersion,
            MaximumProtocolVersion = VirtualOutputProtocol.MaximumVersion
        }).Accepted);
        if (!stopHost)
        {
            Assert.Contains("bootstrap delivery", Assert.Single(fixture.Status.GetSnapshots()).LastFailure, StringComparison.Ordinal);
            Assert.Equal(1, Assert.Single(fixture.Status.GetSnapshots()).RestartCount);
        }
    }

    /// <summary>Verifies externally supervised workers retain enrollment renewal instead of managed recovery.</summary>
    [Fact]
    public async Task StartAsync_ExternalWorkerLeaseExpires_PreservesExternalOwnership()
    {
        // Arrange
        var time = new ManualTimeProvider();
        await using var fixture = new OwnershipFixture(time);
        var device = fixture.CreateDevice(VirtualOutputExecutionMode.ExternalProcess);
        fixture.Configuration = fixture.Configuration with { VirtualDevices = [device] };
        await fixture.StartAsync();
        await time.WaitForTimerAsync(TimeSpan.FromSeconds(1));
        var bootstrap = fixture.GetBootstrap(VirtualOutputExecutionMode.ExternalProcess, device.Id);
        fixture.Register(bootstrap);

        // Act
        time.Advance(TimeSpan.FromSeconds(61));
        await time.WaitForTimerAsync(TimeSpan.FromSeconds(1));
        var renewed = fixture.Enrollments.Take(device.Id);

        // Assert
        Assert.Empty(fixture.Processes);
        Assert.Equal(1, fixture.ActivationAttempts);
        Assert.NotNull(renewed);
        Assert.True(renewed.Bootstrap.Generation > bootstrap.Generation);
        Assert.Equal(VirtualOutputRuntimeState.Starting, Assert.Single(fixture.Status.GetSnapshots()).State);
        Assert.Equal(0, Assert.Single(fixture.Status.GetSnapshots()).RestartCount);
    }

    /// <summary>Verifies shutdown cancels liveness timers and prevents later recovery of a stopped managed worker.</summary>
    [Fact]
    public async Task StopAsync_LivenessWait_CancelsTimerWithoutRestarting()
    {
        // Arrange
        var time = new ManualTimeProvider();
        await using var fixture = new OwnershipFixture(time);
        var device = fixture.CreateDevice(VirtualOutputExecutionMode.ManagedProcess);
        fixture.Configuration = fixture.Configuration with { VirtualDevices = [device] };
        await fixture.StartAsync();
        await time.WaitForTimerAsync(TimeSpan.FromSeconds(1));
        var process = Assert.Single(fixture.Processes);

        // Act
        await fixture.StopAsync().WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromMinutes(2));

        // Assert
        Assert.Equal(0, time.TimerCount);
        Assert.True(process.Completion.IsCompleted);
        Assert.Single(fixture.Processes);
        Assert.Equal(1, fixture.ActivationAttempts);
        Assert.DoesNotContain(fixture.Status.GetSnapshots(), snapshot => snapshot.State == VirtualOutputRuntimeState.Failed);
    }

    /// <summary>Verifies stalled drains share lifecycle time instead of multiplying the wait by output count.</summary>
    /// <param name="workerCount">The number of independently hosted outputs.</param>
    /// <param name="shutdown">Whether to exercise host shutdown rather than configuration reconciliation.</param>
    [Theory]
    [InlineData(1, false)]
    [InlineData(8, false)]
    [InlineData(16, false)]
    [InlineData(1, true)]
    [InlineData(8, true)]
    [InlineData(16, true)]
    public async Task Lifecycle_MultipleStalledDrains_DoesNotSerializeGracePeriods(int workerCount, bool shutdown)
    {
        // Arrange
        var time = new ManualTimeProvider();
        await using var fixture = new OwnershipFixture(time) { RegisterDuringLaunch = true };
        var devices = Enumerable.Range(0, workerCount).Select(_ => fixture.CreateDevice(VirtualOutputExecutionMode.ManagedProcess)).ToArray();
        fixture.Configuration = fixture.Configuration with
        {
            VirtualDevices = devices,
            VirtualOutputWorkerPolicies = [Assert.Single(fixture.Configuration.VirtualOutputWorkerPolicies) with { DrainTimeoutSeconds = 2, LeaseGracePeriodSeconds = 60 }]
        };
        await fixture.StartAsync();
        await WaitUntilAsync(() => fixture.Status.GetSnapshots().Count(snapshot => snapshot.State == VirtualOutputRuntimeState.Healthy) == workerCount,
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Act
        var startedAt = time.GetUtcNow();
        var processes = fixture.Processes.ToArray();
        Task? lifecycle = null;
        var batchSize = shutdown ? workerCount : Math.Min(workerCount, 4);
        try
        {
            if (shutdown)
            {
                lifecycle = fixture.StopAsync();
            }
            else
            {
                fixture.Configuration = fixture.Configuration with { VirtualDevices = [] };
                lifecycle = fixture.ReconcileAsync();
            }
            for (var completed = 0; completed < workerCount; completed += batchSize)
            {
                var draining = Math.Min(batchSize, workerCount - completed);
                await time.WaitForTimerAsync(TimeSpan.FromSeconds(2), draining);
                Assert.Equal(draining, fixture.Status.GetSnapshots().Count(snapshot => snapshot.State == VirtualOutputRuntimeState.Draining));
                Assert.Equal(completed, processes.Count(process => process.Completion.IsCompleted));
                time.Advance(TimeSpan.FromSeconds(1));
                Assert.False(lifecycle.IsCompleted);
                Assert.Equal(completed, processes.Count(process => process.Completion.IsCompleted));
                time.Advance(TimeSpan.FromSeconds(1));
                await WaitUntilAsync(() => processes.Count(process => process.Completion.IsCompleted) == completed + draining,
                    TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            }
            await lifecycle.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

            // Assert
            Assert.All(processes, process => Assert.True(process.Completion.IsCompletedSuccessfully));
            Assert.Equal(TimeSpan.FromSeconds(2 * (int)Math.Ceiling(workerCount / (double)batchSize)), time.GetUtcNow() - startedAt);
        }
        finally
        {
            foreach (var process in processes)
            {
                fixture.ExitProcess(process);
            }
            if (lifecycle is not null)
            {
                await lifecycle.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            }
        }
    }

    /// <summary>Verifies cancellation of reconciliation cannot strand a managed child removed from active ownership.</summary>
    [Theory]
    [InlineData("remove")]
    [InlineData("disable")]
    [InlineData("replace")]
    public async Task StopAsync_DuringWorkerDrain_CompletesChildCleanup(string change)
    {
        // Arrange
        await using var fixture = new OwnershipFixture();
        var device = fixture.CreateDevice(VirtualOutputExecutionMode.ManagedProcess);
        fixture.Configuration = fixture.Configuration with
        {
            VirtualDevices = [device],
            VirtualOutputWorkerPolicies = [Assert.Single(fixture.Configuration.VirtualOutputWorkerPolicies) with { DrainTimeoutSeconds = 60 }]
        };
        await fixture.StartAsync();
        await WaitUntilAsync(() => fixture.Status.GetSnapshots().Any(snapshot => snapshot.State == VirtualOutputRuntimeState.Starting && snapshot.ProcessId is not null),
            TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        var process = Assert.Single(fixture.Processes);
        fixture.Register(fixture.GetBootstrap(VirtualOutputExecutionMode.ManagedProcess, device.Id));
        fixture.Configuration = fixture.Configuration with
        {
            VirtualDevices = change switch
            {
                "remove" => [],
                "disable" => [device with { Enabled = false }],
                _ => [device with { Name = "Replacement" }]
            }
        };
        fixture.SignalSettingsChanged();
        await WaitUntilAsync(() => fixture.Status.GetSnapshots().Any(snapshot => snapshot.State == VirtualOutputRuntimeState.Draining),
            TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        // Act
        await fixture.StopAsync().WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        // Assert
        Assert.True(process.Completion.IsCompletedSuccessfully);
        await process.Received().DisposeAsync();
        Assert.Single(fixture.Processes);
    }

    /// <summary>Verifies forced-stop failures still dispose an owned child before surfacing the failure.</summary>
    [Fact]
    public async Task Reconcile_ForcedStopFails_DisposesChild()
    {
        // Arrange
        await using var fixture = new OwnershipFixture();
        var device = fixture.CreateDevice(VirtualOutputExecutionMode.ManagedProcess);
        fixture.Configuration = fixture.Configuration with { VirtualDevices = [device] };
        await fixture.ReconcileAsync();
        var process = Assert.Single(fixture.Processes);
        process.StopAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(Task.FromException(new IOException("Synthetic stop failure.")));
        fixture.Configuration = fixture.Configuration with { VirtualDevices = [] };

        // Act
        await Assert.ThrowsAsync<IOException>(() => fixture.ReconcileAsync());

        // Assert
        Assert.True(process.Completion.IsCompletedSuccessfully);
        await process.Received().DisposeAsync();
    }

    /// <summary>Verifies direct reconciliation cancellation still propagates after its removed child is disposed.</summary>
    [Fact]
    public async Task Reconcile_CanceledDrain_CleansChildAndPropagatesCancellation()
    {
        // Arrange
        await using var fixture = new OwnershipFixture();
        var device = fixture.CreateDevice(VirtualOutputExecutionMode.ManagedProcess);
        fixture.Configuration = fixture.Configuration with
        {
            VirtualDevices = [device],
            VirtualOutputWorkerPolicies = [Assert.Single(fixture.Configuration.VirtualOutputWorkerPolicies) with { DrainTimeoutSeconds = 60 }]
        };
        await fixture.ReconcileAsync();
        var process = Assert.Single(fixture.Processes);
        fixture.Configuration = fixture.Configuration with { VirtualDevices = [] };
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var reconcile = fixture.ReconcileAsync(cancellation.Token);
        await WaitUntilAsync(() => fixture.Status.GetSnapshots().Any(snapshot => snapshot.State == VirtualOutputRuntimeState.Draining),
            TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        // Act
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reconcile.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));

        // Assert
        Assert.True(process.Completion.IsCompletedSuccessfully);
        await process.Received().DisposeAsync();
    }

    /// <summary>Verifies a gracefully exiting child is disposed without invoking the forced-stop path.</summary>
    [Fact]
    public async Task Reconcile_ChildExitsDuringDrain_DoesNotForceStop()
    {
        // Arrange
        await using var fixture = new OwnershipFixture();
        var device = fixture.CreateDevice(VirtualOutputExecutionMode.ManagedProcess);
        fixture.Configuration = fixture.Configuration with
        {
            VirtualDevices = [device],
            VirtualOutputWorkerPolicies = [Assert.Single(fixture.Configuration.VirtualOutputWorkerPolicies) with { DrainTimeoutSeconds = 60 }]
        };
        await fixture.ReconcileAsync();
        var process = Assert.Single(fixture.Processes);
        fixture.Configuration = fixture.Configuration with { VirtualDevices = [] };
        var reconcile = fixture.ReconcileAsync();
        await WaitUntilAsync(() => fixture.Status.GetSnapshots().Any(snapshot => snapshot.State == VirtualOutputRuntimeState.Draining),
            TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        // Act
        fixture.ExitProcess(process);
        await reconcile.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        // Assert
        await process.DidNotReceive().StopAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        await process.Received().DisposeAsync();
        Assert.Empty(fixture.Status.GetSnapshots());
    }

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
        var time = new ManualTimeProvider();
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
            NullLogger<VirtualOutputSupervisor>.Instance,
            time);

        // Act
        await supervisor.StartAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => status.GetSnapshots().Any(snapshot => snapshot.State == VirtualOutputRuntimeState.Failed && snapshot.RestartCount == 1),
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => status.GetSnapshots().Any(snapshot => snapshot.State == VirtualOutputRuntimeState.Failed && snapshot.RestartCount == 2),
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
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
        private readonly ILineupSettingsService _settings;
        private readonly ConcurrentDictionary<IVirtualOutputWorkerProcess, TaskCompletionSource<int>> _completions = new();
        private readonly ConcurrentDictionary<VirtualDeviceId, VirtualOutputWorkerBootstrap> _bootstraps = new();
        private readonly ConcurrentDictionary<VirtualDeviceId, IVirtualOutputWorkerProcess> _processesByDevice = new();
        private readonly ConcurrentDictionary<VirtualDeviceId, VirtualOutputSessionContext> _sessions = new();
        private readonly TaskCompletionSource _launchRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _activationAttempts;

        internal OwnershipFixture(TimeProvider? timeProvider = null, TimeSpan? bootstrapLifetime = null, IVirtualOutputCertificateResolver? certificateResolver = null)
        {
            Authority = new VirtualOutputSessionAuthority(timeProvider);
            Enrollments = new VirtualOutputExternalEnrollmentStore(timeProvider);
            var settings = Substitute.For<ILineupSettingsService>();
            _settings = settings;
            settings.Settings.Returns(_ => new LineupSettings { Streaming = Configuration });
            var catalog = Substitute.For<IVirtualOutputWorkerCatalog>();
            catalog.ResolveAsync("ownership-test", Arg.Any<CancellationToken>()).Returns(_ =>
            {
                Interlocked.Increment(ref _activationAttempts);
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
                            SettingsDescriptor = new Lineup.Core.Common.Configuration.AdapterSettingsDescriptor { AdapterKind = "ownership-test" }
                        }
                    });
            });
            var launcher = Substitute.For<IVirtualOutputWorkerLauncher>();
            launcher.StartAsync(Arg.Any<VirtualOutputWorkerPackage>(), Arg.Any<VirtualOutputWorkerBootstrap>(), Arg.Any<CancellationToken>()).Returns(async call =>
            {
                var bootstrap = call.Arg<VirtualOutputWorkerBootstrap>();
                _bootstraps[bootstrap.VirtualDevice.Id] = bootstrap;
                var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                var process = Substitute.For<IVirtualOutputWorkerProcess>();
                _completions[process] = completion;
                _processesByDevice[bootstrap.VirtualDevice.Id] = process;
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
                lock (Processes)
                {
                    Processes.Add(process);
                }
                if (RegisterDuringLaunch)
                {
                    Register(bootstrap);
                    Status.Update(bootstrap.VirtualDevice.Id, current => current! with { State = VirtualOutputRuntimeState.Healthy, ProtocolVersion = VirtualOutputProtocol.MaximumVersion });
                }
                if (BlockLaunch)
                {
                    try
                    {
                        await _launchRelease.Task.WaitAsync(call.Arg<CancellationToken>());
                    }
                    catch (OperationCanceledException)
                    {
                        await process.DisposeAsync();
                        throw;
                    }
                }
                return process;
            });
            _supervisor = new VirtualOutputSupervisor(
                settings,
                new VirtualOutputRuntimeOptions
                {
                    WorkerPackageRoot = "unused",
                    IpcEndpoint = new VirtualOutputIpcEndpoint { Transport = VirtualOutputIpcTransport.NamedPipe, Address = "unused" },
                    BootstrapLifetime = bootstrapLifetime ?? TimeSpan.FromMinutes(1)
                },
                catalog,
                launcher,
                certificateResolver ?? Substitute.For<IVirtualOutputCertificateResolver>(),
                Authority,
                Status,
                Enrollments,
                Logger,
                timeProvider);
        }

        /// <summary>Gets captured output lifecycle diagnostics.</summary>
        internal ILogger<VirtualOutputSupervisor> Logger { get; } = Substitute.For<ILogger<VirtualOutputSupervisor>>();

        /// <summary>Gets whether hosted reconciliation remains active.</summary>
        internal bool IsRunning => _supervisor.ExecuteTask is { IsCompleted: false };

        internal StreamingConfiguration Configuration { get; set; } = new()
        {
            VirtualOutputWorkerPolicies = [new VirtualOutputWorkerPolicyDefinition { Key = "ownership", Name = "Ownership", MaximumRestarts = 0, DrainTimeoutSeconds = 0 }]
        };

        internal VirtualOutputSessionAuthority Authority { get; }

        internal VirtualOutputRuntimeStatus Status { get; } = new();

        internal VirtualOutputExternalEnrollmentStore Enrollments { get; }

        internal List<IVirtualOutputWorkerProcess> Processes { get; } = [];

        internal bool FailActivation { get; set; }

        /// <summary>Gets or sets whether the controlled launcher stalls until cancellation.</summary>
        internal bool BlockLaunch { get; set; }

        /// <summary>Gets or sets whether the controlled worker reports readiness before the launcher returns.</summary>
        internal bool RegisterDuringLaunch { get; set; }

        internal int ActivationAttempts => Volatile.Read(ref _activationAttempts);

        /// <summary>Returns the current controlled managed process for one output.</summary>
        internal IVirtualOutputWorkerProcess? GetProcess(VirtualDeviceId deviceId) => _processesByDevice.GetValueOrDefault(deviceId);

        /// <summary>Allows controlled blocked launches to finish successfully.</summary>
        internal void ReleaseLaunches() => _launchRelease.TrySetResult();

        /// <summary>Returns the last registered managed session for a controlled output.</summary>
        internal VirtualOutputSessionContext GetSession(VirtualDeviceId deviceId) => _sessions[deviceId];

        /// <summary>Retries one failed output through the operator recovery contract.</summary>
        internal Task<VirtualOutputWorkerRecoveryResult> RetryAsync(VirtualDeviceId deviceId, CancellationToken? cancellationToken = null) =>
            _supervisor.RetryAsync(deviceId, cancellationToken ?? TestContext.Current.CancellationToken);

        /// <summary>Runs a controlled transition through the production per-output synchronization.</summary>
        internal Task TransitionAsync(VirtualDeviceId deviceId, Func<CancellationToken, Task> transition)
        {
            var method = typeof(VirtualOutputSupervisor).GetMethod("RunTransitionAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);
            return Assert.IsAssignableFrom<Task>(method.Invoke(_supervisor, [deviceId, transition, TestContext.Current.CancellationToken]));
        }

        /// <summary>Starts the hosted reconciliation loop.</summary>
        internal Task StartAsync() => _supervisor.StartAsync(TestContext.Current.CancellationToken);

        /// <summary>Stops the hosted loop and waits for its final cleanup.</summary>
        internal Task StopAsync(CancellationToken? cancellationToken = null) => _supervisor.StopAsync(cancellationToken ?? TestContext.Current.CancellationToken);

        /// <summary>Notifies the hosted loop of updated desired topology.</summary>
        internal void SignalSettingsChanged() => _settings.OnSettingsChanged += Raise.Event<Action>();

        /// <summary>Completes a controlled child without a supervisor stop request.</summary>
        internal void ExitProcess(IVirtualOutputWorkerProcess process) => _completions[process].TrySetResult(0);

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
                return _bootstraps[deviceId];
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
            var session = new VirtualOutputSessionContext { SessionId = registration.SessionId!.Value, Generation = bootstrap.Generation };
            _sessions[bootstrap.VirtualDevice.Id] = session;
            return session;
        }

        internal async Task ReconcileAsync(CancellationToken? cancellationToken = null)
        {
            var token = cancellationToken ?? TestContext.Current.CancellationToken;
            var reconcile = typeof(VirtualOutputSupervisor).GetMethod("ReconcileAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(reconcile);
            await Assert.IsAssignableFrom<Task>(reconcile.Invoke(_supervisor, [token]));
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            Configuration = Configuration with { VirtualDevices = [] };
            await ReconcileAsync();
            await _supervisor.StopAsync(CancellationToken.None);
            foreach (var process in Processes)
            {
                await process.DisposeAsync();
            }
            _supervisor.Dispose();
            Authority.Dispose();
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _utcNow = new(2026, 11, 6, 0, 0, 0, TimeSpan.Zero);

        /// <summary>Gets the number of pending one-shot timers.</summary>
        public int TimerCount
        {
            get
            {
                lock (_timers)
                {
                    return _timers.Count;
                }
            }
        }

        /// <inheritdoc />
        public override DateTimeOffset GetUtcNow()
        {
            lock (_timers)
            {
                return _utcNow;
            }
        }

        /// <inheritdoc />
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }

        /// <summary>Advances the clock and fires elapsed one-shot timers.</summary>
        public void Advance(TimeSpan duration)
        {
            ManualTimer[] elapsed;
            lock (_timers)
            {
                _utcNow += duration;
                elapsed = _timers.Where(timer => timer.DueUtc <= _utcNow).ToArray();
                foreach (var timer in elapsed)
                {
                    _timers.Remove(timer);
                }
            }
            foreach (var timer in elapsed)
            {
                timer.Callback(timer.State);
            }
        }

        /// <summary>Waits until the supervisor has installed the expected number of timers at the specified deadline.</summary>
        public Task WaitForTimerAsync(TimeSpan dueTime, int count = 1) => WaitUntilAsync(() =>
        {
            lock (_timers)
            {
                return _timers.Count(timer => timer.DueUtc == _utcNow + dueTime) >= count;
            }
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
        {
            /// <summary>Gets the scheduled callback.</summary>
            public TimerCallback Callback { get; } = callback;

            /// <summary>Gets the callback state.</summary>
            public object? State { get; } = state;

            /// <summary>Gets the scheduled expiration.</summary>
            public DateTimeOffset DueUtc { get; private set; }

            /// <inheritdoc />
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                Assert.Equal(Timeout.InfiniteTimeSpan, period);
                lock (owner._timers)
                {
                    owner._timers.Remove(this);
                    if (dueTime != Timeout.InfiniteTimeSpan)
                    {
                        DueUtc = owner._utcNow + dueTime;
                        owner._timers.Add(this);
                    }
                    return true;
                }
            }

            /// <inheritdoc />
            public void Dispose()
            {
                lock (owner._timers)
                {
                    owner._timers.Remove(this);
                }
            }

            /// <inheritdoc />
            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
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
