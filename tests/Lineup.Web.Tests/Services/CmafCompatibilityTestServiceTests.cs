using Lineup.Core;
using Lineup.Web.Services;
using Xunit;

namespace Lineup.Web.Tests.Services;

/// <summary>
/// Verifies exact single-rendition Watch Test planning.
/// </summary>
public class CmafCompatibilityTestServiceTests
{
    /// <summary>
    /// Verifies HEVC Main 10 uses ten-bit pixels, an explicit profile, and browser-compatible signaling.
    /// </summary>
    [Fact]
    public void CreateArguments_HevcMain10_UsesTenBitProfile()
    {
        // Arrange
        var request = new CmafCompatibilityTestRequest
        {
            VideoCodec = CmafTestVideoCodec.Hevc,
            VideoProfile = CmafTestVideoProfile.Main10
        };

        // Act
        var arguments = CmafCompatibilityTestPlanner.CreateArguments(new AppSettings(), request, "manifest.mpd");

        // Assert
        AssertOption(arguments, "-pix_fmt", "yuv420p10le");
        AssertOption(arguments, "-profile:v", "main10");
        AssertOption(arguments, "-tag:v", "hvc1");
        Assert.Equal("hvc1.2.4.L120", CmafCompatibilityTestPlanner.GetVideoCodecString(request.VideoCodec, request.VideoProfile));
    }

    /// <summary>
    /// Verifies burn-in testing labels each channel only while its matching tone is active.
    /// </summary>
    [Fact]
    public void CreateArguments_BurnIn_AddsSynchronizedChannelLabels()
    {
        // Arrange
        var request = new CmafCompatibilityTestRequest
        {
            SubtitleMode = CmafTestSubtitleMode.BurnIn,
            ChannelLayout = CmafTestChannelLayout.Surround3Point0
        };

        // Act
        var arguments = CmafCompatibilityTestPlanner.CreateArguments(new AppSettings(), request, "manifest.mpd");

        // Assert
        var filter = arguments[arguments.ToList().IndexOf("-vf") + 1];
        Assert.Contains("text='Front Left'", filter, StringComparison.Ordinal);
        Assert.Contains("text='Front Right'", filter, StringComparison.Ordinal);
        Assert.Contains("text='Front Center'", filter, StringComparison.Ordinal);
        Assert.Contains("between(mod(t\\,3)\\,0\\,0.75)", filter, StringComparison.Ordinal);
        Assert.Contains("between(mod(t\\,3)\\,2\\,2.75)", filter, StringComparison.Ordinal);
        Assert.Contains("fontfile=", filter, StringComparison.Ordinal);
        Assert.Equal(3, filter.Split("drawtext=", StringSplitOptions.None).Length - 1);
        Assert.Equal(1, arguments.Select((argument, index) => (argument, index)).Count(item => item.argument == "-map" && arguments[item.index + 1] == "0:v:0"));
        AssertOption(arguments, "-t", "600");
    }

    /// <summary>
    /// Verifies WebVTT cues follow the same repeating channel order and active-tone window.
    /// </summary>
    [Fact]
    public void CreateChannelSubtitleWebVtt_Stereo_RepeatsSynchronizedLabels()
    {
        // Arrange
        // Act
        var webVtt = CmafCompatibilityTestPlanner.CreateChannelSubtitleWebVtt(CmafTestChannelLayout.Stereo);

        // Assert
        Assert.StartsWith("WEBVTT\n\n", webVtt, StringComparison.Ordinal);
        Assert.Contains("00:00:00.000 --> 00:00:00.750\nFront Left", webVtt, StringComparison.Ordinal);
        Assert.Contains("00:00:01.000 --> 00:00:01.750\nFront Right", webVtt, StringComparison.Ordinal);
        Assert.Contains("00:00:02.000 --> 00:00:02.750\nFront Left", webVtt, StringComparison.Ordinal);
        Assert.Contains("00:09:59.000 --> 00:09:59.750\nFront Right", webVtt, StringComparison.Ordinal);
        Assert.Equal(600, webVtt.Split(" --> ", StringSplitOptions.None).Length - 1);
    }

