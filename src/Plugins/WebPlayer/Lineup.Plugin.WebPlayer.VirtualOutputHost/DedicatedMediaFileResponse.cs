using Lineup.Plugin.WebPlayer.FFmpeg;

namespace Lineup.Plugin.WebPlayer.VirtualOutputHost;

/// <summary>Creates responses for validated artifacts belonging to a dedicated media session.</summary>
internal static class DedicatedMediaFileResponse
{
    /// <summary>
    /// Opens validated session artifacts before responding, preserving subtitle offsets, manifest rewriting, and media range support.
    /// The response executor owns binary streams; text artifacts are consumed and disposed before returning.
    /// </summary>
    internal static async Task<IResult> CreateAsync(string path, string filename, DedicatedCmafSessionPlan plan, long? offset, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-cache, no-store";
        var isSubtitle = string.Equals(Path.GetExtension(filename), ".vtt", StringComparison.OrdinalIgnoreCase);
        var stream = OpenGrowingFile(path);
        if (stream is null)
        {
            if (isSubtitle)
            {
                byte[] emptySubtitleBytes = [];
                context.Response.Headers["X-Lineup-Subtitle-Offset"] = "0";
                return Results.File(emptySubtitleBytes, "text/vtt; charset=utf-8");
            }
            return Results.StatusCode(425);
        }
        var isManifest = string.Equals(filename, CmafStreamPlanner.DashManifestName, StringComparison.Ordinal) ||
            string.Equals(filename, CmafStreamPlanner.HlsManifestName, StringComparison.Ordinal);
        var isMediaPlaylist = filename.StartsWith("media_", StringComparison.Ordinal) && filename.EndsWith(".m3u8", StringComparison.Ordinal);
        if (!isSubtitle && !isManifest && !isMediaPlaylist)
        {
            var transferred = false;
            try
            {
                var result = Results.File(stream, GetContentType(filename), lastModified: File.GetLastWriteTimeUtc(stream.SafeFileHandle), enableRangeProcessing: true);
                transferred = true;
                return result;
            }
            finally
            {
                if (!transferred)
                {
                    await stream.DisposeAsync();
                }
            }
        }
        await using (stream)
        {
            if (isSubtitle)
            {
                var effectiveOffset = Math.Clamp(offset ?? 0, 0, stream.Length);
                stream.Position = effectiveOffset;
                var data = new byte[(int)Math.Min(stream.Length - effectiveOffset, 64 * 1024)];
                await stream.ReadExactlyAsync(data, context.RequestAborted);
                context.Response.Headers["X-Lineup-Subtitle-Offset"] = (effectiveOffset + data.Length).ToString(System.Globalization.CultureInfo.InvariantCulture);
                return Results.File(data, "text/vtt; charset=utf-8");
            }
            using var reader = new StreamReader(stream);
            var content = await reader.ReadToEndAsync(context.RequestAborted);
            if (isManifest)
            {
                if (filename == CmafStreamPlanner.DashManifestName && !CmafStreamPlanner.IsDashManifestComplete(content))
                {
                    return ManifestNotReady();
                }
                content = CmafStreamPlanner.RewriteManifestAudioLabels(content, plan.AudioRenditions);
                if (plan.SourceVideoCodec is { } sourceVideoCodec)
                {
                    content = CmafStreamPlanner.RewriteManifestCodecs(content, sourceVideoCodec);
                }
                return Results.Content(content, GetContentType(filename));
            }
            var dashPath = Path.Combine(Path.GetDirectoryName(path)!, CmafStreamPlanner.DashManifestName);
            await using var dashStream = OpenGrowingFile(dashPath);
            if (dashStream is null)
            {
                return Results.StatusCode(425);
            }
            using var dashReader = new StreamReader(dashStream);
            var dashManifest = await dashReader.ReadToEndAsync(context.RequestAborted);
            if (!CmafStreamPlanner.IsDashManifestComplete(dashManifest))
            {
                return ManifestNotReady();
            }
            return Results.Content(CmafStreamPlanner.RewriteHlsProgramDateTimes(content, dashManifest), "application/vnd.apple.mpegurl");
        }
    }

    private static IResult ManifestNotReady() =>
        Results.Json(new { code = "media_manifest_not_ready", error = "The media manifest is being published.", stage = "delivery" }, statusCode: 425);

    private static FileStream? OpenGrowingFile(string path)
    {
        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static string GetContentType(string filename) =>
        Path.GetExtension(filename).ToLowerInvariant() switch
        {
            ".mpd" => "application/dash+xml",
            ".m3u8" => "application/vnd.apple.mpegurl",
            ".m4s" or ".mp4" => "video/mp4",
            ".vtt" => "text/vtt",
            _ => "application/octet-stream"
        };
}
