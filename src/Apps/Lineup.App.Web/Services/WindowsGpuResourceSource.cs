using System.Globalization;
using System.Runtime.InteropServices;

namespace Lineup.App.Web.Services;

/// <summary>Reads Windows GPU engine activity, summing process instances per engine rather than across independent engines.</summary>
internal sealed class WindowsGpuResourceSource : IGpuResourceSource
{
    private readonly Lazy<IReadOnlyDictionary<ulong, string>> _adapterNames;
    private nint _query;
    private nint _counter;
    private bool _baseline;

    /// <summary>Initializes cached native adapter-name discovery without changing GPU counter identities.</summary>
    internal WindowsGpuResourceSource(ILogger logger)
    {
        _adapterNames = new(() =>
        {
            try
            {
                return ReadAdapterNames();
            }
            catch (Exception exception) when (exception is COMException or GpuCounterException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                logger.LogWarning(exception, "Windows GPU adapter names could not be read; GPU cards will use their counter identities");
                return new Dictionary<ulong, string>();
            }
        });
    }

    /// <inheritdoc />
    public GpuResourceSnapshot Read()
    {
        if (_query == 0)
        {
            Check(PdhOpenQueryW(null, 0, out _query));
            var added = false;
            try
            {
                Check(PdhAddEnglishCounterW(_query, @"\GPU Engine(*)\Utilization Percentage", 0, out _counter));
                added = true;
            }
            finally
            {
                if (!added) Dispose();
            }
        }
        Check(PdhCollectQueryData(_query));
        if (!_baseline)
        {
            _baseline = true;
            return new([], "warming_up");
        }
        uint bytes = 0;
        uint count = 0;
        var status = PdhGetFormattedCounterArrayW(_counter, 0x200, ref bytes, out count, 0);
        if (status is 0x800007d5 or 0xc0000bc6) return new([], "warming_up");
        if (status != 0x800007d2) Check(status);
        if (bytes == 0) return new([], "unsupported");
        if (bytes > 4 * 1024 * 1024) throw new GpuCounterException("Windows GPU counter data exceeded the bounded buffer.");
        var buffer = Marshal.AllocHGlobal(checked((int)bytes));
        try
        {
            // Instance churn can resize the array between sizing and reading; retry on the next shared sample.
            status = PdhGetFormattedCounterArrayW(_counter, 0x200, ref bytes, out count, buffer);
            if (status == 0x800007d2) return new([], "warming_up");
            Check(status);
            var size = Marshal.SizeOf<CounterItem>();
            if ((ulong)count * (uint)size > bytes) throw new GpuCounterException("Windows returned an invalid GPU counter array.");
            var values = new List<(string Name, double Value)>();
            for (var index = 0; index < count; index++)
            {
                var item = Marshal.PtrToStructure<CounterItem>(buffer + checked((int)index * size));
                if (item.Value.Status is 0 or 1 && Marshal.PtrToStringUni(item.Name) is { } name)
                {
                    values.Add((name, item.Value.Value));
                }
            }
            return new(Aggregate(values, _adapterNames.Value));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Aggregates busy time by physical adapter/engine; device and video-class totals use the busiest engine.</summary>
    internal static IReadOnlyList<GpuResourceSample> Aggregate(IEnumerable<(string Name, double Value)> samples, IReadOnlyDictionary<ulong, string>? adapterNames = null)
    {
        var engines = new Dictionary<(string Adapter, string Engine, string Kind), double>();
        foreach (var (name, value) in samples)
        {
            var luid = name.IndexOf("_luid_", StringComparison.OrdinalIgnoreCase);
            var engine = name.IndexOf("_eng_", StringComparison.OrdinalIgnoreCase);
            var kind = name.IndexOf("_engtype_", StringComparison.OrdinalIgnoreCase);
            if (luid < 0 || engine <= luid || kind <= engine || !double.IsFinite(value) || value < 0) continue;
            var key = (name[(luid + 1)..engine].ToLowerInvariant(), name[(engine + 5)..kind], name[(kind + 9)..].Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant());
            engines.TryGetValue(key, out var previous);
            engines[key] = Math.Min(100, previous + value);
        }
        if (engines.Select(item => item.Key.Adapter).Distinct(StringComparer.Ordinal).Take(17).Count() > 16)
        {
            throw new GpuCounterException("More than 16 Windows GPU adapters were reported.");
        }
        return engines.GroupBy(item => item.Key.Adapter, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal).Select((group, index) =>
        {
            double? Video(Func<string, bool> include) => group.Where(item => include(item.Key.Kind)).Select(item => (double?)item.Value).Max();
            return new GpuResourceSample($"windows:{group.Key}", DeviceName(group.Key, index, adapterNames),
                group.Max(item => item.Value),
                Video(kind => kind.Contains("videoencode", StringComparison.Ordinal)),
                Video(kind => kind.Contains("videodecode", StringComparison.Ordinal)),
                Video(kind => kind.StartsWith("video", StringComparison.Ordinal) && !kind.Contains("encode", StringComparison.Ordinal) && !kind.Contains("decode", StringComparison.Ordinal)));
        }).ToArray();
    }

    /// <inheritdoc />
    public void ResetBaseline() => Dispose();

    /// <inheritdoc />
    public void Dispose()
    {
        if (_query != 0) PdhCloseQuery(_query);
        _query = 0;
        _counter = 0;
        _baseline = false;
    }

    private static string DeviceName(string adapter, int index, IReadOnlyDictionary<ulong, string>? names)
    {
        var parts = adapter.Split('_');
        if (names is not null && parts.Length == 5 && parts[1].StartsWith("0x", StringComparison.Ordinal) && parts[2].StartsWith("0x", StringComparison.Ordinal) &&
            uint.TryParse(parts[1].AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var high) &&
            uint.TryParse(parts[2].AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var low) &&
            names.TryGetValue(((ulong)high << 32) | low, out var name) && !string.IsNullOrWhiteSpace(name))
        {
            return name.Trim();
        }
        return $"GPU {(index + 1).ToString(CultureInfo.InvariantCulture)} ({adapter})";
    }

    private static IReadOnlyDictionary<ulong, string> ReadAdapterNames()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DXGI adapter names require Windows.");
        var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
        Marshal.ThrowExceptionForHR(CreateDXGIFactory1(ref iid, out var factory));
        try
        {
            var names = new Dictionary<ulong, string>();
            // IDXGIFactory1::EnumAdapters1 and IDXGIAdapter1::GetDesc1 follow their inherited COM methods.
            var enumerate = NativeMethod<EnumAdapters>(factory, 12);
            for (uint index = 0; index < 32; index++)
            {
                var result = enumerate(factory, index, out var adapter);
                if (result == unchecked((int)0x887a0002)) return names;
                Marshal.ThrowExceptionForHR(result);
                try
                {
                    Marshal.ThrowExceptionForHR(NativeMethod<GetDescription>(adapter, 10)(adapter, out var description));
                    if (!string.IsNullOrWhiteSpace(description.Description))
                    {
                        var luid = ((ulong)description.AdapterLuid.High << 32) | description.AdapterLuid.Low;
                        names[luid] = description.Description.Trim();
                    }
                }
                finally
                {
                    Marshal.Release(adapter);
                }
            }
            throw new GpuCounterException("DXGI adapter-name discovery exceeded the bounded adapter count.");
        }
        finally
        {
            Marshal.Release(factory);
        }
    }

    private static T NativeMethod<T>(nint instance, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * nint.Size));

