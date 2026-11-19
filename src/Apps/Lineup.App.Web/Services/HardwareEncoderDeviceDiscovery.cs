using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Lineup.App.Web.Services;

/// <summary>Identifies a visible encoder device using the identifier accepted by its FFmpeg backend.</summary>
public sealed record HardwareEncoderDevice(string Value, string Label);

/// <summary>Contains discovered candidates and explicitly distinguishes discovery failures from an empty device list.</summary>
public sealed record HardwareEncoderDeviceSnapshot(IReadOnlyList<HardwareEncoderDevice> Devices, bool DiscoveryFailed = false);

/// <summary>Discovers local device candidates without starting an encode or using telemetry identities as encoder indices.</summary>
public interface IHardwareEncoderDeviceDiscovery
{
    /// <summary>Gets cached device candidates for the selected backend in the management host/container.</summary>
    Task<HardwareEncoderDeviceSnapshot> GetDevicesAsync(HardwareVideoEncoder encoder);
}

/// <summary>Discovers bounded DXGI, CUDA and DRM device lists independently of resource monitoring.</summary>
public sealed class HardwareEncoderDeviceDiscovery : IHardwareEncoderDeviceDiscovery
{
    private readonly ILogger<HardwareEncoderDeviceDiscovery> _logger;
    private readonly Func<HardwareVideoEncoder, IReadOnlyList<HardwareEncoderDevice>> _discover;
    private readonly ConcurrentDictionary<HardwareVideoEncoder, Lazy<Task<HardwareEncoderDeviceSnapshot>>> _snapshots = new();

    /// <summary>Initializes native device discovery, deferred until a hardware backend is selected.</summary>
    public HardwareEncoderDeviceDiscovery(ILogger<HardwareEncoderDeviceDiscovery> logger) : this(logger, DiscoverDevices)
    {
    }

    /// <summary>Initializes discovery with a deterministic device reader for lifecycle and failure tests.</summary>
    internal HardwareEncoderDeviceDiscovery(ILogger<HardwareEncoderDeviceDiscovery> logger, Func<HardwareVideoEncoder, IReadOnlyList<HardwareEncoderDevice>> discover)
    {
        _logger = logger;
        _discover = discover;
    }

    /// <inheritdoc />
    public Task<HardwareEncoderDeviceSnapshot> GetDevicesAsync(HardwareVideoEncoder encoder)
    {
        if (encoder is HardwareVideoEncoder.Software or HardwareVideoEncoder.VideoToolbox)
        {
            return Task.FromResult(new HardwareEncoderDeviceSnapshot([]));
        }
        return _snapshots.GetOrAdd(encoder, backend => new(() => Task.Run(() => Discover(backend)))).Value;
    }

    private HardwareEncoderDeviceSnapshot Discover(HardwareVideoEncoder encoder)
    {
        try
        {
            return new(_discover(encoder));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or COMException or GpuCounterException or
            DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            _logger.LogWarning(exception, "GPU device discovery failed for {Encoder}; existing device settings and manual entry remain available", encoder);
            return new([], DiscoveryFailed: true);
        }
    }

    private static IReadOnlyList<HardwareEncoderDevice> DiscoverDevices(HardwareVideoEncoder encoder)
    {
        if (encoder == HardwareVideoEncoder.Nvenc && (OperatingSystem.IsWindows() || OperatingSystem.IsLinux()))
        {
            return ReadCudaDevices();
        }
        if (OperatingSystem.IsWindows() && encoder is HardwareVideoEncoder.Qsv or HardwareVideoEncoder.Amf)
        {
            return CreateWindowsDevices(encoder, WindowsGpuResourceSource.ReadAdapters());
        }
        if (OperatingSystem.IsLinux() && encoder is HardwareVideoEncoder.Qsv or HardwareVideoEncoder.Vaapi)
        {
            return ReadLinuxDevices(encoder, "/dev/dri", "/sys/class/drm");
        }
        return [];
    }

