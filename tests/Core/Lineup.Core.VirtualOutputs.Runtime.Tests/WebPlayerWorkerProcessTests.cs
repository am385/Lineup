using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lineup.Core.Common.Streaming;
using Lineup.Core.Settings;
using Lineup.Core.Streaming;
using Lineup.Core.VirtualOutputs.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Lineup.Core.VirtualOutputs.Runtime.Tests;

/// <summary>Verifies the real dedicated Web Player executable across its HTTP, IPC, FFmpeg, and cleanup boundaries.</summary>
public sealed class WebPlayerWorkerProcessTests
{
    /// <summary>Worker workspace admission fails explicitly before launching media and leaves no reservation or viewer behind.</summary>
    [Fact]
    public async Task Worker_StorageWatermark_RejectsBeforeMediaAllocation()
    {
        // Arrange
        await using var harness = await WebPlayerWorkerHarness.StartAsync(
            (request, _) => Task.FromResult(CreateBrokeredStream(request, new PacedPatternStream(), "storage-generation")),
            TestContext.Current.CancellationToken, resources: new() { MinimumMediaFreeSpaceMib = int.MaxValue });
        using var request = harness.CreateRequest(HttpMethod.Post, "/media/start/2.1", "{}");

        // Act
        using var response = await harness.Client.SendAsync(request, TestContext.Current.CancellationToken);
        var failure = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => harness.MediaAdmission.GetDiagnostics().Producers == 0, TimeSpan.FromSeconds(10));

