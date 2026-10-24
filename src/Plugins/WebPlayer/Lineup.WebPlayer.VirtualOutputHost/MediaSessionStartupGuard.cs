using System.Diagnostics;

namespace Lineup.WebPlayer.VirtualOutputHost;

internal sealed class MediaSessionStartupGuard(string directory, IAsyncDisposable source) : IAsyncDisposable
{
    private readonly List<Process> _processes = [];
    private bool _ownershipTransferred;

    internal void RegisterProcess(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        _processes.Add(process);
    }

    internal void TransferOwnership() => _ownershipTransferred = true;

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
