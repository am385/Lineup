using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lineup.Common.Streaming;
using Lineup.Extensibility.Streaming;
using Lineup.SiliconDust.HDHomeRun.Device.Models;

namespace Lineup.SiliconDust.HDHomeRun.VirtualTuner;

/// <summary>
/// Creates stable virtual HDHomeRun identities.
/// </summary>
public static class HdHomeRunProxyIdentity
{
    private static readonly byte[] ChecksumLookup =
    [
        0xA, 0x5, 0xF, 0x6, 0x7, 0xC, 0x1, 0xB,
        0x9, 0x2, 0x8, 0xD, 0x4, 0x3, 0xE, 0x0
    ];

    /// <summary>Creates a deterministic, checksum-valid HDHomeRun DeviceID.</summary>
    /// <param name="seed">Stable virtual-device seed.</param>
    /// <returns>An eight-character uppercase hexadecimal DeviceID.</returns>
    public static string CreateDeviceId(string seed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(seed);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"lineup-proxy:{seed}"));
        var prefix = Convert.ToHexString(hash)[..7];
        var checksum = 0;
        for (var index = 0; index < prefix.Length; index++)
        {
            var nibble = Convert.ToInt32(prefix[index].ToString(), 16);
            checksum ^= index % 2 == 0 ? ChecksumLookup[nibble] : nibble;
        }

        return $"{prefix}{checksum:X1}";
    }

    /// <summary>Creates a stable opaque authorization value for a virtual device.</summary>
    /// <param name="seed">Stable virtual-device seed.</param>
    /// <returns>A URL-safe authorization string.</returns>
    public static string CreateDeviceAuth(string seed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(seed);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"lineup-auth:{seed}"));
        return Convert.ToBase64String(hash[..18]).Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Determines whether a DeviceID satisfies the SiliconDust checksum.</summary>
    /// <param name="deviceId">Eight-character hexadecimal DeviceID.</param>
    /// <returns><see langword="true"/> when the identifier is valid.</returns>
    public static bool IsValidDeviceId(string deviceId)
    {
        if (deviceId.Length != 8 || !uint.TryParse(deviceId, System.Globalization.NumberStyles.HexNumber, null, out var value) || value is 0 or uint.MaxValue)
        {
            return false;
        }

        var checksum = 0;
        for (var index = 0; index < 8; index++)
        {
            var nibble = (int)((value >> (28 - (index * 4))) & 0x0F);
            checksum ^= index % 2 == 0 ? ChecksumLookup[nibble] : nibble;
        }

        return checksum == 0;
    }
}

/// <summary>Describes the virtual HDHomeRun device exposed by Lineup.</summary>
public sealed record HdHomeRunProxyDiscoverResponse
{
    /// <summary>Gets the client-facing device name.</summary>
    [JsonPropertyName("FriendlyName")]
    public required string FriendlyName { get; init; }

    /// <summary>Gets the emulated device model.</summary>
    [JsonPropertyName("ModelNumber")]
    public required string ModelNumber { get; init; }

    /// <summary>Gets the proxy firmware family.</summary>
    [JsonPropertyName("FirmwareName")]
    public required string FirmwareName { get; init; }

    /// <summary>Gets the Lineup version advertised as firmware.</summary>
    [JsonPropertyName("FirmwareVersion")]
    public required string FirmwareVersion { get; init; }

    /// <summary>Gets the stable virtual DeviceID.</summary>
    [JsonPropertyName("DeviceID")]
    public required string DeviceId { get; init; }

    /// <summary>Gets the stable virtual authorization value.</summary>
    [JsonPropertyName("DeviceAuth")]
    public required string DeviceAuth { get; init; }

    /// <summary>Gets the externally reachable proxy base URL.</summary>
    [JsonPropertyName("BaseURL")]
    public required string BaseUrl { get; init; }

    /// <summary>Gets the externally reachable proxy lineup URL.</summary>
    [JsonPropertyName("LineupURL")]
    public required string LineupUrl { get; init; }

