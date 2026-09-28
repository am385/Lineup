using Lineup.Web.Services;
using Xunit;

namespace Lineup.Web.Tests.Services;

/// <summary>
/// Verifies browser compatibility profile validation and copy-first planning.
/// </summary>
public class CmafCompatibilityProfileTests
{
    /// <summary>
    /// Verifies the server-owned catalog covers protocols, video profiles, audio layouts, and subtitle modes without duplicate identifiers.
    /// </summary>
    [Fact]
    public void Catalog_AllCases_CoversIndependentDimensions()
    {
        // Arrange
        var cases = CmafCompatibilityTestCatalog.All;

        // Act
        var identifiers = cases.Select(test => test.Id).ToArray();

        // Assert
        Assert.Equal(identifiers.Length, identifiers.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(cases, test => test.Kind == CmafCapabilityKind.Protocol && test.Request.Protocol == CmafProtocol.Dash);
        Assert.Contains(cases, test => test.Kind == CmafCapabilityKind.Protocol && test.Request.Protocol == CmafProtocol.Hls);
        Assert.Contains(cases, test => test.Kind == CmafCapabilityKind.Video && test.Request.VideoCodec == CmafTestVideoCodec.Hevc && test.Request.VideoProfile == CmafTestVideoProfile.Main10);
        Assert.Contains(cases, test => test.Kind == CmafCapabilityKind.Audio && test.Request.AudioCodec == CmafTestAudioCodec.Aac && test.Request.ChannelLayout == CmafTestChannelLayout.Surround7Point1);
        Assert.Contains(cases, test => test.Kind == CmafCapabilityKind.SubtitleSidecar);
        Assert.Contains(cases, test => test.Kind == CmafCapabilityKind.SubtitleBurnIn);
    }

    /// <summary>
    /// Verifies a complete profile is accepted while a client-modified catalog request is rejected.
    /// </summary>
    [Fact]
    public void IsValid_CompleteProfileRejectsModifiedCase()
    {
        // Arrange
        var profile = Profile();
        var modified = profile with
        {
            Results =
            [
                profile.Results[0] with { Request = profile.Results[0].Request with { Quality = WebPlayerQuality.Low } },
                .. profile.Results.Skip(1)
            ]
        };

        // Act
        var valid = CmafCompatibilityProfilePolicy.IsValid(profile);
        var modifiedValid = CmafCompatibilityProfilePolicy.IsValid(modified);

        // Assert
        Assert.True(valid);
        Assert.False(modifiedValid);
    }

    /// <summary>
    /// Verifies measured HEVC Main 10 support authorizes source copy at the tested resolution.
    /// </summary>
    [Fact]
    public void CreateVideoRenditions_AutoWithMeasuredMain10_CopiesSource()
    {
        // Arrange
        var video = Track(0, MediaTrackType.Video, "hevc") with { Width = 1920, Height = 1080, Profile = "Main 10", Level = 153 };
        var request = new CmafStreamRequest
        {
            PreferredVideo = CmafPreferredVideo.Auto,
            CompatibilityProfile = Profile()
        };

        // Act
        var plan = Assert.Single(CmafStreamPlanner.CreateVideoRenditions(video, request, burnIn: false));

        // Assert
        Assert.True(plan.CopySource);
        Assert.Equal("hevc", plan.Codec);
    }

    /// <summary>
    /// Verifies Auto audio emits only measured source copy and otherwise chooses the measured fallback that preserves the most channels.
    /// </summary>
    [Fact]
    public void CreatePresentationAudioRenditions_Auto_UsesOneBestOutputPerSource()
    {
        // Arrange
        var source = new MediaProbeResult(
        [
            Track(0, MediaTrackType.Video, "h264") with { Width = 1920, Height = 1080 },
            Track(1, MediaTrackType.Audio, "ac3") with { Channels = 6 },
            Track(2, MediaTrackType.Audio, "ac4") with { Channels = 8 }
        ], null);
        var profile = Profile();

        // Act
        var renditions = CmafStreamPlanner.CreatePresentationAudioRenditions(
            source,
            source.Tracks[1],
            CmafPreferredAudio.Auto,
            CmafFallbackAudio.AacStereo,
            profile);

        // Assert
        Assert.Collection(
            renditions,
            first =>
            {
                Assert.Equal(1, first.Source.Index);
                Assert.True(first.Plan.CopySource);
                Assert.Equal("ac3", first.Plan.Codec);
            },
            second =>
            {
                Assert.Equal(2, second.Source.Index);
                Assert.False(second.Plan.CopySource);
                Assert.Equal("aac", second.Plan.Codec);
                Assert.Equal(8, second.Plan.Channels);
            });
    }

    /// <summary>
    /// Verifies an audio-only override leaves measured protocol, video, and subtitle results unchanged.
    /// </summary>
    [Fact]
    public void CreateEffectiveProfile_AudioOnly_PreservesOtherCapabilityGroups()
    {
        // Arrange
        var profile = Profile();
        var overrides = new CmafStreamOverrides
        {
            Enabled = true,
            Audio = CmafPreferredAudio.Fallback
        };

        // Act
        var effective = CmafCompatibilityOverridePolicy.CreateEffectiveProfile(profile, overrides);

        // Assert
        Assert.NotNull(effective);
        Assert.True(effective.IsOverrideProfile);
        Assert.True(CmafCompatibilityProfilePolicy.IsUsable(effective));
        Assert.All(effective.Results.Where(result => result.Kind == CmafCapabilityKind.Audio), result => Assert.Equal(CmafCapabilityStatus.Failed, result.Status));
        Assert.Equal(
            profile.Results.Where(result => result.Kind != CmafCapabilityKind.Audio),
            effective.Results.Where(result => result.Kind != CmafCapabilityKind.Audio));
    }

    /// <summary>
    /// Verifies overriding only fallback audio retains measured source-copy decisions and forces the selected fallback when copy is unavailable.
    /// </summary>
    [Fact]
    public void ApplyOverrides_FallbackAudioOnly_PreservesSourceCopyAndForcesFallback()
    {
        // Arrange
        var source = new MediaProbeResult(
        [
            Track(0, MediaTrackType.Video, "h264"),
            Track(1, MediaTrackType.Audio, "ac3") with { Channels = 6 },
            Track(2, MediaTrackType.Audio, "ac4") with { Channels = 6 }
        ], null);
        var request = CmafStreamPlanner.ApplyOverrides(new CmafStreamRequest
        {
            PreferredAudio = CmafPreferredAudio.Auto,
            CompatibilityProfile = Profile(),
            Overrides = new CmafStreamOverrides
            {
                Enabled = true,
                FallbackAudio = CmafFallbackAudio.Ac3
            }
        });

        // Act
        var renditions = CmafStreamPlanner.CreatePresentationAudioRenditions(
            source,
            source.Tracks[1],
            request.PreferredAudio,
            CmafStreamPlanner.ResolveFallbackAudio(request),
            request.CompatibilityProfile,
            request.Overrides.FallbackAudio);

        // Assert
        Assert.True(renditions[0].Plan.CopySource);
        Assert.Equal("ac3", renditions[0].Plan.Codec);
        Assert.False(renditions[1].Plan.CopySource);
        Assert.Equal("ac3", renditions[1].Plan.Codec);
    }

    private static CmafCompatibilityProfile Profile()
    {
        var results = CmafCompatibilityTestCatalog.All
            .Select(test => new CmafCapabilityResult
            {
                CaseId = test.Id,
                Kind = test.Kind,
                Request = test.Request,
                Status = test.IsUnavailable ? CmafCapabilityStatus.Unavailable : CmafCapabilityStatus.Passed
            })
            .ToArray();
        return new CmafCompatibilityProfile
        {
            BrowserIdentity = "test-browser",
            CompletedAtUtc = DateTimeOffset.UtcNow,
            Claims = new CmafBrowserClaims(),
            Results = results
        };
    }

    private static MediaTrackMetadata Track(int index, MediaTrackType type, string codec) =>
        new(index, type, codec, null, null, null, null, null);
}
