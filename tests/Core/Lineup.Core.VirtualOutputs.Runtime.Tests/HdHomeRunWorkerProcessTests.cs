using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Lineup.Core.Common.Streaming;
using Lineup.Core.Common.Guides;
using Lineup.Core.Guides;
using Lineup.Core.Settings;
using Lineup.Core.Streaming;
using Lineup.Core.Extensibility.Streaming;
using Lineup.Plugin.SiliconDust.HDHomeRun.VirtualTuner;
using Lineup.Plugin.LineupLink.Protocol;
using Lineup.Plugin.LineupLink.Virtual;
using Lineup.Core.VirtualOutputs.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Lineup.Core.VirtualOutputs.Runtime.Tests;

/// <summary>Verifies real HDHomeRun capacity and connection admission shared with a dedicated Lineup Link worker.</summary>
public sealed class HdHomeRunWorkerProcessTests
{
    /// <summary>A real dedicated Lineup Link endpoint reports guide capacity explicitly and remains usable for a corrected snapshot.</summary>
    [Fact]
    public async Task LineupLink_GuideEnvelope_ReturnsStructuredFailureAndRecovers()
    {
        // Arrange
        await using var harness = await Harness.StartAsync(null, false, lineupLink: true);
        var channel = new VirtualGuideChannel { ChannelId = new(harness.DeviceId, Guid.NewGuid()), Number = "1", Name = "Example", Programs = [] };
        var snapshot = new VirtualGuideSnapshot
        {
            VirtualDeviceId = harness.DeviceId,
            Generation = new(DateTimeOffset.UtcNow, "coherent-guide"),
            Channels = Enumerable.Repeat(channel, GuideTransfer.MaximumChannels + 1).ToArray()
        };
        harness.Guides.BuildSnapshotAsync(harness.DeviceId, Arg.Any<CancellationToken>()).Returns(_ => snapshot);
        var path = LineupLinkProtocol.GuidePath(harness.DeviceId.Value);

        // Act
        using var rejected = await harness.Client.GetAsync(path, TestContext.Current.CancellationToken);
        var error = await rejected.Content.ReadFromJsonAsync<LineupLinkError>(TestContext.Current.CancellationToken);
        snapshot = snapshot with { Channels = [channel] };
        using var recovered = await harness.Client.GetAsync(path, TestContext.Current.CancellationToken);
        var guide = await recovered.Content.ReadFromJsonAsync<LineupLinkGuide>(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.ServiceUnavailable, rejected.StatusCode);
        Assert.Equal("guide_limit", Assert.IsType<LineupLinkError>(error).Code);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Equal("1", Assert.Single(Assert.IsType<LineupLinkGuide>(guide).Channels).Key);
        Assert.False(harness.HasExited);
    }

    /// <summary>Verifies different production workers share admission, preserve authentication and return capacity after a process crash.</summary>
    [Fact]
    public async Task Admission_LineupLinkAndHdHomeRunProcesses_ShareBudgetAndRecoverAfterCrash()
    {
        // Arrange
        var settings = Substitute.For<ILineupSettingsService>();
        settings.Settings.Returns(new LineupSettings { Streaming = new StreamingConfiguration { MaximumPluginConnections = 1 } });
        var budget = new PluginConnectionBudget(settings);
        await using var hdHomeRun = await Harness.StartAsync(null, false, budget: budget);
        await using var lineupLink = await Harness.StartAsync(null, false, lineupLink: true, budget: budget);
        using var recovery = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = hdHomeRun.Client.BaseAddress };
        var manifestPath = $"{LineupLinkProtocol.DevicePath(lineupLink.DeviceId.Value)}/manifest";

