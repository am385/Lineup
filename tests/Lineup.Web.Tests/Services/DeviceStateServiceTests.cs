using System.Reflection;
using Lineup.HDHomeRun.Device;
using Lineup.HDHomeRun.Device.Models;
using Lineup.HDHomeRun.Device.Protocol;
using Lineup.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Lineup.Web.Tests.Services;

/// <summary>
/// Verifies serialized device-state operations.
/// </summary>
public class DeviceStateServiceTests
{
    /// <summary>
    /// Verifies that concurrent discovery requests never execute discovery concurrently.
    /// </summary>
    [Fact]
    public async Task DiscoverDeviceAsync_ConcurrentCalls_AreSerialized()
    {
        // Arrange
        var activeCalls = 0;
        var maximumActiveCalls = 0;
        var client = CreateDeviceClient();
        client.DiscoverDeviceAsync(Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var active = Interlocked.Increment(ref activeCalls);
            UpdateMaximum(ref maximumActiveCalls, active);
            await Task.Delay(30, call.Arg<CancellationToken>());
            Interlocked.Decrement(ref activeCalls);
            return CreateDeviceInfo();
        });
        var protocolService = CreateProtocolService();
        protocolService.GetDeviceByIpAsync("tuner.local", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Lineup.HDHomeRun.Device.Protocol.HDHomeRunDevice?>(null));
        var service = CreateService(client, protocolService);

