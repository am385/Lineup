using Lineup.Common.Configuration;

namespace Lineup.Common.Streaming;

/// <summary>Describes one supported operating-system and architecture pair.</summary>
public sealed record VirtualOutputWorkerPlatform
{
    /// <summary>Gets the normalized operating-system identifier.</summary>
    public required string OperatingSystem { get; init; }

    /// <summary>Gets the normalized process architecture identifier.</summary>
    public required string Architecture { get; init; }
}

/// <summary>Declares one virtual-output worker without loading executable plugin code.</summary>
public sealed record VirtualOutputWorkerManifest
{
    /// <summary>Gets the manifest schema version.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Gets the stable worker package identifier.</summary>
    public required string WorkerId { get; init; }

    /// <summary>Gets the package version.</summary>
    public required string Version { get; init; }

    /// <summary>Gets the human-readable publisher identity.</summary>
    public required string Publisher { get; init; }

    /// <summary>Gets the executable path relative to the installed package root.</summary>
    public required string ExecutablePath { get; init; }

    /// <summary>Gets the lowercase hexadecimal SHA-256 executable hash.</summary>
    public required string ExecutableSha256 { get; init; }

    /// <summary>Gets an optional detached package-signature reference.</summary>
    public string? SignatureReference { get; init; }

    /// <summary>Gets the virtual adapter kinds implemented by the executable.</summary>
    public IReadOnlyList<string> AdapterKinds { get; init; } = [];

    /// <summary>Gets supported execution platforms.</summary>
    public IReadOnlyList<VirtualOutputWorkerPlatform> Platforms { get; init; } = [];

    /// <summary>Gets the minimum supported IPC protocol version.</summary>
    public int MinimumProtocolVersion { get; init; }

    /// <summary>Gets the maximum supported IPC protocol version.</summary>
    public int MaximumProtocolVersion { get; init; }

    /// <summary>Gets the minimum compatible Lineup version.</summary>
    public required string MinimumLineupVersion { get; init; }

    /// <summary>Gets an optional maximum compatible Lineup version.</summary>
    public string? MaximumLineupVersion { get; init; }

    /// <summary>Gets the declarative adapter settings schema.</summary>
    public required AdapterSettingsDescriptor SettingsDescriptor { get; init; }
}
