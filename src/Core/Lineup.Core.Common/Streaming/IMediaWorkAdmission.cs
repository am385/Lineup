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
    public static void Check(string path, int minimumFreeSpaceMib)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minimumFreeSpaceMib);
        if (minimumFreeSpaceMib == 0)
        {
            return;
        }
        var fullPath = Path.GetFullPath(path);
        var volume = DriveInfo.GetDrives()
            .Where(drive => fullPath.StartsWith(drive.Name, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            .OrderByDescending(drive => drive.Name.Length)
            .FirstOrDefault() ?? throw new IOException("The media workspace volume could not be resolved.");
        if (volume.AvailableFreeSpace < minimumFreeSpaceMib * 1024L * 1024L)
        {
            throw new MediaResourceLimitException("media_storage_limit", "The media workspace has less free space than the configured admission watermark.");
        }
    }
}
