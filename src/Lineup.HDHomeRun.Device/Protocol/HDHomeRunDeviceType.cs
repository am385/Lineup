namespace Lineup.HDHomeRun.Device.Protocol;

/// <summary>
/// Known HDHomeRun device types
/// </summary>
public enum HDHomeRunDeviceType : uint
{
    /// <summary>Wildcard to match any device type</summary>
    Wildcard = 0xFFFFFFFF,

    /// <summary>HDHomeRun tuner device</summary>
    Tuner = 0x00000001,

    /// <summary>HDHomeRun storage device (DVR)</summary>
    Storage = 0x00000005
}

/// <summary>
/// Known HDHomeRun device IDs
/// </summary>
public static class HDHomeRunDeviceId
{
    /// <summary>Wildcard to match any device</summary>
    public const uint Wildcard = 0xFFFFFFFF;
}
