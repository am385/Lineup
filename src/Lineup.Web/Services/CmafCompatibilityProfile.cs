namespace Lineup.Web.Services;

/// <summary>
/// Identifies the capability isolated by one automated Watch Test case.
/// </summary>
public enum CmafCapabilityKind
{
    /// <summary>Manifest protocol playback.</summary>
    Protocol,

    /// <summary>Video codec, profile, and resolution playback.</summary>
    Video,

    /// <summary>Audio codec and channel-layout playback.</summary>
    Audio,

    /// <summary>Browser-rendered WebVTT sidecar playback.</summary>
    SubtitleSidecar,

    /// <summary>Playback of video containing server-rendered subtitles.</summary>
    SubtitleBurnIn
}

/// <summary>
/// Identifies the outcome of one automated Watch Test case.
/// </summary>
public enum CmafCapabilityStatus
{
    /// <summary>The exact presentation played successfully.</summary>
    Passed,

    /// <summary>The exact presentation failed to play.</summary>
    Failed,

    /// <summary>The server cannot generate a deterministic presentation for this capability.</summary>
    Unavailable
}

/// <summary>
/// Identifies the synthetic HEVC profile used by Watch Test.
/// </summary>
public enum CmafTestVideoProfile
{
    /// <summary>Eight-bit AVC or HEVC output.</summary>
    Main,

    /// <summary>Ten-bit HEVC Main 10 output.</summary>
    Main10
}

/// <summary>
/// Identifies synthetic subtitle behavior used by Watch Test.
/// </summary>
public enum CmafTestSubtitleMode
{
    /// <summary>No subtitles are included.</summary>
    None,

    /// <summary>A browser-rendered WebVTT sidecar is attached.</summary>
    WebVttSidecar,

    /// <summary>A visible subtitle-like slate is encoded into the video.</summary>
    BurnIn
}

/// <summary>
/// Describes the browser claims captured with a compatibility profile.
/// </summary>
public sealed record CmafBrowserClaims
{
    /// <summary>Gets whether Shaka reports browser support.</summary>
    public bool ShakaSupported { get; init; }

    /// <summary>Gets whether Media Source Extensions are available.</summary>
    public bool MediaSourceAvailable { get; init; }

    /// <summary>Gets the browser's native HLS claim.</summary>
    public string? NativeHls { get; init; }

    /// <summary>Gets the browser's H.264 claim.</summary>
    public bool? H264 { get; init; }

    /// <summary>Gets the browser's HEVC Main claim.</summary>
    public bool? Hevc { get; init; }

    /// <summary>Gets the browser's HEVC Main 10 claim.</summary>
    public bool? HevcMain10 { get; init; }

    /// <summary>Gets the browser's AAC claim.</summary>
    public bool? Aac { get; init; }

    /// <summary>Gets the browser's AC-3 claim.</summary>
    public bool? Ac3 { get; init; }

    /// <summary>Gets the browser's E-AC-3 claim.</summary>
    public bool? Eac3 { get; init; }

    /// <summary>Gets the browser's AC-4 claim.</summary>
    public bool? Ac4 { get; init; }
}

/// <summary>
/// Defines one independent automated Watch Test case.
/// </summary>
public sealed record CmafCapabilityTestCase
{
    /// <summary>Gets the stable case identifier.</summary>
    public required string Id { get; init; }

    /// <summary>Gets the user-facing case label.</summary>
    public required string Label { get; init; }

    /// <summary>Gets the isolated capability kind.</summary>
    public CmafCapabilityKind Kind { get; init; }

    /// <summary>Gets the exact synthetic request.</summary>
    public required CmafCompatibilityTestRequest Request { get; init; }

    /// <summary>Gets whether the case is diagnostic-only because no deterministic encoder exists.</summary>
    public bool IsUnavailable { get; init; }
}

