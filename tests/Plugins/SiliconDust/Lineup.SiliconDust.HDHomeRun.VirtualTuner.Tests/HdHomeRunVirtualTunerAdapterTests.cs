using System.Text.Json;
using System.Xml.Linq;
using Lineup.Common.Configuration;
using Lineup.Extensibility.Configuration;
using Lineup.Common.Streaming;
using Lineup.Extensibility.Streaming;
using Lineup.SiliconDust.HDHomeRun.Device.Models;
using Lineup.SiliconDust.HDHomeRun.Device.Protocol;
using Xunit;

namespace Lineup.SiliconDust.HDHomeRun.VirtualTuner.Tests;

/// <summary>Tests the virtual HDHomeRun adapter.</summary>
public sealed class HdHomeRunVirtualTunerAdapterTests
{
    /// <summary>Verifies network advertisement requires and persists a valid public base URL.</summary>
    [Fact]
    public void Settings_EnableAdvertisement_RequiresValidBaseUrl()
    {
        // Arrange
        var driver = new HdHomeRunVirtualTunerDriver();
        var configuration = driver.CreateDefaultConfiguration(VirtualDeviceId.New());
        var enabledEdit = new AdapterSettingEdit
        {
            Key = "enableNetworkAdvertisement",
            Action = AdapterSettingEditAction.Set,
            Value = JsonSerializer.SerializeToElement(true)
        };

        // Act
        var invalid = driver.ApplySettings(configuration, [enabledEdit]);
        var valid = driver.ApplySettings(
            configuration,
            [
                enabledEdit,
                new AdapterSettingEdit
                {
                    Key = "advertisedBaseUrl",
                    Action = AdapterSettingEditAction.Set,
                    Value = JsonSerializer.SerializeToElement("https://lineup.example/")
                }
            ]);
        var updated = valid.Configuration?.Configuration.Deserialize<HdHomeRunVirtualTunerConfiguration>();

        // Assert
        Assert.Empty(AdapterSettingsValidator.ValidateDescriptor(driver.SettingsDescriptor));
        Assert.False(invalid.Succeeded);
        Assert.True(valid.Succeeded);
        Assert.True(updated?.EnableNetworkAdvertisement);
        Assert.Equal("https://lineup.example/", updated?.AdvertisedBaseUrl);
    }

    /// <summary>Verifies adapter identity, capabilities, and typed configuration validation.</summary>
    [Fact]
    public void Validate_ReportsKindShapeAndCapErrors()
    {
        // Arrange
        var driver = new HdHomeRunVirtualTunerDriver();
        var wrongKind = Configuration("other", new HdHomeRunVirtualTunerConfiguration { TunerCountCap = 256 });
        var wrongShape = new AdapterConfiguration(driver.Kind, JsonSerializer.SerializeToElement("invalid"));

        // Act
        var kindAndCapErrors = driver.Validate(wrongKind);
        var shapeErrors = driver.Validate(wrongShape);

        // Assert
        Assert.Equal(HdHomeRunVirtualTunerDriver.AdapterKind, driver.Kind);
        Assert.True(driver.Capabilities.SupportsNetworkDiscovery);
        Assert.True(driver.Capabilities.SupportsJsonLineup);
        Assert.True(driver.Capabilities.SupportsXmlLineup);
        Assert.True(driver.Capabilities.SupportsM3uLineup);
        Assert.Equal(byte.MaxValue, driver.Capabilities.MaximumTunerCount);
        Assert.Equal(2, kindAndCapErrors.Count);
        Assert.Single(shapeErrors);
    }

    /// <summary>Verifies subscribed finite capacities are summed while unbounded capacity is ignored.</summary>
    [Fact]
    public void GetTunerCount_SumsFiniteSubscribedSnapshotsAndAppliesCap()
    {
        // Arrange
        var driver = new HdHomeRunVirtualTunerDriver();
        var snapshots = new[]
        {
            Snapshot(2),
            Snapshot(null),
            Snapshot(4)
        };
        var configuration = Configuration(driver.Kind, new HdHomeRunVirtualTunerConfiguration { TunerCountCap = 5 });

        // Act
        var tunerCount = driver.GetTunerCount(snapshots, configuration);

        // Assert
        Assert.Equal(5, tunerCount);
    }