        // Act
        await Task.WhenAll(service.DiscoverDeviceAsync(TestContext.Current.CancellationToken), service.DiscoverDeviceAsync(TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(1, maximumActiveCalls);
        Assert.False(service.IsDiscovering);
        Assert.NotNull(service.DeviceInfo);
        await client.Received(2).DiscoverDeviceAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Verifies that tuner refresh waits asynchronously for the refresh gate.
    /// </summary>
    [Fact]
    public async Task RefreshTunerStatusAsync_GateOccupied_WaitsForOwner()
    {
        // Arrange
        var service = CreateService(CreateDeviceClient(), CreateProtocolService());
        var gateField = typeof(DeviceStateService).GetField("_operationGate", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(gateField);
        var gate = Assert.IsType<SemaphoreSlim>(gateField.GetValue(service));
        await gate.WaitAsync(TestContext.Current.CancellationToken);

        // Act
        var refresh = service.RefreshTunerStatusAsync(TestContext.Current.CancellationToken);
        await Task.Delay(30, TestContext.Current.CancellationToken);
        var completedWhileOccupied = refresh.IsCompleted;
        gate.Release();
        await refresh;

        // Assert
        Assert.False(completedWhileOccupied);
        Assert.False(service.IsRefreshingTuners);
    }

    /// <summary>
    /// Verifies endpoint replacement waits for an active tuner stop and preserves the replacement state.
    /// </summary>
    [Fact]
    public async Task DiscoverDeviceAsync_DuringStop_WaitsAndInstallsReplacementAfterStop()
    {
        // Arrange
        var client = CreateDeviceClient();
        client.DiscoverDeviceAsync(Arg.Any<CancellationToken>()).Returns(CreateDeviceInfo(0));
        var original = CreateProtocolDevice("192.0.2.10");
        var replacement = CreateProtocolDevice("192.0.2.11");
        var stopStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        original.StopStreamingAsync(0, Arg.Any<CancellationToken>()).Returns(async call =>
        {
            stopStarted.TrySetResult();
            await releaseStop.Task.WaitAsync(call.Arg<CancellationToken>());
        });
        var protocolService = CreateProtocolService();
        protocolService.GetDeviceByIpAsync("tuner.local", Arg.Any<CancellationToken>())
            .Returns(original, replacement);
        var service = CreateService(client, protocolService);
        await service.DiscoverDeviceAsync(TestContext.Current.CancellationToken);

        // Act
        var stop = service.StopTunerAsync(0, TestContext.Current.CancellationToken);
        await stopStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        var discovery = service.DiscoverDeviceAsync(TestContext.Current.CancellationToken);
        await Task.Delay(30, TestContext.Current.CancellationToken);
        var discoveryWaited = !discovery.IsCompleted;
        releaseStop.TrySetResult();
        await Task.WhenAll(stop, discovery);

        // Assert
        Assert.True(discoveryWaited);
        Assert.Same(replacement, service.ProtocolDevice);
        await original.Received(1).StopStreamingAsync(0, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Verifies endpoint replacement waits for restart and newer discovery state remains installed.
    /// </summary>
    [Fact]
    public async Task DiscoverDeviceAsync_DuringRestart_InstallsReplacementWithoutOlderStateClear()
    {
        // Arrange
        var client = CreateDeviceClient();
        client.DiscoverDeviceAsync(Arg.Any<CancellationToken>()).Returns(CreateDeviceInfo(0));
        var original = CreateProtocolDevice("192.0.2.10");
        var replacement = CreateProtocolDevice("192.0.2.11");
        var restartStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRestart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        original.RestartAsync(Arg.Any<CancellationToken>()).Returns(async call =>
        {
            restartStarted.TrySetResult();
            await releaseRestart.Task.WaitAsync(call.Arg<CancellationToken>());
        });
        var protocolService = CreateProtocolService();
        protocolService.GetDeviceByIpAsync("tuner.local", Arg.Any<CancellationToken>())
            .Returns(original, replacement);
        var service = CreateService(client, protocolService);
        await service.DiscoverDeviceAsync(TestContext.Current.CancellationToken);

        // Act
        var restart = service.RestartDeviceAsync(TestContext.Current.CancellationToken);
        await restartStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        var discovery = service.DiscoverDeviceAsync(TestContext.Current.CancellationToken);
        await Task.Delay(30, TestContext.Current.CancellationToken);
        var discoveryWaited = !discovery.IsCompleted;
        releaseRestart.TrySetResult();
        await Task.WhenAll(restart, discovery);

        // Assert
        Assert.True(discoveryWaited);
        Assert.Same(replacement, service.ProtocolDevice);
        Assert.NotNull(service.DeviceInfo);
    }

    /// <summary>
    /// Verifies a published tuner snapshot remains safe to enumerate after discovery clears current state.
    /// </summary>
    [Fact]
    public async Task DiscoverDeviceAsync_WhenLaterDiscoveryFails_PreservesPublishedTunerSnapshot()
    {
        // Arrange
        var client = CreateDeviceClient();
        client.DiscoverDeviceAsync(Arg.Any<CancellationToken>()).Returns(Task.FromException<HDHomeRunDeviceInfo>(new HttpRequestException("Unavailable")));
        var service = CreateService(client, CreateProtocolService());
        var statusesField = typeof(DeviceStateService).GetField("_tunerStatuses", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(statusesField);
        statusesField.SetValue(service, new TunerStatus[] { new() { TunerIndex = 0 } });
        var publishedSnapshot = service.TunerStatuses;

        // Act
        await service.DiscoverDeviceAsync(TestContext.Current.CancellationToken);
        var capturedStatuses = publishedSnapshot.ToArray();

        // Assert
        Assert.Single(capturedStatuses);
        Assert.Empty(service.TunerStatuses);
    }

    private static DeviceStateService CreateService(HDHomeRunDeviceClient client, HDHomeRunService protocolService)
    {
        var settings = Substitute.For<IAppSettingsService>();
        settings.Settings.Returns(new AppSettings { DeviceAddress = "tuner.local" });
        return new DeviceStateService(NullLogger<DeviceStateService>.Instance, client, protocolService, settings);
    }

    private static HDHomeRunService CreateProtocolService()
    {
        return Substitute.For<HDHomeRunService>(NullLoggerFactory.Instance, Substitute.For<IHDHomeRunHttpControlFactory>());
    }

    private static HDHomeRunDeviceClient CreateDeviceClient()
    {
        return Substitute.For<HDHomeRunDeviceClient>(
            NullLogger<HDHomeRunDeviceClient>.Instance,
            new HttpClient { BaseAddress = new Uri("http://tuner.local/") },
            null);
    }

    private static HDHomeRunDeviceInfo CreateDeviceInfo(int tunerCount = 2)
    {
        return new HDHomeRunDeviceInfo
        {
            FriendlyName = "Tuner",
            ModelNumber = "HDHR",
            FirmwareName = "hdhomerun",
            FirmwareVersion = "1",
            DeviceID = "12345678",
            DeviceAuth = "auth",
            BaseURL = "http://tuner.local",
            LineupURL = "http://tuner.local/lineup.json",
            TunerCount = tunerCount
        };
    }

    private static HDHomeRunDevice CreateProtocolDevice(string address)
    {
        var deviceInfo = new HDHomeRunDiscoveredDevice
        {
            IpAddress = System.Net.IPAddress.Parse(address),
            DeviceId = 0x12345678,
            DeviceType = HDHomeRunDeviceType.Tuner,
            TunerCount = 0,
            BaseUrl = $"http://{address}"
        };
        return Substitute.For<HDHomeRunDevice>(deviceInfo, NullLoggerFactory.Instance, Substitute.For<IHDHomeRunHttpControlFactory>());
    }

    private static void UpdateMaximum(ref int maximum, int value)
    {
        var current = Volatile.Read(ref maximum);
        while (current < value)
        {
            var observed = Interlocked.CompareExchange(ref maximum, value, current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }
}