/// <summary>
/// Stores the measured result for one independent compatibility case.
/// </summary>
public sealed record CmafCapabilityResult
{
    /// <summary>Gets the stable test-case identifier.</summary>
    public required string CaseId { get; init; }

    /// <summary>Gets the isolated capability kind.</summary>
    public CmafCapabilityKind Kind { get; init; }

    /// <summary>Gets the measured status.</summary>
    public CmafCapabilityStatus Status { get; init; }

    /// <summary>Gets the exact request used for the test.</summary>
    public required CmafCompatibilityTestRequest Request { get; init; }

    /// <summary>Gets the actual video codec reported by Shaka.</summary>
    public string? VideoCodec { get; init; }

    /// <summary>Gets the actual audio codec reported by Shaka.</summary>
    public string? AudioCodec { get; init; }

    /// <summary>Gets the actual audio channel count reported by Shaka.</summary>
    public int? AudioChannels { get; init; }

    /// <summary>Gets whether the expected WebVTT cue became active.</summary>
    public bool SubtitleCueVisible { get; init; }

    /// <summary>Gets structured failure details suitable for diagnostics.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// Stores a complete browser-local Watch compatibility profile.
/// </summary>
public sealed record CmafCompatibilityProfile
{
    /// <summary>Gets the browser-local storage key.</summary>
    public const string StorageKey = "lineup-watch-cmaf-compatibility-v1";

    /// <summary>Gets the current profile schema version.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Gets the profile schema version.</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Gets whether this is a server-derived request-scoped override profile.</summary>
    public bool IsOverrideProfile { get; init; }

    /// <summary>Gets a browser/runtime identity used to reject profiles copied between clients.</summary>
    public required string BrowserIdentity { get; init; }

    /// <summary>Gets when the complete suite finished.</summary>
    public DateTimeOffset CompletedAtUtc { get; init; }

    /// <summary>Gets browser-declared codec support recorded for diagnostics.</summary>
    public required CmafBrowserClaims Claims { get; init; }

    /// <summary>Gets every measured independent capability result.</summary>
    public required IReadOnlyList<CmafCapabilityResult> Results { get; init; }
}

/// <summary>
/// Validates profiles and derives trusted measured capabilities.
/// </summary>
public static class CmafCompatibilityProfilePolicy
{
    /// <summary>Returns whether a profile is complete, current, bounded, and internally valid.</summary>
    public static bool IsValid(CmafCompatibilityProfile? profile)
    {
        if (profile is null ||
            profile.IsOverrideProfile ||
            profile.SchemaVersion != CmafCompatibilityProfile.CurrentSchemaVersion ||
            string.IsNullOrWhiteSpace(profile.BrowserIdentity) ||
            profile.BrowserIdentity.Length > 512 ||
            profile.Claims is null ||
            profile.Claims.NativeHls?.Length > 32 ||
            profile.CompletedAtUtc == default ||
            profile.Results is null ||
            profile.Results.Count == 0 ||
            profile.Results.Count > 64)
        {
            return false;
        }

        var catalog = CmafCompatibilityTestCatalog.All.ToDictionary(test => test.Id, StringComparer.Ordinal);
        return profile.Results.Count == catalog.Count &&
            profile.Results.All(result =>
                !string.IsNullOrWhiteSpace(result.CaseId) &&
                catalog.TryGetValue(result.CaseId, out var test) &&
                result.Kind == test.Kind &&
                result.Request == test.Request &&
                Enum.IsDefined(result.Status) &&
                (test.IsUnavailable ? result.Status == CmafCapabilityStatus.Unavailable : result.Status != CmafCapabilityStatus.Unavailable) &&
                (result.VideoCodec is null || result.VideoCodec.Length <= 128) &&
                (result.AudioCodec is null || result.AudioCodec.Length <= 128) &&
                (result.Error is null || result.Error.Length <= 2_048)) &&
            profile.Results.Select(result => result.CaseId).Distinct(StringComparer.Ordinal).Count() == profile.Results.Count;
    }

