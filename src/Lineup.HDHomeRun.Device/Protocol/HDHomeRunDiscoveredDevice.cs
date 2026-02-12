using System.Net;

namespace Lineup.HDHomeRun.Device.Protocol;

/// <summary>
/// Represents information about a discovered HDHomeRun device
/// </summary>
public record HDHomeRunDiscoveredDevice
{
    /// <summary>
    /// The IP address of the device
    /// </summary>
    public required IPAddress IpAddress { get; init; }

    /// <summary>
    /// The unique device ID (8 hex characters)
    /// </summary>
    public required uint DeviceId { get; init; }

    /// <summary>
    /// The device type
    /// </summary>
    public required HDHomeRunDeviceType DeviceType { get; init; }

    /// <summary>
    /// Number of tuners on the device
    /// </summary>
    public int TunerCount { get; init; }

    /// <summary>
    /// Base URL for HTTP API
    /// </summary>
    public string? BaseUrl { get; init; }

    /// <summary>
    /// Lineup URL for channel information
    /// </summary>
    public string? LineupUrl { get; init; }

    /// <summary>
    /// Device authorization string
    /// </summary>
    public string? DeviceAuth { get; init; }

    /// <summary>
    /// Storage ID (for DVR devices)
    /// </summary>
    public string? StorageId { get; init; }

    /// <summary>
    /// Storage URL (for DVR devices)
    /// </summary>
    public string? StorageUrl { get; init; }

    /// <summary>
    /// Gets the device ID as a hex string (e.g., "1040ABCD")
    /// </summary>
    public string DeviceIdHex => DeviceId.ToString("X8");

    /// <summary>
    /// Gets the friendly device name
    /// </summary>
    public string FriendlyName => $"HDHomeRun {DeviceIdHex}";

    public override string ToString() => $"{FriendlyName} @ {IpAddress}";
}
