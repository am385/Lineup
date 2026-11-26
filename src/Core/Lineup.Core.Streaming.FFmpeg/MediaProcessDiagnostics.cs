using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

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
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["CorrelationId"] = System.Diagnostics.Activity.Current?.TraceId.ToString()
        });
        var cause = exception.GetBaseException();
        var processFailure = cause as MediaProcessException;
        logger.LogWarning(
            "Media startup failure at {StartupStage}, session {MediaSessionId}, request {BrokerRequestId}, stream {StreamSessionId}, exit {ProcessExitCode}, source exit {UpstreamProcessExitCode}. " +
            "Diagnostics: {DiagnosticTail}. Source diagnostics: {UpstreamDiagnosticTail}",
            stage, sessionId, requestId, streamSessionId, exitCode ?? processFailure?.ExitCode, processFailure?.ExitCode,
            GetTail(diagnostics ?? processFailure?.Diagnostics ?? cause.Message), GetTail(processFailure?.Diagnostics ?? cause.Message));
    }

    /// <summary>Reports a published producer's unexpected exit with bounded diagnostics and its independent source-termination reason.</summary>
    public static void LogUnexpectedExit(ILogger logger, string sessionId, int exitCode, string diagnostics, string? sourceTerminationCode)
    {
        logger.LogWarning(
            "Media processor exited unexpectedly after playback started, session {MediaSessionId}, exit {ProcessExitCode}, source termination {SourceTerminationCode}. Diagnostics: {DiagnosticTail}",
            sessionId, exitCode, sourceTerminationCode, GetTail(diagnostics));
    }

    /// <summary>Reports an unrequested caption-extractor exit with drained diagnostics, without stopping otherwise healthy playback.</summary>
    public static async Task ObserveCaptionExitAsync(Process process, Task<string> diagnostics, ILogger logger, string sessionId, Func<string?> sourceTerminationCode, CancellationToken cancellationToken)
    {
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            var exitCode = process.ExitCode;
            var tail = await diagnostics.WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            logger.LogWarning(
                "Embedded-caption extractor exited unexpectedly, session {MediaSessionId}, exit {ProcessExitCode}, source termination {SourceTerminationCode}. Diagnostics: {DiagnosticTail}",
                sessionId, exitCode, sourceTerminationCode(), GetTail(tail));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Drains without owning the reader, retaining at most fifty non-empty lines from the last 16,384 characters with normalized LF separators.</summary>
    /// <param name="reader">The borrowed diagnostic reader.</param>
    /// <param name="cancellationToken">Stops diagnostic drainage.</param>
    /// <param name="lineObserved">Optional observer of complete lines no longer than 1,024 characters.</param>
    public static async Task<string> ReadTailAsync(TextReader reader, CancellationToken cancellationToken = default, Action<string>? lineObserved = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var buffer = new char[16_384];
        var offset = 0;
        var retained = 0;
        var line = lineObserved is null ? null : new StringBuilder();
        var truncated = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = await reader.ReadAsync(buffer.AsMemory(offset), cancellationToken);
            if (count == 0)
            {
                break;
            }
            if (line is not null && lineObserved is not null)
            {
                for (var index = offset; index < offset + count; index++)
                {
                    var character = buffer[index];
                    if (character is '\r' or '\n')
                    {
                        if (!truncated && line.Length > 0)
                        {
                            lineObserved(line.ToString());
                        }
                        line.Clear();
                        truncated = false;
                    }
                    else if (!truncated)
                    {
                        if (line.Length == 1024)
                        {
                            line.Clear();
                            truncated = true;
                        }
                        else
                        {
                            line.Append(character);
                        }
                    }
                }
            }
            offset = (offset + count) % buffer.Length;
            retained = Math.Min(buffer.Length, retained + count);
        }
        if (line is { Length: > 0 } && !truncated && lineObserved is not null)
        {
            lineObserved(line.ToString());
        }
        var tail = retained < buffer.Length
            ? new string(buffer, 0, retained)
            : string.Concat(buffer.AsSpan(offset), buffer.AsSpan(0, offset));
        return GetTail(tail);
    }
}