        // Act
        using var unauthenticatedRequest = new HttpRequestMessage(HttpMethod.Get, manifestPath);
        unauthenticatedRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "invalid-test-token");
        using var unauthenticated = await lineupLink.Client.SendAsync(unauthenticatedRequest, TestContext.Current.CancellationToken);
        using var refused = await lineupLink.Client.GetAsync(manifestPath, TestContext.Current.CancellationToken);
        hdHomeRun.Client.Dispose();
        await WaitForConnectionsAsync(budget, 0);
        using var admitted = await lineupLink.Client.GetAsync(manifestPath, TestContext.Current.CancellationToken);
        using var otherRefused = await recovery.GetAsync("/discover.json", TestContext.Current.CancellationToken);
        lineupLink.KillWorker();
        await WaitForConnectionsAsync(budget, 0);
        using var recovered = await recovery.GetAsync("/discover.json", TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Contains("resource_limit", await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.OK, admitted.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, otherRefused.StatusCode);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Equal(1, budget.GetSnapshot().ActiveConnections);
        Assert.Equal(1, budget.GetSnapshot().WorkerConnections[hdHomeRun.DeviceId]);
        Assert.False(hdHomeRun.HasExited);
    }

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

    private static async Task WaitForConnectionsAsync(IPluginConnectionBudget budget, int expected)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        while (budget.GetSnapshot().ActiveConnections != expected)
        {
            await Task.Delay(10, deadline.Token);
        }
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
        private readonly string? _lineupLinkToken;

        private Harness(int? cap, bool selected, bool lineupLink, IPluginConnectionBudget? budget)
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
            IVirtualTunerDriver driver = lineupLink ? new LineupLinkVirtualTunerDriver() : new HdHomeRunVirtualTunerDriver();
            var configuration = JsonSerializer.SerializeToElement(new HdHomeRunVirtualTunerConfiguration { TunerCountCap = cap });
            if (lineupLink)
            {
                var grant = LineupLinkAccessGrantManager.Create(new LineupLinkVirtualConfiguration { RequireEncryption = false }, "Worker test", null, DateTime.UtcNow);
                Assert.Empty(grant.Errors);
                _lineupLinkToken = Assert.IsType<string>(grant.GeneratedToken);
                configuration = JsonSerializer.SerializeToElement(grant.Configuration);
            }
            var device = new VirtualDeviceDefinition
            {
                Id = VirtualDeviceId.New(),
                Name = "Aggregate",
                Adapter = new AdapterConfiguration(driver.Kind, configuration),
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
                Guides, Substitute.For<IVirtualMpegTsOutputService>(), activeStreams);
            _server = new VirtualOutputIpcServer(endpoint, _authority, broker, new VirtualOutputRuntimeStatus(), settings, activeStreams,
                NullLogger<VirtualOutputIpcServer>.Instance, budget ?? new PluginConnectionBudget(settings));
            var capabilities = new HashSet<VirtualOutputCapability> { VirtualOutputCapability.ReadManifest, VirtualOutputCapability.ReportHealth, VirtualOutputCapability.AdmitConnection };
            if (lineupLink)
            {
                capabilities.Add(VirtualOutputCapability.ReadGuide);
            }
            var ticket = _authority.CreateActivation(
                device.Id, driver.Kind, capabilities,
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

        /// <summary>Gets the guide projection supplied through the real authenticated broker.</summary>
        internal IGuideOrchestrator Guides { get; } = Substitute.For<IGuideOrchestrator>();

        /// <summary>Gets the worker's loopback HTTP client.</summary>
        internal HttpClient Client { get; }

        /// <summary>Gets the isolated worker's process identity.</summary>
        internal int ProcessId => _worker!.Id;

        /// <summary>Gets whether the isolated worker has exited.</summary>
        internal bool HasExited => _worker!.HasExited;

        /// <summary>Gets the assigned production virtual device.</summary>
        internal VirtualDeviceId DeviceId => _bootstrap.VirtualDevice.Id;

        /// <summary>Simulates a crash of this owned worker without closing its public client first.</summary>
        internal void KillWorker() => _worker!.Kill(entireProcessTree: true);

        /// <summary>Starts the actual worker and waits for its HTTP listener.</summary>
        internal static async Task<Harness> StartAsync(int? cap, bool selected, bool lineupLink = false, IPluginConnectionBudget? budget = null)
        {
            var harness = new Harness(cap, selected, lineupLink, budget);
            try
            {
                await harness._server.StartAsync(TestContext.Current.CancellationToken);
                var executable = lineupLink ? "Lineup.Plugin.LineupLink.VirtualOutputHost.dll" : "Lineup.Plugin.SiliconDust.HDHomeRun.VirtualOutputHost.dll";
                var path = Path.Combine(AppContext.BaseDirectory, executable);
                var packageRoot = Environment.GetEnvironmentVariable("LINEUP_TEST_WORKER_PACKAGE_ROOT");
                if (!string.IsNullOrWhiteSpace(packageRoot))
                {
                    var catalog = new VirtualOutputWorkerCatalog(packageRoot);
                    var package = await catalog.ResolveAsync(lineupLink ? "lineup-link" : "hdhomerun", TestContext.Current.CancellationToken);
                    path = Path.Combine(package.PackageRoot, package.Manifest.ExecutablePath);
                }
                var managedAssembly = string.Equals(Path.GetExtension(path), ".dll", StringComparison.OrdinalIgnoreCase);
                var start = new ProcessStartInfo(managedAssembly ? "dotnet" : path)
                {
                    WorkingDirectory = Path.GetDirectoryName(path)!,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                if (managedAssembly)
                {
                    start.ArgumentList.Add(path);
                }
                harness._worker = Process.Start(start) ?? throw new InvalidOperationException($"Unable to start {executable}.");
                harness._output = harness._worker.StandardOutput.ReadToEndAsync();
                harness._error = harness._worker.StandardError.ReadToEndAsync();
                await harness._worker.StandardInput.WriteLineAsync(JsonSerializer.Serialize(harness._bootstrap, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                harness._worker.StandardInput.Close();
                await harness.WaitForReadyAsync();
                if (lineupLink)
                {
                    harness.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", harness._lineupLinkToken);
                }
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
                    throw new InvalidOperationException($"Virtual output worker exited: {await _output!} {await _error!}");
                }
                try
                {
                    var path = _lineupLinkToken is null ? "/lineup_status.json" : $"{LineupLinkProtocol.DevicePath(DeviceId.Value)}/manifest";
                    using var response = await Client.GetAsync(path, TestContext.Current.CancellationToken);
                    if (_lineupLinkToken is not null && response.StatusCode == HttpStatusCode.Unauthorized)
                    {
                        return;
                    }
                    response.EnsureSuccessStatusCode();
                    return;
                }
                catch (HttpRequestException)
                {
                    await Task.Delay(50, TestContext.Current.CancellationToken);
                }
            }
            throw new TimeoutException("Virtual output worker did not open its listener.");
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