    /// <summary>Gets the effective physical tuner count.</summary>
    [JsonPropertyName("TunerCount")]
    public required int TunerCount { get; init; }
}

/// <summary>Creates HDHomeRun response contracts from virtual-device state.</summary>
public static class HdHomeRunProxyResponseFactory
{
    /// <summary>Creates the discovery document for a virtual HDHomeRun device.</summary>
    /// <param name="friendlyName">Client-facing device name.</param>
    /// <param name="deviceId">Stable virtual DeviceID.</param>
    /// <param name="deviceAuth">Stable virtual authorization value.</param>
    /// <param name="baseUri">Externally reachable virtual-device base URI.</param>
    /// <param name="tunerCount">Aggregated advertised tuner count.</param>
    /// <param name="firmwareVersion">Lineup version advertised as firmware.</param>
    /// <returns>The discover.json response contract.</returns>
    public static HdHomeRunProxyDiscoverResponse CreateDiscover(
        string friendlyName,
        string deviceId,
        string deviceAuth,
        Uri baseUri,
        byte tunerCount,
        string firmwareVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(friendlyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceAuth);
        ArgumentNullException.ThrowIfNull(baseUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(firmwareVersion);
        return new HdHomeRunProxyDiscoverResponse
        {
            FriendlyName = friendlyName,
            ModelNumber = "LINEUP-1",
            FirmwareName = "lineup_proxy",
            FirmwareVersion = firmwareVersion,
            DeviceId = deviceId,
            DeviceAuth = deviceAuth,
            BaseUrl = baseUri.AbsoluteUri.TrimEnd('/'),
            LineupUrl = new Uri(baseUri, "lineup.json").AbsoluteUri,
            TunerCount = tunerCount
        };
    }
}

/// <summary>Reports the static channel-scan state of a virtual HDHomeRun device.</summary>
public sealed record HdHomeRunProxyLineupStatus
{
    /// <summary>Gets a value indicating whether a channel scan is running.</summary>
    [JsonPropertyName("ScanInProgress")]
    public int ScanInProgress => 0;

    /// <summary>Gets a value indicating whether scanning through the proxy is supported.</summary>
    [JsonPropertyName("ScanPossible")]
    public int ScanPossible => 0;

    /// <summary>Gets the virtual lineup source.</summary>
    [JsonPropertyName("Source")]
    public string Source => "Antenna";

    /// <summary>Gets the available virtual lineup sources.</summary>
    [JsonPropertyName("SourceList")]
    public string[] SourceList => ["Antenna"];
}

/// <summary>Describes one channel in the virtual HDHomeRun lineup.</summary>
public sealed record HdHomeRunProxyChannel
{
    private static readonly HashSet<string> OwnedPropertyNames = typeof(HdHomeRunProxyChannel)
        .GetProperties(BindingFlags.Instance | BindingFlags.Public)
        .Where(property => property.GetCustomAttribute<JsonExtensionDataAttribute>() == null)
        .Select(property => property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? property.Name)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets the virtual channel number.</summary>
    [JsonPropertyName("GuideNumber")]
    public required string GuideNumber { get; init; }

    /// <summary>Gets the channel display name.</summary>
    [JsonPropertyName("GuideName")]
    public required string GuideName { get; init; }

