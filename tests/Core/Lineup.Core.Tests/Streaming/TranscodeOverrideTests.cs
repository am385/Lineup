using Lineup.Common.Streaming;
using Lineup.Extensibility.Streaming;
using Xunit;

namespace Lineup.Core.Tests.Streaming;

/// <summary>
/// Verifies sparse transcoding inheritance.
/// </summary>
public sealed class TranscodeOverrideTests
{
    /// <summary>
    /// Verifies unspecified virtual values continue to inherit global defaults.
    /// </summary>
    [Fact]
    public void Apply_VirtualSparseOverride_InheritsUnspecifiedDefaults()
    {
        // Arrange
        var defaults = new VirtualTranscodeProfile
        {
            Audio = OutputAudioPolicy.Eac3,
            Ac4Audio = OutputAudioPolicy.Ac3,
            ConvertHevcToH264 = false,
            Deinterlace = SourceDeinterlacePolicy.SourceFrameRate,
            VideoPreset = VideoEncoderPreset.Fast,
            VideoQuality = 19,
            MaximumVideoBitRateMbps = 12
        };
        var overrides = new VirtualTranscodeOverrides { ConvertHevcToH264 = true };

        // Act
        var effective = overrides.Apply(defaults);

        // Assert
        Assert.True(effective.ConvertHevcToH264);
        Assert.Equal(OutputAudioPolicy.Eac3, effective.Audio);
        Assert.Equal(OutputAudioPolicy.Ac3, effective.Ac4Audio);
        Assert.Equal(SourceDeinterlacePolicy.SourceFrameRate, effective.Deinterlace);
        Assert.Equal(VideoEncoderPreset.Fast, effective.VideoPreset);
        Assert.Equal(19, effective.VideoQuality);
        Assert.Equal(12, effective.MaximumVideoBitRateMbps);
    }

    /// <summary>
    /// Verifies unspecified physical values continue to inherit global defaults.
    /// </summary>
    [Fact]
    public void Apply_PhysicalSparseOverride_InheritsUnspecifiedDefaults()
    {
        // Arrange
        var defaults = new PhysicalTranscodeProfile
        {
            ConvertHevcToH264 = true,
            Deinterlace = SourceDeinterlacePolicy.SourceFieldRate,
            Audio = OutputAudioPolicy.Ac3,
            Ac4Audio = OutputAudioPolicy.Eac3,
            VideoPreset = VideoEncoderPreset.Medium,
            VideoQuality = 18,
            MaximumVideoBitRateMbps = 20
        };
        var overrides = new PhysicalTranscodeOverrides { VideoQuality = 23 };

        // Act
        var effective = overrides.Apply(defaults);

        // Assert
        Assert.True(effective.ConvertHevcToH264);
        Assert.Equal(SourceDeinterlacePolicy.SourceFieldRate, effective.Deinterlace);
        Assert.Equal(OutputAudioPolicy.Ac3, effective.Audio);
        Assert.Equal(OutputAudioPolicy.Eac3, effective.Ac4Audio);
        Assert.Equal(VideoEncoderPreset.Medium, effective.VideoPreset);
        Assert.Equal(23, effective.VideoQuality);
        Assert.Equal(20, effective.MaximumVideoBitRateMbps);
    }
}