    /// <summary>Returns whether a measured or server-derived effective profile can be used by the planner.</summary>
    public static bool IsUsable(CmafCompatibilityProfile? profile)
    {
        if (profile is null)
        {
            return false;
        }
        if (!profile.IsOverrideProfile)
        {
            return IsValid(profile);
        }

        var catalog = CmafCompatibilityTestCatalog.All.ToDictionary(test => test.Id, StringComparer.Ordinal);
        if (profile.Results.Any(result => !catalog.ContainsKey(result.CaseId)))
        {
            return false;
        }

        var measured = profile with
        {
            IsOverrideProfile = false,
            Results = profile.Results
                .Select(result => catalog[result.CaseId].IsUnavailable ? result with { Status = CmafCapabilityStatus.Unavailable } : result)
                .ToArray()
        };
        return IsValid(measured);
    }

    /// <summary>Returns whether an exact protocol completed successful playback.</summary>
    public static bool SupportsProtocol(CmafCompatibilityProfile profile, CmafProtocol protocol) =>
        Passed(profile, CmafCapabilityKind.Protocol, result => result.Request.Protocol == protocol);

    /// <summary>Returns whether measured video support covers the source codec, profile, and dimensions.</summary>
    public static bool SupportsVideo(CmafCompatibilityProfile profile, MediaTrackMetadata? video)
    {
        if (video is null)
        {
            return false;
        }

        var codec = string.Equals(video.Codec, "h264", StringComparison.OrdinalIgnoreCase)
            ? CmafTestVideoCodec.H264
            : string.Equals(video.Codec, "hevc", StringComparison.OrdinalIgnoreCase)
                ? CmafTestVideoCodec.Hevc
                : (CmafTestVideoCodec?)null;
        if (!codec.HasValue)
        {
            return false;
        }

        var requiredProfile = string.Equals(video.Profile, "Main 10", StringComparison.OrdinalIgnoreCase)
            ? CmafTestVideoProfile.Main10
            : CmafTestVideoProfile.Main;
        return Passed(
            profile,
            CmafCapabilityKind.Video,
            result => result.Request.VideoCodec == codec &&
                result.Request.VideoProfile == requiredProfile &&
                ResolutionCovers(result.Request.Quality, video.Width, video.Height));
    }

    /// <summary>Returns whether measured audio support covers the source codec and channel count.</summary>
    public static bool SupportsAudio(CmafCompatibilityProfile profile, MediaTrackMetadata? audio)
    {
        if (audio is null || !TryMapAudioCodec(audio.Codec, out var codec))
        {
            return false;
        }

        var channels = audio.Channels ?? 2;
        return Passed(
            profile,
            CmafCapabilityKind.Audio,
            result => result.Request.AudioCodec == codec &&
                CmafCompatibilityTestPlanner.GetChannelCount(result.Request.ChannelLayout) >= channels);
    }

    /// <summary>Returns the best measured fallback audio profile for the requested source channel count.</summary>
    public static CmafFallbackAudio? SelectFallbackAudio(CmafCompatibilityProfile profile, int sourceChannels)
    {
        var candidates = new[]
        {
            (Codec: CmafTestAudioCodec.Eac3, Fallback: CmafFallbackAudio.Eac3, Maximum: 6),
            (Codec: CmafTestAudioCodec.Ac3, Fallback: CmafFallbackAudio.Ac3, Maximum: 6),
            (Codec: CmafTestAudioCodec.Aac, Fallback: CmafFallbackAudio.AacUpTo7Point1, Maximum: 8),
            (Codec: CmafTestAudioCodec.Aac, Fallback: CmafFallbackAudio.AacUpTo5Point1, Maximum: 6),
            (Codec: CmafTestAudioCodec.Aac, Fallback: CmafFallbackAudio.AacStereo, Maximum: 2)
        };
        return candidates
            .Select((candidate, codecRank) => new
            {
                Candidate = candidate,
                CodecRank = codecRank,
                OutputChannels = Math.Min(Math.Max(sourceChannels, 2), candidate.Maximum)
            })
            .Where(item => Passed(
                profile,
                CmafCapabilityKind.Audio,
                result => result.Request.AudioCodec == item.Candidate.Codec &&
                    CmafCompatibilityTestPlanner.GetChannelCount(result.Request.ChannelLayout) >= item.OutputChannels))
            .OrderByDescending(item => item.OutputChannels)
            .ThenBy(item => item.CodecRank)
            .Select(item => (CmafFallbackAudio?)item.Candidate.Fallback)
            .FirstOrDefault();
    }

