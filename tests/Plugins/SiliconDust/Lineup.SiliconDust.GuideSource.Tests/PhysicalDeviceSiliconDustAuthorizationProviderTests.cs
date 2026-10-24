using Lineup.Common.Streaming;
using Lineup.Extensibility.Streaming;
using NSubstitute;
using Xunit;

namespace Lineup.SiliconDust.GuideSource.Tests;

/// <summary>
/// Verifies physical-device-scoped SiliconDust authorization.
/// </summary>
public sealed class PhysicalDeviceSiliconDustAuthorizationProviderTests
{
    /// <summary>
    /// Verifies authorization is requested only from the identified physical device capability.
    /// </summary>
    [Fact]
    public async Task GetDeviceAuthAsync_DeviceId_UsesMatchingShortLivedCapability()
    {
        // Arrange
        var deviceId = PhysicalDeviceId.New();
        var device = Substitute.For<IPhysicalTunerDevice, IPhysicalDeviceAuthorizationCapability>();
        device.Snapshot.Returns(new PhysicalDeviceSnapshot
        {
            Id = deviceId,
            Kind = "fake",
            Name = "Device",
            Capacity = new PhysicalTunerCapacity(1)
        });
        ((IPhysicalDeviceAuthorizationCapability)device).GetAuthorizationAsync(Arg.Any<CancellationToken>()).Returns("short-lived");
        var devices = Substitute.For<IPhysicalDeviceResolver>();
        devices.GetRequired(deviceId).Returns(device);
        var provider = new PhysicalDeviceSiliconDustAuthorizationProvider(devices);

        // Act
        var authorization = await provider.GetDeviceAuthAsync(deviceId, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("short-lived", authorization);
        devices.Received(1).GetRequired(deviceId);
        await ((IPhysicalDeviceAuthorizationCapability)device).Received(1).GetAuthorizationAsync(Arg.Any<CancellationToken>());
    }
}
