using System.Net;
using Lineup.Plugin.SiliconDust.HDHomeRun.Device.Models;
using Lineup.Plugin.SiliconDust.HDHomeRun.Device.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Lineup.Plugin.SiliconDust.HDHomeRun.Device.Tests;

/// <summary>
/// Verifies cached native device ownership.
/// </summary>
public class HDHomeRunServiceTests
{
    /// <summary>Verifies activation metadata initializes and caches control without using network discovery.</summary>
    [Theory]
    [InlineData("192.0.2.10")]
    [InlineData("2001:db8::10")]
    public async Task GetDeviceFromDiscoveryAsync_ReusesIdentityAndEndpointsWithoutDiscovery(string address)
    {
        // Arrange
        var factory = Substitute.For<IHDHomeRunHttpControlFactory>();
        using var service = new HDHomeRunService(NullLoggerFactory.Instance, factory);
        var information = CreateDeviceInfo();

        // Act
        var device = await service.GetDeviceFromDiscoveryAsync(address, information, TestContext.Current.CancellationToken);
        var repeated = await service.GetDeviceFromDiscoveryAsync(address, information, TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(device);
        Assert.Same(device, repeated);
        Assert.Equal(IPAddress.Parse(address), device.DeviceInfo.IpAddress);
        Assert.Equal(0x12345678u, device.DeviceInfo.DeviceId);
        Assert.Equal(2, device.DeviceInfo.TunerCount);
        Assert.Equal(information.BaseURL, device.DeviceInfo.BaseUrl);
        Assert.Equal(information.LineupURL, device.DeviceInfo.LineupUrl);
        Assert.Empty(factory.ReceivedCalls());
    }

    /// <summary>Verifies canceled control initialization does not publish a device or consume fresh discovery.</summary>
    [Fact]
    public async Task GetDeviceFromDiscoveryAsync_CanceledInitialization_AllowsRetry()
    {
        // Arrange
        var factory = Substitute.For<IHDHomeRunHttpControlFactory>();
        using var service = new HDHomeRunService(NullLoggerFactory.Instance, factory);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        var information = CreateDeviceInfo();

        // Act
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.GetDeviceFromDiscoveryAsync("192.0.2.10", information, cancellation.Token));
        var device = await service.GetDeviceFromDiscoveryAsync("192.0.2.10", information, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.NotNull(device);
        Assert.Empty(factory.ReceivedCalls());
    }

    /// <summary>
    /// Verifies that unchanged endpoint information reuses one cached device under concurrency.
    /// </summary>
    [Fact]
    public async Task GetDevice_ConcurrentSameEndpoint_ReturnsSingleInstance()
    {
        // Arrange
        using var service = CreateService();
        var discovered = CreateDiscoveredDevice("192.0.2.10", "http://192.0.2.10");

        // Act
        var devices = await Task.WhenAll(
            Enumerable.Range(0, 32).Select(_ => Task.Run(() => service.GetDevice(discovered))));

        // Assert
        Assert.All(devices, device => Assert.Same(devices[0], device));
    }

    /// <summary>
    /// Verifies that changed endpoint information replaces the cached device for the same DeviceID.
    /// </summary>
    [Fact]
    public void GetDevice_EndpointChanged_ReplacesCachedInstance()
    {
        // Arrange
        var service = CreateService();
        var original = service.GetDevice(CreateDiscoveredDevice("192.0.2.10", "http://192.0.2.10"));
        var disposedField = typeof(HDHomeRunDevice).GetField("_disposed", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(disposedField);

        // Act
        var replacement = service.GetDevice(CreateDiscoveredDevice("192.0.2.11", "http://192.0.2.11"));
        var disposedAfterReplacement = Assert.IsType<int>(disposedField.GetValue(original));
        service.Dispose();
        var disposedWithService = Assert.IsType<int>(disposedField.GetValue(original));

        // Assert
        Assert.NotSame(original, replacement);
        Assert.Equal(IPAddress.Parse("192.0.2.11"), replacement.DeviceInfo.IpAddress);
        Assert.Equal(0, disposedAfterReplacement);
        Assert.Equal(1, disposedWithService);
    }

    private static HDHomeRunDiscoveredDevice CreateDiscoveredDevice(string address, string baseUrl)
    {
        return new HDHomeRunDiscoveredDevice
        {
            IpAddress = IPAddress.Parse(address),
            DeviceId = 0x12345678,
            DeviceType = HDHomeRunDeviceType.Tuner,
            TunerCount = 2,
            BaseUrl = baseUrl,
            LineupUrl = $"{baseUrl}/lineup.json"
        };
    }

    private static HDHomeRunService CreateService()
    {
        return new HDHomeRunService(NullLoggerFactory.Instance, Substitute.For<IHDHomeRunHttpControlFactory>());
    }

    private static HDHomeRunDeviceInfo CreateDeviceInfo() => new()
    {
        FriendlyName = "Test device",
        ModelNumber = "HDFX",
        FirmwareName = "hdhomerun",
        FirmwareVersion = "20261101",
        DeviceID = "12345678",
        DeviceAuth = "test-authorization",
        TunerCount = 2,
        BaseURL = "https://device.local:8443",
        LineupURL = "https://device.local:8443/lineup.json"
    };
}
