using Xunit;

namespace Lineup.Core.Tests.Streaming;

/// <summary>
/// Verifies sparse transcoding inheritance.
/// </summary>
public sealed class TranscodeOverrideTests
{
    /// <summary>Verifies experimental compensation inherits unless explicitly enabled or disabled, including configurations saved before the option existed.</summary>
    [Theory]
    [InlineData("{}", null, false)]
    [InlineData("{}", true, true)]
    [InlineData("""{"Ac4TimestampCompensation":true}""", null, true)]
    [InlineData("""{"Ac4TimestampCompensation":true}""", false, false)]
    public void Apply_Ac4TimestampCompensation_PreservesOptInAndExplicitDisable(string json, bool? compensate, bool expected)
    {
        // Arrange
        var physical = System.Text.Json.JsonSerializer.Deserialize<PhysicalTranscodeProfile>(json)!;
        var virtualProfile = System.Text.Json.JsonSerializer.Deserialize<VirtualTranscodeProfile>(json)!;

        // Act
        var physicalEffective = new PhysicalTranscodeOverrides { Ac4TimestampCompensation = compensate }.Apply(physical);
        var virtualEffective = new VirtualTranscodeOverrides { Ac4TimestampCompensation = compensate }.Apply(virtualProfile);

        // Assert
        Assert.Equal(expected, physicalEffective.Ac4TimestampCompensation);
        Assert.Equal(expected, virtualEffective.Ac4TimestampCompensation);
    }

    /// <summary>Verifies a device can inherit GPU settings or explicitly clear every field by choosing Software.</summary>
    [Fact]
    public void Apply_HardwareSettings_InheritsOrReplacesAsOneUnit()
    {
        // Arrange
        var hardware = new HardwareEncodingSettings { Encoder = HardwareVideoEncoder.Nvenc, Device = "1", AdvancedArguments = "preset=p6", AllowSoftwareFallback = true };
        var physical = new PhysicalTranscodeProfile { HardwareEncoding = hardware };
        var virtualProfile = new VirtualTranscodeProfile { HardwareEncoding = hardware };

        // Act
        var inherited = new PhysicalTranscodeOverrides().Apply(physical);
        var software = new PhysicalTranscodeOverrides { HardwareEncoding = new() }.Apply(physical);
        var virtualSoftware = new VirtualTranscodeOverrides { HardwareEncoding = new() }.Apply(virtualProfile);

        // Assert
        Assert.Equal(hardware, inherited.HardwareEncoding);
        Assert.Equal(new HardwareEncodingSettings(), software.HardwareEncoding);
        Assert.Equal(new HardwareEncodingSettings(), virtualSoftware.HardwareEncoding);
    }

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
