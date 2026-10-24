using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lineup.LineupLink.Protocol;

/// <summary>Identifies whether one Lineup Link pair requires encrypted transport.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<LineupLinkTransportSecurity>))]
public enum LineupLinkTransportSecurity
{
    /// <summary>Allow authenticated clear-text HTTP transport.</summary>
    Http,

    /// <summary>Require HTTPS transport.</summary>
    Https
}

/// <summary>Creates and verifies Lineup Link bearer tokens.</summary>
public static class LineupLinkToken
{
    private const string TokenPrefix = "llk1_";

    /// <summary>Creates a new 256-bit URL-safe bearer token.</summary>
    public static string Generate()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Creates a new self-identifying 256-bit bearer token for one access grant.</summary>
    public static LineupLinkGeneratedToken Generate(Guid grantId)
    {
        if (grantId == Guid.Empty)
        {
            throw new ArgumentException("An access-grant ID is required.", nameof(grantId));
        }

        var token = $"{TokenPrefix}{grantId:N}_{Generate()}";
        return new LineupLinkGeneratedToken(grantId, token, Hash(token));
    }

    /// <summary>Creates the persisted SHA-256 token hash.</summary>
    public static string Hash(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }

    /// <summary>Verifies one token against a persisted hash in fixed time.</summary>
    public static bool Verify(string token, string expectedHash)
    {
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(expectedHash))
        {
            return false;
        }