    /// <summary>Returns whether the selected subtitle mode completed successful playback.</summary>
    public static bool SupportsSubtitle(CmafCompatibilityProfile profile, CmafTestSubtitleMode mode) =>
        Passed(
            profile,
            mode == CmafTestSubtitleMode.WebVttSidecar ? CmafCapabilityKind.SubtitleSidecar : CmafCapabilityKind.SubtitleBurnIn,
            result => result.Request.SubtitleMode == mode);

    private static bool Passed(CmafCompatibilityProfile profile, CmafCapabilityKind kind, Func<CmafCapabilityResult, bool> predicate) =>
        IsUsable(profile) &&
        profile.Results.Any(result => result.Kind == kind && result.Status == CmafCapabilityStatus.Passed && predicate(result));

    private static bool TryMapAudioCodec(string codec, out CmafTestAudioCodec result)
    {
        var normalized = codec.ToLowerInvariant();
        result = normalized switch
        {
            "aac" => CmafTestAudioCodec.Aac,
            "ac3" => CmafTestAudioCodec.Ac3,
            "eac3" => CmafTestAudioCodec.Eac3,
            "ac4" => CmafTestAudioCodec.Ac4,
            _ => default
        };
        return normalized is "aac" or "ac3" or "eac3" or "ac4";
    }

    private static bool ResolutionCovers(WebPlayerQuality quality, int? width, int? height)
    {
        var tested = CmafCompatibilityTestPlanner.GetResolution(quality);
        return (!width.HasValue || tested.Width >= width.Value) &&
            (!height.HasValue || tested.Height >= height.Value);
    }
}

/// <summary>
/// Derives a request-scoped compatibility matrix while preserving non-overridden measured result groups.
/// </summary>
public static class CmafCompatibilityOverridePolicy
{
    /// <summary>Creates the temporary effective matrix used for one tune request.</summary>
    public static CmafCompatibilityProfile? CreateEffectiveProfile(CmafCompatibilityProfile? measured, CmafStreamOverrides overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        if (!CmafCompatibilityProfilePolicy.IsValid(measured) || !overrides.Enabled)
        {
            return measured;
        }

        var results = measured!.Results
            .Select(result => ApplyResult(result, overrides))
            .ToArray();
        return measured with { IsOverrideProfile = true, Results = results };
    }