    private static void Check(uint status)
    {
        if (status != 0) throw new GpuCounterException($"Windows GPU counter query failed with status 0x{status:x8}.");
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct CounterValue
    {
        /// <summary>Native counter status.</summary>
        [FieldOffset(0)] public uint Status;
        /// <summary>Native formatted percentage.</summary>
        [FieldOffset(8)] public double Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CounterItem
    {
        /// <summary>Native instance name.</summary>
        public nint Name;
        /// <summary>Native counter value.</summary>
        public CounterValue Value;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct AdapterDescription
    {
        /// <summary>Driver-provided display name.</summary>
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        /// <summary>PCI vendor identifier.</summary>
        public uint VendorId;
        /// <summary>PCI device identifier.</summary>
        public uint DeviceId;
        /// <summary>PCI subsystem identifier.</summary>
        public uint SubsystemId;
        /// <summary>PCI revision identifier.</summary>
        public uint Revision;
        /// <summary>Dedicated video memory in bytes.</summary>
        public nuint DedicatedVideoMemory;
        /// <summary>Dedicated system memory in bytes.</summary>
        public nuint DedicatedSystemMemory;
        /// <summary>Shared system memory in bytes.</summary>
        public nuint SharedSystemMemory;
        /// <summary>Adapter identity, with native 32-bit field alignment.</summary>
        public AdapterLuid AdapterLuid;
        /// <summary>DXGI adapter flags, including software-renderer status.</summary>
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterLuid
    {
        /// <summary>Low 32 bits of the adapter identity.</summary>
        public uint Low;
        /// <summary>High 32 bits of the adapter identity.</summary>
        public uint High;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdapters(nint factory, uint index, out nint adapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDescription(nint adapter, out AdapterDescription description);
    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(ref Guid iid, out nint factory);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhOpenQueryW(string? source, nuint userData, out nint query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhAddEnglishCounterW(nint query, string path, nuint userData, out nint counter);
    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCollectQueryData(nint query);
    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhGetFormattedCounterArrayW(nint counter, uint format, ref uint bytes, out uint count, nint buffer);
    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCloseQuery(nint query);
}
