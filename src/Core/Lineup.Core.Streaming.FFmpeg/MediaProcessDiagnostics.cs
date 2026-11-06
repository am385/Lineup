using System.Collections.Concurrent;

namespace Lineup.Core.Streaming.FFmpeg;

/// <summary>Continuously drains media-process diagnostics without retaining an unbounded process history.</summary>
public static class MediaProcessDiagnostics
{
    /// <summary>Normalizes a diagnostic suffix to at most fifty non-empty lines and 16,384 characters.</summary>
    public static string GetTail(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var tail = value[^Math.Min(value.Length, 16_384)..];
        return string.Join("\n", tail.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).TakeLast(50));
    }

    /// <summary>Appends one live diagnostic line while bounding both retained line count and total character count.</summary>
    public static void AppendLine(ConcurrentQueue<string> lines, string line)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(line);
        lines.Enqueue(GetTail(line));
        while (lines.Count > 50 || lines.Sum(value => value.Length) + Math.Max(0, lines.Count - 1) > 16_384)
        {
            lines.TryDequeue(out _);
        }
    }

    /// <summary>Retains bounded failure details and correlation fields without serializing an unbounded exception history.</summary>
    public static void LogFailure(ILogger logger, Exception exception, string stage, string sessionId, string? requestId = null, string? streamSessionId = null, int? exitCode = null, string? diagnostics = null)
    {
        var cause = exception.GetBaseException();
        var processFailure = cause as MediaProcessException;
        logger.LogWarning(
            "Media startup failure at {StartupStage}, session {MediaSessionId}, request {BrokerRequestId}, stream {StreamSessionId}, exit {ProcessExitCode}, source exit {UpstreamProcessExitCode}. " +
            "Diagnostics: {DiagnosticTail}. Source diagnostics: {UpstreamDiagnosticTail}",
            stage, sessionId, requestId, streamSessionId, exitCode ?? processFailure?.ExitCode, processFailure?.ExitCode,
            GetTail(diagnostics ?? processFailure?.Diagnostics ?? cause.Message), GetTail(processFailure?.Diagnostics ?? cause.Message));
    }

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
        return GetTail(tail);
    }
}
