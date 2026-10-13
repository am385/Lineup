using Lineup.Common.Configuration;
using Lineup.LineupLink.Protocol;

namespace Lineup.LineupLink.Virtual;

/// <summary>Represents one independently managed credential for a Lineup Link virtual output.</summary>
public sealed record LineupLinkAccessGrant
{
    /// <summary>Gets the stable grant identifier.</summary>
    public required Guid Id { get; init; }

    /// <summary>Gets the user-facing grant name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the persisted SHA-256 bearer-token hash.</summary>
    public required string TokenHash { get; init; }

    /// <summary>Gets when the grant was created in UTC.</summary>
    public required DateTime CreatedAtUtc { get; init; }

    /// <summary>Gets when the token was last rotated in UTC.</summary>
    public DateTime? RotatedAtUtc { get; init; }

    /// <summary>Gets when the grant expires in UTC.</summary>
    public DateTime? ExpiresAtUtc { get; init; }

    /// <summary>Gets when the grant was revoked in UTC.</summary>
    public DateTime? RevokedAtUtc { get; init; }
}

/// <summary>Authenticates Lineup Link bearer tokens and returns their safe grant identity.</summary>
public static class LineupLinkAccessGrantAuthenticator
{
    /// <summary>Authenticates one bearer token against the configured grants.</summary>
    public static LineupLinkAuthenticatedGrant? Authenticate(
        string? token,
        LineupLinkVirtualConfiguration configuration,
        DateTime utcNow)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var grants = configuration.AccessGrants;
        LineupLinkAccessGrant? grant;
        if (!LineupLinkToken.TryGetGrantId(token, out var grantId))
        {
            return null;
        }
        grant = grants.SingleOrDefault(candidate => candidate.Id == grantId);
        if (grant is null || !LineupLinkToken.Verify(token, grant.TokenHash))
        {
            return null;
        }

        if (grant.RevokedAtUtc.HasValue || grant.ExpiresAtUtc <= utcNow)
        {
            return null;
        }

        return new LineupLinkAuthenticatedGrant(grant.Id, grant.Name);
    }
}

/// <summary>Identifies the access grant that authenticated one Lineup Link request.</summary>
/// <param name="Id">The stable grant identifier.</param>
/// <param name="Name">The safe user-facing grant name.</param>
public sealed record LineupLinkAuthenticatedGrant(Guid Id, string Name);

/// <summary>Manages Lineup Link access-grant lifecycle and validation.</summary>
public static class LineupLinkAccessGrantManager
{
    /// <summary>Gets the maximum number of grants allowed on one Lineup Link instance.</summary>
    public const int MaximumGrantCount = 64;

    /// <summary>Gets the maximum access-grant name length.</summary>
    public const int MaximumNameLength = 100;

