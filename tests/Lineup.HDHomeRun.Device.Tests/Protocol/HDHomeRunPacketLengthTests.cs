using Lineup.HDHomeRun.Device.Protocol;
using Xunit;

namespace Lineup.HDHomeRun.Device.Tests.Protocol;

/// <summary>
/// Tests HDHomeRun TLV length wire encoding.
/// </summary>
public class HDHomeRunPacketLengthTests
{
    /// <summary>
    /// Verifies official boundary wire vectors and round-trip decoding.
    /// </summary>
    /// <param name="length">The tag value length.</param>
    /// <param name="firstLengthByte">The expected first length byte.</param>
    /// <param name="secondLengthByte">The expected second length byte, or null for short encoding.</param>
    [Theory]
    [InlineData(127, 0x7F, -1)]
    [InlineData(128, 0x80, 0x01)]
    [InlineData(255, 0xFF, 0x01)]
    [InlineData(300, 0xAC, 0x02)]
    [InlineData(1449, 0xA9, 0x0B)]
    public void AddTag_WithBoundaryLength_EncodesOfficialVectorAndRoundTrips(int length, int firstLengthByte, int secondLengthByte)
    {
        // Arrange
        var value = Enumerable.Range(0, length).Select(static index => (byte)index).ToArray();

        // Act
        var packet = new HDHomeRunPacketBuilder()
            .AddTag(HDHomeRunTagType.BaseUrl, value)
            .Build(HDHomeRunPacketType.DiscoverReply);
        var reader = new HDHomeRunPacketReader(packet);
        var read = reader.TryReadTag(out var tag, out var decodedValue);

        // Assert
        Assert.Equal((byte)firstLengthByte, packet[HDHomeRunPacketBuilder.HeaderSize + 1]);
        if (secondLengthByte >= 0)
        {
            Assert.Equal((byte)secondLengthByte, packet[HDHomeRunPacketBuilder.HeaderSize + 2]);
        }
        Assert.True(read);
        Assert.Equal(HDHomeRunTagType.BaseUrl, tag);
        Assert.Equal(value, decodedValue.ToArray());
        Assert.False(reader.HasError);
    }

    /// <summary>
    /// Verifies values beyond the maximum supported packet size are rejected.
    /// </summary>
    [Fact]
    public void AddTag_WithValueBeyondPacketMaximum_ThrowsArgumentOutOfRangeException()
    {
        // Arrange
        var value = new byte[1450];
        var builder = new HDHomeRunPacketBuilder();

        // Act
        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => builder.AddTag(HDHomeRunTagType.BaseUrl, value));

        // Assert
        Assert.Equal("value", exception.ParamName);
    }
}
