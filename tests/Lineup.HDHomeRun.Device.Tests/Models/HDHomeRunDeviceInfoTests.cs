using Lineup.HDHomeRun.Device.Models;
using Xunit;

namespace Lineup.HDHomeRun.Device.Tests.Models;

public class HDHomeRunDeviceInfoTests
{
    [Fact]
    public void HDHomeRunDeviceInfo_CanBeCreated_WithAllProperties()
    {
        // Arrange & Act
        var deviceInfo = new HDHomeRunDeviceInfo
        {
            FriendlyName = "HDHomeRun FLEX 4K",
            ModelNumber = "HDFX-4K",
            FirmwareName = "hdhomerun_dvr",
            FirmwareVersion = "20231015",
            DeviceID = "12345678",
            DeviceAuth = "auth-token-here",
            BaseURL = "http://10.0.0.10",
            LineupURL = "http://10.0.0.10/lineup.json",
            TunerCount = 4
        };

        // Assert
        Assert.Equal("HDHomeRun FLEX 4K", deviceInfo.FriendlyName);
        Assert.Equal("HDFX-4K", deviceInfo.ModelNumber);
        Assert.Equal("20231015", deviceInfo.FirmwareVersion);
        Assert.Equal("12345678", deviceInfo.DeviceID);
        Assert.Equal("auth-token-here", deviceInfo.DeviceAuth);
        Assert.Equal(4, deviceInfo.TunerCount);
    }

    [Fact]
    public void HDHomeRunDeviceInfo_Record_SupportsEquality()
    {
        // Arrange
        var deviceInfo1 = new HDHomeRunDeviceInfo
        {
            FriendlyName = "Test Device",
            ModelNumber = "TEST-1",
            FirmwareName = "test",
            FirmwareVersion = "1.0",
            DeviceID = "ABCD1234",
            DeviceAuth = "auth-123",
            BaseURL = "http://10.0.0.1",
            LineupURL = "http://10.0.0.1/lineup.json",
            TunerCount = 2
        };

        var deviceInfo2 = new HDHomeRunDeviceInfo
        {
            FriendlyName = "Test Device",
            ModelNumber = "TEST-1",
            FirmwareName = "test",
            FirmwareVersion = "1.0",
            DeviceID = "ABCD1234",
            DeviceAuth = "auth-123",
            BaseURL = "http://10.0.0.1",
            LineupURL = "http://10.0.0.1/lineup.json",
            TunerCount = 2
        };

        // Assert
        Assert.Equal(deviceInfo1, deviceInfo2);
    }
}