    /// <summary>Filters physical DXGI candidates without renumbering native adapter indices.</summary>
    internal static IReadOnlyList<HardwareEncoderDevice> CreateWindowsDevices(HardwareVideoEncoder encoder, IReadOnlyList<WindowsGpuResourceSource.Adapter> adapters) =>
        adapters.Where(adapter => !adapter.Software && (encoder == HardwareVideoEncoder.Qsv && adapter.VendorId == 0x8086 ||
            encoder == HardwareVideoEncoder.Amf && adapter.VendorId == 0x1002))
            .Select(adapter => new HardwareEncoderDevice(adapter.Index.ToString(CultureInfo.InvariantCulture), $"{adapter.Name} ({adapter.Index.ToString(CultureInfo.InvariantCulture)})"))
            .ToArray();

    /// <summary>Reads exposed render nodes, restricting QSV candidates to Intel devices while retaining actual node numbers.</summary>
    internal static IReadOnlyList<HardwareEncoderDevice> ReadLinuxDevices(HardwareVideoEncoder encoder, string deviceRoot, string sysfsRoot)
    {
        if (!Directory.Exists(deviceRoot)) return [];
        var nodes = Directory.EnumerateFiles(deviceRoot, "renderD*")
            .Where(path => Path.GetFileName(path) is { Length: > 7 } name && name.AsSpan(7).IndexOfAnyExceptInRange('0', '9') < 0)
            .Order(StringComparer.Ordinal).Take(33).ToArray();
        if (nodes.Length > 32) throw new GpuCounterException("DRM discovery exceeded the bounded render-node count.");
        var devices = new List<HardwareEncoderDevice>();
        foreach (var node in nodes)
        {
            var name = Path.GetFileName(node);
            var vendorPath = Path.Combine(sysfsRoot, name, "device", "vendor");
            var vendor = File.Exists(vendorPath) ? File.ReadAllText(vendorPath).Trim().ToLowerInvariant() : string.Empty;
            if (encoder == HardwareVideoEncoder.Qsv && vendor != "0x8086") continue;
            var manufacturer = vendor switch { "0x8086" => "Intel", "0x1002" => "AMD", "0x10de" => "NVIDIA", _ => "DRM" };
            var device = $"/dev/dri/{name}";
            devices.Add(new(device, $"{manufacturer} ({device})"));
        }
        return devices;
    }

    private static IReadOnlyList<HardwareEncoderDevice> ReadCudaDevices()
    {
        var library = NativeLibrary.Load(OperatingSystem.IsWindows() ? "nvcuda.dll" : "libcuda.so.1");
        try
        {
            T Function<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
            var status = Function<CudaInitialize>("cuInit")(0);
            if (status == 100) return [];
            CheckCuda(status);
            CheckCuda(Function<CudaGetCount>("cuDeviceGetCount")(out var count));
            if (count is < 0 or > 32) throw new GpuCounterException("CUDA reported an invalid or excessive device count.");
            var getDevice = Function<CudaGetDevice>("cuDeviceGet");
            var getName = Function<CudaGetName>("cuDeviceGetName");
            var devices = new List<HardwareEncoderDevice>();
            for (var index = 0; index < count; index++)
            {
                CheckCuda(getDevice(out var device, index));
                var name = new StringBuilder(256);
                CheckCuda(getName(name, name.Capacity, device));
                var value = index.ToString(CultureInfo.InvariantCulture);
                devices.Add(new(value, $"{name} ({value})"));
            }
            return devices;
        }
        finally
        {
            NativeLibrary.Free(library);
        }
    }

    private static void CheckCuda(int status)
    {
        if (status != 0) throw new GpuCounterException($"CUDA device discovery failed with status {status}.");
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int CudaInitialize(uint flags);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int CudaGetCount(out int count);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int CudaGetDevice(out int device, int ordinal);
    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Ansi)]
    private delegate int CudaGetName(StringBuilder name, int length, int device);
}
