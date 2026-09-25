using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace Lineup.Web.Services;

/// <summary>
/// Identifies and evaluates process-owned transient stream directories.
/// </summary>
internal static class TransientDirectoryOwnership
{
    /// <summary>Gets the application-owned transient HLS directory name.</summary>
    public const string HlsDirectoryName = "hdhomerun-hls";

    private static readonly long ProcessStartTimeTicks = GetCurrentProcessStartTimeTicks();
    private static readonly string ProcessInstanceId = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Gets the directory name owned by the current process instance.
    /// </summary>
    public static string CurrentDirectoryName { get; } =
        CreateDirectoryName(ProcessInstanceId);

    /// <summary>
    /// Gets a process-owned directory beneath the supplied transient root.
    /// </summary>
    /// <param name="rootDirectory">The shared transient root.</param>
    /// <returns>The current process's isolated directory.</returns>
    public static string GetCurrentDirectory(string rootDirectory) => Path.Combine(rootDirectory, CurrentDirectoryName);

    /// <summary>
    /// Gets a uniquely isolated directory owned by the current process.
    /// </summary>
    /// <param name="rootDirectory">The shared transient root.</param>
    /// <returns>A unique current-process directory.</returns>
    public static string CreateCurrentDirectory(string rootDirectory) => Path.Combine(rootDirectory, CreateDirectoryName(Guid.NewGuid().ToString("N")));

    /// <summary>
    /// Determines whether the named directory belongs to a live process instance.
    /// </summary>
    /// <param name="directoryName">The final directory path component.</param>
    /// <returns>The owner status.</returns>
    public static TransientDirectoryOwnerStatus GetOwnerStatus(string directoryName) =>
        GetOwnerStatus(directoryName, GetProcessStartTimeTicks);

    /// <summary>
    /// Determines whether the named directory belongs to a live process instance using a test process lookup.
    /// </summary>
    /// <param name="directoryName">The final directory path component.</param>
    /// <param name="getProcessStartTimeTicks">Resolves a live process's UTC start time ticks, or <see langword="null"/> when the process is absent.</param>
    /// <returns>The owner status.</returns>
    internal static TransientDirectoryOwnerStatus GetOwnerStatus(string directoryName, Func<int, long?> getProcessStartTimeTicks)
    {
        var parts = directoryName.Split('-', 3, StringSplitOptions.None);
        if (parts.Length < 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var processId))
        {
            return TransientDirectoryOwnerStatus.Unknown;
        }

        long? liveStartTimeTicks;
        try
        {
            liveStartTimeTicks = getProcessStartTimeTicks(processId);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or Win32Exception)
        {
            return TransientDirectoryOwnerStatus.Unknown;
        }

        if (!liveStartTimeTicks.HasValue)
        {
            return TransientDirectoryOwnerStatus.Inactive;
        }

        if (parts.Length == 2)
        {
            return Guid.TryParseExact(parts[1], "N", out _)
                ? TransientDirectoryOwnerStatus.Active
                : TransientDirectoryOwnerStatus.Unknown;
        }

        return long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var recordedStartTimeTicks) &&
            Guid.TryParseExact(parts[2], "N", out _)
                ? recordedStartTimeTicks == liveStartTimeTicks.Value
                    ? TransientDirectoryOwnerStatus.Active
                    : TransientDirectoryOwnerStatus.Inactive
                : TransientDirectoryOwnerStatus.Unknown;
    }

    private static long? GetProcessStartTimeTicks(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.HasExited ? null : process.StartTime.ToUniversalTime().Ticks;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static long GetCurrentProcessStartTimeTicks()
    {
        using var process = Process.GetCurrentProcess();
        return process.StartTime.ToUniversalTime().Ticks;
    }

    private static string CreateDirectoryName(string instanceId) =>
        string.Create(CultureInfo.InvariantCulture, $"{Environment.ProcessId}-{ProcessStartTimeTicks}-{instanceId}");
}

/// <summary>
/// Describes whether a transient directory's owning process instance is active.
/// </summary>
internal enum TransientDirectoryOwnerStatus
{
    /// <summary>The recorded process instance is no longer active.</summary>
    Inactive,

    /// <summary>The recorded process instance is active.</summary>
    Active,

    /// <summary>The directory owner cannot be determined safely.</summary>
    Unknown
}
