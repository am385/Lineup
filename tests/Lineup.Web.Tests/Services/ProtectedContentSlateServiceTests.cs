using Lineup.Web.Services;
using Xunit;

namespace Lineup.Web.Tests.Services;

/// <summary>
/// Verifies protected-content slate argument planning.
/// </summary>
public class ProtectedContentSlateServiceTests
{
    /// <summary>
    /// Verifies that fMP4 slates contain browser-compatible video and silent audio.
    /// </summary>
    [Fact]
    public void Fmp4Arguments_CreateProtectedContentSlate()
    {
        // Arrange
        const string channel = "117.1";

        // Act
        var arguments = ProtectedContentSlatePlanner.CreatePipeArguments(HostedStreamFormat.FragmentedMp4, channel);

        // Assert
        Assert.Contains("color=c=0x20252b:s=1280x720:r=30", arguments);
        Assert.Contains(arguments, argument => argument.Contains("Content Protected - Channel 117.1", StringComparison.Ordinal));
        AssertOption(arguments, "-c:v", "libx264");
        AssertOption(arguments, "-c:a", "aac");
        AssertOption(arguments, "-f", "mp4");
        Assert.Equal("pipe:1", arguments[^1]);
    }

    /// <summary>
    /// Verifies that HLS slates target the requested playlist.
    /// </summary>
    [Fact]
    public void HlsArguments_TargetRequestedPlaylist()
    {
        // Arrange
        const string playlistPath = @"C:\temp\protected\stream.m3u8";

        // Act
        var arguments = ProtectedContentSlatePlanner.CreateHlsArguments("117.1", playlistPath);

        AssertOption(arguments, "-f", "hls");
        AssertOption(arguments, "-hls_segment_type", "mpegts");
        // Assert
        Assert.Equal(playlistPath, arguments[^1]);
    }

    private static void AssertOption(IReadOnlyList<string> arguments, string option, string expectedValue)
    {
        var index = arguments.ToList().LastIndexOf(option);
        Assert.True(index >= 0);
        Assert.Equal(expectedValue, arguments[index + 1]);
    }
}
