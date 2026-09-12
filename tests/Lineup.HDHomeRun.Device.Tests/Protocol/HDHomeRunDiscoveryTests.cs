using System.Buffers.Binary;
using System.Net;
using Lineup.HDHomeRun.Device.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lineup.HDHomeRun.Device.Tests.Protocol;

/// <summary>
/// Tests HDHomeRun discovery response parsing.
/// </summary>
public class HDHomeRunDiscoveryTests
{
    /// <summary>
    /// Verifies short fixed-width values are ignored and do not prevent parsing a later valid response.
    /// </summary>
    [Fact]
    public void ParseDiscoveryResponse_WithShortFixedWidthValues_IgnoresPacketsAndParsesSubsequentResponse()
    {
        // Arrange
        using var discovery = new HDHomeRunDiscovery(NullLogger<HDHomeRunDiscovery>.Instance);
        var endpoint = new IPEndPoint(IPAddress.Parse("192.0.2.20"), HDHomeRunDiscovery.DiscoveryPort);
        var shortDeviceId = BuildPacket([(HDHomeRunTagType.DeviceId, new byte[] { 1, 2 })]);
        var shortDeviceType = BuildPacket(
        [
            (HDHomeRunTagType.DeviceId, UInt32Bytes(0x12345678)),
            (HDHomeRunTagType.DeviceType, new byte[] { 1 })
        ]);
        var valid = BuildValidResponse();

        // Act
        var shortDeviceIdResult = discovery.ParseDiscoveryResponse(shortDeviceId, endpoint);
        var shortDeviceTypeResult = discovery.ParseDiscoveryResponse(shortDeviceType, endpoint);
        var validResult = discovery.ParseDiscoveryResponse(valid, endpoint);

        // Assert
        Assert.Null(shortDeviceIdResult);
        Assert.Null(shortDeviceTypeResult);
        Assert.NotNull(validResult);
        Assert.Equal(0x12345678u, validResult.DeviceId);
    }

    /// <summary>
    /// Verifies a truncated trailing TLV is rejected without affecting a subsequent valid response.
    /// </summary>
    [Fact]
    public void ParseDiscoveryResponse_WithMalformedTrailingTlv_IgnoresPacketAndParsesSubsequentResponse()
    {
        // Arrange
        using var discovery = new HDHomeRunDiscovery(NullLogger<HDHomeRunDiscovery>.Instance);
        var endpoint = new IPEndPoint(IPAddress.Parse("192.0.2.21"), HDHomeRunDiscovery.DiscoveryPort);
        var malformed = BuildPacket(
        [
            (HDHomeRunTagType.DeviceId, UInt32Bytes(0x12345678)),
            (HDHomeRunTagType.BaseUrl, new byte[] { 5, (byte)'x' })
        ], rawValues: true);
        var valid = BuildValidResponse();

        // Act
        var malformedResult = discovery.ParseDiscoveryResponse(malformed, endpoint);
        var validResult = discovery.ParseDiscoveryResponse(valid, endpoint);

        // Assert
        Assert.Null(malformedResult);
        Assert.NotNull(validResult);
        Assert.Equal(HDHomeRunDeviceType.Tuner, validResult.DeviceType);
    }

    private static byte[] BuildValidResponse()
        => BuildPacket([(HDHomeRunTagType.DeviceId, UInt32Bytes(0x12345678)), (HDHomeRunTagType.DeviceType, UInt32Bytes((uint)HDHomeRunDeviceType.Tuner))]);

    private static byte[] UInt32Bytes(uint value)
    {
        var bytes = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] BuildPacket((HDHomeRunTagType Tag, byte[] Value)[] tags, bool rawValues = false)
    {
        using var payload = new MemoryStream();
        foreach (var (tag, value) in tags)
        {
            payload.WriteByte((byte)tag);
            if (!rawValues || tag == HDHomeRunTagType.DeviceId)
            {
                payload.WriteByte((byte)value.Length);
            }
            payload.Write(value);
        }

        var payloadBytes = payload.ToArray();
        var packet = new byte[HDHomeRunPacketBuilder.HeaderSize + payloadBytes.Length + HDHomeRunPacketBuilder.CrcSize];
        BinaryPrimitives.WriteUInt16BigEndian(packet, (ushort)HDHomeRunPacketType.DiscoverReply);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)payloadBytes.Length);
        payloadBytes.CopyTo(packet, HDHomeRunPacketBuilder.HeaderSize);
        var crc = HDHomeRunPacketBuilder.CalculateCrc32(packet.AsSpan(0, packet.Length - HDHomeRunPacketBuilder.CrcSize));
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(packet.Length - HDHomeRunPacketBuilder.CrcSize), crc);
        return packet;
    }
}
