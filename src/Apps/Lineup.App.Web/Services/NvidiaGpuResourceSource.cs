using System.Runtime.InteropServices;
using System.Text;

namespace Lineup.App.Web.Services;

/// <summary>Reads NVIDIA's installed management library; no CUDA context or monitoring subprocess is created.</summary>
internal sealed class NvidiaGpuResourceSource : IGpuResourceSource
{
    private nint _library;
    private bool _initialized;
    private Initialize? _initialize;
    private Initialize? _shutdown;
    private GetCount? _getCount;
    private GetHandle? _getHandle;
    private GetText? _getName;
    private GetText? _getUuid;
    private GetUsage? _getUsage;
    private GetVideoUsage? _getEncode;
    private GetVideoUsage? _getDecode;

    /// <inheritdoc />
    public GpuResourceSnapshot Read()
    {
        if (_library == 0)
        {
            if (!NativeLibrary.TryLoad("libnvidia-ml.so.1", out _library)) return new([], "unsupported");
            var bound = false;
            try
            {
                _initialize = Required<Initialize>("nvmlInit_v2");
                _shutdown = Required<Initialize>("nvmlShutdown");
                _getCount = Required<GetCount>("nvmlDeviceGetCount_v2");
                _getHandle = Required<GetHandle>("nvmlDeviceGetHandleByIndex_v2");
                _getName = Required<GetText>("nvmlDeviceGetName");
                _getUuid = Required<GetText>("nvmlDeviceGetUUID");
                _getUsage = Required<GetUsage>("nvmlDeviceGetUtilizationRates");
                _getEncode = Optional<GetVideoUsage>("nvmlDeviceGetEncoderUtilization");
                _getDecode = Optional<GetVideoUsage>("nvmlDeviceGetDecoderUtilization");
                bound = true;
            }
            finally
            {
                if (!bound) Dispose();
            }
        }
        if (!_initialized)
        {
            var status = _initialize!();
            if (status is 3 or 9) return new([], "unsupported");
            Check(status);
            _initialized = true;
        }
        Check(_getCount!(out var count));
        if (count > 16) throw new GpuCounterException("NVIDIA reported more than 16 GPU devices.");
        var devices = new List<GpuResourceSample>();
        for (uint index = 0; index < count; index++)
        {
            Check(_getHandle!(index, out var device));
            var name = new StringBuilder(128);
            var uuid = new StringBuilder(128);
            Check(_getName!(device, name, 128));
            Check(_getUuid!(device, uuid, 128));
            var status = _getUsage!(device, out var usage);
            var total = status == 3 ? (double?)null : Percent(status, usage.Gpu);
            var encode = Video(_getEncode, device);
            var decode = Video(_getDecode, device);
            devices.Add(new($"nvidia:{uuid}", name.ToString(), total, encode, decode, null,
                total is null && encode is null && decode is null ? "unsupported" : null));
        }
        return new(devices, count == 0 ? "unsupported" : null);
    }

    /// <inheritdoc />
    public void ResetBaseline() => Dispose();

    /// <inheritdoc />
    public void Dispose()
    {
        if (_initialized) _shutdown!();
        _initialized = false;
        if (_library != 0) NativeLibrary.Free(_library);
        _library = 0;
    }

    private T Required<T>(string name) where T : Delegate =>
        Optional<T>(name) ?? throw new GpuCounterException($"The installed NVIDIA management library does not expose {name}.");

    private T? Optional<T>(string name) where T : Delegate =>
        NativeLibrary.TryGetExport(_library, name, out var function) ? Marshal.GetDelegateForFunctionPointer<T>(function) : null;

    private static double? Video(GetVideoUsage? read, nint device)
    {
        if (read is null) return null;
        var status = read(device, out var percent, out _);
        return status == 3 ? null : Percent(status, percent);
    }

    private static double Percent(uint status, uint value)
    {
        Check(status);
        if (value > 100) throw new GpuCounterException("NVIDIA returned an out-of-range GPU percentage.");
        return value;
    }

    private static void Check(uint status)
    {
        if (status == 4) throw new UnauthorizedAccessException("NVIDIA GPU counters require driver monitoring access.");
        if (status != 0) throw new GpuCounterException($"NVIDIA GPU counter query failed with status {status}.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Utilization
    {
        /// <summary>Graphics/compute activity.</summary>
        public uint Gpu;
        /// <summary>Memory-controller activity, not memory allocation.</summary>
        public uint Memory;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint Initialize();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint GetCount(out uint count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint GetHandle(uint index, out nint device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private delegate uint GetText(nint device, StringBuilder value, uint length);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint GetUsage(nint device, out Utilization usage);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint GetVideoUsage(nint device, out uint percent, out uint period);
}
