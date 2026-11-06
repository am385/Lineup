using Lineup.Common.Streaming;
using Lineup.VirtualOutputs.Protocol;
using Lineup.WebPlayer.VirtualOutputHost;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Lineup.WebPlayer.Tests;

/// <summary>Verifies dedicated artifact responses retain opened files across concurrent cleanup.</summary>
public sealed class DedicatedMediaFileResponseTests
{
    /// <summary>Verifies a partially published DASH snapshot is retryable instead of a malformed success or unhandled XML failure.</summary>
    [Theory]
    [InlineData("manifest.mpd")]
    [InlineData("media_0.m3u8")]
    public async Task CreateAsync_DashPublicationIncomplete_ReturnsRetryableResponse(string filename)
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-media-publication-");
        var path = Path.Combine(root.FullName, filename);
        await File.WriteAllTextAsync(Path.Combine(root.FullName, "manifest.mpd"), "<MPD", TestContext.Current.CancellationToken);
        if (filename != "manifest.mpd")
        {
            await File.WriteAllTextAsync(path, "#EXTM3U\nchunk-0-00001.m4s", TestContext.Current.CancellationToken);
        }
        var plan = CreatePlan(root.FullName);
        var context = new DefaultHttpContext();
        try
        {
            // Act
            var result = await DedicatedMediaFileResponse.CreateAsync(path, filename, plan, null, context);

            // Assert
            Assert.Equal(425, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>Verifies session cleanup before response execution cannot remove an in-flight media or range response.</summary>
    [Theory]
    [InlineData("init-0.mp4", false)]
    [InlineData("chunk-0-00001.m4s", false)]
    [InlineData("init-0.mp4", true)]
    [InlineData("chunk-0-00001.m4s", true)]
    public async Task CreateAsync_ArtifactDeletedBeforeExecution_ServesOpenedBytes(string filename, bool range)
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-media-response-");
        var path = Path.Combine(root.FullName, filename);
        byte[] content = [0, 1, 2, 3, 255];
        await File.WriteAllBytesAsync(path, content, TestContext.Current.CancellationToken);
        var lastModified = File.GetLastWriteTimeUtc(path).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        using var body = new MemoryStream();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Method = HttpMethods.Get;
        context.Response.Body = body;
        if (range)
        {
            context.Request.Headers.Range = "bytes=1-3";
        }
        var plan = CreatePlan(root.FullName);

        // Act
        var result = await DedicatedMediaFileResponse.CreateAsync(path, filename, plan, null, context);
        root.Delete(recursive: true);
        await result.ExecuteAsync(context);

        // Assert
        Assert.Equal(range ? StatusCodes.Status206PartialContent : StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("video/mp4", context.Response.ContentType);
        Assert.Equal(range ? content[1..4] : content, body.ToArray());
        Assert.Equal("no-cache, no-store", context.Response.Headers.CacheControl.ToString());
        Assert.Equal(lastModified, context.Response.Headers.LastModified.ToString());
        if (range)
        {
            Assert.Equal("bytes 1-3/5", context.Response.Headers.ContentRange.ToString());
        }
        Assert.False(Assert.IsType<FileStreamHttpResult>(result).FileStream.CanRead);
    }

    /// <summary>Verifies text artifacts are read while their handle is owned and survive cleanup before response execution.</summary>
    [Theory]
    [InlineData("manifest.mpd", "<MPD xmlns=\"urn:mpeg:dash:schema:mpd:2011\" />", "application/dash+xml", "MPD")]
    [InlineData("master.m3u8", "#EXTM3U", "application/vnd.apple.mpegurl", "#EXTM3U")]
    [InlineData("captions-0.vtt", "WEBVTT\n\ncue", "text/vtt; charset=utf-8", "cue")]
    public async Task CreateAsync_TextArtifactDeletedBeforeExecution_PreservesResponse(string filename, string content, string contentType, string expectedMarker)
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-media-text-");
        var path = Path.Combine(root.FullName, filename);
        await File.WriteAllTextAsync(path, content, TestContext.Current.CancellationToken);
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        using var body = new MemoryStream();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Response.Body = body;
        var plan = CreatePlan(root.FullName);

        // Act
        var result = await DedicatedMediaFileResponse.CreateAsync(path, filename, plan, null, context);
        root.Delete(recursive: true);
        await result.ExecuteAsync(context);

        // Assert
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.StartsWith(contentType, context.Response.ContentType, StringComparison.Ordinal);
        Assert.Contains(expectedMarker, System.Text.Encoding.UTF8.GetString(body.ToArray()), StringComparison.Ordinal);
    }

    /// <summary>Verifies incremental subtitle offsets retain their bounds and byte responses across cleanup.</summary>
    [Theory]
    [InlineData(7, "\ncue")]
    [InlineData(999, "")]
    public async Task CreateAsync_SubtitleOffset_PreservesIncrementalBytes(long offset, string expectedContent)
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-media-subtitle-");
        var path = Path.Combine(root.FullName, "captions-0.vtt");
        await File.WriteAllTextAsync(path, "WEBVTT\n\ncue", TestContext.Current.CancellationToken);
        var context = new DefaultHttpContext();
        var plan = CreatePlan(root.FullName);

        // Act
        var result = await DedicatedMediaFileResponse.CreateAsync(path, "captions-0.vtt", plan, offset, context);
        root.Delete(recursive: true);

        // Assert
        Assert.Equal(expectedContent, System.Text.Encoding.UTF8.GetString(Assert.IsType<FileContentHttpResult>(result).FileContents.Span));
        Assert.Equal("11", context.Response.Headers["X-Lineup-Subtitle-Offset"].ToString());
    }

