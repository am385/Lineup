namespace Lineup.LineupLink.Virtual;

/// <summary>Configures one Lineup Link virtual output.</summary>
public sealed record LineupLinkVirtualConfiguration
{
    /// <summary>Gets the stable provider identity used to scope exported channel keys.</summary>
    public Guid InstanceId { get; init; } = Guid.NewGuid();

    /// <summary>Gets the externally reachable Lineup base URL used in connection bundles.</summary>
    public string PublicBaseUrl { get; init; } = string.Empty;

    /// <summary>Gets whether requests must use HTTPS.</summary>
    public bool RequireEncryption { get; init; } = true;

    /// <summary>Gets independently managed access grants.</summary>
    public IReadOnlyList<LineupLinkAccessGrant> AccessGrants { get; init; } = [];

    /// <summary>Gets an optional SHA-256 server-certificate fingerprint included in connection bundles.</summary>
    public string? CertificateSha256 { get; init; }
}
