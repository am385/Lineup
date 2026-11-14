using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Lineup.App.Web.Tests.Services;

/// <summary>Verifies shared FFmpeg transforms and slates drain noisy children without retaining lifetime diagnostics.</summary>
public sealed class FfmpegStreamDiagnosticsTests
{
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
