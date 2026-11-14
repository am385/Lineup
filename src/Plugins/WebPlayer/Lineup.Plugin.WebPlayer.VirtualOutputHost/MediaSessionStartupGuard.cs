using System.Diagnostics;

namespace Lineup.Plugin.WebPlayer.VirtualOutputHost;

/// <summary>Represents the media session startup guard.</summary>
/// <param name="directory">The directory value.</param>
/// <param name="source">The source value.</param>
internal sealed class MediaSessionStartupGuard(string directory, IAsyncDisposable source) : IAsyncDisposable
{
    private readonly List<Process> _processes = [];
    private bool _ownershipTransferred;

    /// <summary>Registers process.</summary>
    /// <param name="process">The process value.</param>
    internal void RegisterProcess(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        _processes.Add(process);
    }

    /// <summary>Executes transfer ownership.</summary>
    internal void TransferOwnership() => _ownershipTransferred = true;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_ownershipTransferred)
        {
            return;
        }
        try
        {
            foreach (var process in _processes)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    await process.WaitForExitAsync();
                    await Lineup.Core.Streaming.FFmpeg.MediaProcessAdmission.WaitForReleaseAsync(process);
                }
                catch (InvalidOperationException)
                {
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        finally
        {
            try
            {
                await source.DisposeAsync();
            }
            finally
            {
                await DeleteDirectoryAsync(directory);
            }
        }
    }

    /// <summary>Deletes directory asynchronously.</summary>
    /// <param name="directory">The directory value.</param>
    /// <returns>A task that completes when the operation has finished.</returns>
    internal static async Task DeleteDirectoryAsync(string directory)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
                return;
            }
            catch (IOException) when (attempt < 2)
            {
                await Task.Delay(100);
            }
        }
    }
}
