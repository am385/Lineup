using System.Text.Json;
using Lineup.Common.Streaming;
using Lineup.SiliconDust.HDHomeRun.DiscoveryRuntime;
using Lineup.SiliconDust.HDHomeRun.VirtualTuner;
using Lineup.VirtualOutputs.Protocol;
using Xunit;

namespace Lineup.SiliconDust.HDHomeRun.VirtualTuner.Tests;

/// <summary>Verifies shared HDHomeRun discovery worker configuration.</summary>
public sealed class HdHomeRunDiscoveryRuntimeTests
{
    /// <summary>Verifies management-hosted advertisements use the discovery worker's default policy.</summary>
    [Fact]
    public void ResolveWorkerPolicy_ManagementHostedAdvertisement_ReturnsDiscoveryDefault()
    {
        // Arrange
        var virtualDeviceId = VirtualDeviceId.New();
        var configuration = new StreamingConfiguration
        {
            VirtualDevices =
            [
                new VirtualDeviceDefinition
                {
                    Id = virtualDeviceId,
                    Name = "Management hosted",
                    Adapter = new AdapterConfiguration(HdHomeRunVirtualTunerDriver.AdapterKind, JsonSerializer.SerializeToElement(new { }))
                }
            ]
        };
        var advertisement = CreateAdvertisement(virtualDeviceId);

        // Act
        var policy = HdHomeRunDiscoveryWorkerSupervisor.ResolveWorkerPolicy(configuration, [advertisement]);

        // Assert
        Assert.Equal("hdhomerun-discovery-worker", policy.Key);
        Assert.Equal("HDHomeRun discovery worker", policy.Name);
    }

    /// <summary>Verifies a dedicated listener continues to supply its configured worker policy.</summary>
    [Fact]
    public void ResolveWorkerPolicy_DedicatedListener_ReturnsConfiguredPolicy()
    {
        // Arrange
        var virtualDeviceId = VirtualDeviceId.New();
        var configuredPolicy = new VirtualOutputWorkerPolicyDefinition
        {
            Key = "dedicated-worker",
            Name = "Dedicated worker",
            MaximumMemoryMib = 256
        };
        var configuration = new StreamingConfiguration
        {
            VirtualDevices =
            [
                new VirtualDeviceDefinition
                {
                    Id = virtualDeviceId,
                    Name = "Dedicated",
                    Adapter = new AdapterConfiguration(HdHomeRunVirtualTunerDriver.AdapterKind, JsonSerializer.SerializeToElement(new { })),
                    Hosting = new VirtualOutputHostingDefinition { WorkerPolicyKey = configuredPolicy.Key }
                }
            ],
            VirtualOutputWorkerPolicies = [configuredPolicy]
        };
        var advertisement = CreateAdvertisement(virtualDeviceId);

        // Act
        var policy = HdHomeRunDiscoveryWorkerSupervisor.ResolveWorkerPolicy(configuration, [advertisement]);

        // Assert
        Assert.Same(configuredPolicy, policy);
    }

    /// <summary>Verifies multiple generated advertisements are sorted by their serialized external DeviceID.</summary>
    [Fact]
    public void OrderAdvertisements_MultipleGeneratedMetadata_ReturnsDeviceIdOrder()
    {
        // Arrange
        var later = CreateAdvertisement(VirtualDeviceId.New(), "5000F99D");
        var earlier = CreateAdvertisement(VirtualDeviceId.New(), "12345678");

        // Act
        var ordered = HdHomeRunDiscoveryWorkerSupervisor.OrderAdvertisements([later, earlier]);

        // Assert
        Assert.Equal([earlier.VirtualDeviceId, later.VirtualDeviceId], ordered.Select(item => item.VirtualDeviceId));
    }

    private static VirtualOutputAdvertisementSnapshot CreateAdvertisement(VirtualDeviceId virtualDeviceId, string deviceId = "5000F99D") =>
        new()
        {
            VirtualDeviceId = virtualDeviceId,
            AdapterKind = HdHomeRunVirtualTunerDriver.AdapterKind,
            Name = "HDHomeRun",
            PublicBaseUri = new Uri("http://127.0.0.1:8080"),
            Metadata = JsonSerializer.SerializeToElement(new HdHomeRunDiscoveryAdvertisementMetadata
            {
                DeviceId = deviceId,
                DeviceAuth = "test"
            })
        };
}
