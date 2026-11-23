using System.Net;
using System.Text;
using Lineup.Plugin.SiliconDust.HDHomeRun.Device.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Lineup.Plugin.SiliconDust.HDHomeRun.Device.Tests.Protocol;

/// <summary>
/// Tests high-level native protocol recovery behavior.
/// </summary>
public class HDHomeRunDeviceNativeTests
{
    /// <summary>Verifies ambiguous legacy HTML never supplies sibling state or bypasses receiver-specific native status.</summary>
    /// <param name="active">Whether the requested receiver is active in native status.</param>
    /// <param name="legacyHtml">Whole-device legacy content that cannot establish the requested receiver.</param>
    [Theory]
    [InlineData(true, "<html>tuner0: ch=atsc:527000000 vchannel=104.1; tuner1: none, not in use</html>")]
    [InlineData(false, "<html>tuner1: ch=atsc:527000000 vchannel=104.1</html>")]
    [InlineData(true, "<html>unsupported legacy layout</html>")]
    [InlineData(true, "")]
    public async Task GetTunerStatusAsync_LegacyHtml_UsesRequestedNativeReceiver(bool active, string legacyHtml)
    {
        // Arrange
        static byte[] Response(string value) => new HDHomeRunPacketBuilder()
            .AddTag(HDHomeRunTagType.GetSetValue, $"{value}\0")
            .Build(HDHomeRunPacketType.GetSetReply);
        using var nativeStream = new ResponseStream(
        [
            .. Response(active ? "ch=atsc:527000000 lock=8vsb ss=99 snq=98 seq=100 bps=12000000 pps=100" : "ch=none lock=none ss=0 snq=0 seq=0 bps=0 pps=0"),
            .. Response(active ? "atsc:527000000" : "none"),
            .. Response(active ? "104.1" : "none"),
            .. Response(active ? "http://192.0.2.50:5000" : "none")
        ]);
        using var nativeControl = new HDHomeRunControl(nativeStream, NullLogger<HDHomeRunControl>.Instance);
        var handler = new LegacyStatusHandler(legacyHtml);
        using var httpControl = new HDHomeRunHttpControl("http://192.0.2.10", NullLogger<HDHomeRunHttpControl>.Instance, new HttpClient(handler));
        var httpFactory = Substitute.For<IHDHomeRunHttpControlFactory>();
        httpFactory.Create(Arg.Any<string>()).Returns(httpControl);
        using var device = CreateDevice(httpFactory, nativeControl);

        // Act
        var status = await device.GetTunerStatusAsync(0, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(active, status.IsActive);
        Assert.Equal(active ? "atsc:527000000" : "none", status.Channel);
        Assert.Equal(["/tuner0/status", "/tuner0/channel", "/tuner0/vchannel", "/tuner0/target"], nativeStream.Writes.Select(ReadName));
        Assert.Equal(["/status.json"], handler.RequestPaths);
    }

    /// <summary>Verifies modern receiver identifiers match exactly, preserving ordinary idle status without native work.</summary>
    [Fact]
    public async Task GetTunerStatusAsync_ModernIdleReceiver_DoesNotUseSimilarlyPrefixedSibling()
    {
        // Arrange
        using var nativeStream = new FailingReadStream(new IOException("Idle HTTP status must not use native control."));
        using var nativeControl = new HDHomeRunControl(nativeStream, NullLogger<HDHomeRunControl>.Instance);
        using var httpControl = new HDHomeRunHttpControl("http://192.0.2.10", NullLogger<HDHomeRunHttpControl>.Instance,
            new HttpClient(new StaticResponseHandler("""[{"Resource":"tuner10","VctName":"Sibling","VctNumber":"104.1","Frequency":527000000},{"Resource":"tuner1"}]""")));
        var httpFactory = Substitute.For<IHDHomeRunHttpControlFactory>();
        httpFactory.Create(Arg.Any<string>()).Returns(httpControl);
        using var device = CreateDevice(httpFactory, nativeControl);

        // Act
        var status = await device.GetTunerStatusAsync(1, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(status.IsActive);
        Assert.Null(status.Channel);
        Assert.Null(status.VirtualChannel);
    }

    /// <summary>Verifies unreadable HTTP status falls through to receiver-specific native status.</summary>
    [Fact]
    public async Task GetTunerStatusAsync_ModernStatusMalformed_UsesNativeStatus()
    {
        // Arrange
        var response = new HDHomeRunPacketBuilder().AddTag(HDHomeRunTagType.GetSetValue, "none\0").Build(HDHomeRunPacketType.GetSetReply);
        using var nativeStream = new ResponseStream([.. response, .. response, .. response, .. response]);
        using var nativeControl = new HDHomeRunControl(nativeStream, NullLogger<HDHomeRunControl>.Instance);
        using var httpControl = new HDHomeRunHttpControl("http://192.0.2.10", NullLogger<HDHomeRunHttpControl>.Instance,
            new HttpClient(new StaticResponseHandler("malformed status document")));
        var httpFactory = Substitute.For<IHDHomeRunHttpControlFactory>();
        httpFactory.Create(Arg.Any<string>()).Returns(httpControl);
        using var device = CreateDevice(httpFactory, nativeControl);

        // Act
        var status = await device.GetTunerStatusAsync(0, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("none", status.Channel);
        Assert.Equal(["/tuner0/status", "/tuner0/channel", "/tuner0/vchannel", "/tuner0/target"], nativeStream.Writes.Select(ReadName));
    }

    /// <summary>Verifies unavailable receiver-specific transports report failure instead of a success-shaped idle snapshot.</summary>
    [Fact]
    public async Task GetTunerStatusAsync_NoReceiverSpecificTransport_ReportsFailure()
    {
        // Arrange
        using var nativeControl = new HDHomeRunControl(_ => Task.FromException<Stream>(new IOException("Native unavailable.")), NullLogger<HDHomeRunControl>.Instance);
        using var httpControl = new HDHomeRunHttpControl("http://192.0.2.10", NullLogger<HDHomeRunHttpControl>.Instance, new HttpClient(new LegacyStatusHandler("none")));
        var httpFactory = Substitute.For<IHDHomeRunHttpControlFactory>();
        httpFactory.Create(Arg.Any<string>()).Returns(httpControl);
        using var device = CreateDevice(httpFactory, nativeControl);

        // Act
        var failure = await Assert.ThrowsAsync<HDHomeRunException>(() => device.GetTunerStatusAsync(0, TestContext.Current.CancellationToken));

        // Assert
        Assert.Contains("Receiver-specific status is unavailable", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies ATSC 3.0 native status retains detailed metrics while using the HTTP network rate when native bitrate is zero.
    /// </summary>
    [Fact]
    public async Task GetTunerStatusAsync_Atsc3NativeBitrateMissing_UsesHttpNetworkRate()
    {
        // Arrange
        static byte[] Response(string value) => new HDHomeRunPacketBuilder()
            .AddTag(HDHomeRunTagType.GetSetValue, $"{value}\0")
            .Build(HDHomeRunPacketType.GetSetReply);
        using var nativeStream = new ResponseStream(
        [
            .. Response("ch=atsc3:527000000:0 lock=atsc3 ss=99 snq=98 seq=100 bps=0 pps=109"),
            .. Response("atsc3:527000000:0"),
            .. Response("104.1"),
            .. Response("http://192.0.2.50:5000")
        ]);
        using var nativeControl = new HDHomeRunControl(nativeStream, NullLogger<HDHomeRunControl>.Instance);
        using var httpControl = new HDHomeRunHttpControl(
            "http://192.0.2.10",
            NullLogger<HDHomeRunHttpControl>.Instance,
            new HttpClient(new StaticResponseHandler(
                """[{"Resource":"tuner0","VctNumber":"104.1","VctName":"PBS","Frequency":527000000,"NetworkRate":1636864}]""")));
        var httpFactory = Substitute.For<IHDHomeRunHttpControlFactory>();
        httpFactory.Create(Arg.Any<string>()).Returns(httpControl);
        using var device = new HDHomeRunDevice(
            new HDHomeRunDiscoveredDevice
            {
                IpAddress = IPAddress.Parse("192.0.2.10"),
                DeviceId = 0x12345678,
                DeviceType = HDHomeRunDeviceType.Tuner,
                BaseUrl = "http://192.0.2.10"
            },
            NullLoggerFactory.Instance,
            httpFactory,
            nativeControl);

        // Act
        var status = await device.GetTunerStatusAsync(0, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("atsc3", status.LockType);
        Assert.Equal(109, status.PacketsPerSecond);
        Assert.Equal(13_094_912, status.BitsPerSecond);
        Assert.Equal("atsc3:527000000:0", status.Channel);
        Assert.Equal("104.1", status.VirtualChannel);
    }

    /// <summary>
    /// Verifies a native exchange failure does not discard tuner status already returned by HTTP.
    /// </summary>
    [Fact]
    public async Task GetTunerStatusAsync_WhenNativeExchangeFails_ReturnsHttpStatus()
    {
        // Arrange
        using var nativeStream = new FailingReadStream(new IOException("native unavailable"));
        using var nativeControl = new HDHomeRunControl(nativeStream, NullLogger<HDHomeRunControl>.Instance);
        using var httpControl = CreateActiveHttpControl();
        var httpFactory = Substitute.For<IHDHomeRunHttpControlFactory>();
        httpFactory.Create(Arg.Any<string>()).Returns(httpControl);
        using var device = CreateDevice(httpFactory, nativeControl);

        // Act
        var status = await device.GetTunerStatusAsync(0, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("527000000", status.Channel);
        Assert.Equal("104.1", status.VirtualChannel);
        Assert.Equal("locked", status.LockType);
        Assert.Equal(13_094_912, status.BitsPerSecond);
    }

    /// <summary>
    /// Verifies caller cancellation during native enrichment is propagated even when HTTP status succeeded.
    /// </summary>
    [Fact]
    public async Task GetTunerStatusAsync_WhenNativeExchangeIsCanceled_PropagatesCallerCancellation()
    {
        // Arrange
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var nativeStream = new CancelingReadStream(cancellation);
        using var nativeControl = new HDHomeRunControl(nativeStream, NullLogger<HDHomeRunControl>.Instance);
        using var httpControl = CreateActiveHttpControl();
        var httpFactory = Substitute.For<IHDHomeRunHttpControlFactory>();
        httpFactory.Create(Arg.Any<string>()).Returns(httpControl);
        using var device = CreateDevice(httpFactory, nativeControl);

        // Act
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => device.GetTunerStatusAsync(0, cancellation.Token));

        // Assert
        Assert.NotNull(exception);
        Assert.True(cancellation.IsCancellationRequested);
    }

    /// <summary>
    /// Verifies a transient connection failure does not prevent a later successful attempt.
    /// </summary>
    [Fact]
    public async Task GetVariableAsync_AfterTransientConnectionFailure_RetriesSuccessfully()
    {
        // Arrange
        var creations = 0;
        var response = new HDHomeRunPacketBuilder()
            .AddTag(HDHomeRunTagType.GetSetValue, "model\0")
            .Build(HDHomeRunPacketType.GetSetReply);
        var stream = new ResponseStream(response);
        HDHomeRunControl? failedControl = null;
        HDHomeRunControl CreateControl()
        {
            creations++;
            if (creations == 1)
            {
                failedControl = new HDHomeRunControl(
                    _ => Task.FromException<Stream>(new IOException("transient")),
                    NullLogger<HDHomeRunControl>.Instance);
                return failedControl;
            }

            return new HDHomeRunControl(stream, NullLogger<HDHomeRunControl>.Instance);
        }

        using var device = CreateDevice(CreateControl);

        // Act
        await Assert.ThrowsAsync<HDHomeRunException>(
            () => device.GetVariableAsync("/sys/model", TestContext.Current.CancellationToken));
        var result = await device.GetVariableAsync("/sys/model", TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("model", result);
        Assert.Equal(2, creations);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => failedControl!.ConnectAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Verifies concurrent callers share one lazily created and connected control.
    /// </summary>
    [Fact]
    public async Task GetVariableAsync_WhenCalledConcurrently_CreatesAndConnectsOneControl()
    {
        // Arrange
        var response = new HDHomeRunPacketBuilder()
            .AddTag(HDHomeRunTagType.GetSetValue, "model\0")
            .Build(HDHomeRunPacketType.GetSetReply);
        using var stream = new ResponseStream([.. response, .. response]);
        var connectionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseConnection = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var creations = 0;
        var connections = 0;
        HDHomeRunControl CreateControl()
        {
            Interlocked.Increment(ref creations);
            return new HDHomeRunControl(
                async cancellationToken =>
                {
                    Interlocked.Increment(ref connections);
                    connectionStarted.TrySetResult();
                    await releaseConnection.Task.WaitAsync(cancellationToken);
                    return stream;
                },
                NullLogger<HDHomeRunControl>.Instance);
        }

        var device = CreateDevice(CreateControl);
        var cancellationToken = TestContext.Current.CancellationToken;

        // Act
        var first = device.GetVariableAsync("/sys/model", cancellationToken);
        await connectionStarted.Task.WaitAsync(cancellationToken);
        var second = device.GetVariableAsync("/sys/model", cancellationToken);
        releaseConnection.TrySetResult();
        var results = await Task.WhenAll(first, second);
        device.Dispose();

        // Assert
        Assert.Equal(["model", "model"], results);
        Assert.Equal(1, creations);
        Assert.Equal(1, connections);
        Assert.True(stream.WasDisposed);
    }

    /// <summary>
    /// Verifies caller cancellation is propagated rather than converted to native unavailability.
    /// </summary>
    [Fact]
    public async Task GetVariableAsync_WhenConnectionIsCanceled_PropagatesCallerCancellation()
    {
        // Arrange
        static async Task<Stream> ConnectAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        }

        using var control = new HDHomeRunControl(ConnectAsync, NullLogger<HDHomeRunControl>.Instance);
        using var device = CreateDevice(control);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();

        // Act
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => device.GetVariableAsync("/sys/model", cancellation.Token));

        // Assert
        Assert.NotNull(exception);
        Assert.True(cancellation.IsCancellationRequested);
    }

    /// <summary>
    /// Verifies the high-level acquisition API returns the key sent to the device.
    /// </summary>
    [Fact]
    public async Task AcquireLockAsync_WhenSuccessful_ReturnsActualLockKey()
    {
        // Arrange
        var response = new HDHomeRunPacketBuilder()
            .AddTag(HDHomeRunTagType.GetSetValue, "ok\0")
            .Build(HDHomeRunPacketType.GetSetReply);
        var stream = new ResponseStream(response);
        using var control = new HDHomeRunControl(stream, NullLogger<HDHomeRunControl>.Instance);
        using var device = CreateDevice(control);

        // Act
        var result = await device.AcquireLockAsync(0, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(result.Value.ToString(), ReadValue(stream.Writes.Single()));
        Assert.Null(ReadLockKey(stream.Writes.Single()));
    }

    /// <summary>
    /// Verifies stopping succeeds only after both native writes complete.
    /// </summary>
    [Fact]
    public async Task StopStreamingAsync_WhenBothWritesSucceed_CompletesSuccessfully()
    {
        // Arrange
        var response = new HDHomeRunPacketBuilder()
            .AddTag(HDHomeRunTagType.GetSetValue, "none\0")
            .Build(HDHomeRunPacketType.GetSetReply);
        using var stream = new ResponseStream([.. response, .. response]);
        using var control = new HDHomeRunControl(stream, NullLogger<HDHomeRunControl>.Instance);
        using var device = CreateDevice(control);

        // Act
        await device.StopStreamingAsync(0, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, stream.Writes.Count);
        Assert.Contains("/tuner0/target", ReadName(stream.Writes[0]));
        Assert.Contains("/tuner0/channel", ReadName(stream.Writes[1]));
    }

    /// <summary>
    /// Verifies a rejected native stop write is surfaced to the caller.
    /// </summary>
    [Fact]
    public async Task StopStreamingAsync_WhenChannelStopIsRejected_ThrowsHdHomeRunException()
    {
        // Arrange
        var success = new HDHomeRunPacketBuilder()
            .AddTag(HDHomeRunTagType.GetSetValue, "none\0")
            .Build(HDHomeRunPacketType.GetSetReply);
        var rejection = new HDHomeRunPacketBuilder()
            .AddTag(HDHomeRunTagType.ErrorMessage, "busy\0")
            .Build(HDHomeRunPacketType.GetSetReply);
        using var stream = new ResponseStream([.. success, .. rejection]);
        using var control = new HDHomeRunControl(stream, NullLogger<HDHomeRunControl>.Instance);
        using var device = CreateDevice(control);

        // Act
        var exception = await Assert.ThrowsAsync<HDHomeRunException>(
            () => device.StopStreamingAsync(0, TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal("busy", exception.Message);
        Assert.Equal(2, stream.Writes.Count);
    }

    /// <summary>
    /// Verifies unavailable native control is reported because HTTP cannot stop a tuner.
    /// </summary>
    [Fact]
    public async Task StopStreamingAsync_WhenNativeControlIsUnavailable_PropagatesConnectionFailure()
    {
        // Arrange
        using var device = CreateDevice(
            () => new HDHomeRunControl(
                _ => Task.FromException<Stream>(new IOException("offline")),
                NullLogger<HDHomeRunControl>.Instance));

        // Act
        var exception = await Assert.ThrowsAsync<IOException>(
            () => device.StopStreamingAsync(0, TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal("offline", exception.Message);
    }

    /// <summary>
    /// Verifies restart propagates caller cancellation without attempting HTTP fallback.
    /// </summary>
    [Fact]
    public async Task RestartAsync_WhenCallerCancels_PropagatesCancellation()
    {
        // Arrange
        static async Task<Stream> ConnectAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        }

        using var control = new HDHomeRunControl(ConnectAsync, NullLogger<HDHomeRunControl>.Instance);
        using var device = CreateDevice(control);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();

        // Act
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => device.RestartAsync(cancellation.Token));

        // Assert
        Assert.NotNull(exception);
        Assert.True(cancellation.IsCancellationRequested);
    }

    private static uint? ReadLockKey(byte[] packet)
    {
        var reader = new HDHomeRunPacketReader(packet);
        while (reader.TryReadTag(out var tag, out var value))
        {
            if (tag == HDHomeRunTagType.GetSetLockkey)
            {
                return HDHomeRunPacketReader.ReadUInt32(value);
            }
        }

        return null;
    }

    private static string? ReadValue(byte[] packet)
    {
        var reader = new HDHomeRunPacketReader(packet);
        while (reader.TryReadTag(out var tag, out var value))
        {
            if (tag == HDHomeRunTagType.GetSetValue)
            {
                return HDHomeRunPacketReader.ReadString(value);
            }
        }

        return null;
    }

    private static string? ReadName(byte[] packet)
    {
        var reader = new HDHomeRunPacketReader(packet);
        while (reader.TryReadTag(out var tag, out var value))
        {
            if (tag == HDHomeRunTagType.GetSetName)
            {
                return HDHomeRunPacketReader.ReadString(value);
            }
        }

        return null;
    }

    private static HDHomeRunDevice CreateDevice(HDHomeRunControl control)
        => new(
            new HDHomeRunDiscoveredDevice
            {
                IpAddress = IPAddress.Loopback,
                DeviceId = 0x12345678,
                DeviceType = HDHomeRunDeviceType.Tuner
            },
            NullLoggerFactory.Instance,
            Substitute.For<IHDHomeRunHttpControlFactory>(),
            control);

    private static HDHomeRunDevice CreateDevice(IHDHomeRunHttpControlFactory httpControlFactory, HDHomeRunControl control)
        => new(
            new HDHomeRunDiscoveredDevice
            {
                IpAddress = IPAddress.Parse("192.0.2.10"),
                DeviceId = 0x12345678,
                DeviceType = HDHomeRunDeviceType.Tuner,
                BaseUrl = "http://192.0.2.10"
            },
            NullLoggerFactory.Instance,
            httpControlFactory,
            control);

    private static HDHomeRunHttpControl CreateActiveHttpControl() =>
        new(
            "http://192.0.2.10",
            NullLogger<HDHomeRunHttpControl>.Instance,
            new HttpClient(new StaticResponseHandler(
                """[{"Resource":"tuner0","VctNumber":"104.1","VctName":"PBS","Frequency":527000000,"NetworkRate":1636864}]""")));

    private static HDHomeRunDevice CreateDevice(Func<HDHomeRunControl> controlFactory)
        => new(
            new HDHomeRunDiscoveredDevice
            {
                IpAddress = IPAddress.Loopback,
                DeviceId = 0x12345678,
                DeviceType = HDHomeRunDeviceType.Tuner
            },
            NullLoggerFactory.Instance,
            Substitute.For<IHDHomeRunHttpControlFactory>(),
            controlFactory);

    private sealed class ResponseStream(byte[] response) : MemoryStream(response)
    {
        /// <summary>Gets the writes.</summary>
        public List<byte[]> Writes { get; } = [];

        /// <summary>Gets or sets the was disposed.</summary>
        public bool WasDisposed { get; private set; }

        /// <inheritdoc />
        public override bool CanWrite => true;

        /// <inheritdoc />
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Writes.Add(buffer.ToArray());
            return ValueTask.CompletedTask;
        }

        /// <inheritdoc/>
        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class FailingReadStream(Exception exception) : MemoryStream
    {
        /// <inheritdoc />
        public override bool CanWrite => true;

        /// <inheritdoc />
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(exception);

        /// <inheritdoc />
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private sealed class CancelingReadStream(CancellationTokenSource cancellation) : MemoryStream
    {
        /// <inheritdoc />
        public override bool CanWrite => true;

        /// <inheritdoc />
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            return ValueTask.FromCanceled<int>(cancellation.Token);
        }

        /// <inheritdoc />
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private sealed class StaticResponseHandler(string content) : HttpMessageHandler
    {
        /// <inheritdoc/>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json")
            });
    }

    private sealed class LegacyStatusHandler(string content) : HttpMessageHandler
    {
        /// <summary>Gets the requested endpoint paths without contacting a real device.</summary>
        public List<string> RequestPaths { get; } = [];

        /// <inheritdoc />
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            RequestPaths.Add(path);
            return Task.FromResult(new HttpResponseMessage(path == "/status.json" ? HttpStatusCode.NotFound : HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "text/html")
            });
        }
    }
}
