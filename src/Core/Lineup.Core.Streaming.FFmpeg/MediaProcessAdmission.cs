using System.Diagnostics;
using Lineup.Core.Common.Streaming;

namespace Lineup.Core.Streaming.FFmpeg;

/// <summary>Reserves one processing operation before launching media and retains it until operating-system process exit.</summary>
public static class MediaProcessAdmission
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Process, ReleaseCompletion> Releases = new();

    /// <summary>Awaits acknowledged resource release after process exit, without depending on the caller's process wrapper lifetime.</summary>
    public static Task WaitForReleaseAsync(Process process) => Releases.TryGetValue(process, out var release) ? release.Completion : Task.CompletedTask;

    /// <summary>Checks workspace storage and admits a process before invoking its factory.</summary>
    public static Process Start(Func<Process> start, IMediaWorkAdmission? admission, string? workspace = null)
    {
        ArgumentNullException.ThrowIfNull(start);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        IAsyncDisposable? reservation;
        try
        {
            reservation = admission is null ? null : Task.Run(async () =>
                await admission.AcquireAsync(MediaWorkKind.Processing, cancellationToken: deadline.Token)).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            throw new MediaResourceLimitException("media_admission_timeout", "Media processing admission did not complete within its ten-second deadline.");
        }
        try
        {
            if (workspace is not null)
            {
                admission?.CheckWorkspace(workspace);
            }
            var process = start();
            if (reservation is not null)
            {
                try
                {
                    var observer = Process.GetProcessById(process.Id);
                    Releases.Add(process, new(ReleaseAfterExitAsync(observer, reservation)));
                    reservation = null;
                }
                catch (ArgumentException) when (process.HasExited)
                {
                    // The process finished before an independent lifetime handle could be opened.
                }
            }
            return process;
        }
        finally
        {
            reservation?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static async Task ReleaseAfterExitAsync(Process observer, IAsyncDisposable reservation)
    {
        using (observer)
        {
            await observer.WaitForExitAsync().ConfigureAwait(false);
            await reservation.DisposeAsync().ConfigureAwait(false);
        }

    }

    private sealed record ReleaseCompletion(Task Completion);
}
