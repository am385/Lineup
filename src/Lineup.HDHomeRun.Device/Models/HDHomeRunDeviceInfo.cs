namespace Lineup.HDHomeRun.Device.Models;

/// <summary>
/// Represents device information from the HDHomeRun device discovery endpoint (discover.json)
/// Contains hardware, firmware, and network information about the device
/// </summary>
public record HDHomeRunDeviceInfo
{
    /// <summary>
    /// User-friendly name of the device (e.g., "HDHomeRun FLEX 4K")
    /// </summary>
    public required string FriendlyName { get; init; }

    /// <summary>
    /// Model number of the device (e.g., "HDFX-4K")
    /// </summary>
    public required string ModelNumber { get; init; }

    /// <summary>
    /// Firmware variant name (e.g., "hdhomerun_dvr_atsc3")
    /// </summary>
    public required string FirmwareName { get; init; }

    /// <summary>
    /// Firmware version identifier (e.g., "20250815")
    /// </summary>
    public required string FirmwareVersion { get; init; }

    /// <summary>
    /// Unique device identifier
    /// </summary>
    public required string DeviceID { get; init; }

    /// <summary>
    /// Authentication token required for API access
    /// </summary>
    public required string DeviceAuth { get; init; }

    /// <summary>
    /// Base URL for accessing the device (e.g., "http://192.168.1.200")
    /// </summary>
    public required string BaseURL { get; init; }

    /// <summary>
    /// URL endpoint for retrieving the channel lineup
    /// </summary>
    public required string LineupURL { get; init; }

    /// <summary>
    /// Number of tuners available on the device
    /// </summary>
    public required int TunerCount { get; init; }
}
