using Lineup.HDHomeRun.Device.Protocol;
using Lineup.Web.Services;
using Xunit;

namespace Lineup.Web.Tests.Services;

/// <summary>
/// Verifies SiliconDust request matching and discovery reply formatting.
/// </summary>
public class HdHomeRunDiscoveryProtocolTests
{
    private const uint DeviceId = 0x12345678;

    /// <summary>
    /// Verifies that requests with no DeviceID and wildcard DeviceIDs match the virtual tuner.
    /// </summary>
    /// <param name="includeWildcard">Whether to include an explicit wildcard DeviceID.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TryCreateReply_WildcardOrMissingDeviceId_ReturnsAdvertisedDevice(bool includeWildcard)
    {
        // Arrange
        var builder = new HDHomeRunPacketBuilder()
            .AddTag(HDHomeRunTagType.DeviceType, (uint)HDHomeRunDeviceType.Tuner);
        if (includeWildcard)
        {
            builder.AddTag(HDHomeRunTagType.DeviceId, HDHomeRunDeviceId.Wildcard);
        }

        var request = builder.Build(HDHomeRunPacketType.DiscoverRequest);
        var device = CreateDevice();

        // Act
        var matched = HdHomeRunDiscoveryProtocol.TryCreateReply(request, device, out var reply);

        // Assert
        Assert.True(matched);
        Assert.NotNull(reply);
        Assert.True(HDHomeRunPacketBuilder.VerifyCrc32(reply));
        AssertReplyContents(reply);
    }

    /// <summary>
    /// Verifies that a request for another DeviceID is ignored.
    /// </summary>
    [Fact]
    public void TryCreateReply_DifferentDeviceId_DoesNotMatch()
    {
        // Arrange
        var request = new HDHomeRunPacketBuilder()
            .AddTag(HDHomeRunTagType.DeviceId, 0x87654321)
            .Build(HDHomeRunPacketType.DiscoverRequest);

        // Act
        var matched = HdHomeRunDiscoveryProtocol.TryCreateReply(request, CreateDevice(), out var reply);

        // Assert
        Assert.False(matched);
        Assert.Null(reply);
    }

    /// <summary>
    /// Verifies that a request for the exact virtual DeviceID matches.
    /// </summary>
    [Fact]
    public void TryCreateReply_ExactDeviceId_Matches()
    {
        // Arrange
        var request = new HDHomeRunPacketBuilder()
            .AddTag(HDHomeRunTagType.DeviceId, DeviceId)
            .Build(HDHomeRunPacketType.DiscoverRequest);

        // Act
        var matched = HdHomeRunDiscoveryProtocol.TryCreateReply(request, CreateDevice(), out var reply);

        // Assert
        Assert.True(matched);
        Assert.NotNull(reply);
    }

    /// <summary>
    /// Verifies that malformed and non-discovery packets fail cheap SiliconDust prevalidation.
    /// </summary>
    [Fact]
    public void IsDiscoveryRequest_MalformedOrWrongPacketType_ReturnsFalse()
    {
        // Arrange
        var malformed = new byte[] { 1, 2, 3 };
        var wrongType = new HDHomeRunPacketBuilder().Build(HDHomeRunPacketType.DiscoverReply);

        // Act
        var malformedValid = HdHomeRunDiscoveryProtocol.IsDiscoveryRequest(malformed);
        var wrongTypeValid = HdHomeRunDiscoveryProtocol.IsDiscoveryRequest(wrongType);

        // Assert
        Assert.False(malformedValid);
        Assert.False(wrongTypeValid);
    }

    private static HdHomeRunAdvertisedDevice CreateDevice()
    {
        return new HdHomeRunAdvertisedDevice
        {
            Profile = new HdHomeRunProxyProfileSnapshot
            {
                Settings = new HdHomeRunProxyProfileSettings { PhysicalAddress = "tuner.local" },
                PhysicalDevice = CreatePhysicalDevice(),
                IsPrimary = true,
                DeviceId = DeviceId,
                DeviceAuth = "virtual-auth",
                TunerCount = 4,
                FriendlyName = "Lineup Tuner",
                PhysicalBaseUri = new Uri("http://tuner.local/")
            },
            BaseUri = new Uri("http://lineup.local:8080/")
        };
    }

    private static Lineup.HDHomeRun.Device.Models.HDHomeRunDeviceInfo CreatePhysicalDevice()
    {
        return new Lineup.HDHomeRun.Device.Models.HDHomeRunDeviceInfo
        {
            FriendlyName = "Tuner",
            ModelNumber = "HDHR",
            FirmwareName = "hdhomerun",
            FirmwareVersion = "1",
            DeviceID = "12345678",
            DeviceAuth = string.Empty,
            BaseURL = "http://tuner.local",
            LineupURL = "http://tuner.local/lineup.json",
            TunerCount = 4
        };
    }

    private static void AssertReplyContents(byte[] reply)
    {
        var reader = new HDHomeRunPacketReader(reply);
        var values = new Dictionary<HDHomeRunTagType, string>();
        while (reader.TryReadTag(out var tag, out var value))
        {
            values[tag] = tag is HDHomeRunTagType.BaseUrl or HDHomeRunTagType.LineupUrl or HDHomeRunTagType.DeviceAuthStr
                ? HDHomeRunPacketReader.ReadString(value)
                : Convert.ToHexString(value);
        }

        Assert.True(reader.IsValid);
        Assert.False(reader.HasError);
        Assert.Equal(HDHomeRunPacketType.DiscoverReply, reader.PacketType);
        Assert.Equal("00000001", values[HDHomeRunTagType.DeviceType]);
        Assert.Equal("12345678", values[HDHomeRunTagType.DeviceId]);
        Assert.Equal("04", values[HDHomeRunTagType.TunerCount]);
        Assert.Equal("http://lineup.local:8080", values[HDHomeRunTagType.BaseUrl]);
        Assert.Equal("http://lineup.local:8080/lineup.json", values[HDHomeRunTagType.LineupUrl]);
        Assert.Equal("virtual-auth", values[HDHomeRunTagType.DeviceAuthStr]);
    }
}
