using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Lineup.App.Web.Services;

/// <summary>Reads i915 device-wide engine busy-time events through read-only Linux performance counters.</summary>
internal sealed class IntelGpuResourceSource(string path) : IGpuResourceSource
{
    private readonly List<Engine> _engines = [];
    private bool _opened;

    /// <inheritdoc />
    public GpuResourceSnapshot Read()
    {
        if (!_opened) Open();
        var values = new List<(string Name, double? Busy)>();
        foreach (var engine in _engines)
        {
            if (ReadCounter(engine.Handle, out var current, (nuint)Marshal.SizeOf<PerfValue>()) != Marshal.SizeOf<PerfValue>())
            {
                throw new GpuCounterException($"Reading the Intel GPU counter failed with errno {Marshal.GetLastPInvokeError()}.");
            }
            values.Add((engine.Name, engine.Previous is { } previous ? Busy(previous.Value, previous.Running, current.Value, current.Running) : null));
            engine.Previous = current;
        }
        double? Maximum(Func<string, bool> include) => values.Where(item => include(item.Name)).Select(item => item.Busy).Max();
        var total = Maximum(_ => true);
        var video = Maximum(name => name.StartsWith("vcs", StringComparison.Ordinal) || name.StartsWith("vecs", StringComparison.Ordinal));
        return new([new($"intel:{Path.GetFileName(path)}", $"Intel GPU ({Path.GetFileName(path)})", total, null, null, video,
            total is null ? "warming_up" : null)]);
    }

    /// <summary>Computes a device engine's busy fraction using scheduled counter time, rejecting reset or unavailable counters.</summary>
    internal static double? Busy(ulong previous, ulong previousRunning, ulong current, ulong running) =>
        current < previous || running <= previousRunning ? null : Math.Min(100, (current - previous) / (double)(running - previousRunning) * 100);

    /// <inheritdoc />
    public void ResetBaseline() => Dispose();

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var engine in _engines) engine.Handle.Dispose();
        _engines.Clear();
        _opened = false;
    }

    private void Open()
    {
        if (!OperatingSystem.IsLinux()) throw new GpuCounterException("Intel PMU telemetry requires Linux.");
        var number = RuntimeInformation.ProcessArchitecture switch { Architecture.X64 => 298, Architecture.Arm64 => 241, _ => 0 };
        if (number == 0) throw new GpuCounterException("Intel GPU counters are unsupported on this process architecture.");
        if (!uint.TryParse(File.ReadAllText(Path.Combine(path, "type")).Trim(), CultureInfo.InvariantCulture, out var type))
        {
            throw new GpuCounterException("The Intel driver returned an invalid PMU type.");
        }
        var mask = Path.Combine(path, "cpumask");
        var cpu = 0;
        if (File.Exists(mask) && (!int.TryParse(File.ReadAllText(mask).Trim().Split(',', '-')[0], CultureInfo.InvariantCulture, out cpu) || cpu < 0))
        {
            throw new GpuCounterException("The Intel driver returned an invalid PMU CPU mask.");
        }
        try
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(path, "events"), "*-busy").Order(StringComparer.Ordinal).Take(64))
            {
                var text = File.ReadAllText(file).Trim();
                if (File.ReadAllText(file + ".unit").Trim() != "ns") throw new GpuCounterException("The Intel GPU busy counter does not report nanoseconds.");
                if (!text.StartsWith("config=0x", StringComparison.Ordinal) ||
                    !ulong.TryParse(text.AsSpan(9), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var config))
                {
                    throw new GpuCounterException("The Intel driver exposes an unsupported PMU event format.");
                }
                var attributes = new PerfAttributes { Type = type, Size = 64, Config = config, ReadFormat = 3 };
                var descriptor = PerfEventOpen(number, ref attributes, -1, cpu, -1, 8);
                if (descriptor < 0)
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error is 1 or 13) throw new UnauthorizedAccessException("Intel GPU PMU access is denied by the host/container performance-counter policy.");
                    throw new GpuCounterException($"Opening the Intel GPU PMU counter failed with errno {error}.");
                }
                _engines.Add(new(Path.GetFileName(file), new((nint)descriptor, true)));
            }
            if (_engines.Count == 0) throw new GpuCounterException("The Intel GPU PMU has no readable engine busy events.");
            _opened = true;
        }
        finally
        {
            if (!_opened) Dispose();
        }
    }

    private sealed class Engine(string name, SafeFileHandle handle)
    {
        /// <summary>Kernel engine event name.</summary>
        public string Name { get; } = name;
        /// <summary>Owned, close-on-exec counter descriptor.</summary>
        public SafeFileHandle Handle { get; } = handle;
        /// <summary>Previous monotonic observation.</summary>
        public PerfValue? Previous { get; set; }
    }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct PerfAttributes
    {
        /// <summary>PMU event source type.</summary>
        [FieldOffset(0)] public uint Type;
        /// <summary>Version-zero Linux attribute size.</summary>
        [FieldOffset(4)] public uint Size;
        /// <summary>Driver-published event configuration.</summary>
        [FieldOffset(8)] public ulong Config;
        /// <summary>Includes enabled/running time in counter reads.</summary>
        [FieldOffset(32)] public ulong ReadFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PerfValue
    {
        /// <summary>Engine busy nanoseconds.</summary>
        public ulong Value;
        /// <summary>Counter enabled nanoseconds.</summary>
        public ulong Enabled;
        /// <summary>Counter scheduled nanoseconds.</summary>
        public ulong Running;
    }

    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static extern long PerfEventOpen(long number, ref PerfAttributes attributes, int pid, int cpu, int group, ulong flags);
    [DllImport("libc", EntryPoint = "read", SetLastError = true)]
    private static extern nint ReadCounter(SafeFileHandle descriptor, out PerfValue value, nuint count);
}