        byte[] expected;
        try
        {
            expected = Convert.FromBase64String(expectedHash);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return actual.Length == expected.Length && CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>Reads the access-grant identifier from a self-identifying token.</summary>
    public static bool TryGetGrantId(string token, out Guid grantId)
    {
        grantId = Guid.Empty;
        if (!token.StartsWith(TokenPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var separator = token.IndexOf('_', TokenPrefix.Length);
        return separator > TokenPrefix.Length &&
            Guid.TryParseExact(token[TokenPrefix.Length..separator], "N", out grantId) &&
            separator < token.Length - 1;
    }
}

/// <summary>Contains one generated Lineup Link token and its persisted representation.</summary>
/// <param name="GrantId">The stable access-grant identifier.</param>
/// <param name="Token">The plaintext bearer token shown once.</param>
/// <param name="TokenHash">The persisted SHA-256 token hash.</param>
public sealed record LineupLinkGeneratedToken(Guid GrantId, string Token, string TokenHash);

/// <summary>Portable connection data copied from a remote virtual device to a local physical device.</summary>
public sealed record LineupLinkConnectionBundle
{
    /// <summary>Gets the connection-bundle schema version.</summary>
    public int BundleVersion { get; init; } = LineupLinkConnectionBundleCodec.Version;

    /// <summary>Gets the required Lineup Link wire-protocol version.</summary>
    public int ProtocolVersion { get; init; } = LineupLinkProtocol.Version;

    /// <summary>Gets the remote Lineup base URL, or null when the bundle creator has not assigned one.</summary>
    public string? BaseUrl { get; init; }

    /// <summary>Gets the remote virtual-device identity.</summary>
    public required Guid VirtualDeviceId { get; init; }

    /// <summary>Gets the bearer token.</summary>
    public required string AccessToken { get; init; }

    /// <summary>Gets the required transport security.</summary>
    public LineupLinkTransportSecurity TransportSecurity { get; init; } = LineupLinkTransportSecurity.Https;

    /// <summary>Gets an optional SHA-256 server-certificate fingerprint.</summary>
    public string? CertificateSha256 { get; init; }
}

/// <summary>Serializes and validates Lineup Link connection bundles.</summary>
public static class LineupLinkConnectionBundleCodec
{
    /// <summary>Gets the current connection-bundle schema version.</summary>
    public const int Version = 1;

    /// <summary>Gets the placeholder used when displaying a bundle without its raw token.</summary>
    public const string RedactedAccessToken = "[redacted]";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    /// <summary>Serializes a validated connection bundle.</summary>
    public static string Serialize(LineupLinkConnectionBundle bundle)
    {
        Validate(bundle);
        return JsonSerializer.Serialize(bundle, SerializerOptions);
    }

    /// <summary>Serializes a validated connection bundle with its access token redacted.</summary>
    public static string SerializeRedacted(LineupLinkConnectionBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        return Serialize(bundle with { AccessToken = RedactedAccessToken });
    }

    /// <summary>Parses and validates a connection bundle.</summary>
    public static LineupLinkConnectionBundle Deserialize(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        LineupLinkConnectionBundle bundle;
        try
        {
            bundle = JsonSerializer.Deserialize<LineupLinkConnectionBundle>(text, SerializerOptions)
                ?? throw new ArgumentException("The Lineup Link connection bundle is empty.", nameof(text));
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("The Lineup Link connection bundle is not valid JSON.", nameof(text), exception);
        }

        Validate(bundle);
        return bundle;
    }

    /// <summary>Validates one connection bundle.</summary>
    public static void Validate(LineupLinkConnectionBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        if (bundle.BundleVersion != Version)
        {
            throw new ArgumentException($"Unsupported Lineup Link connection bundle version {bundle.BundleVersion}.", nameof(bundle));
        }
        if (bundle.ProtocolVersion != LineupLinkProtocol.Version)
        {
            throw new ArgumentException($"Unsupported Lineup Link protocol version {bundle.ProtocolVersion}.", nameof(bundle));
        }
        if (!string.IsNullOrWhiteSpace(bundle.BaseUrl) &&
            !LineupLinkConfigurationValidation.TryNormalizeBaseUri(bundle.BaseUrl, bundle.TransportSecurity, out _))
        {
            throw new ArgumentException("The Lineup Link base URL does not match the selected transport security.", nameof(bundle));
        }
        if (bundle.VirtualDeviceId == Guid.Empty)
        {
            throw new ArgumentException("A remote virtual-device ID is required.", nameof(bundle));
        }
        if (string.IsNullOrWhiteSpace(bundle.AccessToken))
        {
            throw new ArgumentException("An access token is required.", nameof(bundle));
        }
        if (!LineupLinkConfigurationValidation.IsValidCertificateSha256(bundle.CertificateSha256))
        {
            throw new ArgumentException("The certificate fingerprint must contain 64 hexadecimal SHA-256 characters.", nameof(bundle));
        }
    }
}

/// <summary>Shared Lineup Link configuration validation.</summary>
public static class LineupLinkConfigurationValidation
{
    /// <summary>Normalizes an HTTP or HTTPS base URL when it matches the selected transport mode.</summary>
    public static bool TryNormalizeBaseUri(string? value, LineupLinkTransportSecurity security, out Uri baseUri)
    {
        baseUri = null!;
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var parsed)
            || string.IsNullOrWhiteSpace(parsed.Host)
            || parsed.AbsolutePath != "/"
            || !string.IsNullOrEmpty(parsed.Query)
            || !string.IsNullOrEmpty(parsed.Fragment)
            || !string.IsNullOrEmpty(parsed.UserInfo))
        {
            return false;
        }

        var requiredScheme = security == LineupLinkTransportSecurity.Https ? Uri.UriSchemeHttps : Uri.UriSchemeHttp;
        if (!string.Equals(parsed.Scheme, requiredScheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        baseUri = new UriBuilder(parsed) { Path = "/" }.Uri;
        return true;
    }

    /// <summary>Gets whether an optional SHA-256 certificate fingerprint is valid.</summary>
    public static bool IsValidCertificateSha256(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var normalized = NormalizeCertificateSha256(value);
        return normalized.Length == 64 && normalized.All(Uri.IsHexDigit);
    }

    /// <summary>Normalizes a SHA-256 certificate fingerprint.</summary>
    public static string NormalizeCertificateSha256(string value) =>
        value.Replace(":", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal).Trim().ToUpperInvariant();
}