    /// <summary>Verifies aggregate capacities are bounded by the one-byte discovery contract.</summary>
    [Fact]
    public void GetTunerCount_WithoutConfiguredCap_ClampsToProtocolMaximum()
    {
        // Arrange
        var driver = new HdHomeRunVirtualTunerDriver();
        var configuration = Configuration(driver.Kind, new HdHomeRunVirtualTunerConfiguration());

        // Act
        var tunerCount = driver.GetTunerCount([Snapshot(200), Snapshot(100)], configuration);

        // Assert
        Assert.Equal(byte.MaxValue, tunerCount);
    }

    /// <summary>Verifies selected subscriptions exclude snapshots that are not subscribed.</summary>
    [Fact]
    public void GetTunerCount_SelectedSubscription_SumsOnlySelectedSnapshots()
    {
        // Arrange
        var driver = new HdHomeRunVirtualTunerDriver();
        var selected = Snapshot(2);
        var excluded = Snapshot(8);
        var virtualDevice = new VirtualDeviceDefinition
        {
            Id = VirtualDeviceId.New(),
            Name = "Living Room",
            Adapter = Configuration(driver.Kind, new HdHomeRunVirtualTunerConfiguration()),
            Subscription = new PhysicalDeviceSubscription
            {
                Mode = PhysicalDeviceSubscriptionMode.Selected,
                SelectedDeviceIds = new HashSet<PhysicalDeviceId> { selected.Id }
            }
        };

        // Act
        var tunerCount = driver.GetTunerCount(virtualDevice, [selected, excluded]);

        // Assert
        Assert.Equal(2, tunerCount);
    }

    /// <summary>Verifies generated identities remain deterministic and checksum-valid.</summary>
    [Fact]
    public void Identity_CreatesStableProtocolValues()
    {
        // Arrange
        const string seed = "virtual-device";

        // Act
        var first = HdHomeRunProxyIdentity.CreateDeviceId(seed);
        var second = HdHomeRunProxyIdentity.CreateDeviceId(seed);
        var authorization = HdHomeRunProxyIdentity.CreateDeviceAuth(seed);

        // Assert
        Assert.Equal(first, second);
        Assert.True(HdHomeRunProxyIdentity.IsValidDeviceId(first));
        Assert.NotEmpty(authorization);
    }

    /// <summary>Verifies a persisted external DeviceID survives configuration reloads unchanged.</summary>
    [Fact]
    public void GetDeviceId_PersistedConfiguration_ReturnsStableValidIdentity()
    {
        // Arrange
        var driver = new HdHomeRunVirtualTunerDriver();
        var persistedDeviceId = HdHomeRunProxyIdentity.CreateDeviceId("persisted-external-device");
        var device = new VirtualDeviceDefinition
        {
            Id = VirtualDeviceId.New(),
            Name = "Aggregate",
            Adapter = new AdapterConfiguration(
                HdHomeRunVirtualTunerDriver.AdapterKind,
                JsonSerializer.SerializeToElement(new HdHomeRunVirtualTunerConfiguration { DeviceId = persistedDeviceId }))
        };
        var reloaded = JsonSerializer.Deserialize<VirtualDeviceDefinition>(JsonSerializer.Serialize(device))!;

        // Act
        var deviceId = driver.GetDeviceId(reloaded);

        // Assert
        Assert.Equal(persistedDeviceId, deviceId);
        Assert.True(HdHomeRunProxyIdentity.IsValidDeviceId(deviceId));
    }

    /// <summary>Verifies discover responses use adapter-owned identity and URL conventions.</summary>
    [Fact]
    public void CreateDiscover_WritesVirtualDeviceContract()
    {
        // Arrange
        var baseUri = new Uri("http://lineup.local/hdhomerun/12345678/");

        // Act
        var response = HdHomeRunProxyResponseFactory.CreateDiscover("Living Room", "12345678", "virtual-auth", baseUri, 6, "2.2.0");
        var json = JsonSerializer.SerializeToElement(response);

        // Assert
        Assert.Equal("LINEUP-1", json.GetProperty("ModelNumber").GetString());
        Assert.Equal("12345678", json.GetProperty("DeviceID").GetString());
        Assert.Equal("http://lineup.local/hdhomerun/12345678", json.GetProperty("BaseURL").GetString());
        Assert.Equal("http://lineup.local/hdhomerun/12345678/lineup.json", json.GetProperty("LineupURL").GetString());
        Assert.Equal(6, json.GetProperty("TunerCount").GetInt32());
    }

