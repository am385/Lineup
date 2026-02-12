namespace Lineup.HDHomeRun.Device;

/// <summary>
/// Constants for HDHomeRun device HTTP API endpoints.
/// </summary>
public static class DeviceEndpoints
{
    /// <summary>
    /// Device discovery endpoint path.
    /// </summary>
    public const string DiscoverJson = "discover.json";

    /// <summary>
    /// Channel lineup endpoint path.
    /// </summary>
    public const string LineupJson = "lineup.json";

    /// <summary>
    /// Default streaming port for HDHomeRun devices.
    /// </summary>
    public const int StreamingPort = 5004;
}