    /// <summary>Converts persisted grants to frontend-safe snapshots.</summary>
    public static IReadOnlyList<AdapterAccessGrantSnapshot> GetSnapshots(LineupLinkVirtualConfiguration configuration) =>
        configuration.AccessGrants
            .Select(ToSnapshot)
            .OrderBy(grant => grant.CreatedAtUtc)
            .ThenBy(grant => grant.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>Creates one access grant and returns its plaintext token once.</summary>
    public static LineupLinkAccessGrantMutation Create(
        LineupLinkVirtualConfiguration configuration,
        string name,
        DateTime? expiresAtUtc,
        DateTime utcNow)
    {
        var grants = configuration.AccessGrants.ToList();
        var normalizedName = name.Trim();
        var errors = ValidateNewGrant(grants, normalizedName, expiresAtUtc, utcNow);
        if (errors.Count > 0)
        {
            return new LineupLinkAccessGrantMutation(null, null, null, errors);
        }

        var generated = LineupLinkToken.Generate(Guid.NewGuid());
        var grant = new LineupLinkAccessGrant
        {
            Id = generated.GrantId,
            Name = normalizedName,
            TokenHash = generated.TokenHash,
            CreatedAtUtc = utcNow,
            ExpiresAtUtc = expiresAtUtc
        };
        grants.Add(grant);
        return Success(configuration, grants, grant, generated.Token);
    }

    /// <summary>Rotates one access grant and returns its replacement plaintext token once.</summary>
    public static LineupLinkAccessGrantMutation Rotate(
        LineupLinkVirtualConfiguration configuration,
        Guid grantId,
        DateTime utcNow)
    {
        var grants = configuration.AccessGrants.ToList();
        var index = grants.FindIndex(grant => grant.Id == grantId);
        if (index < 0)
        {
            return Failure("The selected access grant no longer exists.");
        }
        if (grants[index].RevokedAtUtc.HasValue)
        {
            return Failure("A revoked access grant cannot be rotated.");
        }

        var generated = LineupLinkToken.Generate(grantId);
        var grant = grants[index] with
        {
            TokenHash = generated.TokenHash,
            RotatedAtUtc = utcNow
        };
        grants[index] = grant;
        return Success(configuration, grants, grant, generated.Token);
    }

    /// <summary>Revokes one access grant permanently.</summary>
    public static LineupLinkAccessGrantMutation Revoke(
        LineupLinkVirtualConfiguration configuration,
        Guid grantId,
        DateTime utcNow)
    {
        var grants = configuration.AccessGrants.ToList();
        var index = grants.FindIndex(grant => grant.Id == grantId);
        if (index < 0)
        {
            return Failure("The selected access grant no longer exists.");
        }
        if (grants[index].RevokedAtUtc.HasValue)
        {
            return Failure("The selected access grant is already revoked.");
        }

        var grant = grants[index] with { RevokedAtUtc = utcNow };
        grants[index] = grant;
        return Success(configuration, grants, grant, null);
    }

    /// <summary>Permanently removes one revoked access grant.</summary>
    public static LineupLinkAccessGrantMutation Purge(
        LineupLinkVirtualConfiguration configuration,
        Guid grantId)
    {
        var grants = configuration.AccessGrants.ToList();
        var index = grants.FindIndex(grant => grant.Id == grantId);
        if (index < 0)
        {
            return Failure("The selected access grant no longer exists.");
        }
        if (!grants[index].RevokedAtUtc.HasValue)
        {
            return Failure("Only revoked access grants can be purged.");
        }
        var grant = grants[index];
        grants.RemoveAt(index);
        return Success(configuration, grants, grant, null);
    }

    /// <summary>Validates all configured access grants.</summary>
    public static IReadOnlyList<string> Validate(LineupLinkVirtualConfiguration configuration)
    {
        var grants = configuration.AccessGrants;
        var errors = new List<string>();
        if (grants.Count > MaximumGrantCount)
        {
            errors.Add($"Lineup Link supports at most {MaximumGrantCount} access grants per instance.");
        }
        if (grants.Select(grant => grant.Id).Distinct().Count() != grants.Count)
        {
            errors.Add("Lineup Link access-grant IDs must be unique.");
        }
        if (grants.Select(grant => grant.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != grants.Count)
        {
            errors.Add("Lineup Link access-grant names must be unique.");
        }

        foreach (var grant in grants)
        {
            if (grant.Id == Guid.Empty)
            {
                errors.Add("Lineup Link access-grant IDs cannot be empty.");
            }
            if (string.IsNullOrWhiteSpace(grant.Name) || grant.Name.Trim().Length > MaximumNameLength)
            {
                errors.Add($"Lineup Link access-grant names must contain 1 to {MaximumNameLength} characters.");
            }
            if (!IsValidHash(grant.TokenHash))
            {
                errors.Add($"Lineup Link access grant '{grant.Name}' has an invalid token hash.");
            }
            if (grant.CreatedAtUtc.Kind == DateTimeKind.Local ||
                grant.RotatedAtUtc?.Kind == DateTimeKind.Local ||
                grant.ExpiresAtUtc?.Kind == DateTimeKind.Local ||
                grant.RevokedAtUtc?.Kind == DateTimeKind.Local)
            {
                errors.Add($"Lineup Link access grant '{grant.Name}' timestamps must be UTC or unspecified.");
            }
            if (grant.RotatedAtUtc < grant.CreatedAtUtc)
            {
                errors.Add($"Lineup Link access grant '{grant.Name}' cannot rotate before it is created.");
            }
            if (grant.ExpiresAtUtc < grant.CreatedAtUtc)
            {
                errors.Add($"Lineup Link access grant '{grant.Name}' cannot expire before it is created.");
            }
            if (grant.RevokedAtUtc < grant.CreatedAtUtc)
            {
                errors.Add($"Lineup Link access grant '{grant.Name}' cannot be revoked before it is created.");
            }
        }
        return errors;
    }

    private static AdapterAccessGrantSnapshot ToSnapshot(LineupLinkAccessGrant grant) =>
        new()
        {
            Id = grant.Id,
            Name = grant.Name,
            CreatedAtUtc = grant.CreatedAtUtc,
            RotatedAtUtc = grant.RotatedAtUtc,
            ExpiresAtUtc = grant.ExpiresAtUtc,
            RevokedAtUtc = grant.RevokedAtUtc
        };

    private static IReadOnlyList<string> ValidateNewGrant(
        IReadOnlyCollection<LineupLinkAccessGrant> grants,
        string name,
        DateTime? expiresAtUtc,
        DateTime utcNow)
    {
        var errors = new List<string>();
        if (grants.Count >= MaximumGrantCount)
        {
            errors.Add($"Lineup Link supports at most {MaximumGrantCount} access grants per instance.");
        }
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaximumNameLength)
        {
            errors.Add($"Access-grant names must contain 1 to {MaximumNameLength} characters.");
        }
        if (grants.Any(grant => string.Equals(grant.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            errors.Add("Access-grant names must be unique.");
        }
        if (expiresAtUtc <= utcNow)
        {
            errors.Add("Access-grant expiration must be in the future.");
        }
        return errors;
    }

    private static bool IsValidHash(string hash)
    {
        try
        {
            return Convert.FromBase64String(hash).Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static LineupLinkAccessGrantMutation Success(
        LineupLinkVirtualConfiguration configuration,
        IReadOnlyList<LineupLinkAccessGrant> grants,
        LineupLinkAccessGrant grant,
        string? token) =>
        new(
            configuration with { AccessGrants = grants },
            ToSnapshot(grant),
            token,
            []);

    private static LineupLinkAccessGrantMutation Failure(string error) =>
        new(null, null, null, [error]);
}

/// <summary>Returns one Lineup Link access-grant lifecycle mutation.</summary>
/// <param name="Configuration">The updated configuration.</param>
/// <param name="Grant">The affected safe grant snapshot.</param>
/// <param name="GeneratedToken">The generated plaintext token shown once.</param>
/// <param name="Errors">Validation failures.</param>
public sealed record LineupLinkAccessGrantMutation(
    LineupLinkVirtualConfiguration? Configuration,
    AdapterAccessGrantSnapshot? Grant,
    string? GeneratedToken,
    IReadOnlyList<string> Errors);