    /// <summary>Verifies JSON, XML, and M3U representations preserve the emulated lineup contract.</summary>
    [Fact]
    public void LineupFormatter_WritesAllSupportedRepresentations()
    {
        // Arrange
        var source = new HDHomeRunChannel
        {
            GuideNumber = "104.1",
            GuideName = "News \"One\"\nExtra",
            VideoCodec = "HEVC",
            AudioCodec = "AC4",
            HD = true,
            Favorite = true,
            Tags = "subscription",
            URL = "http://physical/auto/v104.1"
        };
        var channel = HdHomeRunProxyChannel.Create(source, new Uri("http://lineup.local/"), convertHevcToH264: true);

        // Act
        var json = JsonDocument.Parse(HdHomeRunProxyLineupFormatter.ToJson([channel]));
        var xml = XDocument.Parse(HdHomeRunProxyLineupFormatter.ToXml([channel]));
        var m3u = HdHomeRunProxyLineupFormatter.ToM3u([channel]);

        // Assert
        Assert.Equal("http://lineup.local/auto/v104.1", json.RootElement[0].GetProperty("URL").GetString());
        Assert.Equal("H264", json.RootElement[0].GetProperty("VideoCodec").GetString());
        Assert.Equal("AC3", xml.Root!.Element("Program")!.Element("AudioCodec")!.Value);
        Assert.Contains("group-title=\"Favorites\"", m3u);
        Assert.EndsWith("http://lineup.local/auto/v104.1\n", m3u);
    }

    /// <summary>Verifies UPnP device output uses the supplied virtual identity and base URI.</summary>
    [Fact]
    public void ToDeviceXml_WritesVirtualDeviceContract()
    {
        // Arrange
        var baseUri = new Uri("http://lineup.local/hdhomerun/12345678/");

        // Act
        var document = XDocument.Parse(HdHomeRunProxyLineupFormatter.ToDeviceXml(baseUri, "Living Room", "12345678"));
        var device = document.Root!.Elements().Single(element => element.Name.LocalName == "device");

        // Assert
        Assert.Equal("http://lineup.local/hdhomerun/12345678", document.Root!.Elements().Single(element => element.Name.LocalName == "URLBase").Value);
        Assert.Equal("Living Room", device.Elements().Single(element => element.Name.LocalName == "friendlyName").Value);
        Assert.Equal("12345678", device.Elements().Single(element => element.Name.LocalName == "serialNumber").Value);
    }

    /// <summary>Verifies UDP discovery replies contain adapter-owned virtual device state.</summary>
    [Fact]
    public void DiscoveryProtocol_WildcardRequest_WritesVirtualDeviceReply()
    {
        // Arrange
        var request = new HDHomeRunPacketBuilder()
            .AddTag(HDHomeRunTagType.DeviceId, HDHomeRunDeviceId.Wildcard)
            .Build(HDHomeRunPacketType.DiscoverRequest);
        var device = new HdHomeRunAdvertisedDevice
        {
            DeviceId = 0x12345678,
            DeviceAuth = "virtual-auth",
            TunerCount = 6,
            BaseUri = new Uri("http://lineup.local/")
        };

        // Act
        var matched = HdHomeRunDiscoveryProtocol.TryCreateReply(request, device, out var reply);
        var reader = new HDHomeRunPacketReader(reply!);
        var tags = new Dictionary<HDHomeRunTagType, byte[]>();
        while (reader.TryReadTag(out var tag, out var value))
        {
            tags[tag] = value.ToArray();
        }

        // Assert
        Assert.True(matched);
        Assert.True(reader.IsValid);
        Assert.False(reader.HasError);
        Assert.Equal("12345678", Convert.ToHexString(tags[HDHomeRunTagType.DeviceId]));
        Assert.Equal("06", Convert.ToHexString(tags[HDHomeRunTagType.TunerCount]));
        Assert.Equal("http://lineup.local/lineup.json", HDHomeRunPacketReader.ReadString(tags[HDHomeRunTagType.LineupUrl]));
    }

    private static AdapterConfiguration Configuration(string kind, HdHomeRunVirtualTunerConfiguration configuration) =>
        new(kind, JsonSerializer.SerializeToElement(configuration));

    private static PhysicalDeviceSnapshot Snapshot(int? tunerCount) => new()
    {
        Id = PhysicalDeviceId.New(),
        Kind = "test",
        Name = "Test",
        Capacity = new PhysicalTunerCapacity(tunerCount)
    };
}