    /// <summary>Gets comma-separated HDHomeRun channel tags.</summary>
    [JsonPropertyName("Tags")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Tags { get; init; }

    /// <summary>Gets the reported source video codec.</summary>
    [JsonPropertyName("VideoCodec")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? VideoCodec { get; init; }

    /// <summary>Gets the client-facing audio codec.</summary>
    [JsonPropertyName("AudioCodec")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AudioCodec { get; init; }

    /// <summary>Gets one when the source is high definition.</summary>
    [JsonPropertyName("HD")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Hd { get; init; }

    /// <summary>Gets one when the source is protected.</summary>
    [JsonPropertyName("DRM")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Drm { get; init; }

    /// <summary>Gets one when the channel is a favorite.</summary>
    [JsonPropertyName("Favorite")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Favorite { get; init; }

    /// <summary>Gets the signal strength reported by the physical device.</summary>
    [JsonPropertyName("SignalStrength")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SignalStrength { get; init; }

    /// <summary>Gets the signal quality reported by the physical device.</summary>
    [JsonPropertyName("SignalQuality")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SignalQuality { get; init; }

    /// <summary>Gets the Lineup-hosted MPEG-TS URL.</summary>
    [JsonPropertyName("URL")]
    public required string Url { get; init; }

    /// <summary>Gets firmware-defined properties that Lineup does not yet model.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; init; }

    /// <summary>Creates a virtual channel from a physical HDHomeRun lineup item.</summary>
    /// <param name="channel">Physical channel metadata.</param>
    /// <param name="baseUri">Externally reachable Lineup base URI.</param>
    /// <param name="convertHevcToH264">Whether HEVC metadata should advertise H.264 output.</param>
    /// <returns>A channel whose stream URL points to Lineup.</returns>
    public static HdHomeRunProxyChannel Create(HDHomeRunChannel channel, Uri baseUri, bool convertHevcToH264 = false)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(baseUri);
        return new HdHomeRunProxyChannel
        {
            GuideNumber = channel.GuideNumber,
            GuideName = channel.GuideName,
            Tags = GetTags(channel.Tags, channel.Favorite, channel.DRM),
            VideoCodec = convertHevcToH264 && string.Equals(channel.VideoCodec, "HEVC", StringComparison.OrdinalIgnoreCase) ? "H264" : channel.VideoCodec,
            AudioCodec = string.Equals(channel.AudioCodec, "AC4", StringComparison.OrdinalIgnoreCase) ? "AC3" : channel.AudioCodec,
            Hd = channel.HD ? 1 : null,
            Drm = channel.DRM ? 1 : null,
            Favorite = channel.Favorite ? 1 : null,
            SignalStrength = channel.SignalStrength,
            SignalQuality = channel.SignalQuality,
            Url = new Uri(baseUri, $"auto/v{Uri.EscapeDataString(channel.GuideNumber)}").AbsoluteUri,
            AdditionalProperties = GetAdditionalProperties(channel.AdditionalProperties)
        };
    }

    /// <summary>Creates a virtual channel with topology identity, favorites, and tags, without asserting source codecs or protection as output properties.</summary>
    public static HdHomeRunProxyChannel Create(ResolvedVirtualChannel channel, Uri baseUri)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(baseUri);
        var favorite = channel.Metadata.TryGetValue("favorite", out var value) && bool.TryParse(value, out var parsed) && parsed;
        return new HdHomeRunProxyChannel
        {
            GuideNumber = channel.Number,
            GuideName = channel.Name,
            Tags = GetTags(channel.Metadata.GetValueOrDefault("tags"), favorite),
            Favorite = favorite ? 1 : null,
            Url = new Uri(baseUri, $"auto/v{Uri.EscapeDataString(channel.Number)}").AbsoluteUri
        };
    }

    private static string? GetTags(string? value, bool favorite, bool drm = false)
    {
        var tags = value?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList() ?? [];
        if (favorite)
        {
            tags.Add("favorite");
        }
        if (drm)
        {
            tags.Add("drm");
        }
        return tags.Count == 0 ? null : string.Join(',', tags.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static Dictionary<string, JsonElement>? GetAdditionalProperties(IReadOnlyDictionary<string, JsonElement>? additionalProperties)
    {
        if (additionalProperties == null)
        {
            return null;
        }

        var result = additionalProperties
            .Where(property => !OwnedPropertyNames.Contains(property.Key))
            .ToDictionary(property => property.Key, property => property.Value, StringComparer.Ordinal);
        return result.Count == 0 ? null : result;
    }
}
