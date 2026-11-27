using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Lineup.Core.Common.Configuration;
using Lineup.Core.Common.Streaming;
using Lineup.Core.Streaming.FFmpeg;
using Lineup.Core.VirtualOutputs.Protocol;
using Lineup.Core.VirtualOutputs.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Lineup.TestSupport;

if (args is ["native-probe"])
{
    using var process = new MediaProcessFactory().Start(new MediaProcessRequest { FileName = "/usr/bin/true", Arguments = [] });
    await process.WaitForExitAsync();
    return process.ExitCode;
}

if (args is ["media", var executable, var report, var mode])
{
    var startInfo = new ProcessStartInfo("dotnet")
    {
        UseShellExecute = false,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        ArgumentList = { executable, "orphan", report }
    };
    using var media = mode == "barrier"
        ? OwnedMediaProcess.Start(startInfo, launched =>
        {
            AtomicTestReport.Write(report + ".barrier", launched.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Thread.Sleep(Timeout.Infinite);
        })
        : new MediaProcessFactory().Start(new MediaProcessRequest
        {
            FileName = startInfo.FileName,
            Arguments = startInfo.ArgumentList.ToArray(),
            RedirectStandardInput = true,
            RedirectStandardOutput = true
        });
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return 0;
}

if (args is ["worker", var packageRoot])
{
    var workerExecutable = "Lineup.TestSupport.FakeFFmpeg.dll";
    await using var executableStream = File.OpenRead(Path.Combine(packageRoot, workerExecutable));
    var hash = Convert.ToHexString(await SHA256.HashDataAsync(executableStream)).ToLowerInvariant();
    var package = new VirtualOutputWorkerPackage
    {
        PackageRoot = packageRoot,
        Manifest = new VirtualOutputWorkerManifest
        {
            WorkerId = "probe",
            Version = "1.0.0",
            Publisher = "Lineup",
            ExecutablePath = workerExecutable,
            ExecutableSha256 = hash,
            AdapterKinds = ["probe"],
            MinimumProtocolVersion = VirtualOutputProtocol.MinimumVersion,
            MaximumProtocolVersion = VirtualOutputProtocol.MaximumVersion,
            MinimumLineupVersion = "3.0.0",
            SettingsDescriptor = new AdapterSettingsDescriptor { AdapterKind = "probe" }
        }
    };
    var bootstrap = new VirtualOutputWorkerBootstrap
    {
        Endpoint = new VirtualOutputIpcEndpoint { Address = "unused-probe" },
        VirtualDevice = new VirtualDeviceDefinition
        {
            Id = VirtualDeviceId.New(),
            Name = "descendant",
            Adapter = new AdapterConfiguration("probe", JsonSerializer.SerializeToElement(new { }))
        },
        WorkerPolicy = new VirtualOutputWorkerPolicyDefinition { Key = "unlimited", Name = "Unlimited" },
        WorkerId = "probe",
        Generation = 1,
        BootstrapToken = $"probe-{Guid.NewGuid():N}",
        BootstrapExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(1)
    };
    var launcher = new VirtualOutputWorkerLauncher(NullLogger<VirtualOutputWorkerLauncher>.Instance);
    await using var worker = await launcher.StartAsync(package, bootstrap);
    var workerIdPath = Path.Combine(packageRoot, "worker.pid");
    await AtomicTestReport.WriteAsync(workerIdPath, worker.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return 0;
}

return 125;
