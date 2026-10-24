using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Lineup.Common.Streaming;
using Lineup.Core.Guides;
using Lineup.Core.Settings;
using Lineup.Core.Streaming;
using Lineup.Extensibility.Streaming;
using Lineup.SiliconDust.HDHomeRun.VirtualTuner;
using Lineup.Streaming.FFmpeg;
using Lineup.VirtualOutputs.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Lineup.VirtualOutputs.Runtime.Tests;

/// <summary>Verifies the real dedicated HDHomeRun worker reads current capacity over authenticated local IPC.</summary>
public sealed class HdHomeRunWorkerProcessTests
{
    /// <summary>Verifies physical capacity changes reach HTTP discovery without replacing the virtual device or restarting its worker.</summary>
    [Theory]
    [InlineData(null, false, 6, 255)]
    [InlineData(5, false, 5, 5)]
    [InlineData(null, true, 4, 255)]
    public async Task Discover_PhysicalCapacityChanges_UpdatesWithoutWorkerRestart(int? cap, bool selected, int expectedGrowth, int expectedMaximum)
    {
        // Arrange
        await using var harness = await Harness.StartAsync(cap, selected);
        using var initial = await harness.DiscoverAsync();
        var processId = harness.ProcessId;
        var virtualDevice = Assert.Single(harness.Settings.Streaming.VirtualDevices);

        // Act
        harness.SetCapacities(4, 2);
        using var increased = await harness.DiscoverAsync();
        harness.SetCapacities();
        using var removed = await harness.DiscoverAsync();
        harness.SetCapacities(300, null);
        using var maximum = await harness.DiscoverAsync();
        harness.SetCapacities(null, null);
        using var unbounded = await harness.DiscoverAsync();

        // Assert
        Assert.Equal(2, initial.RootElement.GetProperty("TunerCount").GetInt32());
        Assert.Equal(expectedGrowth, increased.RootElement.GetProperty("TunerCount").GetInt32());
        Assert.Equal(0, removed.RootElement.GetProperty("TunerCount").GetInt32());
        Assert.Equal(expectedMaximum, maximum.RootElement.GetProperty("TunerCount").GetInt32());
        Assert.Equal(0, unbounded.RootElement.GetProperty("TunerCount").GetInt32());
        Assert.Same(virtualDevice, Assert.Single(harness.Settings.Streaming.VirtualDevices));
        Assert.Equal(processId, harness.ProcessId);
        Assert.False(harness.HasExited);
        foreach (var response in new[] { increased, removed, maximum, unbounded })
        {
            foreach (var property in new[] { "FriendlyName", "DeviceID", "DeviceAuth", "BaseURL", "LineupURL" })
            {
                Assert.Equal(initial.RootElement.GetProperty(property).GetString(), response.RootElement.GetProperty(property).GetString());
            }
        }
        Assert.Equal("Aggregate", initial.RootElement.GetProperty("FriendlyName").GetString());
        Assert.Equal(harness.Client.BaseAddress!.AbsoluteUri.TrimEnd('/'), initial.RootElement.GetProperty("BaseURL").GetString());
    }

