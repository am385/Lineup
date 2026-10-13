namespace Lineup.SiliconDust.HDHomeRun.PhysicalTuner;

/// <summary>
/// Configures a physical HDHomeRun device.
/// </summary>
public sealed record HDHomeRunPhysicalTunerConfiguration
{
    /// <summary>Gets the device host name, IP address, or absolute HTTP address.</summary>
    public required string Address { get; init; }
}
