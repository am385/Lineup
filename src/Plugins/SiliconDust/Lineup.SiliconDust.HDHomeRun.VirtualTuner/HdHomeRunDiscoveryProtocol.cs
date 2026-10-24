using System.Text;
using Lineup.SiliconDust.HDHomeRun.Device.Protocol;

namespace Lineup.SiliconDust.HDHomeRun.VirtualTuner;

/// <summary>Immutable network identity advertised for one virtual HDHomeRun device.</summary>
public sealed record HdHomeRunAdvertisedDevice
{
    /// <summary>Gets the numeric virtual DeviceID.</summary>
    public required uint DeviceId { get; init; }

    /// <summary>Gets the virtual authorization string.</summary>
    public required string DeviceAuth { get; init; }

    /// <summary>Gets the effective number of tuners.</summary>
    public required byte TunerCount { get; init; }

    /// <summary>Gets the explicitly configured device base URI.</summary>
    public required Uri BaseUri { get; init; }

    /// <summary>Gets the virtual device UUID used by SSDP.</summary>
    public string Uuid => DeviceId.ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Parses SiliconDust discovery requests and creates protocol-compatible replies.</summary>
public static class HdHomeRunDiscoveryProtocol
{
    /// <summary>Determines whether a datagram is a valid SiliconDust discovery request.</summary>
    /// <param name="request">Raw SiliconDust packet.</param>
    /// <returns><see langword="true"/> when the packet is a valid discovery request.</returns>
    public static bool IsDiscoveryRequest(ReadOnlySpan<byte> request)
    {
        var reader = new HDHomeRunPacketReader(request);
        if (!reader.IsValid || reader.PacketType != HDHomeRunPacketType.DiscoverRequest)
        {
            return false;
        }
        while (reader.TryReadTag(out _, out _))
        {
        }
        return !reader.HasError;
    }

    /// <summary>Creates a discovery reply when a request targets the advertised tuner.</summary>
    /// <param name="request">Raw SiliconDust discovery packet.</param>
    /// <param name="device">Virtual device to match and advertise.</param>
    /// <param name="reply">CRC-valid discovery reply, when matched.</param>
    /// <returns><see langword="true"/> when the request is valid and matches the device.</returns>
    public static bool TryCreateReply(ReadOnlySpan<byte> request, HdHomeRunAdvertisedDevice device, out byte[]? reply)
    {
        ArgumentNullException.ThrowIfNull(device);
        reply = null;
        var reader = new HDHomeRunPacketReader(request);
        if (!reader.IsValid || reader.PacketType != HDHomeRunPacketType.DiscoverRequest)
        {
            return false;
        }

        uint? requestedDeviceId = null;
        HDHomeRunDeviceType? requestedDeviceType = null;
        while (reader.TryReadTag(out var tag, out var value))
        {
            if (tag == HDHomeRunTagType.DeviceId)
            {
                if (value.Length != sizeof(uint))
                {
                    return false;
                }
                requestedDeviceId = HDHomeRunPacketReader.ReadUInt32(value);
            }
            else if (tag == HDHomeRunTagType.DeviceType)
            {
                if (value.Length != sizeof(uint))
                {
                    return false;
                }
                requestedDeviceType = (HDHomeRunDeviceType)HDHomeRunPacketReader.ReadUInt32(value);
            }
        }

        if (reader.HasError ||
            requestedDeviceId is not null && requestedDeviceId != HDHomeRunDeviceId.Wildcard && requestedDeviceId != device.DeviceId ||
            requestedDeviceType is not null && requestedDeviceType != HDHomeRunDeviceType.Wildcard && requestedDeviceType != HDHomeRunDeviceType.Tuner)
        {
            return false;
        }

        reply = new HDHomeRunPacketBuilder()
            .AddTag(HDHomeRunTagType.DeviceType, (uint)HDHomeRunDeviceType.Tuner)
            .AddTag(HDHomeRunTagType.DeviceId, device.DeviceId)
            .AddTag(HDHomeRunTagType.TunerCount, [device.TunerCount])
            .AddTag(HDHomeRunTagType.BaseUrl, NullTerminate(device.BaseUri.AbsoluteUri.TrimEnd('/')))
            .AddTag(HDHomeRunTagType.LineupUrl, NullTerminate(new Uri(device.BaseUri, "lineup.json").AbsoluteUri))
            .AddTag(HDHomeRunTagType.DeviceAuthStr, NullTerminate(device.DeviceAuth))
            .Build(HDHomeRunPacketType.DiscoverReply);
        return true;
    }

    private static byte[] NullTerminate(string value) => [.. Encoding.UTF8.GetBytes(value), 0];
}
