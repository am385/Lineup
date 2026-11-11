using System.Diagnostics;
using Lineup.Core.Streaming.FFmpeg;

namespace Lineup.Plugin.WebPlayer.VirtualOutputHost;

/// <summary>Waits for a dedicated presentation while observing media-process and upstream lifetimes.</summary>
internal static class DedicatedMediaReadiness
{
    /// <summary>Waits for the initial shared manifests and all expected representation fragments.</summary>
    internal static async Task WaitAsync(
        Process process,
        Task<string> errorTask,
        Task? inputTask,
        string dashPath,
        string hlsPath,
        int representationCount,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var operation = Lineup.Core.Extensibility.Diagnostics.LineupTelemetry.Begin("media_readiness", cancellationToken: cancellationToken);
        using var readinessTimeout = new CancellationTokenSource(timeout);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, readinessTimeout.Token);
        int[] representationFragmentCounts = [];
        try
        {
            var directory = Path.GetDirectoryName(dashPath)!;
            while (true)
            {
                linkedCancellation.Token.ThrowIfCancellationRequested();
                if (inputTask?.IsFaulted == true)
                {
                    try
                    {
                        await inputTask;
                    }
                    catch (Exception exception) when (exception is IOException or MediaProcessException)
                    {
                        throw new MediaInputException("The upstream media stream failed before the presentation was ready.", exception);
                    }
                }
                if (process.HasExited)
                {
                    var error = await errorTask.WaitAsync(linkedCancellation.Token);
                    throw new MediaProcessException(process.ExitCode, error);
                }
                if (inputTask?.IsCompleted == true)
                {
                    await inputTask;
                    throw new MediaInputException("The upstream media stream ended before the presentation was ready.");
                }
                representationFragmentCounts = Enumerable.Range(0, representationCount)
                    .Select(index => Directory.EnumerateFiles(directory, $"chunk-{index}-*.m4s").Take(2).Count())
                    .ToArray();
                if (File.Exists(dashPath) && File.Exists(hlsPath) && representationFragmentCounts.All(count => count >= 2) &&
                    Enumerable.Range(0, representationCount).All(index => File.Exists(Path.Combine(directory, $"init-{index}.mp4"))))
                {
                    operation.Succeed();
                    return;
                }
                await Task.Delay(100, linkedCancellation.Token);
            }
        }
        catch (OperationCanceledException) when (readinessTimeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            await process.WaitForExitAsync(CancellationToken.None);
            var error = await errorTask;
            throw new TimeoutException(
                $"The Web Player media presentation did not become ready within {timeout.TotalSeconds:0.###} seconds. " +
                $"Expected {representationCount} representations with fragment counts [{string.Join(", ", representationFragmentCounts)}]. " +
                $"Media processor exit code: {process.ExitCode}. " +
                $"FFmpeg diagnostics: {GetDiagnosticTail(error)}");
        }
    }

    private static string GetDiagnosticTail(string value)
    {
        var lines = value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(" | ", lines.TakeLast(10));
    }
}
