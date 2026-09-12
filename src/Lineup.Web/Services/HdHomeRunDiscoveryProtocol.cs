using System.Globalization;
using System.Text;
using Lineup.HDHomeRun.Device.Models;
using Lineup.HDHomeRun.Device.Protocol;

namespace Lineup.Web.Services;

/// <summary>
/// Configures one physical HDHomeRun exposed as a virtual proxy device.
/// </summary>
public sealed class HdHomeRunProxyProfileSettings
{
    private int? _tunerCountCap;

    /// <summary>Gets or sets the physical HDHomeRun hostname, address, or base URL.</summary>
    public string PhysicalAddress { get; set; } = string.Empty;

    /// <summary>Gets or sets whether this migrated primary profile follows the legacy device address.</summary>
    public bool UsesLegacyDeviceAddress { get; set; }

    /// <summary>Gets or sets whether this profile is exposed by proxy and discovery endpoints.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets an optional client-facing device name.</summary>
    public string? FriendlyName { get; set; }

    /// <summary>Gets or sets the persisted checksum-valid virtual DeviceID.</summary>
    public string VirtualDeviceId { get; set; } = string.Empty;

    /// <summary>Gets or sets the explicit externally reachable Lineup root URL used by discovery.</summary>
    public string AdvertisedBaseUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets an optional maximum advertised tuner count.</summary>
    public int? TunerCountCap
    {
        get => _tunerCountCap;
        set => _tunerCountCap = value is null ? null : Math.Clamp(value.Value, 1, byte.MaxValue);
    }
}

/// <summary>
/// Resolved immutable identity and physical metadata for one virtual proxy profile.
/// </summary>
public sealed record HdHomeRunProxyProfileSnapshot
{
    /// <summary>Gets the profile's persisted settings.</summary>
    public required HdHomeRunProxyProfileSettings Settings { get; init; }

    /// <summary>Gets the physical device metadata.</summary>
    public required HDHomeRunDeviceInfo PhysicalDevice { get; init; }

    /// <summary>Gets whether this is the primary profile exposed through legacy root routes.</summary>
    public required bool IsPrimary { get; init; }

    /// <summary>Gets the numeric stable virtual DeviceID.</summary>
    public required uint DeviceId { get; init; }

    /// <summary>Gets the uppercase stable virtual DeviceID.</summary>
    public string DeviceIdText => DeviceId.ToString("X8", CultureInfo.InvariantCulture);

    /// <summary>Gets the generated virtual authorization value, which is distinct from physical DeviceAuth.</summary>
    public required string DeviceAuth { get; init; }

    /// <summary>Gets the effective advertised tuner count after applying the optional profile cap.</summary>
    public required byte TunerCount { get; init; }

    /// <summary>Gets the profile's client-facing friendly name.</summary>
    public required string FriendlyName { get; init; }

    /// <summary>Gets the physical device base URI used for device-scoped HTTP access.</summary>
    public required Uri PhysicalBaseUri { get; init; }

    /// <summary>
    /// Resolves the manual HTTP base URI, using the configured advertised root when valid.
    /// </summary>
    /// <param name="requestRoot">Request-derived root used only for manual HTTP access when no advertised root is configured.</param>
    /// <returns>The root or device-scoped virtual base URI.</returns>
    public Uri GetHttpBaseUri(Uri requestRoot)
    {
        var root = HdHomeRunProxyProfileResolver.TryGetHttpRoot(Settings.AdvertisedBaseUrl, out var configuredRoot)
            ? configuredRoot!
            : requestRoot;
        return IsPrimary ? root : new Uri(root, $"hdhomerun/{DeviceIdText}/");
    }

    /// <summary>
    /// Tries to resolve an explicitly configured discovery base URI.
    /// </summary>
    /// <param name="baseUri">Profile-consistent discovery base URI.</param>
    /// <returns><see langword="true"/> when a valid advertised root was configured.</returns>
    public bool TryGetAdvertisedBaseUri(out Uri? baseUri)
    {
        baseUri = null;
        if (!HdHomeRunProxyProfileResolver.TryGetHttpRoot(Settings.AdvertisedBaseUrl, out var root))
        {
            return false;
        }

        baseUri = IsPrimary ? root : new Uri(root!, $"hdhomerun/{DeviceIdText}/");
        return true;
    }
}

