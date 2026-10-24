namespace Lineup.Streaming.FFmpeg;

/// <summary>Continuously drains media-process diagnostics without retaining an unbounded process history.</summary>
public static class MediaProcessDiagnostics
{
    /// <summary>Drains without owning the reader, retaining at most fifty non-empty lines from the last 16,384 characters with normalized LF separators.</summary>
    public static async Task<string> ReadTailAsync(TextReader reader, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var buffer = new char[16_384];
        var offset = 0;
        var retained = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = await reader.ReadAsync(buffer.AsMemory(offset), cancellationToken);
            if (count == 0)
            {
                break;
            }
            offset = (offset + count) % buffer.Length;
            retained = Math.Min(buffer.Length, retained + count);
        }
        var tail = retained < buffer.Length
            ? new string(buffer, 0, retained)
            : string.Concat(buffer.AsSpan(offset), buffer.AsSpan(0, offset));
        return string.Join("\n", tail.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).TakeLast(50));
    }
}
