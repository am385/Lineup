namespace Lineup.HDHomeRun.Device.Protocol;

/// <summary>
/// HDHomeRun protocol tag types used in TLV (Type-Length-Value) packets
/// </summary>
public enum HDHomeRunTagType : byte
{
    /// <summary>Device type tag</summary>
    DeviceType = 0x01,

    /// <summary>Device ID tag (32-bit unique identifier)</summary>
    DeviceId = 0x02,

    /// <summary>Get/Set variable name tag</summary>
    GetSetName = 0x03,

    /// <summary>Get/Set variable value tag</summary>
    GetSetValue = 0x04,

    /// <summary>Get/Set lock key tag</summary>
    GetSetLockkey = 0x15,

    /// <summary>Error message tag</summary>
    ErrorMessage = 0x05,

    /// <summary>Tuner count tag</summary>
    TunerCount = 0x10,

    /// <summary>Device authorization string</summary>
    DeviceAuthStr = 0x29,

    /// <summary>Base URL for HTTP API</summary>
    BaseUrl = 0x2A,

    /// <summary>Device authorization binary</summary>
    DeviceAuthBin = 0x2B,

    /// <summary>Storage ID</summary>
    StorageId = 0x2C,

    /// <summary>Storage URL</summary>
    StorageUrl = 0x2D,

    /// <summary>Lineup URL</summary>
    LineupUrl = 0x27
}
