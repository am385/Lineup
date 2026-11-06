using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lineup.Common.Guides;
using Lineup.Common.Streaming;
using Lineup.Core.Settings;
using Lineup.Core.Streaming;
using Lineup.Streaming.FFmpeg;
using Lineup.VirtualOutputs.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Lineup.VirtualOutputs.Runtime.Tests;

/// <summary>Verifies the real dedicated Web Player executable across its HTTP, IPC, FFmpeg, and cleanup boundaries.</summary>
public sealed class WebPlayerWorkerProcessTests
{
    /// <summary>Verifies normal host builds install discoverable packages that can activate a dedicated Web Player listener.</summary>
    [Theory]
    [InlineData("Lineup.Web", "Debug")]
    [InlineData("Lineup.Web", "Release")]
    [InlineData("Lineup.Tui", "Debug")]
    [InlineData("Lineup.Tui", "Release")]
    public async Task HostBuild_CleanOutput_InstallsAndActivatesWorker(string host, string configuration)
    {
        // Arrange
        var output = Directory.CreateTempSubdirectory("lineup-host-build-");
        try
        {
            var startInfo = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = GetRepositoryRoot(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (var argument in new[]
            {
                "build", Path.Combine("src", "Apps", host, $"{host}.csproj"), "--configuration", configuration,
                "--no-restore", "--output", output.FullName, "-p:SourceRevisionId=worker-packaging-test", "--verbosity", "quiet"
            })
            {
                startInfo.ArgumentList.Add(argument);
            }

            // Act
            await BuildHostAsync(startInfo);
            var catalog = new VirtualOutputWorkerCatalog(Path.Combine(output.FullName, "virtual-output-workers"));
            var package = await catalog.ResolveAsync("web-player", TestContext.Current.CancellationToken);
            var workerPath = Path.Combine(package.PackageRoot, package.Manifest.ExecutablePath);
            var workerModified = File.GetLastWriteTimeUtc(workerPath);
            await BuildHostAsync(startInfo);
            var unchangedWorkerModified = File.GetLastWriteTimeUtc(workerPath);
            var dependencyPath = Path.Combine(package.PackageRoot, "Lineup.WebPlayer.VirtualOutputHost.deps.json");
            File.Delete(dependencyPath);
            await BuildHostAsync(startInfo);
            await using var harness = await WebPlayerWorkerHarness.StartAsync(
                (request, _) => Task.FromResult(CreateBrokeredStream(request, new PacedPatternStream())),
                TestContext.Current.CancellationToken,
                workerPath: workerPath);
            using var response = await harness.Client.GetAsync("/health", TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal(4, (await catalog.LoadAsync(TestContext.Current.CancellationToken)).Count);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(package.Manifest.ExecutableSha256, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(workerPath, TestContext.Current.CancellationToken))), ignoreCase: true);
            Assert.Equal(workerModified, unchangedWorkerModified);
            Assert.Equal(workerModified, File.GetLastWriteTimeUtc(workerPath));
            Assert.True(File.Exists(dependencyPath));
            Assert.True(File.Exists(Path.Combine(package.PackageRoot, "Lineup.WebPlayer.VirtualOutputHost.runtimeconfig.json")));
            Assert.True(File.Exists(Path.Combine(output.FullName, "virtual-output-workers", "packaging.stamp")));
        }
        finally
        {
            output.Delete(recursive: true);
        }
    }

