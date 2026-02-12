namespace Lineup.HDHomeRun.Device.Protocol;

/// <summary>
/// HDHomeRun protocol packet types
/// </summary>
public enum HDHomeRunPacketType : ushort
{
    /// <summary>Discovery request packet</summary>
    DiscoverRequest = 0x0002,

    /// <summary>Discovery reply packet</summary>
    DiscoverReply = 0x0003,

    /// <summary>Get/Set request packet</summary>
    GetSetRequest = 0x0004,

    /// <summary>Get/Set reply packet</summary>
    GetSetReply = 0x0005,

    /// <summary>Upgrade request packet</summary>
    UpgradeRequest = 0x0006,

    /// <summary>Upgrade reply packet</summary>
    UpgradeReply = 0x0007
}
