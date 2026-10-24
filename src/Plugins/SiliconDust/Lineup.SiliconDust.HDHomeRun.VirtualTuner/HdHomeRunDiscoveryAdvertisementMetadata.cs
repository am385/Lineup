namespace Lineup.SiliconDust.HDHomeRun.VirtualTuner;

/// <summary>Contains HDHomeRun-specific fields carried by a provider-neutral advertisement snapshot.</summary>
public sealed record HdHomeRunDiscoveryAdvertisementMetadata
{
    /// <summary>Gets the checksum-valid external HDHomeRun DeviceID.</summary>
    public required string DeviceId { get; init; }

    /// <summary>Gets the derived virtual device authorization value.</summary>
    public required string DeviceAuth { get; init; }
}
