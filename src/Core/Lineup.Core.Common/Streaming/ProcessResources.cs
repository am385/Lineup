using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Lineup.Core.Common.Streaming;

/// <summary>One identity-scoped operating-system process observation; CPU uses 100 percent per logical core.</summary>
public sealed record ProcessResourceSample(string Id, string Role, int ProcessId, double? CpuPercent, long? ResidentBytes, DateTimeOffset ObservedAtUtc, string? UnavailableReason);

/// <summary>Provides cumulative process counters without exposing process control to monitoring consumers.</summary>
public interface IProcessResourceCounter : IDisposable
{
    /// <summary>Gets the original process creation identity when supplied by native counters.</summary>
    long? StartTimeUtcTicks => null;

    /// <summary>Reads cumulative CPU time and current resident bytes, or throws when the original process is unavailable.</summary>
    (TimeSpan Cpu, long ResidentBytes) Read();
}

/// <summary>Tracks explicitly owned processes and reads them only when a monitoring owner requests a sample.</summary>
public sealed class ProcessResourceRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Func<int, IProcessResourceCounter> _open;
    private readonly TimeProvider _clock;
    private bool _endedSinceSample;

    /// <summary>Gets whether the last interval omitted CPU from an owned process that ended between samples.</summary>
    public bool LastSampleIncomplete { get; private set; }

    /// <summary>Gets the number of owned worker targets whose counters must arrive through authenticated worker reports.</summary>
    public int RemoteProcessCount
    {
        get
        {
            lock (_gate)
            {
                return _entries.Values.Count(entry => entry.Role == "remote");
            }
        }
    }

    /// <summary>Gets the process-local registry; entries are never discovered through process enumeration.</summary>
    public static ProcessResourceRegistry Local { get; } = new();

    /// <summary>Initializes a registry with replaceable operating-system counters and elapsed-time source.</summary>
    public ProcessResourceRegistry(Func<int, IProcessResourceCounter>? open = null, TimeProvider? clock = null)
    {
        _open = open ?? (id => new NativeCounter(id));
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Raised for actionable observation failures, once per entry until a successful observation.</summary>
    public event Action<string>? ObservationFailed;

    /// <summary>Registers a known owned process until its independent lifetime owner unregisters it.</summary>
    public string Register(int processId, string role, long? expectedStartTimeUtcTicks = null)
    {
        var id = Guid.NewGuid().ToString("N");
        lock (_gate)
        {
            _entries.Add(id, new(processId, role, expectedStartTimeUtcTicks));
        }
        return id;
    }

    /// <summary>Changes the safe resource role without replacing process identity or counter baselines.</summary>
    public void SetRole(string id, string role)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(id, out var entry))
            {
                entry.Role = role;
            }
        }
    }

    /// <summary>Removes an ended ownership registration and disposes any retained observation handle.</summary>
    public void Unregister(string id)
    {
        lock (_gate)
        {
            if (_entries.Remove(id, out var entry))
            {
                _endedSinceSample |= entry.Role != "remote";
                entry.Counter?.Dispose();
            }
        }
    }

    /// <summary>Pins the process observation while its lifetime is known; failures remain visible in subsequent samples.</summary>
    public void Attach(string id)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(id, out var entry) && !entry.OpenAttempted)
            {
                entry.OpenAttempted = true;
                try
                {
                    var counter = _open(entry.ProcessId);
                    if (entry.ExpectedStartTimeUtcTicks is { } expected && counter.StartTimeUtcTicks != expected)
                    {
                        counter.Dispose();
                        throw new InvalidOperationException("The original process creation identity no longer matches.");
                    }
                    entry.Counter = counter;
                    entry.Failure = null;
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException or IOException or UnauthorizedAccessException)
                {
                    entry.Failure = "process_unavailable";
                    ObservationFailed?.Invoke($"Resource observation could not attach to owned process {entry.ProcessId}: {exception.Message}");
                }
            }
        }
    }

    /// <summary>Discards elapsed CPU boundaries after monitoring was paused without discarding process ownership.</summary>
    public void ResetBaselines()
    {
        lock (_gate)
        {
            foreach (var entry in _entries.Values)
            {
                entry.Previous = null;
            }
        }
    }

    /// <summary>Samples registered original identities exactly once, with unavailable values instead of synthetic zeroes.</summary>
    public IReadOnlyList<ProcessResourceSample> Sample()
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            var timestamp = _clock.GetTimestamp();
            LastSampleIncomplete = _endedSinceSample;
            _endedSinceSample = false;
            var samples = new List<ProcessResourceSample>(_entries.Count);
            foreach (var (id, entry) in _entries)
            {
                if (entry.Role == "remote")
                {
                    continue;
                }
                double? cpu = null;
                long? resident = null;
                var reason = entry.Failure;
                try
                {
                    if (entry.Counter is not null)
                    {
                        var counters = entry.Counter.Read();
                        resident = counters.ResidentBytes;
                        if (entry.Previous is { } previous)
                        {
                            var elapsed = _clock.GetElapsedTime(entry.Timestamp, timestamp).TotalSeconds;
                            if (elapsed > 0 && counters.Cpu >= previous)
                            {
                                cpu = (counters.Cpu - previous).TotalSeconds / elapsed * 100;
                            }
                        }
                        entry.Previous = counters.Cpu;
                        entry.Timestamp = timestamp;
                        entry.WarningReported = false;
                        reason = cpu is null ? "warming_up" : null;
                    }
                }
                catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException or IOException or UnauthorizedAccessException)
                {
                    entry.Previous = null;
                    reason = "process_unavailable";
                    if (!entry.WarningReported)
                    {
                        entry.WarningReported = true;
                        ObservationFailed?.Invoke($"Resource observation failed for owned process {entry.ProcessId}: {exception.Message}");
                    }
                }
                samples.Add(new(id, entry.Role, entry.ProcessId, cpu, resident, now, reason));
            }
            return samples;
        }
    }

    private sealed class Entry(int processId, string role, long? expectedStartTimeUtcTicks)
    {
        public int ProcessId { get; } = processId;
        public long? ExpectedStartTimeUtcTicks { get; } = expectedStartTimeUtcTicks;
        public string Role { get; set; } = role;
        public bool OpenAttempted { get; set; }
        public IProcessResourceCounter? Counter { get; set; }
        public TimeSpan? Previous { get; set; }
        public long Timestamp { get; set; }
        public string? Failure { get; set; } = "process_unavailable";
        public bool WarningReported { get; set; }
    }

    private sealed class NativeCounter : IProcessResourceCounter
    {
        private readonly Process _process;
        private readonly string? _linuxStatPath;
        private readonly long _linuxClockTicks;
        private readonly ulong _linuxStartTicks;

        public NativeCounter(int id)
        {
            _process = Process.GetProcessById(id);
            try
            {
                if (OperatingSystem.IsLinux())
                {
                    // Linux _SC_CLK_TCK has the same selector in glibc and musl.
                    _linuxClockTicks = (long)Sysconf(2);
                    if (_linuxClockTicks <= 0)
                    {
                        throw new NotSupportedException("The operating system did not provide its process CPU clock frequency.");
                    }
                    _linuxStatPath = Path.Combine(Path.DirectorySeparatorChar.ToString(), "proc", id.ToString(CultureInfo.InvariantCulture), "stat");
                    _linuxStartTicks = ReadLinuxCounters().StartTicks;
                }
                StartTimeUtcTicks = _process.StartTime.ToUniversalTime().Ticks;
                _process.EnableRaisingEvents = true;
            }

            catch
            {
                _process.Dispose();
                throw;
            }
        }

        public long? StartTimeUtcTicks { get; }

        public (TimeSpan Cpu, long ResidentBytes) Read()
        {
            if (_process.HasExited)
            {
                throw new InvalidOperationException("The original process has exited.");
            }
            if (_linuxStatPath is not null)
            {
                var counters = ReadLinuxCounters();
                if (counters.StartTicks != _linuxStartTicks)
                {
                    throw new InvalidOperationException("The original process creation identity no longer matches.");
                }
                return (counters.Cpu, counters.ResidentBytes);
            }
            _process.Refresh();
            if (OperatingSystem.IsWindows())
            {
                if (!GetProcessMemoryInfo(_process.SafeHandle, out var memory, (uint)Marshal.SizeOf<MemoryCounters>()))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }
                if ((ulong)memory.WorkingSetSize > long.MaxValue)
                {
                    throw new InvalidDataException("The owned process returned an out-of-range resident counter.");
                }
                return (_process.TotalProcessorTime, (long)memory.WorkingSetSize);
            }
            return (_process.TotalProcessorTime, _process.WorkingSet64);
        }

        private (TimeSpan Cpu, long ResidentBytes, ulong StartTicks) ReadLinuxCounters()
        {
            var stat = File.ReadAllText(_linuxStatPath!);
            var nameEnd = stat.LastIndexOf(')');
            // The tail starts at field 3; the command name can contain spaces and parentheses.
            var fields = nameEnd < 0 ? [] : stat[(nameEnd + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 22 ||
                !ulong.TryParse(fields[11], NumberStyles.None, CultureInfo.InvariantCulture, out var user) ||
                !ulong.TryParse(fields[12], NumberStyles.None, CultureInfo.InvariantCulture, out var system) ||
                !ulong.TryParse(fields[19], NumberStyles.None, CultureInfo.InvariantCulture, out var start) ||
                !long.TryParse(fields[21], NumberStyles.None, CultureInfo.InvariantCulture, out var pages) ||
                pages < 0 || pages > long.MaxValue / Environment.SystemPageSize)
            {
                throw new InvalidDataException("The owned process returned invalid CPU or resident counters.");
            }
            return (TimeSpan.FromSeconds(user / (double)_linuxClockTicks + system / (double)_linuxClockTicks), pages * Environment.SystemPageSize, start);
        }

        public void Dispose() => _process.Dispose();

        [DllImport("psapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessMemoryInfo(SafeProcessHandle process, out MemoryCounters counters, uint size);

        [DllImport("libc", EntryPoint = "sysconf")]
        private static extern nint Sysconf(int name);

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryCounters
        {
            public uint Size;
            public uint PageFaultCount;
            public nuint PeakWorkingSetSize;
            public nuint WorkingSetSize;
            public nuint QuotaPeakPagedPoolUsage;
            public nuint QuotaPagedPoolUsage;
            public nuint QuotaPeakNonPagedPoolUsage;
            public nuint QuotaNonPagedPoolUsage;
            public nuint PagefileUsage;
            public nuint PeakPagefileUsage;
        }
    }
}
