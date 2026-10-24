namespace Lineup.SiliconDust.HDHomeRun.PhysicalTuner;

/// <summary>
/// Configures a physical HDHomeRun device.
/// </summary>
public sealed record HDHomeRunPhysicalTunerConfiguration
{
    /// <summary>Gets the device host name, IP address, or absolute HTTP/HTTPS root URL.</summary>
    /// <remarks>The root URL configures HTTP access; native control uses its host without the scheme or port.</remarks>
    public required string Address { get; init; }
}