/// <summary>
/// Resolves profile settings into isolated virtual device snapshots.
/// </summary>
public static class HdHomeRunProxyProfileResolver
{
    /// <summary>
    /// Creates a profile snapshot without retaining the physical DeviceAuth.
    /// </summary>
    /// <param name="settings">Profile settings.</param>
    /// <param name="physicalDevice">Physical device metadata.</param>
    /// <param name="isPrimary">Whether the profile owns legacy root routes.</param>
    /// <returns>A stable virtual profile snapshot.</returns>
    public static HdHomeRunProxyProfileSnapshot CreateSnapshot(HdHomeRunProxyProfileSettings settings, HDHomeRunDeviceInfo physicalDevice, bool isPrimary)
    {
        var seed = NormalizePhysicalAddress(settings.PhysicalAddress);
        var deviceIdText = HdHomeRunProxyIdentity.IsValidDeviceId(settings.VirtualDeviceId)
            ? settings.VirtualDeviceId.ToUpperInvariant()
            : HdHomeRunProxyIdentity.CreateDeviceId(seed);
        var tunerCount = settings.TunerCountCap is int cap
            ? Math.Min(physicalDevice.TunerCount, cap)
            : physicalDevice.TunerCount;
        return new HdHomeRunProxyProfileSnapshot
        {
            Settings = settings,
            PhysicalDevice = physicalDevice with { DeviceAuth = string.Empty },
            IsPrimary = isPrimary,
            DeviceId = uint.Parse(deviceIdText, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            DeviceAuth = HdHomeRunProxyIdentity.CreateDeviceAuth(seed),
            TunerCount = (byte)Math.Clamp(tunerCount, 0, byte.MaxValue),
            FriendlyName = string.IsNullOrWhiteSpace(settings.FriendlyName)
                ? $"Lineup ({physicalDevice.FriendlyName})"
                : settings.FriendlyName.Trim(),
            PhysicalBaseUri = GetPhysicalBaseUri(settings.PhysicalAddress)
        };
    }

    /// <summary>
    /// Tries to normalize an explicitly configured advertised HTTP root.
    /// </summary>
    /// <param name="value">Configured URL.</param>
    /// <param name="root">Normalized root ending in a slash.</param>
    /// <returns><see langword="true"/> for an absolute HTTP or HTTPS URL.</returns>
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

    /// <summary>
    /// Normalizes a physical HDHomeRun address into an HTTP base URI.
    /// </summary>
    /// <param name="address">Physical hostname, address, or URL.</param>
    /// <returns>An absolute base URI ending in a slash.</returns>
    public static Uri GetPhysicalBaseUri(string address)
    {
        var normalized = address.Trim();
        if (!normalized.Contains("://", StringComparison.Ordinal))
        {
            normalized = $"http://{normalized}";
        }

        return new Uri(normalized.EndsWith('/') ? normalized : $"{normalized}/");
    }

    private static string NormalizePhysicalAddress(string address)
    {
        return GetPhysicalBaseUri(address).AbsoluteUri.TrimEnd('/').ToUpperInvariant();
    }
}

/// <summary>
/// Provides enabled virtual proxy profiles and profile selection.
/// </summary>
public interface IHdHomeRunProxyProfileProvider
{
    /// <summary>Gets all enabled and currently reachable profiles.</summary>
    Task<IReadOnlyList<HdHomeRunProxyProfileSnapshot>> GetProfilesAsync(CancellationToken cancellationToken = default);

    /// <summary>Gets the primary enabled profile, when reachable.</summary>
    Task<HdHomeRunProxyProfileSnapshot?> GetPrimaryProfileAsync(CancellationToken cancellationToken = default);

