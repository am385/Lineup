namespace Lineup.Web.Components.Transcoding;

/// <summary>
/// Provides user-facing labels for transcoder setting values.
/// </summary>
internal static class TranscoderSettingDisplay
{
    /// <summary>Gets a deinterlacing policy label.</summary>
    internal static string Deinterlace(SourceDeinterlacePolicy value) =>
        value switch
        {
            SourceDeinterlacePolicy.Preserve => "Preserve",
            SourceDeinterlacePolicy.SourceFrameRate => "Source frame rate",
            SourceDeinterlacePolicy.SourceFieldRate => "Source field rate",
            _ => value.ToString()
        };

    /// <summary>Gets an audio policy label.</summary>
    internal static string Audio(OutputAudioPolicy value) =>
        value switch
        {
            OutputAudioPolicy.Copy => "Copy",
            OutputAudioPolicy.Ac3 => "AC-3",
            OutputAudioPolicy.Eac3 => "E-AC-3",
            _ => value.ToString()
        };

    /// <summary>Gets a video encoder preset label.</summary>
    internal static string Preset(VideoEncoderPreset value) =>
        value switch
        {
            VideoEncoderPreset.VeryFast => "Very fast",
            VideoEncoderPreset.Faster => "Faster",
            VideoEncoderPreset.Fast => "Fast",
            VideoEncoderPreset.Medium => "Medium",
            _ => value.ToString()
        };
}
