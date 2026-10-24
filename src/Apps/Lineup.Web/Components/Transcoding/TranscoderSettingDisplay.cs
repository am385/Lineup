using Microsoft.Extensions.Localization;

namespace Lineup.Web.Components.Transcoding;

/// <summary>
/// Provides user-facing labels for transcoder setting values.
/// </summary>
internal static class TranscoderSettingDisplay
{
    /// <summary>Gets a deinterlacing policy label.</summary>
    internal static string Deinterlace(SourceDeinterlacePolicy value, IStringLocalizer<UiStrings> text) =>
        value switch
        {
            SourceDeinterlacePolicy.Preserve => text["Transcoding_DeinterlacePreserve"],
            SourceDeinterlacePolicy.SourceFrameRate => text["Transcoding_DeinterlaceSourceFrameRate"],
            SourceDeinterlacePolicy.SourceFieldRate => text["Transcoding_DeinterlaceSourceFieldRate"],
            _ => value.ToString()
        };

    /// <summary>Gets an audio policy label.</summary>
    internal static string Audio(OutputAudioPolicy value, IStringLocalizer<UiStrings> text) =>
        value switch
        {
            OutputAudioPolicy.Copy => text["Transcoding_AudioCopy"],
            OutputAudioPolicy.Ac3 => "AC-3",
            OutputAudioPolicy.Eac3 => "E-AC-3",
            _ => value.ToString()
        };

    /// <summary>Gets a video encoder preset label.</summary>
    internal static string Preset(VideoEncoderPreset value, IStringLocalizer<UiStrings> text) =>
        value switch
        {
            VideoEncoderPreset.VeryFast => text["Transcoding_PresetVeryFast"],
            VideoEncoderPreset.Faster => text["Transcoding_PresetFaster"],
            VideoEncoderPreset.Fast => text["Transcoding_PresetFast"],
            VideoEncoderPreset.Medium => text["Transcoding_PresetMedium"],
            _ => value.ToString()
        };
}
