using System.Text.Json;
using Lineup.Common.Streaming;
using Lineup.Core.Settings;
using Lineup.VirtualOutputs.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Lineup.VirtualOutputs.Runtime.Tests;

/// <summary>Verifies supervisor activation and external re-enrollment honor the complete per-worker lease lifetime.</summary>
public sealed class VirtualOutputLeasePolicyTests
{
    /// <summary>Verifies registration, renewal, and expiry use policy seconds rather than the global lease option.</summary>
    [Theory]
    [InlineData(VirtualOutputExecutionMode.ManagedProcess, 1)]
    [InlineData(VirtualOutputExecutionMode.ManagedProcess, 15)]
    [InlineData(VirtualOutputExecutionMode.ManagedProcess, 300)]
    [InlineData(VirtualOutputExecutionMode.ExternalProcess, 1)]
    [InlineData(VirtualOutputExecutionMode.ExternalProcess, 15)]
    [InlineData(VirtualOutputExecutionMode.ExternalProcess, 300)]
    public async Task Activate_PolicyLease_AppliesToRegistrationRenewalAndExpiry(VirtualOutputExecutionMode mode, int seconds)
    {
        // Arrange
        await using var fixture = new Fixture(mode, seconds);
        var bootstrap = await fixture.StartAsync();

        // Act
        var registration = fixture.Register(bootstrap);
        fixture.Time.Advance(TimeSpan.FromTicks(1));
        var context = new VirtualOutputSessionContext { SessionId = registration.SessionId!.Value, Generation = bootstrap.Generation };
        var renewedAt = fixture.Time.GetUtcNow();
        var renewal = fixture.Authority.Renew(new VirtualOutputLeaseRenewalRequest { Session = context });
        fixture.Time.Advance(TimeSpan.FromSeconds(seconds) - TimeSpan.FromTicks(1));
        var beforeExpiry = fixture.Authority.Authorize(context, VirtualOutputCapability.ReadManifest);
        fixture.Time.Advance(TimeSpan.FromTicks(2));
        var afterExpiry = fixture.Authority.Authorize(context, VirtualOutputCapability.ReadManifest);

        // Assert
        Assert.True(registration.Accepted);
        Assert.Equal(fixture.StartedUtc.AddSeconds(seconds), registration.LeaseExpiresUtc);
        Assert.Equal(fixture.StartedUtc.AddMinutes(1), bootstrap.BootstrapExpiresUtc);
        Assert.Equal(seconds, bootstrap.WorkerPolicy.LeaseGracePeriodSeconds);
        Assert.True(renewal.Accepted);
        Assert.Equal(renewedAt.AddSeconds(seconds), renewal.LeaseExpiresUtc);
        Assert.True(beforeExpiry.Authorized);
        Assert.False(afterExpiry.Authorized);
    }

