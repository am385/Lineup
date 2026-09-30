using Lineup.Web.Services;
using Xunit;

namespace Lineup.Web.Tests.Services;

/// <summary>
/// Verifies protected-content slate argument planning.
/// </summary>
public class ProtectedContentSlateServiceTests
{
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

    /// <summary>
    /// Verifies disabled-channel slates render a distinct message.
    /// </summary>
    [Fact]
    public void PipeArguments_DisabledChannel_UsesDisabledMessage()
    {
        // Arrange
        // Act
        var arguments = ProtectedContentSlatePlanner.CreatePipeArguments(HostedStreamFormat.MpegTs, "9.1", ChannelSlateReason.DisabledChannel);

        // Assert
        Assert.Contains(arguments, argument => argument.Contains("Disabled Channel - Channel 9.1", StringComparison.Ordinal));
        Assert.DoesNotContain(arguments, argument => argument.Contains("Content Protected", StringComparison.Ordinal));
    }

    private static void AssertOption(IReadOnlyList<string> arguments, string option, string expectedValue)
    {
        var index = arguments.ToList().LastIndexOf(option);
        Assert.True(index >= 0);
        Assert.Equal(expectedValue, arguments[index + 1]);
    }
}
