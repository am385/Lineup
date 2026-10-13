using System.Diagnostics;

namespace Lineup.WebPlayer.VirtualOutputHost;

internal sealed class MediaSessionStartupGuard(string directory, IAsyncDisposable source) : IAsyncDisposable
{
    private Process? _process;
    private bool _ownershipTransferred;

    internal void RegisterProcess(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        _process = process;
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
            if (_process is not null)
            {
                try
                {
                    if (!_process.HasExited)
                    {
                        _process.Kill(entireProcessTree: true);
                    }
                    await _process.WaitForExitAsync();
                }
                catch (InvalidOperationException)
                {
                }
                finally
                {
                    _process.Dispose();
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
