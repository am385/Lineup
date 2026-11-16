namespace Lineup.Plugin.SiliconDust.HDHomeRun.PhysicalTuner;

/// <summary>
/// Configures a physical HDHomeRun device.
/// </summary>
public sealed record HDHomeRunPhysicalTunerConfiguration
{
    /// <summary>Gets the device host name, IP address, or absolute HTTP/HTTPS root URL.</summary>
    /// <remarks>The root URL configures HTTP access; native control uses its host without the scheme or port.</remarks>
    public required string Address { get; init; }

    /// <summary>Gets the EXTEND video profile, or <c>default</c> to leave the device's stream request unchanged.</summary>
    /// <remarks>Device default does not force native video. Audio is not transcoded by EXTEND.</remarks>
    public string TranscodeProfile { get; init; } = "default";

    /// <summary>Checks whether a value is device default or an official HTTP transcode profile.</summary>
    internal static bool IsValidProfile(string? profile) =>
        profile is "default" or "heavy" or "mobile" or "internet540" or "internet480" or "internet360" or "internet240";
}