    private static CmafCapabilityResult ApplyResult(CmafCapabilityResult result, CmafStreamOverrides overrides)
    {
        if (result.Kind == CmafCapabilityKind.Protocol && overrides.Protocol is { } protocol)
        {
            var selected = protocol switch
            {
                CmafPlaybackProtocol.Dash => CmafProtocol.Dash,
                CmafPlaybackProtocol.Hls => CmafProtocol.Hls,
                _ => throw new ArgumentException("A protocol override must select DASH or HLS.", nameof(overrides))
            };
            return result with { Status = result.Request.Protocol == selected ? CmafCapabilityStatus.Passed : CmafCapabilityStatus.Failed };
        }

        if (result.Kind == CmafCapabilityKind.Video && overrides.Video is { } video)
        {
            return result with
            {
                Status = video switch
                {
                    CmafPreferredVideo.Source => CmafCapabilityStatus.Passed,
                    CmafPreferredVideo.Fallback => CmafCapabilityStatus.Failed,
                    _ => throw new ArgumentException("A video override must select Source or Fallback.", nameof(overrides))
                }
            };
        }

        if (result.Kind == CmafCapabilityKind.Audio && overrides.Audio is { } audio)
        {
            return result with
            {
                Status = audio switch
                {
                    CmafPreferredAudio.Source => CmafCapabilityStatus.Passed,
                    CmafPreferredAudio.Fallback => CmafCapabilityStatus.Failed,
                    _ => throw new ArgumentException("An audio override must select Source or Fallback.", nameof(overrides))
                }
            };
        }

        return result;
    }
}

/// <summary>
/// Defines the bounded independent compatibility suite.
/// </summary>
public static class CmafCompatibilityTestCatalog
{
    /// <summary>Gets every case required for a complete profile.</summary>
    public static IReadOnlyList<CmafCapabilityTestCase> All { get; } = Create();

    private static IReadOnlyList<CmafCapabilityTestCase> Create()
    {
        var cases = new List<CmafCapabilityTestCase>
        {
            Test("protocol-dash", "DASH", CmafCapabilityKind.Protocol, new() { Protocol = CmafProtocol.Dash }),
            Test("protocol-hls", "HLS", CmafCapabilityKind.Protocol, new() { Protocol = CmafProtocol.Hls }),
            Test("video-h264-1080p", "H.264 High 1080p", CmafCapabilityKind.Video, new() { VideoCodec = CmafTestVideoCodec.H264, Quality = WebPlayerQuality.High }),
            Test("video-hevc-main-1080p", "HEVC Main 1080p", CmafCapabilityKind.Video, new() { VideoCodec = CmafTestVideoCodec.Hevc, VideoProfile = CmafTestVideoProfile.Main, Quality = WebPlayerQuality.High }),
            Test("video-hevc-main10-1080p", "HEVC Main 10 1080p", CmafCapabilityKind.Video, new() { VideoCodec = CmafTestVideoCodec.Hevc, VideoProfile = CmafTestVideoProfile.Main10, Quality = WebPlayerQuality.High })
        };

        foreach (var codec in new[] { CmafTestAudioCodec.Aac, CmafTestAudioCodec.Ac3, CmafTestAudioCodec.Eac3 })
        {
            foreach (var layout in CmafCompatibilityTestPlanner.GetSupportedLayouts(codec))
            {
                cases.Add(Test(
                    $"audio-{codec.ToString().ToLowerInvariant()}-{CmafCompatibilityTestPlanner.GetChannelCount(layout)}ch",
                    $"{codec.ToString().ToUpperInvariant()} {CmafCompatibilityTestPlanner.GetChannelCount(layout)} ch",
                    CmafCapabilityKind.Audio,
                    new() { AudioCodec = codec, ChannelLayout = layout }));
            }
        }

        cases.Add(Test(
            "audio-ac4-unavailable",
            "AC-4 (claim only)",
            CmafCapabilityKind.Audio,
            new() { AudioCodec = CmafTestAudioCodec.Ac4 },
            isUnavailable: true));
        cases.Add(Test(
            "subtitle-webvtt",
            "WebVTT sidecar",
            CmafCapabilityKind.SubtitleSidecar,
            new() { SubtitleMode = CmafTestSubtitleMode.WebVttSidecar }));
        cases.Add(Test(
            "subtitle-burn-in",
            "Subtitle burn-in",
            CmafCapabilityKind.SubtitleBurnIn,
            new() { SubtitleMode = CmafTestSubtitleMode.BurnIn }));
        return cases;
    }

    private static CmafCapabilityTestCase Test(string id, string label, CmafCapabilityKind kind, CmafCompatibilityTestRequest request, bool isUnavailable = false) =>
        new() { Id = id, Label = label, Kind = kind, Request = request, IsUnavailable = isUnavailable };
}
