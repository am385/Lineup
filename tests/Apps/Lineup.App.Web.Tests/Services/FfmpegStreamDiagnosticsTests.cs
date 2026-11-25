using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Lineup.App.Web.Tests.Services;

/// <summary>Isolates noisy FFmpeg child-process tests from unrelated process-start contention.</summary>
[CollectionDefinition("FFmpeg process diagnostics", DisableParallelization = true)]
public sealed class FfmpegProcessDiagnosticsCollection;

/// <summary>Verifies shared FFmpeg transforms and slates drain noisy children without retaining lifetime diagnostics.</summary>
[Collection("FFmpeg process diagnostics")]
public sealed class FfmpegStreamDiagnosticsTests
{
    /// <summary>Verifies new MPEG-TS transforms discover periodic tables and video initialization after joining an established source.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeMpegTs_MidstreamJoin_DiscoversParametersBeforeOutput(bool encodeVideo)
    {
        // Arrange
        var executable = Environment.GetEnvironmentVariable("LINEUP_TEST_MPEGTS_FFMPEG");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(executable), "Set LINEUP_TEST_MPEGTS_FFMPEG to an explicit native FFmpeg executable with lavfi, libx264 and AAC support.");
        Assert.True(File.Exists(executable), $"The configured FFmpeg executable does not exist: {executable}");
        var source = await RunNativeAsync(executable!,
        [
            "-hide_banner", "-loglevel", "error",
            "-f", "lavfi", "-i", "testsrc2=size=64x64:rate=5",
            "-f", "lavfi", "-i", "sine=frequency=1000:sample_rate=48000",
            "-t", "5", "-c:v", "libx264", "-preset", "ultrafast", "-g", "20",
            "-c:a", "aac", "-b:a", "64k", "-pat_period", "2", "-muxrate", "12M", "-f", "mpegts", "pipe:1"
        ]);
        Assert.True(source.ExitCode == 0, source.Diagnostics);
        var profile = new VirtualTranscodeProfile { AlwaysTranscodeVideo = encodeVideo, Audio = OutputAudioPolicy.Copy };
        var arguments = MpegTsTranscodePlanner.CreateFfmpegArguments(
            new Uri("http://lineup.invalid/source"), "h264", [new AudioStreamInfo(1, "aac", 1)], profile).ToList();
        arguments[arguments.IndexOf("http://lineup.invalid/source")] = "pipe:0";
        // Drop the initial tables and SPS/PPS, as a subscription joining an existing producer would.
        var joined = source.Output.AsMemory(188 * 1000).ToArray();

        // Act
        var output = await RunNativeAsync(executable!, arguments, joined);