    /// <summary>Verifies replacement external enrollment uses the same policy lease and does not replace a live session.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(15)]
    [InlineData(300)]
    public async Task ExternalEnrollment_RenewedCredentials_KeepPolicyLease(int seconds)
    {
        // Arrange
        await using var fixture = new Fixture(VirtualOutputExecutionMode.ExternalProcess, seconds);
        var original = await fixture.StartAsync();
        fixture.Time.Advance(TimeSpan.FromMinutes(2));

        // Act
        var replacement = fixture.Enrollments.Take(original.VirtualDevice.Id)!.Bootstrap;
        var registration = fixture.Register(replacement);
        var activeReplacement = fixture.Enrollments.Take(original.VirtualDevice.Id);

        // Assert
        Assert.True(replacement.Generation > original.Generation);
        Assert.NotEqual(original.BootstrapToken, replacement.BootstrapToken);
        Assert.Equal(fixture.Time.GetUtcNow().AddMinutes(1), replacement.BootstrapExpiresUtc);
        Assert.True(registration.Accepted);
        Assert.Equal(fixture.Time.GetUtcNow().AddSeconds(seconds), registration.LeaseExpiresUtc);
        Assert.Null(activeReplacement);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly VirtualOutputSupervisor _supervisor;
        private readonly TaskCompletionSource<VirtualOutputWorkerBootstrap> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<int> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly VirtualDeviceId _deviceId = VirtualDeviceId.New();
        private readonly VirtualOutputExecutionMode _mode;

        public Fixture(VirtualOutputExecutionMode mode, int seconds)
        {
            _mode = mode;
            Authority = new VirtualOutputSessionAuthority(Time);
            Enrollments = new VirtualOutputExternalEnrollmentStore(Time);
            var settings = Substitute.For<ILineupSettingsService>();
            settings.Settings.Returns(new LineupSettings
            {
                Streaming = new StreamingConfiguration
                {
                    VirtualDevices =
                    [
                        new VirtualDeviceDefinition
                        {
                            Id = _deviceId,
                            Name = "Policy lease",
                            Adapter = new AdapterConfiguration("lease-test", JsonSerializer.SerializeToElement(new { })),
                            Hosting = new VirtualOutputHostingDefinition { ExecutionMode = mode, WorkerPolicyKey = "policy" }
                        }
                    ],
                    VirtualOutputWorkerPolicies = [new VirtualOutputWorkerPolicyDefinition { Key = "policy", Name = "Policy", LeaseGracePeriodSeconds = seconds }]
                }
            });
            var catalog = Substitute.For<IVirtualOutputWorkerCatalog>();
            catalog.ResolveAsync("lease-test", Arg.Any<CancellationToken>()).Returns(new VirtualOutputWorkerPackage
            {
                PackageRoot = "unused",
                Manifest = new VirtualOutputWorkerManifest
                {
                    WorkerId = "lease-test",
                    Version = "1.0.0",
                    Publisher = "Test",
                    ExecutablePath = "unused",
                    ExecutableSha256 = new string('0', 64),
                    MinimumLineupVersion = "1.0.0",
                    SettingsDescriptor = new Lineup.Common.Configuration.AdapterSettingsDescriptor { AdapterKind = "lease-test" }
                }
            });
            var process = Substitute.For<IVirtualOutputWorkerProcess>();
            process.Completion.Returns(_completion.Task);
            var launcher = Substitute.For<IVirtualOutputWorkerLauncher>();
            launcher.StartAsync(Arg.Any<VirtualOutputWorkerPackage>(), Arg.Any<VirtualOutputWorkerBootstrap>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    _started.TrySetResult(call.Arg<VirtualOutputWorkerBootstrap>());
                    return process;
                });
            _supervisor = new VirtualOutputSupervisor(
                settings,
                new VirtualOutputRuntimeOptions
                {
                    WorkerPackageRoot = "unused",
                    IpcEndpoint = new VirtualOutputIpcEndpoint { Transport = VirtualOutputIpcTransport.NamedPipe, Address = "unused" },
                    BootstrapLifetime = TimeSpan.FromMinutes(1),
                    LeaseLifetime = TimeSpan.FromSeconds(42)
                },
                catalog,
                launcher,
                Substitute.For<IVirtualOutputCertificateResolver>(),
                Authority,
                new VirtualOutputRuntimeStatus(),
                Enrollments,
                NullLogger<VirtualOutputSupervisor>.Instance,
                Time);
        }

        public ManualTimeProvider Time { get; } = new();

        public DateTimeOffset StartedUtc => new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public VirtualOutputSessionAuthority Authority { get; }

        public VirtualOutputExternalEnrollmentStore Enrollments { get; }

        public async Task<VirtualOutputWorkerBootstrap> StartAsync()
        {
            await _supervisor.StartAsync(TestContext.Current.CancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            if (_mode == VirtualOutputExecutionMode.ManagedProcess)
            {
                return await _started.Task.WaitAsync(timeout.Token);
            }
            while (true)
            {
                if (Enrollments.Take(_deviceId) is { } enrollment)
                {
                    return enrollment.Bootstrap;
                }
                await Task.Delay(10, timeout.Token);
            }
        }

        public VirtualOutputWorkerRegistrationResponse Register(VirtualOutputWorkerBootstrap bootstrap) =>
            Authority.Register(new VirtualOutputWorkerRegistrationRequest
            {
                VirtualDeviceId = bootstrap.VirtualDevice.Id,
                WorkerId = bootstrap.WorkerId,
                Generation = bootstrap.Generation,
                BootstrapToken = bootstrap.BootstrapToken,
                MinimumProtocolVersion = VirtualOutputProtocol.MinimumVersion,
                MaximumProtocolVersion = VirtualOutputProtocol.MaximumVersion
            });

        public async ValueTask DisposeAsync()
        {
            var stopping = _supervisor.StopAsync(CancellationToken.None);
            _completion.TrySetResult(0);
            await stopping;
            _supervisor.Dispose();
            Authority.Dispose();
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _utcTicks = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).UtcTicks;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);

        public void Advance(TimeSpan amount) => Interlocked.Add(ref _utcTicks, amount.Ticks);
    }
}