    /// <summary>Verifies design-time evaluation and the explicit packaging opt-out never publish workers.</summary>
    [Theory]
    [InlineData("DesignTimeBuild")]
    [InlineData("SkipVirtualOutputWorkerPublish")]
    public async Task HostPackagingTarget_Disabled_DoesNotInstallWorkers(string property)
    {
        // Arrange
        var output = Directory.CreateTempSubdirectory("lineup-host-build-disabled-");
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = GetRepositoryRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in new[]
        {
            "msbuild", Path.Combine("src", "Apps", "Lineup.Web", "Lineup.Web.csproj"), "-target:PrepareVirtualOutputWorkersForBuild",
            $"-p:{property}=true", $"-p:OutDir={output.FullName}{Path.DirectorySeparatorChar}", "-p:SourceRevisionId=worker-packaging-test", "-verbosity:quiet"
        })
        {
            startInfo.ArgumentList.Add(argument);
        }
        try
        {
            // Act
            await BuildHostAsync(startInfo);

            // Assert
            Assert.False(Directory.Exists(Path.Combine(output.FullName, "virtual-output-workers")));
        }
        finally
        {
            output.Delete(recursive: true);
        }
    }

    private static string GetRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Lineup.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new DirectoryNotFoundException("Unable to locate the Lineup repository for the host-build regression.");
    }

    private static async Task BuildHostAsync(ProcessStartInfo startInfo)
    {
        using var build = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start the clean host build.");
        var stdout = build.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = build.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        try
        {
            await build.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.True(build.ExitCode == 0, $"{await stdout}{Environment.NewLine}{await stderr}");
        }
        finally
        {
            if (!build.HasExited)
            {
                build.Kill(entireProcessTree: true);
                await build.WaitForExitAsync();
            }
        }
    }

    /// <summary>Verifies a recognized copy failure retries once and reports the actual fallback presentation.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Worker_SourceMuxerFailure_RetriesFallback(bool explicitOverride)
    {
        // Arrange
        await using var harness = await WebPlayerWorkerHarness.StartAsync(
            (request, _) => Task.FromResult(CreateBrokeredStream(request, new PacedPatternStream())),
            TestContext.Current.CancellationToken,
            startupMode: "copy-fails");
        var json = explicitOverride
            ? """{"preferredAudio":0,"fallbackAudio":0,"overrides":{"enabled":true,"audio":0}}"""
            : """{"preferredAudio":0,"fallbackAudio":0}""";
        using var request = harness.CreateRequest(HttpMethod.Post, "/media/start/2.1", json);

        // Act
        using var response = await harness.Client.SendAsync(request, TestContext.Current.CancellationToken);
        var payload = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(payload!["sourceAudioFallbackApplied"]!.GetValue<bool>());
        Assert.Equal("aac", payload["fallbackAudioCodec"]!.GetValue<string>());
        Assert.Equal(["copy", "aac"], harness.MediaStarts.Select(start => start[2]));
        var stream = Assert.Single(harness.ReportedStreams);
        Assert.Equal(nameof(VirtualOutputStreamStartupStage.Ready), stream.WorkerStartupStage);
        Assert.Contains(stream.VirtualOutput!.Tracks, track => track.Type == MediaTrackType.Audio && track.Codec == "aac");
        Assert.DoesNotContain(stream.VirtualOutput.Tracks, track => track.Type == MediaTrackType.Audio && track.Codec == "ac3");
        using var stop = await harness.Client.PostAsync(new Uri(payload["stopUrl"]!.GetValue<string>()), null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, stop.StatusCode);
        await harness.AssertMediaReleasedAsync();
    }

    /// <summary>Verifies unrelated failures never retry and a failed fallback cannot trigger a third attempt.</summary>
    [Theory]
    [InlineData("unrelated", 1, 0)]
    [InlineData("both-fail", 2, 0)]
    [InlineData("both-fail", 1, 1)]
    public async Task Worker_StartupFailure_BoundsRetriesAndReleasesMedia(string mode, int expectedStarts, int preferredAudio)
    {
        // Arrange
        await using var harness = await WebPlayerWorkerHarness.StartAsync(
            (request, _) => Task.FromResult(CreateBrokeredStream(request, new PacedPatternStream())),
            TestContext.Current.CancellationToken,
            startupMode: mode);
        using var request = harness.CreateRequest(HttpMethod.Post, "/media/start/2.1", $$"""{"preferredAudio":{{preferredAudio}}}""");

        // Act
        using var response = await harness.Client.SendAsync(request, TestContext.Current.CancellationToken);
        var payload = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("media_process_failed", payload!["code"]!.GetValue<string>());
        Assert.Equal(expectedStarts, harness.MediaStarts.Count);
        await harness.AssertMediaReleasedAsync();
    }

    /// <summary>Verifies cancellation during fallback readiness releases both attempts and the broker source.</summary>
    [Fact]
    public async Task Worker_DisconnectDuringFallback_ReleasesMedia()
    {
        // Arrange
        await using var harness = await WebPlayerWorkerHarness.StartAsync(
            (request, _) => Task.FromResult(CreateBrokeredStream(request, new PacedPatternStream())),
            TestContext.Current.CancellationToken,
            startupMode: "fallback-holds");
        using var request = harness.CreateRequest(HttpMethod.Post, "/media/start/2.1", """{"preferredAudio":0}""");
        using var disconnect = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var pending = harness.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, disconnect.Token);
        await WaitUntilAsync(() => harness.MediaStarts.Count == 2, TimeSpan.FromSeconds(10));

        // Act
        disconnect.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);

        // Assert
        Assert.Equal(["copy", "aac"], harness.MediaStarts.Select(start => start[2]));
        await harness.AssertMediaReleasedAsync();
    }

    /// <summary>Verifies fallback readiness expires with a structured timeout and releases both media attempts.</summary>
    [Fact]
    public async Task Worker_FallbackReadinessTimeout_ReleasesMedia()
    {
        // Arrange
        await using var harness = await WebPlayerWorkerHarness.StartAsync(
            (request, _) => Task.FromResult(CreateBrokeredStream(request, new PacedPatternStream())),
            TestContext.Current.CancellationToken,
            startupMode: "fallback-holds");
        using var request = harness.CreateRequest(HttpMethod.Post, "/media/start/2.1", """{"preferredAudio":0}""");

        // Act
        using var response = await harness.Client.SendAsync(request, TestContext.Current.CancellationToken);
        var payload = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Equal("media_startup_timeout", payload!["code"]!.GetValue<string>());
        Assert.Equal("readiness", payload["stage"]!.GetValue<string>());
        Assert.Equal(["copy", "aac"], harness.MediaStarts.Select(start => start[2]));
        await harness.AssertMediaReleasedAsync();
    }

    /// <summary>Verifies the real worker starts CMAF media, reports final presentation status, exposes captions, and stops cleanly.</summary>
    [Theory]
    [InlineData(0, "ac3")]
    [InlineData(1, "aac")]
    public async Task Worker_StartAndStop_ReportsFinalPresentationAndCaptions(int preferredAudio, string audioCodec)
    {
        // Arrange
        await using var harness = await WebPlayerWorkerHarness.StartAsync(
            (request, _) => Task.FromResult(CreateBrokeredStream(request, new PacedPatternStream())),
            TestContext.Current.CancellationToken);
        using var request = harness.CreateRequest(
            HttpMethod.Post,
            "/media/start/2.1",
            $$"""{"preferredVideo":1,"preferredAudio":{{preferredAudio}},"fallbackAudio":0,"subtitleTrack":2}""");

        // Act
        using var response = await harness.Client.SendAsync(request, TestContext.Current.CancellationToken);
        var payload = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: TestContext.Current.CancellationToken);
        var manifest = await harness.Client.GetStringAsync(new Uri(payload!["dashManifestUrl"]!.GetValue<string>()), TestContext.Current.CancellationToken);
        var stream = Assert.Single(harness.ReportedStreams);
        var stopResponse = await harness.Client.PostAsync(new Uri(payload["stopUrl"]!.GetValue<string>()), null, TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => harness.ActiveStreams.GetActiveStreams().Count == 0, TimeSpan.FromSeconds(10));

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(payload["sourceAudioFallbackApplied"]!.GetValue<bool>());
        Assert.Equal("*", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Single(payload["subtitles"]!.AsArray());
        Assert.Contains("avc1", manifest, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mp4a", manifest, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HostedStreamFormat.Cmaf, stream.Format);
        Assert.Equal(nameof(VirtualOutputStreamStartupStage.Ready), stream.WorkerStartupStage);
        Assert.Equal(["h264", audioCodec, "webvtt"], stream.VirtualOutput!.Tracks.Select(track => track.Codec));
        Assert.Single(harness.MediaStarts);
        Assert.Equal(HttpStatusCode.NoContent, stopResponse.StatusCode);
        Assert.Empty(harness.ActiveStreams.GetActiveStreams());
    }

    /// <summary>Verifies broker rejections remain browser-readable structured failures with CORS.</summary>
    [Fact]
    public async Task Worker_BrokerRejection_ReturnsStructuredCorsFailure()
    {
        // Arrange
        await using var harness = await WebPlayerWorkerHarness.StartAsync(
            (_, _) => throw new VirtualOutputBrokerException(VirtualOutputFailureCode.SourceNotFound, "The requested channel is unavailable."),
            TestContext.Current.CancellationToken);
        using var request = harness.CreateRequest(HttpMethod.Post, "/media/start/missing", "{}");

        // Act
        using var response = await harness.Client.SendAsync(request, TestContext.Current.CancellationToken);
        var payload = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("*", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Equal("source_not_found", payload!["code"]!.GetValue<string>());
        Assert.Equal("broker", payload["stage"]!.GetValue<string>());
        Assert.Empty(harness.ActiveStreams.GetActiveStreams());
    }

    /// <summary>Verifies real FFmpeg readiness failures return safe structured diagnostics and release the broker stream.</summary>
    [Fact]
    public async Task Worker_MediaProcessorFailure_ReturnsSafeStartupFailure()
    {
        // Arrange
        await using var harness = await WebPlayerWorkerHarness.StartAsync(
            (request, _) => Task.FromResult(CreateBrokeredStream(request, new PacedPatternStream())),
            TestContext.Current.CancellationToken,
            mediaProcessorFails: true);
        using var request = harness.CreateRequest(HttpMethod.Post, "/media/start/2.1", "{}");

        // Act
        using var response = await harness.Client.SendAsync(request, TestContext.Current.CancellationToken);
        var payload = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => harness.ActiveStreams.GetActiveStreams().Count == 0, TimeSpan.FromSeconds(10));

        // Assert
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("*", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Equal("media_process_failed", payload!["code"]!.GetValue<string>());
        Assert.Equal("readiness", payload["stage"]!.GetValue<string>());
        Assert.DoesNotContain("Synthetic", payload["error"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(harness.ActiveStreams.GetActiveStreams());
    }

    /// <summary>Verifies disconnecting the initiating HTTP request during startup releases the correlated broker stream.</summary>
    [Fact]
    public async Task Worker_HttpDisconnectDuringStartup_ReleasesBrokerStream()
    {
        // Arrange
        await using var harness = await WebPlayerWorkerHarness.StartAsync(
            (request, _) => Task.FromResult(CreateBrokeredStream(request, new BlockingReadStream())),
            TestContext.Current.CancellationToken);
        using var request = harness.CreateRequest(HttpMethod.Post, "/media/start/2.1", "{}");
        using var disconnect = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var responseTask = harness.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, disconnect.Token);
        await WaitUntilAsync(() => harness.ActiveStreams.GetActiveStreams().Count == 1, TimeSpan.FromSeconds(10));

        // Act
        disconnect.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await responseTask);
        await WaitUntilAsync(() => harness.ActiveStreams.GetActiveStreams().Count == 0, TimeSpan.FromSeconds(10));

        // Assert
        Assert.Empty(harness.ActiveStreams.GetActiveStreams());
    }

    /// <summary>Verifies a crashed worker loses its lease and Core releases the orphaned broker stream.</summary>
    [Fact]
    public async Task Worker_ProcessCrash_ReleasesBrokerStreamAfterLeaseExpiry()
    {
        // Arrange
        await using var harness = await WebPlayerWorkerHarness.StartAsync(
            (request, _) => Task.FromResult(CreateBrokeredStream(request, new PacedPatternStream())),
            TestContext.Current.CancellationToken,
            TimeSpan.FromSeconds(3));
        using var request = harness.CreateRequest(HttpMethod.Post, "/media/start/2.1", "{}");
        using var response = await harness.Client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await WaitUntilAsync(() => harness.ActiveStreams.GetActiveStreams().Count == 1, TimeSpan.FromSeconds(10));

        // Act
        await harness.KillWorkerAsync();
        await WaitUntilAsync(() => harness.ActiveStreams.GetActiveStreams().Count == 0, TimeSpan.FromSeconds(15));

        // Assert
        Assert.Empty(harness.ActiveStreams.GetActiveStreams());
    }

    private static VirtualOutputBrokeredStream CreateBrokeredStream(VirtualOutputOpenStreamRequest request, Stream stream)
    {
        var activeStreams = WebPlayerWorkerHarness.CurrentActiveStreams
            ?? throw new InvalidOperationException("The test broker is not attached to an active worker harness.");
        var streamSessionId = StreamSessionId.New();
        var source = CreateSourceMetadata();
        activeStreams.Register(new ActiveStreamSnapshot(
            streamSessionId.ToString(),
            request.ChannelKey,
            HostedStreamFormat.MpegTs,
            DateTime.UtcNow,
            source.BitRate,
            []));
        return new VirtualOutputBrokeredStream(
            activeStreams,
            new VirtualMpegTsOutput(
                stream,
                source,
                source,
                new VirtualTranscodeProfile(),
                new ActiveStreamFlowContext()),
            new VirtualOutputOpenStreamResponse
            {
                Accepted = true,
                ContentType = "video/mp2t",
                StreamSessionId = streamSessionId,
                RawSource = source,
                Source = source,
                Profile = new VirtualTranscodeProfile()
            });
    }

    private static MediaProbeResult CreateSourceMetadata() =>
        new(
            [
                new MediaTrackMetadata(0, MediaTrackType.Video, "mpeg2video", 2_000_000, 640, 360, null, null),
                new MediaTrackMetadata(1, MediaTrackType.Audio, "ac3", 192_000, null, null, 2, 48_000),
                new MediaTrackMetadata(2, MediaTrackType.Subtitle, "eia_608", null, null, null, null, null)
                {
                    Title = "Closed Captions",
                    IsEmbeddedClosedCaptions = true,
                    SubtitlePresentation = SubtitlePresentation.WebVtt
                }
            ],
            2_192_000);

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var started = Stopwatch.StartNew();
        while (!condition())
        {
            if (started.Elapsed >= timeout)
            {
                throw new TimeoutException($"The integration condition did not complete within {timeout}.");
            }
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }

    private sealed class WebPlayerWorkerHarness : IAsyncDisposable
    {
        private static readonly AsyncLocal<IActiveStreamRegistry?> ActiveStreamsContext = new();
        private readonly VirtualOutputIpcServer _server;
        private readonly VirtualOutputSessionAuthority _authority;
        private readonly Process _worker;
        private readonly Task<string> _workerOutput;
        private readonly Task<string> _workerError;
        private readonly string _startLog;

        private WebPlayerWorkerHarness(
            VirtualOutputIpcServer server,
            VirtualOutputSessionAuthority authority,
            Process worker,
            HttpClient client,
            IActiveStreamRegistry activeStreams,
            string startLog)
        {
            _server = server;
            _authority = authority;
            _worker = worker;
            Client = client;
            ActiveStreams = activeStreams;
            _startLog = startLog;
            _workerOutput = worker.StandardOutput.ReadToEndAsync();
            _workerError = worker.StandardError.ReadToEndAsync();
        }

        internal static IActiveStreamRegistry? CurrentActiveStreams => ActiveStreamsContext.Value;

        internal HttpClient Client { get; }

        internal IActiveStreamRegistry ActiveStreams { get; }

        internal IReadOnlyList<ActiveStreamSnapshot> ReportedStreams => ActiveStreams.GetActiveStreams();

        /// <summary>Gets each main media attempt's process ID, output directory and first audio codec.</summary>
        internal IReadOnlyList<string[]> MediaStarts => File.Exists(_startLog)
            ? File.ReadAllLines(_startLog).Select(line => line.Split('|')).ToArray()
            : [];

        /// <summary>Verifies that startup or explicit stop released every media attempt and its broker stream.</summary>
        internal async Task AssertMediaReleasedAsync()
        {
            await WaitUntilAsync(() => ActiveStreams.GetActiveStreams().Count == 0 &&
                MediaStarts.All(start => !Directory.Exists(start[1]) && HasExited(int.Parse(start[0], System.Globalization.CultureInfo.InvariantCulture))), TimeSpan.FromSeconds(10));
            Assert.Empty(ActiveStreams.GetActiveStreams());
        }

        private static bool HasExited(int processId)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                return process.HasExited;
            }
            catch (ArgumentException)
            {
                return true;
            }
        }

        internal static async Task<WebPlayerWorkerHarness> StartAsync(
            Func<VirtualOutputOpenStreamRequest, CancellationToken, Task<VirtualOutputBrokeredStream>> openStream,
            CancellationToken cancellationToken,
            TimeSpan? leaseLifetime = null,
            bool mediaProcessorFails = false,
            string? startupMode = null,
            string? workerPath = null)
        {
            var endpoint = CreateEndpoint();
            var authority = new VirtualOutputSessionAuthority();
            var deviceId = VirtualDeviceId.New();
            var activeStreams = new ActiveStreamRegistry();
            ActiveStreamsContext.Value = activeStreams;
            var broker = new TestBroker(openStream);
            var port = AllocatePort();
            var device = CreateDevice(deviceId, port);
            var policy = new VirtualOutputWorkerPolicyDefinition
            {
                Key = "integration",
                Name = "Integration",
                MaximumOutstandingBrokerCalls = 8,
                LeaseGracePeriodSeconds = 3
            };
            var ticket = authority.CreateActivation(
                deviceId,
                "web-player",
                new HashSet<VirtualOutputCapability>
                {
                    VirtualOutputCapability.OpenStream,
                    VirtualOutputCapability.ReportHealth
                },
                TimeSpan.FromMinutes(1),
                leaseLifetime ?? TimeSpan.FromSeconds(30));
            var settings = Substitute.For<ILineupSettingsService>();
            settings.Settings.Returns(new LineupSettings
            {
                Streaming = new StreamingConfiguration
                {
                    VirtualDevices = [device],
                    VirtualOutputWorkerPolicies = [policy]
                }
            });
            var server = new VirtualOutputIpcServer(
                endpoint,
                authority,
                broker,
                new VirtualOutputRuntimeStatus(),
                settings,
                activeStreams,
                NullLogger<VirtualOutputIpcServer>.Instance);
            await server.StartAsync(cancellationToken);
            var bootstrap = new VirtualOutputWorkerBootstrap
            {
                Endpoint = endpoint,
                VirtualDevice = device,
                SecurityPolicies =
                [
                    new VirtualOutputSecurityPolicyDefinition
                    {
                        Key = "integration",
                        Name = "Integration",
                        AllowedNetworks = ["127.0.0.0/8", "::1/128"]
                    }
                ],
                WorkerPolicy = policy,
                WorkerId = ticket.WorkerId,
                Generation = ticket.Generation,
                BootstrapToken = ticket.BootstrapToken,
                BootstrapExpiresUtc = ticket.ExpiresUtc
            };
            var startLog = Path.Combine(Path.GetTempPath(), $"lineup-media-starts-{Guid.NewGuid():N}.txt");
            var worker = StartWorker(bootstrap, mediaProcessorFails, startupMode, startLog, workerPath);
            var client = new HttpClient
            {
                BaseAddress = new Uri($"http://127.0.0.1:{port}"),
                Timeout = TimeSpan.FromSeconds(35)
            };
            var harness = new WebPlayerWorkerHarness(server, authority, worker, client, activeStreams, startLog);
            try
            {
                await harness.WaitForReadyAsync(cancellationToken);
                return harness;
            }
            catch
            {
                await harness.DisposeAsync();
                throw;
            }
        }

        internal HttpRequestMessage CreateRequest(HttpMethod method, string path, string? json = null)
        {
            var request = new HttpRequestMessage(method, path);
            request.Headers.Add("Origin", "http://lineup.test");
            if (json is not null)
            {
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }
            return request;
        }

        internal async Task KillWorkerAsync()
        {
            if (!_worker.HasExited)
            {
                _worker.Kill(entireProcessTree: true);
            }
            await _worker.WaitForExitAsync();
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await KillWorkerAsync();
            await _server.StopAsync(CancellationToken.None);
            _server.Dispose();
            _authority.Dispose();
            _worker.Dispose();
            File.Delete(_startLog);
            ActiveStreamsContext.Value = null;
        }

        private async Task WaitForReadyAsync(CancellationToken cancellationToken)
        {
            var started = Stopwatch.StartNew();
            while (started.Elapsed < TimeSpan.FromSeconds(15))
            {
                if (_worker.HasExited)
                {
                    throw new InvalidOperationException(
                        $"The Web Player worker exited during startup. stdout: {await _workerOutput} stderr: {await _workerError}");
                }
                try
                {
                    using var response = await Client.GetAsync("/", cancellationToken);
                    return;
                }
                catch (HttpRequestException)
                {
                    await Task.Delay(100, cancellationToken);
                }
            }
            throw new TimeoutException("The Web Player worker did not open its HTTP listener.");
        }

        private static Process StartWorker(VirtualOutputWorkerBootstrap bootstrap, bool mediaProcessorFails, string? startupMode, string startLog, string? workerPath)
        {
            workerPath ??= Path.Combine(AppContext.BaseDirectory, "Lineup.WebPlayer.VirtualOutputHost.dll");
            if (!File.Exists(workerPath))
            {
                throw new FileNotFoundException("The dedicated Web Player worker was not copied to the integration-test output.", workerPath);
            }
            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add(workerPath);
            startInfo.Environment["LINEUP_FFMPEG_EXECUTABLE"] = ResolveFakeMediaProcessorPath();
            startInfo.Environment["LINEUP_FAKE_FFMPEG_START_LOG"] = startLog;
            startInfo.Environment["LINEUP_FAKE_FFMPEG_STARTUP_MODE"] = startupMode;
            if (mediaProcessorFails)
            {
                startInfo.Environment["LINEUP_FAKE_FFMPEG_FAIL"] = "1";
            }
            var worker = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start the dedicated Web Player worker.");
            worker.StandardInput.WriteLine(JsonSerializer.Serialize(bootstrap, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            worker.StandardInput.Close();
            return worker;
        }

        private static string ResolveFakeMediaProcessorPath()
        {
            var path = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "Lineup.FakeFFmpeg",
                "release",
                "Lineup.FakeFFmpeg.dll"));
            return File.Exists(path)
                ? path
                : throw new FileNotFoundException("The deterministic media-processor test double was not built.", path);
        }

        private static VirtualDeviceDefinition CreateDevice(VirtualDeviceId deviceId, int port) =>
            new()
            {
                Id = deviceId,
                Name = "Integration Web Player",
                Adapter = new AdapterConfiguration("web-player", JsonSerializer.SerializeToElement(new { })),
                Hosting = new VirtualOutputHostingDefinition
                {
                    ExecutionMode = VirtualOutputExecutionMode.ManagedProcess,
                    WorkerPolicyKey = "integration",
                    Bindings =
                    [
                        new VirtualOutputBindingDefinition
                        {
                            Transport = VirtualOutputTransport.Http,
                            Addresses = ["127.0.0.1"],
                            Port = port,
                            PublicBaseUri = $"http://127.0.0.1:{port}/",
                            SecurityPolicyKey = "integration"
                        }
                    ]
                }
            };

        private static VirtualOutputIpcEndpoint CreateEndpoint() =>
            OperatingSystem.IsWindows()
                ? new VirtualOutputIpcEndpoint
                {
                    Transport = VirtualOutputIpcTransport.NamedPipe,
                    Address = $"lineup-web-player-integration-{Guid.NewGuid():N}"
                }
                : new VirtualOutputIpcEndpoint
                {
                    Transport = VirtualOutputIpcTransport.UnixDomainSocket,
                    Address = Path.Combine(Path.GetTempPath(), $"lineup-web-player-integration-{Guid.NewGuid():N}.sock")
                };

        private static int AllocatePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
    }

    private sealed class TestBroker(
        Func<VirtualOutputOpenStreamRequest, CancellationToken, Task<VirtualOutputBrokeredStream>> openStream) : IVirtualOutputBroker
    {
        public Task<VirtualOutputManifest> GetManifestAsync(VirtualOutputManifestRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<VirtualOutputLineupResponse> GetLineupAsync(VirtualOutputLineupRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<VirtualOutputGuideResponse> GetGuideAsync(VirtualOutputGuideRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<VirtualOutputBrokeredStream> OpenStreamAsync(VirtualOutputOpenStreamRequest request, CancellationToken cancellationToken = default) =>
            openStream(request, cancellationToken);
    }

    private sealed class PacedPatternStream : Stream
    {
        private readonly CancellationTokenSource _disposed = new();

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposed.Token);
            try
            {
                await Task.Delay(10, linkedCancellation.Token);
            }
            catch (OperationCanceledException) when (_disposed.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                return 0;
            }
            var length = Math.Min(buffer.Length, 188 * 7);
            buffer.Span[..length].Clear();
            for (var offset = 0; offset < length; offset += 188)
            {
                buffer.Span[offset] = 0x47;
            }
            return length;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask DisposeAsync()
        {
            _disposed.Cancel();
            _disposed.Dispose();
            GC.SuppressFinalize(this);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingReadStream : Stream
    {
        private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await _disposed.Task.WaitAsync(cancellationToken);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask DisposeAsync()
        {
            _disposed.TrySetResult();
            GC.SuppressFinalize(this);
            return ValueTask.CompletedTask;
        }
    }
}
