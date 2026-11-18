namespace Lineup.App.Web.Services;

/// <summary>One whole-device observation; null metrics are unavailable, not idle.</summary>
public sealed record GpuResourceSample(string Id, string Name, double? Utilization, double? Encode, double? Decode, double? Video, string? UnavailableReason = null);

/// <summary>GPU devices visible to the management host and an explicit discovery or collection state.</summary>
public sealed record GpuResourceSnapshot(IReadOnlyList<GpuResourceSample> Devices, string? UnavailableReason = null);

/// <summary>Reads device-wide GPU counters without starting monitoring subprocesses.</summary>
public interface IGpuResourceSource : IDisposable
{
    /// <summary>Reads available devices; unsupported metrics remain null.</summary>
    GpuResourceSnapshot Read();

    /// <summary>Discards elapsed-counter baselines when collection pauses.</summary>
    void ResetBaseline();
}

/// <summary>Discovers native GPU counters once on demand and isolates their failures from process monitoring.</summary>
public sealed class NativeGpuResourceSource : IGpuResourceSource
{
    private readonly ILogger<NativeGpuResourceSource> _logger;
    private readonly Func<IReadOnlyList<IGpuResourceSource>> _discover;
    private readonly HashSet<int> _failed = [];
    private readonly Dictionary<int, IReadOnlyList<GpuResourceSample>> _knownDevices = [];
    private IReadOnlyList<IGpuResourceSource>? _sources;
    private bool _disposed;

    /// <summary>Initializes platform-native, read-only GPU monitoring.</summary>
    public NativeGpuResourceSource(ILogger<NativeGpuResourceSource> logger) : this(logger, () => Discover(logger))
    {
    }

    /// <summary>Initializes GPU monitoring with deterministic provider discovery.</summary>
    internal NativeGpuResourceSource(ILogger<NativeGpuResourceSource> logger, Func<IReadOnlyList<IGpuResourceSource>> discover)
    {
        _logger = logger;
        _discover = discover;
    }

    /// <inheritdoc />
    public GpuResourceSnapshot Read()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            _sources ??= _discover();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (_failed.Add(-1))
            {
                _logger.LogWarning(exception, "GPU counter discovery failed; GPU readings are unavailable");
            }
            return new([], exception is UnauthorizedAccessException ? "permission_denied" : "read_failed");
        }
        var devices = new List<GpuResourceSample>();
        string? reason = "unsupported";
        string? failureReason = null;
        for (var index = 0; index < _sources.Count; index++)
        {
            try
            {
                var snapshot = _sources[index].Read();
                devices.AddRange(snapshot.Devices);
                if (snapshot.Devices.Count > 0) _knownDevices[index] = snapshot.Devices;
                else AddUnavailable(index, snapshot.UnavailableReason ?? "unsupported");
                reason = snapshot.UnavailableReason ?? reason;
                _failed.Remove(index);
            }
            catch (Exception exception) when (exception is GpuCounterException or IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                failureReason = exception is UnauthorizedAccessException ? "permission_denied" : "read_failed";
                AddUnavailable(index, failureReason);
                if (_failed.Add(index))
                {
                    _logger.LogWarning(exception, "Native GPU provider {Provider} could not read counters", _sources[index].GetType().Name);
                }
            }
        }
        return new(devices, failureReason ?? (devices.Count == 0 ? reason : null));

        void AddUnavailable(int provider, string state)
        {
            if (!_knownDevices.TryGetValue(provider, out var known)) return;
            devices.AddRange(known.Select(device => device with { Utilization = null, Encode = null, Decode = null, Video = null, UnavailableReason = state }));
        }
    }

    /// <inheritdoc />
    public void ResetBaseline()
    {
        foreach (var source in _sources ?? [])
        {
            source.ResetBaseline();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var source in _sources ?? [])
        {
            source.Dispose();
        }
    }

    private static IReadOnlyList<IGpuResourceSource> Discover(ILogger logger)
    {
        if (OperatingSystem.IsWindows()) return WindowsGpuResourceDiscovery.Discover(logger);
        if (OperatingSystem.IsLinux()) return LinuxGpuResourceDiscovery.Discover(logger);
        return [];
    }
}

/// <summary>A native GPU operation failed without affecting media processing.</summary>
internal sealed class GpuCounterException : Exception
{
    /// <summary>Initializes a diagnostic native-counter failure.</summary>
    internal GpuCounterException(string message) : base(message)
    {
    }
}
