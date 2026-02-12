using System.Text.Json.Serialization;
using Lineup.HDHomeRun.Device.Json;

namespace Lineup.HDHomeRun.Device.Models;

/// <summary>
/// Represents a channel from the HDHomeRun device lineup (lineup.json)
/// This is the local device's channel information
/// </summary>
public record HDHomeRunChannel
{
    /// <summary>
    /// Channel number (e.g., "2.1", "5.1")
    /// </summary>
    public required string GuideNumber { get; init; }

    /// <summary>
    /// Display name of the channel (e.g., "WFMY-HD", "PBS NC")
    /// </summary>
    public required string GuideName { get; init; }

    /// <summary>
    /// Video codec used by the channel (e.g., "MPEG2", "H264", "HEVC")
    /// </summary>
    public string? VideoCodec { get; init; }

    /// <summary>
    /// Audio codec used by the channel (e.g., "AC3", "AC4")
    /// </summary>
    public string? AudioCodec { get; init; }

    /// <summary>
    /// Indicates if the channel is HD
    /// </summary>
    [JsonConverter(typeof(BoolToIntOrNullConverter))]
    public bool HD { get; init; }

    /// <summary>
    /// Indicates if the channel has DRM protection
    /// </summary>
    [JsonConverter(typeof(BoolToIntOrNullConverter))]
    public bool DRM { get; init; }

    /// <summary>
    /// Indicates if the channel is marked as a favorite
    /// </summary>
    [JsonConverter(typeof(BoolToIntOrNullConverter))]
    public bool Favorite { get; init; }

    /// <summary>
    /// Signal strength percentage (0-100)
    /// </summary>
    public int SignalStrength { get; init; }

    /// <summary>
    /// Signal quality percentage (0-100)
    /// </summary>
    public int SignalQuality { get; init; }

    /// <summary>
    /// Streaming URL for the channel
    /// </summary>
    public required string URL { get; init; }
}
