using System.IO.Compression;

namespace Lineup.Streaming.Sweep;

/// <summary>One explicitly captured browser response body or its diagnostic capture failure.</summary>
internal sealed record FragmentResponse(string Url, byte[]? Body, string? Error);

/// <summary>Retains completed media bodies independently of Playwright's best-effort trace resource capture.</summary>
internal static class FragmentCapture
{
    /// <summary>Identifies DASH metadata, independent initialization and media responses worth retaining.</summary>
    public static bool IsResource(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.AbsolutePath.EndsWith("/manifest.mpd", StringComparison.Ordinal)
            || (uri.Segments.Last().StartsWith("init-", StringComparison.Ordinal) && uri.AbsolutePath.EndsWith(".mp4", StringComparison.Ordinal))
            || FragmentAlignment.Parse(url, DateTimeOffset.UnixEpoch) is not null);

    /// <summary>Records body-capture failure explicitly; incomplete media cannot count as completed timing samples.</summary>
    public static async Task<FragmentResponse> ReadAsync(string url, Func<Task<byte[]>> readBody, TimeSpan? timeout = null)
    {
        try
        {
            var body = await readBody().WaitAsync(timeout ?? TimeSpan.FromSeconds(60));
            if (body.Length == 0 || body.Length > 64 * 1024 * 1024)
            {
                throw new InvalidDataException("Response body is empty or exceeds the bounded media inspection limit.");
            }
            return new(url, body, null);
        }
        catch (Exception error) when (error is PlaywrightException or IOException or TimeoutException)
        {
            return new(url, null, error.Message);
        }
    }

    /// <summary>Writes an auditable trace-compatible resource archive, including explicit failed-capture records.</summary>
    public static async Task SaveAsync(string path, IReadOnlyList<FragmentResponse> responses)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var records = new List<string>();
        for (var index = 0; index < responses.Count; index++)
        {
            var response = responses[index];
            var name = response.Body is null ? null : $"{index:D6}.bin";
            if (response.Body is not null)
            {
                await using var output = archive.CreateEntry("resources/" + name).Open();
                await output.WriteAsync(response.Body);
            }
            records.Add(JsonSerializer.Serialize(new
            {
                snapshot = new
                {
                    request = new { url = response.Url },
                    response = new { status = 200, content = new { _sha1 = name }, captureError = response.Error }
                }
            }));
        }
        await using var writer = new StreamWriter(archive.CreateEntry("trace.network").Open());
        await writer.WriteAsync(string.Join("\n", records));
    }
}
