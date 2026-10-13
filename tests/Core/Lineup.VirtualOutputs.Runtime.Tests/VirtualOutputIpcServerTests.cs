using System.Text.Json;
using Lineup.Common.Streaming;
using Lineup.Core.Settings;
using Lineup.VirtualOutputs.Hosting;
using Lineup.VirtualOutputs.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Lineup.VirtualOutputs.Runtime.Tests;

/// <summary>Verifies real local IPC framing, registration, authorization context, and typed broker dispatch.</summary>
public sealed class VirtualOutputIpcServerTests
{
    /// <summary>Verifies a worker can register and request its manifest over the platform local transport.</summary>
    [Fact]
    public async Task Client_RegisterAndGetManifest_RoundTripsOverLocalIpc()
    {
        // Arrange
        var endpoint = CreateEndpoint();
        using var authority = new VirtualOutputSessionAuthority();
        var deviceId = VirtualDeviceId.New();
        var ticket = authority.CreateActivation(
            deviceId,
            "test-worker",
            new HashSet<VirtualOutputCapability>
            {
                VirtualOutputCapability.ReadManifest,
                VirtualOutputCapability.ReportHealth
            },
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(1));
        var broker = Substitute.For<IVirtualOutputBroker>();
        broker.GetManifestAsync(Arg.Any<VirtualOutputManifestRequest>(), Arg.Any<CancellationToken>())
            .Returns(new VirtualOutputManifest
            {
                VirtualDeviceId = deviceId,
                Name = "Isolated output",
                TunerCount = 4
            });
        var status = new VirtualOutputRuntimeStatus();
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
                        Name = "Isolated output",
                        Adapter = new AdapterConfiguration("test", JsonSerializer.SerializeToElement(new { })),
                        Hosting = new VirtualOutputHostingDefinition
                        {
                            WorkerPolicyKey = "default"
                        }
                    }
                ],
                VirtualOutputWorkerPolicies =
                [
                    new VirtualOutputWorkerPolicyDefinition
                    {
                        Key = "default",
                        Name = "Default"
                    }
                ]
            }
        });
        var server = new VirtualOutputIpcServer(
            endpoint,
            authority,
            broker,
            status,
            settings,
            NullLogger<VirtualOutputIpcServer>.Instance);
        await server.StartAsync(TestContext.Current.CancellationToken);
        var client = new VirtualOutputBrokerClient(endpoint);

        // Act
        var registration = await client.RegisterAsync(new VirtualOutputWorkerRegistrationRequest
        {
            WorkerId = ticket.WorkerId,
            VirtualDeviceId = ticket.VirtualDeviceId,
            Generation = ticket.Generation,
            BootstrapToken = ticket.BootstrapToken,
            MinimumProtocolVersion = 1,
            MaximumProtocolVersion = 1
        }, TestContext.Current.CancellationToken);
        var manifest = await client.GetManifestAsync(new VirtualOutputManifestRequest
        {
            Session = new VirtualOutputSessionContext
            {
                SessionId = registration.SessionId!.Value,
                Generation = ticket.Generation
            }
        }, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(registration.Accepted);
        Assert.Equal(deviceId, manifest.VirtualDeviceId);
        Assert.Equal(4, manifest.TunerCount);
        await server.StopAsync(TestContext.Current.CancellationToken);
        server.Dispose();
    }

    /// <summary>Verifies one worker cannot exceed its configured concurrent broker-call budget.</summary>
    [Fact]
    public async Task Client_ConcurrentBrokerCalls_RejectsCallsBeyondPolicy()
    {
        // Arrange
        var endpoint = CreateEndpoint();
        using var authority = new VirtualOutputSessionAuthority();
        var deviceId = VirtualDeviceId.New();
        var ticket = authority.CreateActivation(
            deviceId,
            "test-worker",
            new HashSet<VirtualOutputCapability>
            {
                VirtualOutputCapability.ReadManifest,
                VirtualOutputCapability.ReportHealth
            },
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(1));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var broker = Substitute.For<IVirtualOutputBroker>();
        broker.GetManifestAsync(Arg.Any<VirtualOutputManifestRequest>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                entered.TrySetResult();
                await release.Task;
                return new VirtualOutputManifest
                {
                    VirtualDeviceId = deviceId,
                    Name = "Isolated output"
                };
            });
        var settings = CreateSettings(deviceId, maximumOutstandingBrokerCalls: 1);
        var server = new VirtualOutputIpcServer(
            endpoint,
            authority,
            broker,
            new VirtualOutputRuntimeStatus(),
            settings,
            NullLogger<VirtualOutputIpcServer>.Instance);
        await server.StartAsync(TestContext.Current.CancellationToken);
        var client = new VirtualOutputBrokerClient(endpoint);
        var registration = await client.RegisterAsync(new VirtualOutputWorkerRegistrationRequest
        {
            WorkerId = ticket.WorkerId,
            VirtualDeviceId = ticket.VirtualDeviceId,
            Generation = ticket.Generation,
            BootstrapToken = ticket.BootstrapToken,
            MinimumProtocolVersion = 1,
            MaximumProtocolVersion = 1
        }, TestContext.Current.CancellationToken);
        var request = new VirtualOutputManifestRequest
        {
            Session = new VirtualOutputSessionContext
            {
                SessionId = registration.SessionId!.Value,
                Generation = ticket.Generation
            }
        };
        var firstCall = client.GetManifestAsync(request, TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);

        // Act
        var exception = await Assert.ThrowsAsync<VirtualOutputClientException>(
            () => client.GetManifestAsync(request, TestContext.Current.CancellationToken));
        release.TrySetResult();
        await firstCall;

        // Assert
        Assert.Equal(VirtualOutputFailureCode.ResourceLimit, exception.FailureCode);
        await server.StopAsync(TestContext.Current.CancellationToken);
        server.Dispose();
    }

    /// <summary>Verifies a rejected registration cannot replace the healthy runtime-status projection.</summary>
    [Fact]
    public async Task Client_RejectedRegistration_DoesNotCreateRuntimeStatus()
    {
        // Arrange
        var endpoint = CreateEndpoint();
        using var authority = new VirtualOutputSessionAuthority();
        var deviceId = VirtualDeviceId.New();
        var ticket = authority.CreateActivation(
            deviceId,
            "test-worker",
            new HashSet<VirtualOutputCapability> { VirtualOutputCapability.ReportHealth },
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(1));
        var status = new VirtualOutputRuntimeStatus();
        var server = new VirtualOutputIpcServer(
            endpoint,
            authority,
            Substitute.For<IVirtualOutputBroker>(),
            status,
            CreateSettings(deviceId, maximumOutstandingBrokerCalls: 1),
            NullLogger<VirtualOutputIpcServer>.Instance);
        await server.StartAsync(TestContext.Current.CancellationToken);
        var client = new VirtualOutputBrokerClient(endpoint);

        // Act
        var registration = await client.RegisterAsync(new VirtualOutputWorkerRegistrationRequest
        {
            WorkerId = ticket.WorkerId,
            VirtualDeviceId = ticket.VirtualDeviceId,
            Generation = ticket.Generation,
            BootstrapToken = "invalid-token",
            MinimumProtocolVersion = 1,
            MaximumProtocolVersion = 1
        }, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(registration.Accepted);
        Assert.Empty(status.GetSnapshots());
        await server.StopAsync(TestContext.Current.CancellationToken);
        server.Dispose();
    }

    private static ILineupSettingsService CreateSettings(
        VirtualDeviceId deviceId,
        int maximumOutstandingBrokerCalls)
    {
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
                        Name = "Isolated output",
                        Adapter = new AdapterConfiguration("test", JsonSerializer.SerializeToElement(new { })),
                        Hosting = new VirtualOutputHostingDefinition
                        {
                            WorkerPolicyKey = "default"
                        }
                    }
                ],
                VirtualOutputWorkerPolicies =
                [
                    new VirtualOutputWorkerPolicyDefinition
                    {
                        Key = "default",
                        Name = "Default",
                        MaximumOutstandingBrokerCalls = maximumOutstandingBrokerCalls
                    }
                ]
            }
        });
        return settings;
    }

    private static VirtualOutputIpcEndpoint CreateEndpoint()
    {
        if (OperatingSystem.IsWindows())
        {
            return new VirtualOutputIpcEndpoint
            {
                Transport = VirtualOutputIpcTransport.NamedPipe,
                Address = $"lineup-tests-{Guid.NewGuid():N}"
            };
        }
        return new VirtualOutputIpcEndpoint
        {
            Transport = VirtualOutputIpcTransport.UnixDomainSocket,
            Address = Path.Combine(Path.GetTempPath(), $"lineup-tests-{Guid.NewGuid():N}.sock")
        };
    }
}