    /// <summary>Verifies HLS media playlists use the opened DASH timeline, or retain their retryable missing-timeline response.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateAsync_MediaPlaylist_PreservesTimelineResponse(bool timelineExists)
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-media-playlist-");
        var path = Path.Combine(root.FullName, "media_0.m3u8");
        const string playlist = "#EXTM3U\n#EXTINF:2,\n#EXT-X-PROGRAM-DATE-TIME:2026-10-06T05:42:38.000Z\nchunk-0-00001.m4s";
        const string dash = """
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" availabilityStartTime="2026-10-06T05:42:38.000Z">
              <Period><AdaptationSet><Representation id="0">
                <SegmentTemplate timescale="1000" startNumber="1"><SegmentTimeline><S t="2000" d="2000" /></SegmentTimeline></SegmentTemplate>
              </Representation></AdaptationSet></Period>
            </MPD>
            """;
        await File.WriteAllTextAsync(path, playlist, TestContext.Current.CancellationToken);
        if (timelineExists)
        {
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "manifest.mpd"), dash, TestContext.Current.CancellationToken);
        }
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        using var body = new MemoryStream();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Response.Body = body;
        var plan = CreatePlan(root.FullName);

        // Act
        var result = await DedicatedMediaFileResponse.CreateAsync(path, "media_0.m3u8", plan, null, context);
        root.Delete(recursive: true);
        await result.ExecuteAsync(context);

        // Assert
        Assert.Equal(timelineExists ? StatusCodes.Status200OK : 425, context.Response.StatusCode);
        if (timelineExists)
        {
            Assert.StartsWith("application/vnd.apple.mpegurl", context.Response.ContentType, StringComparison.Ordinal);
            Assert.Contains("#EXT-X-PROGRAM-DATE-TIME:2026-10-06T05:42:40.000Z", System.Text.Encoding.UTF8.GetString(body.ToArray()), StringComparison.Ordinal);
        }
        else
        {
            Assert.Empty(body.ToArray());
        }
    }

    /// <summary>Verifies missing artifacts preserve retryable media and empty incremental subtitle responses.</summary>
    [Theory]
    [InlineData("manifest.mpd", false)]
    [InlineData("chunk-0-00001.m4s", false)]
    [InlineData("captions-0.vtt", false)]
    [InlineData("manifest.mpd", true)]
    [InlineData("captions-0.vtt", true)]
    public async Task CreateAsync_ArtifactAbsentAtOpen_PreservesMissingResponse(string filename, bool removeDirectory)
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-media-missing-");
        var path = Path.Combine(root.FullName, filename);
        var plan = CreatePlan(root.FullName);
        if (removeDirectory)
        {
            root.Delete(recursive: true);
        }
        var context = new DefaultHttpContext();

        // Act
        var result = await DedicatedMediaFileResponse.CreateAsync(path, filename, plan, 7, context);

        // Assert
        if (filename.EndsWith(".vtt", StringComparison.Ordinal))
        {
            Assert.Empty(Assert.IsType<FileContentHttpResult>(result).FileContents.ToArray());
            Assert.Equal("0", context.Response.Headers["X-Lineup-Subtitle-Offset"].ToString());
        }
        else
        {
            Assert.Equal(425, Assert.IsType<StatusCodeHttpResult>(result).StatusCode);
        }
        if (!removeDirectory)
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>Verifies unexpected storage access failures are not changed into retryable missing-artifact responses.</summary>
    [Fact]
    public async Task CreateAsync_DirectoryInsteadOfFile_PropagatesAccessFailure()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-media-directory-");
        var plan = CreatePlan(root.FullName);
        var context = new DefaultHttpContext();

        // Act
        var exception = await Record.ExceptionAsync(() => DedicatedMediaFileResponse.CreateAsync(root.FullName, "init-0.mp4", plan, null, context));

        // Assert
        Assert.IsType<UnauthorizedAccessException>(exception);
        root.Delete(recursive: true);
    }

    private static DedicatedCmafSessionPlan CreatePlan(string directory)
    {
        var source = new MediaProbeResult(
        [
            new MediaTrackMetadata(0, MediaTrackType.Video, "h264", null, 1280, 720, null, null),
            new MediaTrackMetadata(1, MediaTrackType.Audio, "aac", null, null, null, 2, 48_000)
        ], null);
        return DedicatedCmafSessionPlan.Create(new VirtualOutputOpenStreamResponse
        {
            Accepted = true,
            Source = source,
            RawSource = source,
            Profile = new VirtualTranscodeProfile()
        }, new CmafStreamRequest(), directory);
    }
}