        // Assert
        Assert.True(output.ExitCode == 0, output.Diagnostics);
        Assert.DoesNotContain("Could not find codec parameters", output.Diagnostics, StringComparison.Ordinal);
        foreach (var track in new[] { "v", "a" })
        {
            var decoded = await RunNativeAsync(executable!,
            [
                "-hide_banner", "-loglevel", "error", "-i", "pipe:0",
                "-map", $"0:{track}:0", $"-frames:{track}", "1", "-f", "framehash", "pipe:1"
            ], output.Output);
            Assert.True(decoded.ExitCode == 0, decoded.Diagnostics);
            Assert.Matches(@"(?m)^0,\s*-?\d+,\s*-?\d+,\s*\d+,\s*[1-9]\d*,", System.Text.Encoding.UTF8.GetString(decoded.Output));
        }
    }

    /// <summary>Verifies copied AC-4 repair survives hardware priming and software retry while preserving owned-process cleanup.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Ac4Copy_OutputPrimingAndFallback_PreserveSignalingAndLifetime(bool gpu, bool failHardware)
    {
        // Arrange
        var encoder = OperatingSystem.IsMacOS() ? HardwareVideoEncoder.VideoToolbox : HardwareVideoEncoder.Nvenc;
        var hardware = new HardwareEncodingSettings { Encoder = gpu ? encoder : HardwareVideoEncoder.Software, AllowSoftwareFallback = true };
        var plan = MpegTsAc4CopyPlan.Create(["ac4"], OutputAudioPolicy.Copy, 1);
        var expected = MpegTsAc4SignalingStreamTests.CreateTransport(plan.PacketIds[0], registered: true);
        var factory = new TransportFactory(plan.PacketIds[0], encoder, failHardware);
        List<string> software = ["-i", "pipe:0", "-c:v:0", "libx264"];
        plan.AddArguments(software);
        software.AddRange(["-f", "mpegts", "pipe:1"]);
        var arguments = HardwareVideoEncodingPlanner.Apply(software, hardware);
        var transform = new FfmpegStreamTransform(arguments, factory, NullLogger.Instance, "AC-4 transport", hardware, software, plan);
        await using var input = new BlockingInput();
        await using var lease = await transform.StartAsync(input, TestContext.Current.CancellationToken);
        var received = new byte[expected.Length];

        // Act
        await lease.Stream.ReadExactlyAsync(received, TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await lease.DisposeAsync();

        // Assert
        Assert.Equal(expected, received);
        Assert.Equal(gpu ? failHardware ? 4 : 3 : 1, factory.ProcessExits.Count);
        Assert.All(factory.ProcessExits, exit => Assert.True(exit.IsCompletedSuccessfully));
        Assert.True(input.Canceled);
        Assert.Equal(gpu && failHardware, lease.VideoEncoding?.SoftwareFallbackApplied);
    }

    /// <summary>Verifies the signaling wrapper does not replace a real FFmpeg terminal error with a missing-table or EOF error.</summary>
    [Fact]
    public async Task Ac4Copy_OutputFailure_PreservesProcessDiagnostics()
    {
        // Arrange
        var plan = MpegTsAc4CopyPlan.Create(["ac4"], OutputAudioPolicy.Copy, 0);
        var factory = new TransportFactory(plan.PacketIds[0], HardwareVideoEncoder.Software, false, exitCode: 7);
        var transform = new FfmpegStreamTransform([], factory, NullLogger.Instance, "AC-4 failure", ac4CopyPlan: plan);
        await using var input = new BlockingInput();
        await using var lease = await transform.StartAsync(input, TestContext.Current.CancellationToken);

        // Act
        var exception = await Record.ExceptionAsync(() => lease.Stream.CopyToAsync(Stream.Null, TestContext.Current.CancellationToken));
        await lease.DisposeAsync();

        // Assert
        var failure = Assert.IsType<MediaProcessException>(exception);
        Assert.Equal(7, failure.ExitCode);
        Assert.Contains("Synthetic transport failure.", failure.Diagnostics, StringComparison.Ordinal);
        Assert.All(factory.ProcessExits, exit => Assert.True(exit.IsCompletedSuccessfully));
    }

    /// <summary>Verifies a real failing child retains its exit status separately from its bounded stderr tail.</summary>
    [Fact]
    public async Task Output_NoisyFailure_RetainsStructuredExitDiagnostics()
    {
        // Arrange
        var factory = CreateFactory(2_097_152, 234, hold: false);
        await using var input = new BlockingInput();
        await using var lease = await OpenAsync(factory, input, slate: false, TestContext.Current.CancellationToken);

        // Act
        var exception = await Record.ExceptionAsync(() => lease.Stream.CopyToAsync(Stream.Null, TestContext.Current.CancellationToken));

        // Assert
        var failure = Assert.IsType<MediaProcessException>(exception);
        Assert.Equal(234, failure.Data["ExitCode"]);
        var diagnostics = Assert.IsType<string>(failure.Data["Diagnostics"]);
        Assert.True(diagnostics.Length <= 16_384);
        Assert.EndsWith("final-error", diagnostics, StringComparison.Ordinal);
    }

    /// <summary>Verifies failures retain a bounded useful suffix even when a child emits a huge unterminated line.</summary>
    [Theory]
    [InlineData(false, 65_536)]
    [InlineData(false, 2_097_152)]
    [InlineData(true, 65_536)]
    [InlineData(true, 2_097_152)]
    public async Task Output_NoisyFailure_RetainsBoundedTail(bool slate, int length)
    {
        // Arrange
        var factory = CreateFactory(length, 7, hold: false);
        await using var input = new BlockingInput();
        await using var lease = await OpenAsync(factory, input, slate, TestContext.Current.CancellationToken);

        // Act
        var exception = await Record.ExceptionAsync(() =>
            lease.Stream.CopyToAsync(Stream.Null, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        // Assert
        Assert.NotNull(exception);
        if (slate)
        {
            Assert.IsType<MpegTsTranscodeException>(exception);
        }
        else
        {
            Assert.IsType<MediaProcessException>(exception);
        }
        Assert.Contains("code 7", exception.Message, StringComparison.Ordinal);
        Assert.EndsWith("final-error", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Earlier diagnostic", exception.Message, StringComparison.Ordinal);
        Assert.True(exception.Message.Length <= 16_512, $"The failure retained {exception.Message.Length} diagnostic characters.");
    }

    /// <summary>Verifies noisy successful children still produce their output and clean end of stream.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Output_NoisySuccess_PreservesOutput(bool slate)
    {
        // Arrange
        var factory = CreateFactory(1_048_576, 0, hold: false);
        await using var input = new BlockingInput();
        await using var lease = await OpenAsync(factory, input, slate, TestContext.Current.CancellationToken);
        await using var output = new MemoryStream();

        // Act
        await lease.Stream.CopyToAsync(output, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(new byte[] { 0x47 }, output.ToArray());
    }

    /// <summary>Verifies drainage continues before child exit and disposing an active noisy process releases the child and input pump.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Output_OpenNoisyChild_DrainsAndDisposes(bool slate)
    {
        // Arrange
        Process? observer = null;
        var factory = CreateFactory(1_048_576, 0, hold: true, started: process => observer = Process.GetProcessById(process.Id));
        await using var input = new BlockingInput();
        await using var lease = await OpenAsync(factory, input, slate, TestContext.Current.CancellationToken);
        using var ownedObserver = Assert.IsType<Process>(observer);
        var buffer = new byte[1];

        // Act
        await lease.Stream.ReadExactlyAsync(buffer, TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0x47, buffer[0]);
        Assert.False(ownedObserver.HasExited);

        // Act
        await lease.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Assert
        Assert.True(ownedObserver.HasExited);
        if (!slate)
        {
            Assert.True(input.Canceled);
        }
    }

    /// <summary>Verifies explicit caller cancellation remains effective after a transform has drained high-volume diagnostics.</summary>
    [Fact]
    public async Task StartAsync_CallerCancels_ReleasesNoisyTransform()
    {
        // Arrange
        Process? observer = null;
        var factory = CreateFactory(1_048_576, 0, hold: true, started: process => observer = Process.GetProcessById(process.Id));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await using var input = new BlockingInput();
        await using var lease = await OpenAsync(factory, input, slate: false, cancellation.Token);
        using var ownedObserver = Assert.IsType<Process>(observer);
        await lease.Stream.ReadExactlyAsync(new byte[1], TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Act
        await cancellation.CancelAsync();
        await lease.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Assert
        Assert.True(ownedObserver.HasExited);
        Assert.True(input.Canceled);
    }

    private sealed class TransportFactory(int audioPid, HardwareVideoEncoder encoder, bool failHardware, int exitCode = 0) : IMediaProcessFactory
    {
        /// <summary>Gets original-process exit signals retained after lease disposal.</summary>
        public List<Task> ProcessExits { get; } = [];

        /// <inheritdoc />
        public Process Start(MediaProcessRequest request)
        {
            var configuration = new DirectoryInfo(AppContext.BaseDirectory).Name;
            var executable = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "Lineup.TestSupport.FakeFFmpeg", configuration, "Lineup.TestSupport.FakeFFmpeg.dll"));
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add(executable);
            if (request.Arguments.Contains("-encoders"))
            {
                start.ArgumentList.Add("probe-json");
                start.ArgumentList.Add($"V {HardwareVideoEncodingPlanner.GetEncoderName(encoder)}");
            }
            else if (request.Arguments.Contains("testsrc2=size=128x128:rate=25"))
            {
                start.ArgumentList.Add("probe-json");
                start.ArgumentList.Add(string.Empty);
            }
            else if (failHardware && request.Arguments.Contains(HardwareVideoEncodingPlanner.GetEncoderName(encoder)))
            {
                start.Environment["LINEUP_FAKE_FFMPEG_HARDWARE_MODE"] = "probe-passes-playback-fails";
                foreach (var argument in request.Arguments)
                {
                    start.ArgumentList.Add(argument);
                }
            }
            else
            {
                start.ArgumentList.Add("transport");
                start.ArgumentList.Add(Convert.ToBase64String(MpegTsAc4SignalingStreamTests.CreateTransport(audioPid)));
                start.ArgumentList.Add(exitCode.ToString(CultureInfo.InvariantCulture));
                start.ArgumentList.Add(exitCode == 0 ? "hold" : "exit");
            }
            var process = OwnedMediaProcess.Start(start);
            var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            process.Exited += (_, _) => exited.TrySetResult();
            process.EnableRaisingEvents = true;
            if (process.HasExited)
            {
                exited.TrySetResult();
            }
            ProcessExits.Add(exited.Task);
            return process;
        }
    }

    private static async Task<(int ExitCode, byte[] Output, string Diagnostics)> RunNativeAsync(string executable, IReadOnlyList<string> arguments, byte[]? input = null)
    {
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        watchdog.CancelAfter(TimeSpan.FromSeconds(30));
        using var process = new MediaProcessFactory().Start(new MediaProcessRequest
        {
            FileName = executable,
            Arguments = arguments,
            RedirectStandardInput = true,
            RedirectStandardOutput = true
        });
        await using var source = new MemoryStream(input ?? []);
        await using var output = new MemoryStream();
        var diagnostics = process.StandardError.ReadToEndAsync(watchdog.Token);
        var drain = process.StandardOutput.BaseStream.CopyToAsync(output, watchdog.Token);
        var pump = MediaStreamPump.PumpAsync(source, process, NullLogger.Instance, "native MPEG-TS regression", watchdog.Token);
        try
        {
            await process.WaitForExitAsync(watchdog.Token);
            await Task.WhenAll(pump, drain);
            return (process.ExitCode, output.ToArray(), await diagnostics);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
            await Task.WhenAll(pump, drain, diagnostics).WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    private static IMediaProcessFactory CreateFactory(int length, int exitCode, bool hold, Action<Process>? started = null)
    {
        var factory = Substitute.For<IMediaProcessFactory>();
        factory.Start(Arg.Any<MediaProcessRequest>()).Returns(_ =>
        {
            var configuration = new DirectoryInfo(AppContext.BaseDirectory).Name;
            var executable = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "Lineup.TestSupport.FakeFFmpeg", configuration, "Lineup.TestSupport.FakeFFmpeg.dll"));
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add(executable);
            start.ArgumentList.Add("diagnostics");
            start.ArgumentList.Add(length.ToString(CultureInfo.InvariantCulture));
            start.ArgumentList.Add(exitCode.ToString(CultureInfo.InvariantCulture));
            start.ArgumentList.Add(hold ? "hold" : "exit");
            var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start the owned diagnostic child.");
            started?.Invoke(process);
            return process;
        });
        return factory;
    }

    private static async ValueTask<IMediaStreamLease> OpenAsync(IMediaProcessFactory factory, Stream input, bool slate, CancellationToken cancellationToken) =>
        slate
            ? new SlateLease(new ChannelSlateStreamFactory(factory).OpenMpegTs("7.1"))
            : await new FfmpegStreamTransform([], factory, NullLogger.Instance, "diagnostic-test").StartAsync(input, cancellationToken);

    private sealed class SlateLease(Stream stream) : IMediaStreamLease
    {
        /// <summary>Gets the stream.</summary>
        public Stream Stream => stream;
        /// <summary>Gets the content type.</summary>
        public string? ContentType => "video/mp2t";
        /// <inheritdoc />
        public ValueTask DisposeAsync() => stream.DisposeAsync();
    }

    private sealed class BlockingInput : Stream
    {
        /// <summary>Gets whether the transform canceled its input pump.</summary>
        public bool Canceled { get; private set; }
        /// <inheritdoc />
        public override bool CanRead => true;
        /// <inheritdoc />
        public override bool CanSeek => false;
        /// <inheritdoc />
        public override bool CanWrite => false;
        /// <inheritdoc />
        public override long Length => throw new NotSupportedException();
        /// <inheritdoc />
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        /// <inheritdoc />
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Canceled = true;
                throw;
            }
        }

        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        /// <inheritdoc />
        public override void Flush() => throw new NotSupportedException();
        /// <inheritdoc />
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        /// <inheritdoc />
        public override void SetLength(long value) => throw new NotSupportedException();
        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
