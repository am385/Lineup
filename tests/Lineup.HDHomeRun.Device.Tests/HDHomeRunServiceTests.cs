using System.Net;
using Lineup.HDHomeRun.Device.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Lineup.HDHomeRun.Device.Tests;

/// <summary>
/// Verifies cached native device ownership.
/// </summary>
public class HDHomeRunServiceTests
{
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
}
