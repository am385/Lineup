using System.Globalization;
using Lineup.Common.Streaming;
using Lineup.Extensibility.Streaming;

namespace Lineup.SiliconDust.HDHomeRun.VirtualTuner;

/// <summary>Resolved runtime profile for one enabled virtual HDHomeRun device.</summary>
public sealed record HdHomeRunProxyProfileSnapshot
{
    /// <summary>Gets the configured virtual device.</summary>
    public required VirtualDeviceDefinition VirtualDevice { get; init; }

    /// <summary>Gets whether this profile owns the root protocol routes.</summary>
    public required bool IsPrimary { get; init; }

    /// <summary>Gets the numeric stable virtual DeviceID.</summary>
    public required uint DeviceId { get; init; }

    /// <summary>Gets the generated virtual authorization value.</summary>
    public required string DeviceAuth { get; init; }

    /// <summary>Gets the effective advertised receiver count.</summary>
    public required byte TunerCount { get; init; }

    /// <summary>Gets the optional configured receiver-count cap.</summary>
    public int? TunerCountCap { get; init; }

    /// <summary>Gets the client-facing device name.</summary>
    public required string FriendlyName { get; init; }

    /// <summary>Gets the explicitly configured external root URL.</summary>
    public string? AdvertisedBaseUrl { get; init; }

    /// <summary>Gets the uppercase stable virtual DeviceID.</summary>
    public string DeviceIdText => DeviceId.ToString("X8", CultureInfo.InvariantCulture);

    /// <summary>Resolves the manual HTTP base URI for this profile.</summary>
    public Uri GetHttpBaseUri(Uri requestRoot)
    {
        ArgumentNullException.ThrowIfNull(requestRoot);
        var root = HdHomeRunProxyProfileResolver.TryGetHttpRoot(AdvertisedBaseUrl, out var configuredRoot)
            ? configuredRoot!
            : requestRoot;
        return IsPrimary ? root : new Uri(root, $"hdhomerun/{DeviceIdText}/");
    }

    /// <summary>Tries to resolve the explicitly configured discovery base URI.</summary>
    public bool TryGetAdvertisedBaseUri(out Uri? baseUri)
    {
        baseUri = null;
        if (!HdHomeRunProxyProfileResolver.TryGetHttpRoot(AdvertisedBaseUrl, out var root))
        {
            return false;
        }

        baseUri = IsPrimary ? root : new Uri(root!, $"hdhomerun/{DeviceIdText}/");
        return true;
    }
}

/// <summary>Provides enabled virtual HDHomeRun profiles and profile selection.</summary>
public interface IHdHomeRunProxyProfileProvider
{
    /// <summary>Gets every enabled profile.</summary>
    Task<IReadOnlyList<HdHomeRunProxyProfileSnapshot>> GetProfilesAsync(CancellationToken cancellationToken = default);

    /// <summary>Gets the primary enabled profile.</summary>
    Task<HdHomeRunProxyProfileSnapshot?> GetPrimaryProfileAsync(CancellationToken cancellationToken = default);

    /// <summary>Finds a profile by virtual DeviceID.</summary>
    Task<HdHomeRunProxyProfileSnapshot?> FindProfileAsync(string virtualDeviceId, CancellationToken cancellationToken = default);
}

/// <summary>Resolves HDHomeRun-specific network addresses.</summary>
public static class HdHomeRunProxyProfileResolver
{
    /// <summary>Tries to normalize an explicitly configured HTTP root.</summary>
    public static bool TryGetHttpRoot(string? value, out Uri? root)
    {
        root = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return false;
        }

        root = uri.AbsoluteUri.EndsWith('/') ? uri : new Uri($"{uri.AbsoluteUri}/");
        return true;
    }

    /// <summary>Normalizes an HDHomeRun hostname, address, or URL.</summary>
    public static Uri GetPhysicalBaseUri(string address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        var normalized = address.Trim();
        if (!normalized.Contains("://", StringComparison.Ordinal))
        {
            normalized = $"http://{normalized}";
        }

        return new Uri(normalized.EndsWith('/') ? normalized : $"{normalized}/");
    }
}

/// <summary>Creates network advertisements from resolved virtual profiles.</summary>
public static class HdHomeRunAdvertisedDeviceFactory
{
    /// <summary>Creates advertisements for profiles with explicit external URLs.</summary>
    public static IReadOnlyList<HdHomeRunAdvertisedDevice> Create(IEnumerable<HdHomeRunProxyProfileSnapshot> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        return profiles
            .Select(profile => profile.TryGetAdvertisedBaseUri(out var baseUri)
                ? new HdHomeRunAdvertisedDevice
                {
                    DeviceId = profile.DeviceId,
                    DeviceAuth = profile.DeviceAuth,
                    TunerCount = profile.TunerCount,
                    BaseUri = baseUri!
                }
                : null)
            .OfType<HdHomeRunAdvertisedDevice>()
            .ToArray();
    }
}