        // Assert
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("media_storage_limit", failure!["code"]!.GetValue<string>());
        Assert.Empty(harness.MediaStarts);
        Assert.Empty(harness.ReportedStreams);
        Assert.Equal(0, harness.MediaAdmission.GetDiagnostics().Processing);
    }

    /// <summary>Measures the retained full-rate IPC inputs while proving equivalent viewers use one encoder and independent admission slots.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public async Task Worker_SharedProducer_MeasuresIndependentViewerDrainOverhead(int viewers)
    {
        // Arrange
        var sources = new System.Collections.Concurrent.ConcurrentQueue<PacedPatternStream>();
        await using var harness = await WebPlayerWorkerHarness.StartAsync((request, _) =>
        {
            var source = new PacedPatternStream(controlled: true);
            sources.Enqueue(source);
            return Task.FromResult(CreateBrokeredStream(request, source, "measured-generation"));
        }, TestContext.Current.CancellationToken, resources: new() { MaximumProducers = 1, MaximumProcessingOperations = 2 });
        var responses = new List<JsonObject>();
        for (var index = 0; index < viewers; index++)
        {
            using var request = harness.CreateRequest(HttpMethod.Post, "/media/start/2.1", "{}");
            using var response = await harness.Client.SendAsync(request, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            responses.Add((await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: TestContext.Current.CancellationToken))!);
        }

        // Act
        foreach (var source in sources) { source.ReleasePackets(200); }
        await WaitUntilAsync(() => sources.All(source => source.BytesRead == 200 * 188 * 7), TimeSpan.FromSeconds(10));
        var usage = harness.MediaAdmission.GetDiagnostics();
        var admittedViewers = harness.ReportedStreams.Count;
        var ipcBytes = sources.Sum(source => source.BytesRead);
        var cleanup = Stopwatch.StartNew();
        foreach (var response in responses)
        {
            using var stopped = await harness.Client.PostAsync(new Uri(response["stopUrl"]!.GetValue<string>()), null, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, stopped.StatusCode);
        }
        await harness.AssertMediaReleasedAsync();
        await WaitUntilAsync(() => harness.MediaAdmission.GetDiagnostics() is { Producers: 0, Processing: 0 }, TimeSpan.FromSeconds(10));
        cleanup.Stop();

        // Assert
        Assert.Equal(viewers, admittedViewers);
        Assert.Single(harness.MediaStarts);
        Assert.Equal(1, usage.Producers);
        Assert.Equal(2, usage.Processing);
        Assert.Equal(viewers * 263200L, ipcBytes);
        TestContext.Current.TestOutputHelper?.WriteLine($"Viewers={viewers}; producer=1; total IPC bytes={ipcBytes}; extra-viewer drain bytes={(viewers - 1) * 263200L}; cleanup={cleanup.Elapsed.TotalMilliseconds:0.0}ms.");
    }

    /// <summary>Real dedicated media can be reclaimed under host pressure only after its final viewer leaves, without waiting for warm expiry.</summary>
    [Fact]
    public async Task Worker_ProducerPressure_RetiresOnlyZeroViewerWarmOutput()
    {
        // Arrange
        await using var harness = await WebPlayerWorkerHarness.StartAsync(
            (request, _) => Task.FromResult(CreateBrokeredStream(request, new PacedPatternStream(), "retained-generation", keepWarmSeconds: 60)),
            TestContext.Current.CancellationToken, resources: new() { MaximumProducers = 1, MaximumProcessingOperations = 2 });
        using var request = harness.CreateRequest(HttpMethod.Post, "/media/start/2.1", "{}");
        using var response = await harness.Client.SendAsync(request, TestContext.Current.CancellationToken);
        var presentation = (await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: TestContext.Current.CancellationToken))!;

        // Act
        await Assert.ThrowsAsync<MediaResourceLimitException>(() =>
            harness.MediaAdmission.AcquireAsync(MediaWorkKind.Producer, cancellationToken: TestContext.Current.CancellationToken).AsTask());
        using var stop = await harness.Client.PostAsync(new Uri(presentation["stopUrl"]!.GetValue<string>()), null, TestContext.Current.CancellationToken);
        await using var replacement = await harness.MediaAdmission.AcquireAsync(MediaWorkKind.Producer, cancellationToken: TestContext.Current.CancellationToken);
        await harness.AssertMediaReleasedAsync();
        await WaitUntilAsync(() => harness.MediaAdmission.GetDiagnostics().Processing == 0, TimeSpan.FromSeconds(10));

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, stop.StatusCode);
        Assert.Empty(harness.ReportedStreams);
        Assert.Equal(1, harness.MediaAdmission.GetDiagnostics().Producers);
    }

    /// <summary>Verifies process-start observations can read a live append-only writer and never expose its unfinished final record.</summary>
    [Fact]
    public void MediaStarts_ActiveWriter_ReadsOnlyCompleteRecords()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-start-log-");
        try
        {
            var path = Path.Combine(root.FullName, "starts.txt");
            using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            writer.Write(Encoding.UTF8.GetBytes("100|first-output|copy\n101|second-output|a"));
            writer.Flush();

            // Act
            var first = ReadMediaStarts(path);

            // Assert
            Assert.Equal(["100", "first-output", "copy"], Assert.Single(first));

            // Act
            writer.Write(Encoding.UTF8.GetBytes("ac\n"));
            writer.Flush();
            var completed = ReadMediaStarts(path);

            // Assert
            Assert.Equal(2, completed.Count);
            Assert.Equal(["101", "second-output", "aac"], completed[1]);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>Verifies normal host builds install discoverable packages that can activate a dedicated Web Player listener.</summary>
    [Theory]
    [InlineData("Lineup.App.Web", "Debug")]
    [InlineData("Lineup.App.Web", "Release")]
    [InlineData("Lineup.App.Tui", "Debug")]
    [InlineData("Lineup.App.Tui", "Release")]
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
            var dependencyPath = Path.Combine(package.PackageRoot, "Lineup.Plugin.WebPlayer.VirtualOutputHost.deps.json");
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
            Assert.True(File.Exists(Path.Combine(package.PackageRoot, "Lineup.Plugin.WebPlayer.VirtualOutputHost.runtimeconfig.json")));
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
            "msbuild", Path.Combine("src", "Apps", "Lineup.App.Web", "Lineup.App.Web.csproj"), "-target:PrepareVirtualOutputWorkersForBuild",
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

    /// <summary>Verifies a real worker crash releases its broker stream through disconnect handling without waiting for lease expiry.</summary>
    [Fact]
    public async Task Worker_ProcessCrash_ReleasesBrokerStreamOnDisconnect()
    {
        // Arrange
        await using var harness = await WebPlayerWorkerHarness.StartAsync(
            (request, _) => Task.FromResult(CreateBrokeredStream(request, new PacedPatternStream())),
            TestContext.Current.CancellationToken,
            TimeSpan.FromMinutes(5));
        using var request = harness.CreateRequest(HttpMethod.Post, "/media/start/2.1", "{}");
        using var response = await harness.Client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await WaitUntilAsync(() => harness.ActiveStreams.GetActiveStreams().Count == 1, TimeSpan.FromSeconds(10));

        // Act
        await harness.KillWorkerAsync();
        await WaitUntilAsync(() => harness.ActiveStreams.GetActiveStreams().Count == 0, TimeSpan.FromSeconds(30));

        // Assert
        Assert.Empty(harness.ActiveStreams.GetActiveStreams());
    }

    /// <summary>Verifies independently admitted viewer aliases share one producer and stopping its initiating viewer leaves the other playing.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Worker_EquivalentViewers_ShareProducerAndStopIndependently(bool stopFromHost)
    {
        // Arrange
        await using var harness = await WebPlayerWorkerHarness.StartAsync(
            (request, _) => Task.FromResult(CreateBrokeredStream(request, new PacedPatternStream(), "physical-generation-one")),
            TestContext.Current.CancellationToken);
        using var firstRequest = harness.CreateRequest(HttpMethod.Post, "/media/start/2.1", "{}");
        using var secondRequest = harness.CreateRequest(HttpMethod.Post, "/media/start/2.1", "{}");

        // Act
        using var firstResponse = await harness.Client.SendAsync(firstRequest, TestContext.Current.CancellationToken);
        var first = (await firstResponse.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: TestContext.Current.CancellationToken))!;
        var firstStream = Assert.Single(harness.ReportedStreams);
        using var secondResponse = await harness.Client.SendAsync(secondRequest, TestContext.Current.CancellationToken);
        var second = (await secondResponse.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: TestContext.Current.CancellationToken))!;
        var joined = harness.ReportedStreams;
        if (stopFromHost)
        {
            Assert.True(harness.ActiveStreams.RequestStop(firstStream.SessionId));
        }
        else
        {
            using var stopped = await harness.Client.PostAsync(new Uri(first["stopUrl"]!.GetValue<string>()), null, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, stopped.StatusCode);
        }
        await WaitUntilAsync(() => harness.ReportedStreams.Count == 1, TimeSpan.FromSeconds(10));
        using var remainingMedia = await harness.Client.GetAsync(new Uri(second["dashManifestUrl"]!.GetValue<string>()), TestContext.Current.CancellationToken);
        using var removedMedia = await harness.Client.GetAsync(new Uri(first["dashManifestUrl"]!.GetValue<string>()), TestContext.Current.CancellationToken);
        using var finalStop = await harness.Client.PostAsync(new Uri(second["stopUrl"]!.GetValue<string>()), null, TestContext.Current.CancellationToken);
        await harness.AssertMediaReleasedAsync();

        // Assert
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        Assert.Equal(2, joined.Count);
        Assert.Equal(2, joined.Select(stream => stream.WorkerMediaSessionId).Distinct().Count());
        Assert.NotNull(Assert.Single(joined.Select(stream => stream.Flow!.ReusableOutputId).Distinct()));
        Assert.Single(harness.MediaStarts);
        Assert.Equal(HttpStatusCode.OK, remainingMedia.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, removedMedia.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, finalStop.StatusCode);
    }

    /// <summary>A real broker EOF is reported through the worker's passive status route for every surviving alias.</summary>
    [Fact]
    public async Task Worker_SourceEof_ReportsRetryableStatusAndReleasesMedia()
    {
        // Arrange
        var sources = new System.Collections.Concurrent.ConcurrentQueue<PacedPatternStream>();
        await using var harness = await WebPlayerWorkerHarness.StartAsync((request, _) =>
        {
            var source = new PacedPatternStream();
            sources.Enqueue(source);
            return Task.FromResult(CreateBrokeredStream(request, source, "source-eof-generation"));
        }, TestContext.Current.CancellationToken);
        using var request = harness.CreateRequest(HttpMethod.Post, "/media/start/2.1", "{}");
        using var response = await harness.Client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var presentation = (await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: TestContext.Current.CancellationToken))!;
        var id = presentation["sessionId"]!.GetValue<string>();
        using var secondRequest = harness.CreateRequest(HttpMethod.Post, "/media/start/2.1", "{}");
        using var secondResponse = await harness.Client.SendAsync(secondRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        var second = (await secondResponse.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: TestContext.Current.CancellationToken))!;
        var secondId = second["sessionId"]!.GetValue<string>();

        // Act
        sources.First().End();
        await WaitUntilAsync(async () =>
        {
            var status = await harness.Client.GetFromJsonAsync<JsonObject>($"/media/status/{id}", TestContext.Current.CancellationToken);
            return status!["isActive"]!.GetValue<bool>() == false;
        }, TimeSpan.FromSeconds(10));
        var ended = await harness.Client.GetFromJsonAsync<JsonObject>($"/media/status/{id}", TestContext.Current.CancellationToken);
        var secondEnded = await harness.Client.GetFromJsonAsync<JsonObject>($"/media/status/{secondId}", TestContext.Current.CancellationToken);
        await harness.AssertMediaReleasedAsync();

        // Assert
        Assert.True(ended!["retryable"]!.GetValue<bool>());
        Assert.Equal("source_eof", ended["code"]!.GetValue<string>());
        Assert.True(secondEnded!["retryable"]!.GetValue<bool>());
        Assert.Single(harness.MediaStarts);
    }

    /// <summary>Verifies canceling the initiating HTTP request does not cancel a producer another admitted viewer is still starting.</summary>
    [Fact]
    public async Task Worker_InitiatingViewerDisconnects_DoesNotCancelSharedStartup()
    {
        // Arrange
        await using var harness = await WebPlayerWorkerHarness.StartAsync(
            (request, _) => Task.FromResult(CreateBrokeredStream(request, new PacedPatternStream(), "physical-generation-one")),
            TestContext.Current.CancellationToken, startupMode: "hold-readiness");
        using var firstRequest = harness.CreateRequest(HttpMethod.Post, "/media/start/2.1", "{}");
        using var firstCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var firstResponse = harness.Client.SendAsync(firstRequest, firstCancellation.Token);
        await WaitUntilAsync(() => harness.MediaStarts.Count == 1, TimeSpan.FromSeconds(10));
        using var secondRequest = harness.CreateRequest(HttpMethod.Post, "/media/start/2.1", "{}");
        var secondResponse = harness.Client.SendAsync(secondRequest, TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => harness.ReportedStreams.Count == 2, TimeSpan.FromSeconds(10));
        await WaitUntilAsync(async () =>
        {
            var health = await harness.Client.GetFromJsonAsync<JsonObject>("/health", TestContext.Current.CancellationToken);
            return health!["activeSessions"]!.GetValue<int>() == 2;
        }, TimeSpan.FromSeconds(10));

        // Act
        firstCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await firstResponse);
        await WaitUntilAsync(() => harness.ReportedStreams.Count == 1, TimeSpan.FromSeconds(10));
        await harness.ReleaseReadinessAsync();
        using var ready = await secondResponse;
        var second = (await ready.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: TestContext.Current.CancellationToken))!;
        using var stopped = await harness.Client.PostAsync(new Uri(second["stopUrl"]!.GetValue<string>()), null, TestContext.Current.CancellationToken);
        await harness.AssertMediaReleasedAsync();

        // Assert
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Single(harness.MediaStarts);
        Assert.Equal(HttpStatusCode.NoContent, stopped.StatusCode);
    }

    /// <summary>Verifies warm producer retention releases viewer admission, reuses media, and eventually cleans the retained source.</summary>
    [Fact]
    public async Task Worker_WarmProducer_HasNoActiveViewerAndReusesUntilExpiry()
    {
        // Arrange
        await using var harness = await WebPlayerWorkerHarness.StartAsync(
            (request, _) => Task.FromResult(CreateBrokeredStream(request, new PacedPatternStream(), "physical-generation-one", keepWarmSeconds: 3)),
            TestContext.Current.CancellationToken);
        using var firstRequest = harness.CreateRequest(HttpMethod.Post, "/media/start/2.1", "{}");
        using var firstResponse = await harness.Client.SendAsync(firstRequest, TestContext.Current.CancellationToken);
        var first = (await firstResponse.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: TestContext.Current.CancellationToken))!;

        // Act
        using var firstStop = await harness.Client.PostAsync(new Uri(first["stopUrl"]!.GetValue<string>()), null, TestContext.Current.CancellationToken);
        var warm = await harness.Client.GetFromJsonAsync<JsonObject>("/health", TestContext.Current.CancellationToken);
        var activeWhileWarm = harness.ReportedStreams;
        using var secondRequest = harness.CreateRequest(HttpMethod.Post, "/media/start/2.1", "{}");
        using var secondResponse = await harness.Client.SendAsync(secondRequest, TestContext.Current.CancellationToken);
        var second = (await secondResponse.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: TestContext.Current.CancellationToken))!;
        using var secondStop = await harness.Client.PostAsync(new Uri(second["stopUrl"]!.GetValue<string>()), null, TestContext.Current.CancellationToken);
        await harness.AssertMediaReleasedAsync();
        await WaitUntilAsync(async () =>
        {
            var health = await harness.Client.GetFromJsonAsync<JsonObject>("/health", TestContext.Current.CancellationToken);
            return health!["activeProducers"]!.GetValue<int>() == 0;
        }, TimeSpan.FromSeconds(10));

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, firstStop.StatusCode);
        Assert.Empty(activeWhileWarm);
        Assert.Equal(0, warm!["activeSessions"]!.GetValue<int>());
        Assert.Equal(1, warm["activeProducers"]!.GetValue<int>());
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        Assert.Single(harness.MediaStarts);
        Assert.Equal(HttpStatusCode.NoContent, secondStop.StatusCode);
    }

    /// <summary>Verifies source generation and effective encoding-plan differences cannot reuse an unrelated producer.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Worker_DifferentSourcesOrPlans_DoNotShareProducer(bool changeSource)
    {
        // Arrange
        var opens = 0;
        await using var harness = await WebPlayerWorkerHarness.StartAsync(
            (request, _) => Task.FromResult(CreateBrokeredStream(request, new PacedPatternStream(),
                changeSource ? $"physical-generation-{Interlocked.Increment(ref opens)}" : "physical-generation-one")),
            TestContext.Current.CancellationToken);
        using var firstRequest = harness.CreateRequest(HttpMethod.Post, "/media/start/2.1", "{}");
        using var secondRequest = harness.CreateRequest(HttpMethod.Post, "/media/start/2.1", changeSource ? "{}" : """{"preferredAudio":1}""");

        // Act
        using var firstResponse = await harness.Client.SendAsync(firstRequest, TestContext.Current.CancellationToken);
        using var secondResponse = await harness.Client.SendAsync(secondRequest, TestContext.Current.CancellationToken);
        var first = (await firstResponse.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: TestContext.Current.CancellationToken))!;
        var second = (await secondResponse.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: TestContext.Current.CancellationToken))!;
        var producers = harness.ReportedStreams.Select(stream => stream.Flow!.ReusableOutputId).Distinct().ToArray();
        using var firstStop = await harness.Client.PostAsync(new Uri(first["stopUrl"]!.GetValue<string>()), null, TestContext.Current.CancellationToken);
        using var secondStop = await harness.Client.PostAsync(new Uri(second["stopUrl"]!.GetValue<string>()), null, TestContext.Current.CancellationToken);
        await harness.AssertMediaReleasedAsync();

        // Assert
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        Assert.Equal(2, harness.MediaStarts.Count);
        Assert.Equal(2, producers.Length);
    }

    private static VirtualOutputBrokeredStream CreateBrokeredStream(VirtualOutputOpenStreamRequest request, Stream stream, string? producerNodeId = null, int keepWarmSeconds = 0)
    {
        var activeStreams = WebPlayerWorkerHarness.CurrentActiveStreams
            ?? throw new InvalidOperationException("The test broker is not attached to an active worker harness.");
        var streamSessionId = StreamSessionId.New();
        var source = CreateSourceMetadata();
        var flow = new ActiveStreamFlowContext { ProducerNodeId = producerNodeId };
        var brokered = new VirtualOutputBrokeredStream(
            activeStreams,
            new VirtualMpegTsOutput(
                stream,
                source,
                source,
                new VirtualTranscodeProfile(),
                flow),
            new VirtualOutputOpenStreamResponse
            {
                Accepted = true,
                ContentType = "video/mp2t",
                StreamSessionId = streamSessionId,
                RawSource = source,
                Source = source,
                Profile = new VirtualTranscodeProfile(),
                ProducerNodeId = producerNodeId,
                KeepWarmPeriodSeconds = keepWarmSeconds
            }, retainedProducerSource: request.RetainSourceForProducer);
        activeStreams.Register(new ActiveStreamSnapshot(
            streamSessionId.ToString(), request.ChannelKey, HostedStreamFormat.MpegTs, DateTime.UtcNow, source.BitRate, [])
        { Flow = flow }, brokered.RequestViewerStop);
        return brokered;
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

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var started = Stopwatch.StartNew();
        while (!await condition())
        {
            if (started.Elapsed >= timeout)
            {
                throw new TimeoutException($"The integration condition did not complete within {timeout}.");
            }
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }

    private static IReadOnlyList<string[]> ReadMediaStarts(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        // The producer may still be appending its final record.
        return reader.ReadToEnd().Split('\n')[..^1].Select(line => line.TrimEnd('\r').Split('|')).ToArray();
    }

    private sealed class WebPlayerWorkerHarness : IAsyncDisposable
    {
        private static readonly AsyncLocal<IActiveStreamRegistry?> _activeStreamsContext = new();
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
            string startLog,
            MediaWorkAdmission mediaAdmission
        )
        {
            _server = server;
            _authority = authority;
            _worker = worker;
            Client = client;
            ActiveStreams = activeStreams;
            _startLog = startLog;
            MediaAdmission = mediaAdmission;
            _workerOutput = worker.StandardOutput.ReadToEndAsync();
            _workerError = worker.StandardError.ReadToEndAsync();
        }

        /// <summary>Gets the current active streams.</summary>
        internal static IActiveStreamRegistry? CurrentActiveStreams => _activeStreamsContext.Value;

        /// <summary>Gets the client.</summary>
        internal HttpClient Client { get; }

        /// <summary>Gets the active streams.</summary>
        internal IActiveStreamRegistry ActiveStreams { get; }

        /// <summary>Gets authoritative resource accounting shared with the real dedicated process.</summary>
        internal MediaWorkAdmission MediaAdmission { get; }

        /// <summary>Gets the reported streams.</summary>
        internal IReadOnlyList<ActiveStreamSnapshot> ReportedStreams => ActiveStreams.GetActiveStreams();

        /// <summary>Gets each main media attempt's process ID, output directory and first audio codec.</summary>
        internal IReadOnlyList<string[]> MediaStarts => ReadMediaStarts(_startLog);

        /// <summary>Releases deterministic readiness after concurrent viewers have joined a pending producer.</summary>
        internal Task ReleaseReadinessAsync() => File.WriteAllTextAsync(_startLog + ".ready", string.Empty, TestContext.Current.CancellationToken);

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

        /// <summary>Starts the web player worker harness asynchronously.</summary>
        /// <param name="openStream">The open stream value.</param>
        /// <param name="cancellationToken">Token used to cancel the operation.</param>
        /// <param name="leaseLifetime">The lease lifetime value.</param>
        /// <param name="mediaProcessorFails">The media processor fails value.</param>
        /// <param name="startupMode">The startup mode value.</param>
        /// <param name="workerPath">The worker path value.</param>
        /// <param name="resources">The resources value.</param>
        /// <returns>The operation's result.</returns>
        internal static async Task<WebPlayerWorkerHarness> StartAsync(
            Func<VirtualOutputOpenStreamRequest, CancellationToken, Task<VirtualOutputBrokeredStream>> openStream,
            CancellationToken cancellationToken,
            TimeSpan? leaseLifetime = null,
            bool mediaProcessorFails = false,
            string? startupMode = null,
            string? workerPath = null,
            StreamingResourceOptions? resources = null
        )
        {
            var endpoint = CreateEndpoint();
            var authority = new VirtualOutputSessionAuthority();
            var deviceId = VirtualDeviceId.New();
            var activeStreams = new ActiveStreamRegistry();
            _activeStreamsContext.Value = activeStreams;
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
                    VirtualOutputCapability.AdmitConnection,
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
            var mediaAdmission = new MediaWorkAdmission(resources ?? new());
            var server = new VirtualOutputIpcServer(
                endpoint,
                authority,
                broker,
                new VirtualOutputRuntimeStatus(),
                settings,
                activeStreams,
                NullLogger<VirtualOutputIpcServer>.Instance,
                new PluginConnectionBudget(settings),
                mediaAdmission, resources);
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
            var worker = await StartWorkerAsync(bootstrap, mediaProcessorFails, startupMode, startLog, workerPath, cancellationToken);
            var client = new HttpClient
            {
                BaseAddress = new Uri($"http://127.0.0.1:{port}"),
                Timeout = TimeSpan.FromSeconds(35)
            };
            var harness = new WebPlayerWorkerHarness(server, authority, worker, client, activeStreams, startLog, mediaAdmission);
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

        /// <summary>Creates request.</summary>
        /// <param name="method">The method value.</param>
        /// <param name="path">Path used by the operation.</param>
        /// <param name="json">The JSON value.</param>
        /// <returns>The operation's result.</returns>
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

        /// <summary>Executes kill worker asynchronously.</summary>
        /// <returns>A task that completes when the operation has finished.</returns>
        internal async Task KillWorkerAsync()
        {
            if (!_worker.HasExited)
            {
                _worker.Kill(entireProcessTree: true);
            }
            await _worker.WaitForExitAsync();
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await KillWorkerAsync();
            TestContext.Current.TestOutputHelper?.WriteLine($"Worker stdout:{Environment.NewLine}{await _workerOutput}");
            TestContext.Current.TestOutputHelper?.WriteLine($"Worker stderr:{Environment.NewLine}{await _workerError}");
            await _server.StopAsync(CancellationToken.None);
            _server.Dispose();
            _authority.Dispose();
            _worker.Dispose();
            File.Delete(_startLog);
            File.Delete(_startLog + ".ready");
            _activeStreamsContext.Value = null;
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

        private static async Task<Process> StartWorkerAsync(VirtualOutputWorkerBootstrap bootstrap, bool mediaProcessorFails, string? startupMode, string startLog, string? workerPath, CancellationToken cancellationToken)
        {
            var packageRoot = Environment.GetEnvironmentVariable("LINEUP_TEST_WORKER_PACKAGE_ROOT");
            if (workerPath is null && !string.IsNullOrWhiteSpace(packageRoot))
            {
                var package = await new VirtualOutputWorkerCatalog(packageRoot).ResolveAsync("web-player", cancellationToken);
                workerPath = Path.Combine(package.PackageRoot, package.Manifest.ExecutablePath);
            }
            workerPath ??= Path.Combine(AppContext.BaseDirectory, "Lineup.Plugin.WebPlayer.VirtualOutputHost.dll");
            if (!File.Exists(workerPath))
            {
                throw new FileNotFoundException("The dedicated Web Player worker was not copied to the integration-test output.", workerPath);
            }
            var managedAssembly = string.Equals(Path.GetExtension(workerPath), ".dll", StringComparison.OrdinalIgnoreCase);
            var startInfo = new ProcessStartInfo
            {
                FileName = managedAssembly ? "dotnet" : workerPath,
                WorkingDirectory = Path.GetDirectoryName(workerPath)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            if (managedAssembly)
            {
                startInfo.ArgumentList.Add(workerPath);
            }
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
                "Lineup.TestSupport.FakeFFmpeg",
                "release",
                "Lineup.TestSupport.FakeFFmpeg.dll"));
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

    private sealed class TestBroker(Func<VirtualOutputOpenStreamRequest, CancellationToken, Task<VirtualOutputBrokeredStream>> openStream) : IVirtualOutputBroker
    {
        /// <summary>Gets manifest asynchronously.</summary>
        /// <param name="request">The request value.</param>
        /// <param name="cancellationToken">Token used to cancel the operation.</param>
        /// <returns>The operation's result.</returns>
        public Task<VirtualOutputManifest> GetManifestAsync(VirtualOutputManifestRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        /// <summary>Gets lineup asynchronously.</summary>
        /// <param name="request">The request value.</param>
        /// <param name="cancellationToken">Token used to cancel the operation.</param>
        /// <returns>The operation's result.</returns>
        public Task<VirtualOutputLineupResponse> GetLineupAsync(VirtualOutputLineupRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        /// <summary>Gets guide asynchronously.</summary>
        /// <param name="request">The request value.</param>
        /// <param name="cancellationToken">Token used to cancel the operation.</param>
        /// <returns>The operation's result.</returns>
        public Task<VirtualOutputGuideResponse> GetGuideAsync(VirtualOutputGuideRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        /// <summary>Opens stream asynchronously.</summary>
        /// <param name="request">The request value.</param>
        /// <param name="cancellationToken">Token used to cancel the operation.</param>
        /// <returns>The operation's result.</returns>
        public Task<VirtualOutputBrokeredStream> OpenStreamAsync(VirtualOutputOpenStreamRequest request, CancellationToken cancellationToken = default) =>
            openStream(request, cancellationToken);
    }

    private sealed class PacedPatternStream : Stream
    {
        private readonly CancellationTokenSource _disposed = new();
        private readonly SemaphoreSlim? _packets;
        private long _bytesRead;

        /// <summary>Creates a paced source, or a deterministic packet-gated source for IPC measurement.</summary>
        internal PacedPatternStream(bool controlled = false) => _packets = controlled ? new(0) : null;

        /// <summary>Gets actual bytes consumed by the broker's IPC pump.</summary>
        internal long BytesRead => Interlocked.Read(ref _bytesRead);

        /// <summary>Allows exactly this many transport packets to cross the real IPC channel.</summary>
        internal void ReleasePackets(int count) => _packets!.Release(count);

        /// <summary>Ends upstream input independently of viewer stop or worker disposal.</summary>
        internal void End() => _disposed.Cancel();

        /// <inheritdoc />
        public override bool CanRead => true;

        /// <inheritdoc />
        public override bool CanSeek => false;

        /// <inheritdoc />
        public override bool CanWrite => false;

        /// <inheritdoc />
        public override long Length => throw new NotSupportedException();

        /// <inheritdoc />
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        /// <inheritdoc />
        public override void Flush()
        {
        }

        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        /// <inheritdoc />
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposed.Token);
            try
            {
                if (_packets is not null) { await _packets.WaitAsync(linkedCancellation.Token); }
                else { await Task.Delay(10, linkedCancellation.Token); }
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
            Interlocked.Add(ref _bytesRead, length);
            return length;
        }

        /// <inheritdoc />
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        /// <inheritdoc />
        public override void SetLength(long value) => throw new NotSupportedException();

        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        /// <inheritdoc />
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

        /// <inheritdoc />
        public override bool CanRead => true;

        /// <inheritdoc />
        public override bool CanSeek => false;

        /// <inheritdoc />
        public override bool CanWrite => false;

        /// <inheritdoc />
        public override long Length => throw new NotSupportedException();

        /// <inheritdoc />
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        /// <inheritdoc />
        public override void Flush()
        {
        }

        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        /// <inheritdoc />
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await _disposed.Task.WaitAsync(cancellationToken);
            return 0;
        }

        /// <inheritdoc />
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        /// <inheritdoc />
        public override void SetLength(long value) => throw new NotSupportedException();

        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        /// <inheritdoc />
        public override ValueTask DisposeAsync()
        {
            _disposed.TrySetResult();
            GC.SuppressFinalize(this);
            return ValueTask.CompletedTask;
        }
    }
}
