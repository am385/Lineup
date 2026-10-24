using Lineup.Common.Configuration;
using Lineup.Common.Streaming;

namespace Lineup.Extensibility.Configuration;

/// <summary>Exposes portable editing behavior for one adapter configuration.</summary>
public interface IAdapterSettingsProvider
{
    /// <summary>Gets the frontend-neutral descriptor.</summary>
    AdapterSettingsDescriptor SettingsDescriptor { get; }

    /// <summary>Reads current editable values without exposing persisted secrets.</summary>
    AdapterSettingsSnapshot GetSettings(AdapterConfiguration configuration);

    /// <summary>Applies frontend edits and performs adapter-owned normalization.</summary>
    AdapterSettingsApplyResult ApplySettings(
        AdapterConfiguration configuration,
        IReadOnlyList<AdapterSettingEdit> edits);
}

/// <summary>Exposes frontend-neutral lifecycle operations for adapter-owned access grants.</summary>
public interface IAdapterAccessGrantProvider
{
    /// <summary>Gets safely displayable access grants without exposing persisted hashes.</summary>
    IReadOnlyList<AdapterAccessGrantSnapshot> GetAccessGrants(AdapterConfiguration configuration);

    /// <summary>Creates one named access grant and returns its plaintext secret once.</summary>
    AdapterAccessGrantMutationResult CreateAccessGrant(
        AdapterConfiguration configuration,
        string name,
        DateTime? expiresAtUtc,
        DateTime utcNow);

    /// <summary>Rotates one access grant and returns its replacement plaintext secret once.</summary>
    AdapterAccessGrantMutationResult RotateAccessGrant(
        AdapterConfiguration configuration,
        Guid grantId,
        DateTime utcNow);

    /// <summary>Revokes one access grant permanently.</summary>
    AdapterAccessGrantMutationResult RevokeAccessGrant(
        AdapterConfiguration configuration,
        Guid grantId,
        DateTime utcNow);

    /// <summary>Permanently removes one revoked access grant.</summary>
    AdapterAccessGrantMutationResult PurgeAccessGrant(
        AdapterConfiguration configuration,
        Guid grantId);
}
