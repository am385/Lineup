using Lineup.Common.Guides;
using Lineup.Extensibility.Guides;
using Lineup.SiliconDust.HDHomeRun.Device.Models;

namespace Lineup.SiliconDust.HDHomeRun.PhysicalTuner;

/// <summary>
/// Maps provider-specific HDHomeRun lineup channels to Core's neutral channel contract.
/// </summary>
public static class HDHomeRunChannelMapper
{
    /// <summary>
    /// Maps one HDHomeRun lineup channel.
    /// </summary>
    /// <param name="channel">Provider-specific channel.</param>
    /// <returns>The neutral Core channel.</returns>
    public static TunerChannel ToCore(HDHomeRunChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return new TunerChannel
        {
            GuideNumber = channel.GuideNumber,
            GuideName = channel.GuideName,
            VideoCodec = channel.VideoCodec,
            AudioCodec = channel.AudioCodec,
            HD = channel.HD,
            DRM = channel.DRM,
            Favorite = channel.Favorite,
            Tags = channel.Tags,
            SignalStrength = channel.SignalStrength,
            SignalQuality = channel.SignalQuality,
            URL = channel.URL,
            AdditionalProperties = channel.AdditionalProperties
        };
    }
}
