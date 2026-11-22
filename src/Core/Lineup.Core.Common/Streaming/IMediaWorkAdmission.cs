namespace Lineup.Core.Common.Streaming;

/// <summary>Identifies independently enforced media work, not logical viewers.</summary>
public enum MediaWorkKind
{
    /// <summary>A retained acquisition, graph transform, or reusable presentation.</summary>
    Producer,
    /// <summary>One FFmpeg or FFprobe process, including remuxing, captions, and synthetic tests.</summary>
    Processing
}

/// <summary>Admits media work before allocation and retires only explicitly idle owners under pressure.</summary>
public interface IMediaWorkAdmission
{
    /// <summary>Reserves work until disposal; the optional callback must atomically refuse retirement if any consumer has joined.</summary>
    ValueTask<IAsyncDisposable> AcquireAsync(MediaWorkKind kind, Func<CancellationToken, Task<bool>>? retireIdle = null, CancellationToken cancellationToken = default);

    /// <summary>Attempts idle retirement through resource owners, awaiting their cleanup before returning.</summary>
    Task ReclaimIdleAsync(CancellationToken cancellationToken = default);

    /// <summary>Checks available space on the actual workspace volume before new output creation; zero disables the watermark.</summary>
    void CheckWorkspace(string path);
}

/// <summary>Reports explicit media admission rejection before allocating new work.</summary>
public sealed class MediaResourceLimitException(string code, string message) : InvalidOperationException(message)
{
    /// <summary>Gets the stable admission failure code.</summary>
    public string Code { get; } = code;
}

/// <summary>Checks actual workspace volumes without deleting files or treating missing disk information as unlimited space.</summary>
public static class MediaWorkspaceAdmission
{
    /// <summary>Rejects new allocation when available space is below the configured MiB watermark.</summary>
    public static void Check(string path, int minimumFreeSpaceMib) =>
        Check(path, minimumFreeSpaceMib, () => DriveInfo.GetDrives().Select(drive => (drive.Name, (Func<long>)(() => drive.AvailableFreeSpace))));

    /// <summary>Checks the longest containing volume while deferring disk queries until a volume has been selected.</summary>
    /// <param name="path">Workspace path, including a directory that has not yet been created.</param>
    /// <param name="minimumFreeSpaceMib">Minimum permitted free space in MiB, or zero to disable the check.</param>
    /// <param name="getVolumes">Volume roots and their deferred available-space readers.</param>
    internal static void Check(string path, int minimumFreeSpaceMib, Func<IEnumerable<(string Root, Func<long> AvailableFreeSpace)>> getVolumes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minimumFreeSpaceMib);
        if (minimumFreeSpaceMib == 0)
        {
            return;
        }
        var fullPath = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var volume = getVolumes()
            .Select(volume => (Root: Path.TrimEndingDirectorySeparator(volume.Root), volume.AvailableFreeSpace))
            .Where(volume => fullPath.StartsWith(volume.Root, comparison) &&
                (fullPath.Length == volume.Root.Length || Path.EndsInDirectorySeparator(volume.Root) ||
                    fullPath[volume.Root.Length] == Path.DirectorySeparatorChar || fullPath[volume.Root.Length] == Path.AltDirectorySeparatorChar))
            .OrderByDescending(volume => volume.Root.Length)
            .FirstOrDefault();
        if (volume.AvailableFreeSpace is null)
        {
            throw new IOException("The media workspace volume could not be resolved.");
        }
        if (volume.AvailableFreeSpace() < minimumFreeSpaceMib * 1024L * 1024L)
        {
            throw new MediaResourceLimitException("media_storage_limit", "The media workspace has less free space than the configured admission watermark.");
        }
    }
}