    /// <summary>Finds an enabled profile by virtual DeviceID.</summary>
    Task<HdHomeRunProxyProfileSnapshot?> FindProfileAsync(string virtualDeviceId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Resolves current proxy profiles using cached primary state and device-scoped HTTP discovery.
/// </summary>
public sealed class HdHomeRunProxyProfileProvider : IHdHomeRunProxyProfileProvider
{
    private readonly IAppSettingsService _settings;
    private readonly IDeviceStateService _deviceState;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HdHomeRunProxyProfileProvider> _logger;
    private readonly VirtualDeviceStatusCache? _statusCache;

    /// <summary>
    /// Initializes the profile provider.
    /// </summary>
    /// <param name="settings">Current application settings.</param>
    /// <param name="deviceState">Cached state for the legacy primary physical device.</param>
    /// <param name="httpClientFactory">Factory for device-scoped HTTP clients.</param>
    /// <param name="logger">Logger for unreachable-profile diagnostics.</param>
    /// <param name="statusCache">Optional cache updated by normal profile resolution.</param>
    public HdHomeRunProxyProfileProvider(
        IAppSettingsService settings,
        IDeviceStateService deviceState,
        IHttpClientFactory httpClientFactory,
        ILogger<HdHomeRunProxyProfileProvider> logger,
        VirtualDeviceStatusCache? statusCache = null)
    {
        _settings = settings;
        _deviceState = deviceState;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _statusCache = statusCache;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<HdHomeRunProxyProfileSnapshot>> GetProfilesAsync(CancellationToken cancellationToken = default)
    {
        var enabled = GetEnabledProfiles();
        var snapshots = new List<HdHomeRunProxyProfileSnapshot>(enabled.Length);
        for (var index = 0; index < enabled.Length; index++)
        {
            var snapshot = await ResolveAsync(enabled[index], index == 0, cancellationToken);
            if (snapshot != null)
            {
                snapshots.Add(snapshot);
            }
        }

        return snapshots;
    }

    /// <inheritdoc />
    public async Task<HdHomeRunProxyProfileSnapshot?> GetPrimaryProfileAsync(CancellationToken cancellationToken = default)
    {
        var profile = GetEnabledProfiles().FirstOrDefault();
        return profile == null ? null : await ResolveAsync(profile, true, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<HdHomeRunProxyProfileSnapshot?> FindProfileAsync(string virtualDeviceId, CancellationToken cancellationToken = default)
    {
        var enabled = GetEnabledProfiles();
        var profile = enabled.FirstOrDefault(candidate =>
            candidate.VirtualDeviceId.Equals(virtualDeviceId, StringComparison.OrdinalIgnoreCase));
        return profile == null ? null : await ResolveAsync(profile, profile == enabled[0], cancellationToken);
    }

    private HdHomeRunProxyProfileSettings[] GetEnabledProfiles()
    {
        _settings.Settings.EnsureHdHomeRunProxyProfiles();
        if (!_settings.Settings.EnableHdHomeRunProxy)
        {
            return [];
        }

        return _settings.Settings.HdHomeRunProxyProfiles
            .Where(profile => profile.Enabled)
            .DistinctBy(profile => profile.PhysicalAddress.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<HdHomeRunProxyProfileSnapshot?> ResolveAsync(HdHomeRunProxyProfileSettings profile, bool isPrimary, CancellationToken cancellationToken)
    {
        try
        {
            var usesLegacyDevice = HdHomeRunProxyProfileResolver.GetPhysicalBaseUri(profile.PhysicalAddress) ==
                HdHomeRunProxyProfileResolver.GetPhysicalBaseUri(_settings.Settings.DeviceAddress);
            var device = isPrimary && usesLegacyDevice && _deviceState.DeviceInfo != null
                ? _deviceState.DeviceInfo
                : await DiscoverDeviceAsync(profile, cancellationToken);
            var snapshot = HdHomeRunProxyProfileResolver.CreateSnapshot(profile, device, isPrimary);
            _statusCache?.RecordSuccess(snapshot);
            return snapshot;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _statusCache?.RecordFailure(profile, ex.Message);
            _logger.LogWarning(ex, "Unable to resolve HDHomeRun proxy profile at {Address}", profile.PhysicalAddress);
            return null;
        }
    }

    private async Task<HDHomeRunDeviceInfo> DiscoverDeviceAsync(HdHomeRunProxyProfileSettings profile, CancellationToken cancellationToken)
    {
        var baseUri = HdHomeRunProxyProfileResolver.GetPhysicalBaseUri(profile.PhysicalAddress);
        var client = _httpClientFactory.CreateClient("HdHomeRunProxyDevice");
        return await client.GetFromJsonAsync<HDHomeRunDeviceInfo>(new Uri(baseUri, "discover.json"), cancellationToken)
            ?? throw new InvalidOperationException($"No HDHomeRun device metadata was returned by {baseUri}");
    }
}

/// <summary>
/// Immutable network identity advertised for one virtual HDHomeRun profile.
/// </summary>
public sealed record HdHomeRunAdvertisedDevice
{
    /// <summary>Gets the source profile snapshot.</summary>
    public required HdHomeRunProxyProfileSnapshot Profile { get; init; }

    /// <summary>Gets the numeric virtual DeviceID.</summary>
    public uint DeviceId => Profile.DeviceId;

    /// <summary>Gets the virtual authorization string.</summary>
    public string DeviceAuth => Profile.DeviceAuth;

    /// <summary>Gets the effective number of tuners.</summary>
    public byte TunerCount => Profile.TunerCount;

    /// <summary>Gets the explicitly configured profile-consistent base URI.</summary>
    public required Uri BaseUri { get; init; }

    /// <summary>Gets the virtual device UUID used by SSDP.</summary>
    public string Uuid => Profile.DeviceIdText;
}

/// <summary>
/// Creates discovery advertisements from resolved virtual profiles.
/// </summary>
public static class HdHomeRunAdvertisedDeviceFactory
{
    /// <summary>
    /// Creates advertisements only for profiles with valid explicit advertised URLs.
    /// </summary>
    /// <param name="profiles">Resolved enabled profiles.</param>
    /// <returns>Profile-consistent advertisements.</returns>
    public static IReadOnlyList<HdHomeRunAdvertisedDevice> Create(IEnumerable<HdHomeRunProxyProfileSnapshot> profiles)
    {
        return profiles
            .Select(profile => profile.TryGetAdvertisedBaseUri(out var baseUri)
                ? new HdHomeRunAdvertisedDevice { Profile = profile, BaseUri = baseUri! }
                : null)
            .OfType<HdHomeRunAdvertisedDevice>()
            .ToArray();
    }
}

/// <summary>
/// Parses SiliconDust discovery requests and creates protocol-compatible replies.
/// </summary>
public static class HdHomeRunDiscoveryProtocol
{
    /// <summary>
    /// Determines whether a datagram is a valid SiliconDust discovery request.
    /// </summary>
    /// <param name="request">Raw SiliconDust packet.</param>
    /// <returns><see langword="true"/> when the packet is a valid discovery request.</returns>
    public static bool IsDiscoveryRequest(ReadOnlySpan<byte> request)
    {
        var reader = new HDHomeRunPacketReader(request);
        if (!reader.IsValid || reader.PacketType != HDHomeRunPacketType.DiscoverRequest)
        {
            return false;
        }

        while (reader.TryReadTag(out _, out _))
        {
        }

        return !reader.HasError;
    }

    /// <summary>
    /// Creates a discovery reply when a request targets the advertised tuner.
    /// </summary>
    /// <param name="request">Raw SiliconDust discovery packet.</param>
    /// <param name="device">Virtual device to match and advertise.</param>
    /// <param name="reply">CRC-valid discovery reply, when matched.</param>
    /// <returns><see langword="true"/> when the request is valid and matches the device.</returns>
    public static bool TryCreateReply(ReadOnlySpan<byte> request, HdHomeRunAdvertisedDevice device, out byte[]? reply)
    {
        reply = null;
        var reader = new HDHomeRunPacketReader(request);
        if (!reader.IsValid || reader.PacketType != HDHomeRunPacketType.DiscoverRequest)
        {
            return false;
        }

        uint? requestedDeviceId = null;
        HDHomeRunDeviceType? requestedDeviceType = null;
        while (reader.TryReadTag(out var tag, out var value))
        {
            if (tag == HDHomeRunTagType.DeviceId)
            {
                if (value.Length != sizeof(uint))
                {
                    return false;
                }

                requestedDeviceId = HDHomeRunPacketReader.ReadUInt32(value);
            }
            else if (tag == HDHomeRunTagType.DeviceType)
            {
                if (value.Length != sizeof(uint))
                {
                    return false;
                }

                requestedDeviceType = (HDHomeRunDeviceType)HDHomeRunPacketReader.ReadUInt32(value);
            }
        }

        if (reader.HasError ||
            requestedDeviceId is not null && requestedDeviceId != HDHomeRunDeviceId.Wildcard && requestedDeviceId != device.DeviceId ||
            requestedDeviceType is not null && requestedDeviceType != HDHomeRunDeviceType.Wildcard && requestedDeviceType != HDHomeRunDeviceType.Tuner)
        {
            return false;
        }

        var lineupUri = new Uri(device.BaseUri, "lineup.json");
        reply = new HDHomeRunPacketBuilder()
            .AddTag(HDHomeRunTagType.DeviceType, (uint)HDHomeRunDeviceType.Tuner)
            .AddTag(HDHomeRunTagType.DeviceId, device.DeviceId)
            .AddTag(HDHomeRunTagType.TunerCount, [device.TunerCount])
            .AddTag(HDHomeRunTagType.BaseUrl, NullTerminate(device.BaseUri.AbsoluteUri.TrimEnd('/')))
            .AddTag(HDHomeRunTagType.LineupUrl, NullTerminate(lineupUri.AbsoluteUri))
            .AddTag(HDHomeRunTagType.DeviceAuthStr, NullTerminate(device.DeviceAuth))
            .Build(HDHomeRunPacketType.DiscoverReply);
        return true;
    }

    private static byte[] NullTerminate(string value)
    {
        return [.. Encoding.UTF8.GetBytes(value), 0];
    }
}
