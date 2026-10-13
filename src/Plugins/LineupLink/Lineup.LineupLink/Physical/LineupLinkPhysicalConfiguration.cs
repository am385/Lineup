using Lineup.LineupLink.Protocol;

namespace Lineup.LineupLink.Physical;

/// <summary>Configures one remote Lineup Link physical input.</summary>
public sealed record LineupLinkPhysicalConfiguration
{
    /// <summary>Gets the remote Lineup base URL.</summary>
    public string BaseUrl { get; init; } = "https://lineup.local:8443";

    /// <summary>Gets the remote Lineup Link virtual-device identity.</summary>
    public Guid VirtualDeviceId { get; init; }

    /// <summary>Gets the bearer token used for remote requests.</summary>
    public string AccessToken { get; init; } = string.Empty;

    /// <summary>Gets the selected transport security.</summary>
    public LineupLinkTransportSecurity TransportSecurity { get; init; } = LineupLinkTransportSecurity.Https;

    /// <summary>Gets an optional SHA-256 server-certificate fingerprint.</summary>
    public string? CertificateSha256 { get; init; }
}
