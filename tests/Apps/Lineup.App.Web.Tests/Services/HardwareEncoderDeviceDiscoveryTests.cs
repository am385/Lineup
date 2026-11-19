using Lineup.App.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lineup.App.Web.Tests.Services;

/// <summary>Verifies encoder candidates retain backend-native identities rather than monitoring or filtered-list indices.</summary>
public sealed class HardwareEncoderDeviceDiscoveryTests
{
    /// <summary>Verifies filtering never renumbers physical adapters or offers another vendor or a software adapter.</summary>
    [Theory]
    [InlineData(HardwareVideoEncoder.Qsv, "4")]
    [InlineData(HardwareVideoEncoder.Amf, "1,5")]
    public void WindowsDevices_MixedAdapters_PreserveNativeIndices(HardwareVideoEncoder encoder, string expected)
    {
        // Arrange
        WindowsGpuResourceSource.Adapter[] adapters =
        [
            new(0, "NVIDIA", 0x10de, 800, false),
            new(1, "AMD integrated", 0x1002, 200, false),
            new(2, "Software", 0x1002, 300, true),
            new(4, "Intel", 0x8086, 100, false),
            new(5, "AMD discrete", 0x1002, 400, false)
        ];

        // Act
        var devices = HardwareEncoderDeviceDiscovery.CreateWindowsDevices(encoder, adapters);

        // Assert
        Assert.Equal(expected.Split(','), devices.Select(device => device.Value));
        Assert.All(devices, device => Assert.Contains($"({device.Value})", device.Label, StringComparison.Ordinal));
        Assert.DoesNotContain(devices, device => device.Label.Contains("Software", StringComparison.Ordinal));
    }

    /// <summary>Verifies sparse Linux render-node numbers and Intel filtering survive unrelated card and malformed-node entries.</summary>
    [Theory]
    [InlineData(HardwareVideoEncoder.Qsv, "/dev/dri/renderD130")]
    [InlineData(HardwareVideoEncoder.Vaapi, "/dev/dri/renderD128,/dev/dri/renderD130,/dev/dri/renderD135")]
    public void LinuxDevices_ExposedRenderNodes_PreservePathsAndFilterQsv(HardwareVideoEncoder encoder, string expected)
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-encoder-devices-");
        try
        {
            var nodes = Directory.CreateDirectory(Path.Combine(root.FullName, "dri")).FullName;
            var sysfs = Directory.CreateDirectory(Path.Combine(root.FullName, "sys")).FullName;
            foreach (var (name, vendor) in new[] { ("renderD128", "0x1002"), ("renderD130", "0x8086"), ("renderD135", ""), ("card0", "0x8086"), ("renderDinvalid", "0x8086") })
            {
                File.WriteAllText(Path.Combine(nodes, name), string.Empty);
                var device = Directory.CreateDirectory(Path.Combine(sysfs, name, "device"));
                if (vendor.Length > 0) File.WriteAllText(Path.Combine(device.FullName, "vendor"), vendor + "\n");
            }

            // Act
            var devices = HardwareEncoderDeviceDiscovery.ReadLinuxDevices(encoder, nodes, sysfs);

            // Assert
            Assert.Equal(expected.Split(','), devices.Select(device => device.Value));
            Assert.Contains(devices, device => device.Label == "Intel (/dev/dri/renderD130)");
            Assert.All(devices, device => Assert.Empty(HardwareEncodingValidation.Validate(new() { Encoder = encoder, Device = device.Value })));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>Verifies a missing driver is an explicit failure, and concurrent callers share one deferred backend discovery.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Discovery_ConcurrentRequests_CachesCandidatesOrExplicitFailure(bool fail)
    {
        // Arrange
        var calls = 0;
        var service = new HardwareEncoderDeviceDiscovery(NullLogger<HardwareEncoderDeviceDiscovery>.Instance, _ =>
        {
            Interlocked.Increment(ref calls);
            if (fail) throw new DllNotFoundException("Driver not installed.");
            return [new("3", "Candidate (3)")];
        });

        // Act
        var first = service.GetDevicesAsync(HardwareVideoEncoder.Nvenc);
        var second = service.GetDevicesAsync(HardwareVideoEncoder.Nvenc);
        var snapshots = await Task.WhenAll(first, second);

        // Assert
        Assert.Same(first, second);
        Assert.Equal(1, calls);
        Assert.All(snapshots, snapshot =>
        {
            Assert.Equal(fail, snapshot.DiscoveryFailed);
            Assert.Equal(fail ? [] : new[] { "3" }, snapshot.Devices.Select(device => device.Value));
        });
    }

    /// <summary>Verifies software and system-managed VideoToolbox selection never loads device-discovery drivers.</summary>
    [Theory]
    [InlineData(HardwareVideoEncoder.Software)]
    [InlineData(HardwareVideoEncoder.VideoToolbox)]
    public async Task Discovery_SystemManagedEncoder_DoesNotEnumerate(HardwareVideoEncoder encoder)
    {
        // Arrange
        var service = new HardwareEncoderDeviceDiscovery(NullLogger<HardwareEncoderDeviceDiscovery>.Instance, _ => throw new InvalidOperationException("Unexpected discovery."));

        // Act
        var snapshot = await service.GetDevicesAsync(encoder);

        // Assert
        Assert.Empty(snapshot.Devices);
        Assert.False(snapshot.DiscoveryFailed);
    }
}