    /// <summary>Verifies an unavailable current manifest is not disguised as a successful cached discovery response and can recover.</summary>
    [Fact]
    public async Task Discover_ManifestUnavailable_FailsThenRecoversWithoutRestart()
    {
        // Arrange
        await using var harness = await Harness.StartAsync(null, false);
        using var initial = await harness.DiscoverAsync();
        var device = Assert.Single(harness.Settings.Streaming.VirtualDevices);
        var processId = harness.ProcessId;

        // Act
        harness.Settings.Streaming = harness.Settings.Streaming with { VirtualDevices = [device with { Enabled = false }] };
        using var unavailable = await harness.Client.GetAsync("/discover.json", TestContext.Current.CancellationToken);
        harness.Settings.Streaming = harness.Settings.Streaming with { VirtualDevices = [device] };
        harness.SetCapacities(4);
        using var recovered = await harness.DiscoverAsync();

        // Assert
        Assert.Equal(HttpStatusCode.InternalServerError, unavailable.StatusCode);
        Assert.Equal(4, recovered.RootElement.GetProperty("TunerCount").GetInt32());
        Assert.Equal(processId, harness.ProcessId);
        Assert.False(harness.HasExited);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly VirtualOutputSessionAuthority _authority = new();
        private readonly VirtualOutputIpcServer _server;
        private readonly VirtualOutputWorkerBootstrap _bootstrap;
        private readonly PhysicalDeviceId[] _physicalIds = [PhysicalDeviceId.New(), PhysicalDeviceId.New()];
        private IReadOnlyList<IPhysicalTunerDevice> _physicalDevices = [];
        private Process? _worker;
        private Task<string>? _output;
        private Task<string>? _error;

        private Harness(int? cap, bool selected)
        {
            var endpoint = OperatingSystem.IsWindows()
                ? new VirtualOutputIpcEndpoint { Transport = VirtualOutputIpcTransport.NamedPipe, Address = $"lineup-hd-test-{Guid.NewGuid():N}" }
                : new VirtualOutputIpcEndpoint
                {
                    Transport = VirtualOutputIpcTransport.UnixDomainSocket,
                    Address = Path.Combine(Path.GetTempPath(), $"lineup-hd-test-{Guid.NewGuid():N}.sock")
                };
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var driver = new HdHomeRunVirtualTunerDriver();
            var device = new VirtualDeviceDefinition
            {
                Id = VirtualDeviceId.New(),
                Name = "Aggregate",
                Adapter = new AdapterConfiguration(driver.Kind, JsonSerializer.SerializeToElement(new HdHomeRunVirtualTunerConfiguration { TunerCountCap = cap })),
                Subscription = selected
                    ? new PhysicalDeviceSubscription { Mode = PhysicalDeviceSubscriptionMode.Selected, SelectedDeviceIds = new HashSet<PhysicalDeviceId> { _physicalIds[0] } }
                    : new PhysicalDeviceSubscription(),
                Hosting = new VirtualOutputHostingDefinition
                {
                    WorkerPolicyKey = "test",
                    Bindings =
                    [
                        new VirtualOutputBindingDefinition
                        {
                            Addresses = ["127.0.0.1"],
                            Port = port,
                            PublicBaseUri = $"http://127.0.0.1:{port}/",
                            SecurityPolicyKey = "test"
                        }
                    ]
                }
            };
            var policy = new VirtualOutputWorkerPolicyDefinition { Key = "test", Name = "Test" };
            Settings = new LineupSettings
            {
                Streaming = new StreamingConfiguration { VirtualDevices = [device], VirtualOutputWorkerPolicies = [policy] }
            };
            var settings = Substitute.For<ILineupSettingsService>();
            settings.Settings.Returns(Settings);
            var catalog = Substitute.For<IPhysicalDeviceCatalog>();
            catalog.Devices.Returns(_ => Volatile.Read(ref _physicalDevices));
            SetCapacities(2);
            var activeStreams = new ActiveStreamRegistry();
            var broker = new VirtualOutputBroker(
                _authority, settings, new StreamingAdapterRegistry([], [driver], []), catalog, Substitute.For<IStreamingRouteResolver>(),
                Substitute.For<IGuideOrchestrator>(), Substitute.For<IVirtualMpegTsOutputService>(), activeStreams);
            _server = new VirtualOutputIpcServer(endpoint, _authority, broker, new VirtualOutputRuntimeStatus(), settings, activeStreams, NullLogger<VirtualOutputIpcServer>.Instance);
            var ticket = _authority.CreateActivation(
                device.Id, driver.Kind, new HashSet<VirtualOutputCapability> { VirtualOutputCapability.ReadManifest, VirtualOutputCapability.ReportHealth },
                TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
            _bootstrap = new VirtualOutputWorkerBootstrap
            {
                Endpoint = endpoint,
                VirtualDevice = device,
                WorkerPolicy = policy,
                SecurityPolicies = [new VirtualOutputSecurityPolicyDefinition { Key = "test", Name = "Test", AllowedNetworks = ["127.0.0.0/8"] }],
                WorkerId = ticket.WorkerId,
                Generation = ticket.Generation,
                BootstrapToken = ticket.BootstrapToken,
                BootstrapExpiresUtc = ticket.ExpiresUtc
            };
            Client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(10) };
        }

        /// <summary>Gets the current trusted-host settings.</summary>
        internal LineupSettings Settings { get; }

        /// <summary>Gets the worker's loopback HTTP client.</summary>
        internal HttpClient Client { get; }

        /// <summary>Gets the isolated worker's process identity.</summary>
        internal int ProcessId => _worker!.Id;

        /// <summary>Gets whether the isolated worker has exited.</summary>
        internal bool HasExited => _worker!.HasExited;

        /// <summary>Starts the actual worker and waits for its HTTP listener.</summary>
        internal static async Task<Harness> StartAsync(int? cap, bool selected)
        {
            var harness = new Harness(cap, selected);
            try
            {
                await harness._server.StartAsync(TestContext.Current.CancellationToken);
                var path = Path.Combine(AppContext.BaseDirectory, "Lineup.SiliconDust.HDHomeRun.VirtualOutputHost.dll");
                var start = new ProcessStartInfo("dotnet")
                {
                    WorkingDirectory = AppContext.BaseDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                start.ArgumentList.Add(path);
                harness._worker = Process.Start(start) ?? throw new InvalidOperationException("Unable to start the HDHomeRun worker.");
                harness._output = harness._worker.StandardOutput.ReadToEndAsync();
                harness._error = harness._worker.StandardError.ReadToEndAsync();
                await harness._worker.StandardInput.WriteLineAsync(JsonSerializer.Serialize(harness._bootstrap, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                harness._worker.StandardInput.Close();
                await harness.WaitForReadyAsync();
                return harness;
            }
            catch
            {
                await harness.DisposeAsync();
                throw;
            }
        }

        /// <summary>Atomically replaces active physical snapshots without editing the virtual output.</summary>
        internal void SetCapacities(params int?[] capacities)
        {
            var devices = capacities.Select((capacity, index) =>
            {
                var device = Substitute.For<IPhysicalTunerDevice>();
                device.Snapshot.Returns(new PhysicalDeviceSnapshot { Id = _physicalIds[index], Kind = "test", Name = $"Physical {index}", Capacity = new PhysicalTunerCapacity(capacity) });
                return device;
            }).ToArray();
            Volatile.Write(ref _physicalDevices, devices);
        }

        /// <summary>Reads an actual successful worker discovery response.</summary>
        internal async Task<JsonDocument> DiscoverAsync()
        {
            using var response = await Client.GetAsync("/discover.json", TestContext.Current.CancellationToken);
            response.EnsureSuccessStatusCode();
            await using var content = await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken);
            return await JsonDocument.ParseAsync(content, cancellationToken: TestContext.Current.CancellationToken);
        }

        private async Task WaitForReadyAsync()
        {
            var elapsed = Stopwatch.StartNew();
            while (elapsed.Elapsed < TimeSpan.FromSeconds(15))
            {
                if (_worker!.HasExited)
                {
                    throw new InvalidOperationException($"HDHomeRun worker exited: {await _output!} {await _error!}");
                }
                try
                {
                    using var response = await Client.GetAsync("/lineup_status.json", TestContext.Current.CancellationToken);
                    response.EnsureSuccessStatusCode();
                    return;
                }
                catch (HttpRequestException)
                {
                    await Task.Delay(50, TestContext.Current.CancellationToken);
                }
            }
            throw new TimeoutException("HDHomeRun worker did not open its listener.");
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            try
            {
                if (_worker is not null)
                {
                    if (!_worker.HasExited)
                    {
                        _worker.Kill(entireProcessTree: true);
                    }
                    await _worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    _worker.Dispose();
                }
            }
            finally
            {
                await _server.StopAsync(CancellationToken.None);
                _server.Dispose();
                _authority.Dispose();
            }
        }
    }
}