    /// <summary>
    /// Verifies no subtitle video filter is added when subtitle presentation is disabled or uses a sidecar.
    /// </summary>
    [Theory]
    [InlineData(CmafTestSubtitleMode.None)]
    [InlineData(CmafTestSubtitleMode.WebVttSidecar)]
    public void CreateArguments_NonBurnIn_DoesNotAddSubtitleVideoFilter(CmafTestSubtitleMode mode)
    {
        // Arrange
        var request = new CmafCompatibilityTestRequest { SubtitleMode = mode };

        // Act
        var arguments = CmafCompatibilityTestPlanner.CreateArguments(new AppSettings(), request, "manifest.mpd");

        // Assert
        Assert.DoesNotContain("-vf", arguments);
    }

    /// <summary>
    /// Verifies each available codec pair creates only one video and one audio rendition.
    /// </summary>
    [Theory]
    [InlineData(CmafTestVideoCodec.H264, CmafTestAudioCodec.Aac, "libx264", "aac")]
    [InlineData(CmafTestVideoCodec.H264, CmafTestAudioCodec.Ac3, "libx264", "ac3")]
    [InlineData(CmafTestVideoCodec.Hevc, CmafTestAudioCodec.Eac3, "libx265", "eac3")]
    public void Arguments_CreateExactSingleRendition(CmafTestVideoCodec videoCodec, CmafTestAudioCodec audioCodec, string expectedVideoEncoder, string expectedAudioEncoder)
    {
        // Arrange
        var request = new CmafCompatibilityTestRequest
        {
            VideoCodec = videoCodec,
            AudioCodec = audioCodec,
            ChannelLayout = CmafTestChannelLayout.Surround5Point1
        };

        // Act
        var arguments = CmafCompatibilityTestPlanner.CreateArguments(new AppSettings(), request, @"C:\temp\watch-test\manifest.mpd");

        // Assert
        Assert.Equal(2, arguments.Count(argument => argument == "-map"));
        AssertOption(arguments, "-c:v", expectedVideoEncoder);
        AssertOption(arguments, "-c:a", expectedAudioEncoder);
        AssertOption(arguments, "-adaptation_sets", "id=0,streams=v id=1,streams=a");
        Assert.Equal(2, arguments.Count(argument => argument == "-re"));
        Assert.DoesNotContain(arguments, argument => argument.Contains("fallback", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Verifies AAC exposes 7.1 while the bundled Dolby encoders stop at 5.1.
    /// </summary>
    [Fact]
    public void SupportedLayouts_UseEncoderChannelCeilings()
    {
        // Arrange
        // Act
        var aacLayouts = CmafCompatibilityTestPlanner.GetSupportedLayouts(CmafTestAudioCodec.Aac);
        var ac3Layouts = CmafCompatibilityTestPlanner.GetSupportedLayouts(CmafTestAudioCodec.Ac3);
        var eac3Layouts = CmafCompatibilityTestPlanner.GetSupportedLayouts(CmafTestAudioCodec.Eac3);

        // Assert
        Assert.Contains(CmafTestChannelLayout.Surround7Point1, aacLayouts);
        Assert.DoesNotContain(CmafTestChannelLayout.Surround7Point1, ac3Layouts);
        Assert.DoesNotContain(CmafTestChannelLayout.Surround7Point1, eac3Layouts);
        Assert.Contains(CmafTestChannelLayout.Surround5Point1, ac3Layouts);
        Assert.Contains(CmafTestChannelLayout.Surround5Point1, eac3Layouts);
    }

    /// <summary>
    /// Verifies synthetic audio cycles an audible tone through one channel at a time in layout order.
    /// </summary>
    [Theory]
    [InlineData(CmafTestChannelLayout.Mono, "c=mono", 1)]
    [InlineData(CmafTestChannelLayout.Stereo, "c=stereo", 2)]
    [InlineData(CmafTestChannelLayout.Surround5Point1, "c=5.1", 6)]
    [InlineData(CmafTestChannelLayout.Surround7Point1, "c=7.1", 8)]
    public void CreateArguments_AudioLayout_UsesSequentialChannelTones(CmafTestChannelLayout layout, string expectedLayout, int expectedChannels)
    {
        // Arrange
        var request = new CmafCompatibilityTestRequest { ChannelLayout = layout };

        // Act
        var arguments = CmafCompatibilityTestPlanner.CreateArguments(new AppSettings(), request, "manifest.mpd");
        var audioSource = arguments[arguments.ToList().IndexOf("testsrc2=size=1920x1080:rate=30") + 5];

        // Assert
        Assert.StartsWith("aevalsrc=", audioSource, StringComparison.Ordinal);
        Assert.Contains(expectedLayout, audioSource, StringComparison.Ordinal);
        Assert.Equal(expectedChannels, audioSource.Count(character => character == '|') + 1);
        Assert.DoesNotContain("anullsrc", audioSource, StringComparison.Ordinal);
        AssertOption(arguments, "-ac", expectedChannels.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Verifies the displayed channel order matches FFmpeg's selected layout.
    /// </summary>
    [Fact]
    public void ChannelLabels_SurroundSevenPointOne_MatchFfmpegOrder()
    {
        // Arrange
        // Act
        var labels = CmafCompatibilityTestPlanner.GetChannelLabels(CmafTestChannelLayout.Surround7Point1);

        // Assert
        Assert.Equal(["Front Left", "Front Right", "Front Center", "LFE", "Back Left", "Back Right", "Side Left", "Side Right"], labels);
    }

    /// <summary>
    /// Verifies the unavailable AC-4 encoder is reported rather than replaced.
    /// </summary>
    [Fact]
    public void Ac4Request_IsRejectedWithoutFallback()
    {
        // Arrange
        var request = new CmafCompatibilityTestRequest
        {
            AudioCodec = CmafTestAudioCodec.Ac4,
            ChannelLayout = CmafTestChannelLayout.Stereo
        };

        // Act
        var exception = Assert.Throws<NotSupportedException>(() =>
            CmafCompatibilityTestPlanner.CreateArguments(new AppSettings(), request, @"C:\temp\watch-test\manifest.mpd"));

        // Assert
        Assert.Contains("does not include an AC-4 encoder", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies an invalid Dolby channel layout is rejected at the planning boundary.
    /// </summary>
    [Fact]
    public void Eac3SevenPointOne_IsRejected()
    {
        // Arrange
        var request = new CmafCompatibilityTestRequest
        {
            AudioCodec = CmafTestAudioCodec.Eac3,
            ChannelLayout = CmafTestChannelLayout.Surround7Point1
        };

        // Act
        var exception = Assert.Throws<ArgumentException>(() => CmafCompatibilityTestPlanner.ValidateRequest(request));

        // Assert
        Assert.Contains("not supported", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies the quality choices use deterministic resolutions that map to Watch.
    /// </summary>
    [Theory]
    [InlineData(WebPlayerQuality.AppDefault, 1920, 1080)]
    [InlineData(WebPlayerQuality.High, 1920, 1080)]
    [InlineData(WebPlayerQuality.Medium, 1280, 720)]
    [InlineData(WebPlayerQuality.Low, 854, 480)]
    public void Resolution_MapsWatchQuality(WebPlayerQuality quality, int expectedWidth, int expectedHeight)
    {
        // Arrange
        // Act
        var (width, height) = CmafCompatibilityTestPlanner.GetResolution(quality);

        // Assert
        Assert.Equal(expectedWidth, width);
        Assert.Equal(expectedHeight, height);
    }

    /// <summary>
    /// Verifies diagnostics use the codecs FFmpeg actually wrote instead of predicted profile values.
    /// </summary>
    [Fact]
    public void ManifestCodecs_ReturnActualFfmpegValues()
    {
        // Arrange
        const string manifest = """
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011">
              <Period>
                <AdaptationSet>
                  <Representation mimeType="video/mp4" codecs="hvc1.1.6.L120" />
                </AdaptationSet>
                <AdaptationSet>
                  <Representation mimeType="audio/mp4" codecs="ec-3" />
                </AdaptationSet>
              </Period>
            </MPD>
            """;

        // Act
        var (videoCodec, audioCodec) = CmafCompatibilityTestPlanner.ParseManifestCodecs(manifest);

        // Assert
        Assert.Equal("hvc1.1.6.L120", videoCodec);
        Assert.Equal("ec-3", audioCodec);
    }

    private static void AssertOption(IReadOnlyList<string> arguments, string option, string expectedValue)
    {
        var index = arguments.ToList().LastIndexOf(option);
        Assert.True(index >= 0);
        Assert.Equal(expectedValue, arguments[index + 1]);
    }
}
