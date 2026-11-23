using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace Lineup.App.Web.Tests.Services;

/// <summary>
/// Verifies protected-content slate argument planning.
/// </summary>
[Collection("FFmpeg process diagnostics")]
public class ProtectedContentSlateServiceTests
{
    /// <summary>Gets literal channel values exercising both FFmpeg parser layers and disabled text expansion.</summary>
    public static TheoryData<string, ChannelSlateReason> LiteralChannelValues
    {
        get
        {
            var cases = new TheoryData<string, ChannelSlateReason>();
            foreach (var channel in new[]
            {
                "117.1",
                "x',drawtext=textfile=security-canary.txt,drawtext=text='x",
                @"apostrophe's \ backslash: [brackets],semicolon;",
                "literal %{n} %{eif:1+1:d}",
                " leading and trailing ",
                "tabs\tand\nnewlines\rremain literal",
                @"trailing backslash\",
                @"consecutive '' \\ ' quotes",
                "Chaîne 日本語"
            })
            {
                cases.Add(channel, ChannelSlateReason.ContentProtected);
                cases.Add(channel, ChannelSlateReason.DisabledChannel);
            }
            return cases;
        }
    }

    /// <summary>Verifies native FFmpeg receives one literal drawtext filter, cannot consume a canary text file and produces a frame for every output planner.</summary>
    /// <param name="channel">Literal provider text, including the confirmed injection payload.</param>
    /// <param name="reason">The application-authored slate message.</param>
    [Theory]
    [MemberData(nameof(LiteralChannelValues))]
    public async Task NativeArguments_ProviderText_RemainsOneLiteralDrawtextFilter(string channel, ChannelSlateReason reason)
    {
        // Arrange
        var executable = Environment.GetEnvironmentVariable("LINEUP_TEST_SLATE_FFMPEG");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(executable), "Set LINEUP_TEST_SLATE_FFMPEG to an explicit native FFmpeg executable with drawtext support.");
        Assert.True(File.Exists(executable), $"The configured FFmpeg executable does not exist: {executable}");
        var root = Directory.CreateTempSubdirectory("lineup-slate-parser-");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "security-canary.txt"), "OWNED-CANARY-MUST-NOT-BE-CONSUMED", TestContext.Current.CancellationToken);
            var plans = new[]
            {
                ProtectedContentSlatePlanner.CreatePipeArguments(HostedStreamFormat.MpegTs, channel, reason),
                ProtectedContentSlatePlanner.CreateHlsArguments(channel, Path.Combine(root.FullName, "stream.m3u8"), reason),
                ProtectedContentSlatePlanner.CreateCmafArguments(channel, Path.Combine(root.FullName, "manifest.mpd"), reason)
            };
            var title = reason == ChannelSlateReason.DisabledChannel ? "Disabled Channel" : "Content Protected";

            // Act
            foreach (var plan in plans)
            {
                var filter = plan[plan.ToList().IndexOf("-vf") + 1];
                var result = await RenderNativeFrameAsync(executable!, filter, root.FullName);

                // Assert
                Assert.True(result.ExitCode == 0, result.Diagnostics);
                var parsedDiagnostics = OperatingSystem.IsWindows() ? result.Diagnostics.Replace("\r\n", "\n", StringComparison.Ordinal) : result.Diagnostics;
                Assert.Contains($"Setting 'text' to value '{title} - Channel {channel}'", parsedDiagnostics, StringComparison.Ordinal);
                Assert.Contains("Setting 'expansion' to value 'none'", result.Diagnostics, StringComparison.Ordinal);
                Assert.DoesNotContain("Setting 'textfile'", result.Diagnostics, StringComparison.Ordinal);
                var filters = Regex.Matches(result.Diagnostics, @"\[Parsed_drawtext_(\d+) @").Select(match => match.Groups[1].Value).Distinct().ToArray();
                Assert.Single(filters);
                Assert.Matches(@"(?m)^0,\s*-?\d+,\s*-?\d+,\s*\d+,\s*[1-9]\d*,\s*[0-9a-fA-F]+\s*$", result.Frames);
            }
        }
        finally
        {
            root.Delete(recursive: true);
        }
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

        // Assert
        Assert.Equal(2, arguments.Count(argument => argument == "-re"));
        AssertOption(arguments, "-f", "hls");
        AssertOption(arguments, "-hls_segment_type", "mpegts");
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
        Assert.Contains(arguments, argument => argument.Contains(@"Disabled\\\ Channel\\\ -\\\ Channel\\\ 9.1", StringComparison.Ordinal));
        Assert.DoesNotContain(arguments, argument => argument.Contains(@"Content\\\ Protected", StringComparison.Ordinal));
    }

    private static async Task<(int ExitCode, string Diagnostics, string Frames)> RenderNativeFrameAsync(string executable, string filter, string directory)
    {
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        watchdog.CancelAfter(TimeSpan.FromSeconds(30));
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in new[]
        {
            "-nostdin", "-hide_banner", "-loglevel", "debug", "-protocol_whitelist", "file,pipe",
            "-f", "lavfi", "-i", "color=c=0x20252b:s=1280x720:r=30", "-vf", filter,
            "-an", "-frames:v", "1", "-f", "framehash", "pipe:1"
        })
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Native slate FFmpeg did not start.");
        var diagnostics = Task.Run(() => process.StandardError.ReadToEndAsync());
        var frames = Task.Run(() => process.StandardOutput.ReadToEndAsync());
        try
        {
            await process.WaitForExitAsync(watchdog.Token);
            return (process.ExitCode, await diagnostics, await frames);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
            await Task.WhenAll(diagnostics, frames).WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    private static void AssertOption(IReadOnlyList<string> arguments, string option, string expectedValue)
    {
        var index = arguments.ToList().LastIndexOf(option);
        Assert.True(index >= 0);
        Assert.Equal(expectedValue, arguments[index + 1]);
    }
}
