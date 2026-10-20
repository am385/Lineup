using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
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
    /// <summary>Verifies the real worker starts CMAF media, reports final presentation status, exposes captions, and stops cleanly.</summary>
    [Fact]
    public async Task Worker_StartAndStop_ReportsFinalPresentationAndCaptions()
    {
        // Arrange
        await using var harness = await WebPlayerWorkerHarness.StartAsync(
            (request, _) => Task.FromResult(CreateBrokeredStream(request, new PacedPatternStream())),
            TestContext.Current.CancellationToken);
        using var request = harness.CreateRequest(
            HttpMethod.Post,
            "/media/start/2.1",
            """{"preferredVideo":1,"preferredAudio":1,"fallbackAudio":0,"subtitleTrack":2}""");

        // Act
        using var response = await harness.Client.SendAsync(request, TestContext.Current.CancellationToken);
        var payload = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: TestContext.Current.CancellationToken);
        var manifest = await harness.Client.GetStringAsync(new Uri(payload!["dashManifestUrl"]!.GetValue<string>()), TestContext.Current.CancellationToken);
        var stream = Assert.Single(harness.ReportedStreams);
        var stopResponse = await harness.Client.PostAsync(new Uri(payload["stopUrl"]!.GetValue<string>()), null, TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => harness.ActiveStreams.GetActiveStreams().Count == 0, TimeSpan.FromSeconds(10));

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("*", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Single(payload["subtitles"]!.AsArray());
        Assert.Contains("avc1", manifest, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mp4a", manifest, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HostedStreamFormat.Cmaf, stream.Format);
        Assert.Equal(nameof(VirtualOutputStreamStartupStage.Ready), stream.WorkerStartupStage);
        Assert.Equal(["h264", "aac", "webvtt"], stream.VirtualOutput!.Tracks.Select(track => track.Codec));
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
        Assert.Equal("media_startup_failed", payload!["code"]!.GetValue<string>());
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

        private WebPlayerWorkerHarness(
            VirtualOutputIpcServer server,
            VirtualOutputSessionAuthority authority,
            Process worker,
            HttpClient client,
            IActiveStreamRegistry activeStreams)
        {
            _server = server;
            _authority = authority;
            _worker = worker;
            Client = client;
            ActiveStreams = activeStreams;
            _workerOutput = worker.StandardOutput.ReadToEndAsync();
            _workerError = worker.StandardError.ReadToEndAsync();
        }

        internal static IActiveStreamRegistry? CurrentActiveStreams => ActiveStreamsContext.Value;

        internal HttpClient Client { get; }

        internal IActiveStreamRegistry ActiveStreams { get; }

        internal IReadOnlyList<ActiveStreamSnapshot> ReportedStreams => ActiveStreams.GetActiveStreams();

        internal static async Task<WebPlayerWorkerHarness> StartAsync(
            Func<VirtualOutputOpenStreamRequest, CancellationToken, Task<VirtualOutputBrokeredStream>> openStream,
            CancellationToken cancellationToken,
            TimeSpan? leaseLifetime = null,
            bool mediaProcessorFails = false)
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
            var worker = StartWorker(bootstrap, mediaProcessorFails);
            var client = new HttpClient
            {
                BaseAddress = new Uri($"http://127.0.0.1:{port}"),
                Timeout = TimeSpan.FromSeconds(35)
            };
            var harness = new WebPlayerWorkerHarness(server, authority, worker, client, activeStreams);
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

        private static Process StartWorker(VirtualOutputWorkerBootstrap bootstrap, bool mediaProcessorFails)
        {
            var workerPath = Path.Combine(AppContext.BaseDirectory, "Lineup.WebPlayer.VirtualOutputHost.dll");
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
