using Lineup.Plugin.SiliconDust.HDHomeRun.Device.Protocol;
using Lineup.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lineup.Plugin.SiliconDust.HDHomeRun.Device.Tests.Protocol;

/// <summary>
/// Tests native HDHomeRun control exchanges.
/// </summary>
public class HDHomeRunControlTests
{
    /// <summary>
    /// Verifies fragmented packets are read completely and concurrent exchanges are serialized.
    /// </summary>
    [Fact]
    public async Task SendAndReceiveAsync_ReadsFragmentedResponses_AndSerializesConcurrentRequests()
    {
        // Arrange
        var request = new HDHomeRunPacketBuilder()
            .AddTag(HDHomeRunTagType.GetSetName, "/sys/model\0")
            .Build(HDHomeRunPacketType.GetSetRequest);
        var firstResponse = CreateResponse("first");
        var secondResponse = CreateResponse("second");
        using var stream = new ControlledDuplexStream(firstResponse, secondResponse);
        using var control = new HDHomeRunControl(stream, NullLogger<HDHomeRunControl>.Instance);

        // Act
        var cancellationToken = TestContext.Current.CancellationToken;
        var firstExchange = control.SendAndReceiveAsync(request, cancellationToken);
        Task<byte[]>? secondExchange = null;
        Task? secondWrite = null;
        try
        {
            await stream.WaitForWriteCountAsync(1);
            secondExchange = control.SendAndReceiveAsync(request, cancellationToken);
            secondWrite = stream.WaitForWriteCountAsync(2);
            var requestsWereSerialized = !secondExchange.IsCompleted && !secondWrite.IsCompleted;
            stream.ReleaseResponse();
            var firstResult = await firstExchange;
            await secondWrite;
            stream.ReleaseResponse();
            var secondResult = await secondExchange;

            // Assert
            Assert.True(requestsWereSerialized);
            Assert.Equal(firstResponse, firstResult);
            Assert.Equal(secondResponse, secondResult);
            Assert.True(stream.ReadCount >= firstResponse.Length + secondResponse.Length);
        }
        finally
        {
            stream.ReleaseResponse();
            stream.ReleaseResponse();
            await Task.WhenAll(firstExchange, secondExchange ?? Task.CompletedTask, secondWrite ?? Task.CompletedTask)
                .WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        }
    }

    /// <summary>
    /// Verifies a silent device times out and its connection is invalidated.
    /// </summary>
    [Fact]
    public async Task SendAndReceiveAsync_WithSilentResponse_TimesOutAndInvalidatesConnection()
    {
        // Arrange
        var request = CreateRequest();
        var clock = new ControlledTimeProvider();
        var timeout = TimeSpan.FromMilliseconds(30);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var stream = new HangingDuplexStream([]);
        using var control = new HDHomeRunControl(stream, NullLogger<HDHomeRunControl>.Instance, timeout, timeProvider: clock);

        // Act
        var observed = Record.ExceptionAsync(() => control.SendAndReceiveAsync(request, cancellation.Token)).AsTask();
        try
        {
            await stream.ReadBlocked.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            await clock.WaitForTimerAsync(timeout, TestContext.Current.CancellationToken);
            clock.Advance(timeout);
            var exception = await observed.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            // Assert
            Assert.Contains("timed out", Assert.IsType<TimeoutException>(exception).Message);
            Assert.True(stream.WasDisposed);
        }
        finally
        {
            cancellation.Cancel();
            await observed.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// Verifies a response that stops mid-frame times out and invalidates its connection.
    /// </summary>
    [Fact]
    public async Task SendAndReceiveAsync_WithPartialResponse_TimesOutAndInvalidatesConnection()
    {
        // Arrange
        var request = CreateRequest();
        var response = CreateResponse("partial");
        var clock = new ControlledTimeProvider();
        var timeout = TimeSpan.FromMilliseconds(30);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var stream = new HangingDuplexStream(response[..6]);
        using var control = new HDHomeRunControl(stream, NullLogger<HDHomeRunControl>.Instance, timeout, timeProvider: clock);

        // Act
        var observed = Record.ExceptionAsync(() => control.SendAndReceiveAsync(request, cancellation.Token)).AsTask();
        try
        {
            await stream.ReadBlocked.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.Equal(6, stream.ReadCount);
            await clock.WaitForTimerAsync(timeout, TestContext.Current.CancellationToken);
            clock.Advance(timeout);

            // Assert
            Assert.IsType<TimeoutException>(await observed.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
            Assert.True(stream.WasDisposed);
            Assert.Equal(6, stream.ReadCount);
        }
        finally
        {
            cancellation.Cancel();
            await observed.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// Verifies caller cancellation remains distinguishable from an exchange timeout.
    /// </summary>
    [Fact]
    public async Task SendAndReceiveAsync_WhenCallerCancels_ThrowsOperationCanceledException()
    {
        // Arrange
        var request = CreateRequest();
        var clock = new ControlledTimeProvider();
        using var stream = new HangingDuplexStream([]);
        using var control = new HDHomeRunControl(stream, NullLogger<HDHomeRunControl>.Instance, TimeSpan.FromSeconds(2), timeProvider: clock);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        // Act
        var observed = Record.ExceptionAsync(() => control.SendAndReceiveAsync(request, cancellation.Token)).AsTask();
        try
        {
            await stream.ReadBlocked.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.False(observed.IsCompleted);
            cancellation.Cancel();

            // Assert
            Assert.IsAssignableFrom<OperationCanceledException>(await observed.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
            Assert.True(cancellation.IsCancellationRequested);
            Assert.True(stream.WasDisposed);
        }
        finally
        {
            cancellation.Cancel();
            await observed.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// Verifies connection establishment is bounded by the exchange timeout.
    /// </summary>
    [Fact]
    public async Task SendAndReceiveAsync_WithNeverCompletingConnection_TimesOutDeterministically()
    {
        // Arrange
        var request = CreateRequest();
        var clock = new ControlledTimeProvider();
        var timeout = TimeSpan.FromMilliseconds(30);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var connectionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<Stream> ConnectAsync(CancellationToken cancellationToken)
        {
            connectionStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The stalled connection unexpectedly completed.");
        }

        using var control = new HDHomeRunControl(ConnectAsync, NullLogger<HDHomeRunControl>.Instance, timeout, clock);

        // Act
        var observed = Record.ExceptionAsync(() => control.SendAndReceiveAsync(request, cancellation.Token)).AsTask();
        try
        {
            await connectionStarted.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            await clock.WaitForTimerAsync(timeout, TestContext.Current.CancellationToken);
            await clock.WaitForTimerAsync(timeout, TestContext.Current.CancellationToken);
            clock.Advance(timeout);
            var exception = await observed.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            // Assert
            Assert.Contains("timed out", Assert.IsType<TimeoutException>(exception).Message);
            Assert.False(control.IsConnected);
        }
        finally
        {
            cancellation.Cancel();
            await observed.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// Verifies simultaneous tuner locks retain distinct keys and apply them only to matching tuner commands.
    /// </summary>
    [Fact]
    public async Task LockTunerAsync_WithTwoTuners_AppliesDistinctMatchingKeys()
    {
        // Arrange
        using var stream = new ScriptedDuplexStream(CreateResponse("ok"), CreateResponse("ok"), CreateResponse("ok"), CreateResponse("ok"), CreateResponse("ok"));
        var lockKeys = new Queue<uint>([101, 202]);
        using var control = new HDHomeRunControl(stream, NullLogger<HDHomeRunControl>.Instance, createLockKey: lockKeys.Dequeue);
        var cancellationToken = TestContext.Current.CancellationToken;

        // Act
        var firstKey = await control.AcquireTunerLockAsync(0, cancellationToken: cancellationToken);
        var secondKey = await control.AcquireTunerLockAsync(1, cancellationToken: cancellationToken);
        await control.SetTunerChannelAsync(0, "auto:7", cancellationToken);
        await control.SetTunerChannelAsync(1, "auto:9", cancellationToken);
        await control.SetAsync("/sys/restart", "self", cancellationToken);

        // Assert
        Assert.NotNull(firstKey);
        Assert.NotNull(secondKey);
        Assert.NotEqual(firstKey, secondKey);
        Assert.Equal("101", ReadValue(stream.Writes[0]));
        Assert.Null(ReadLockKey(stream.Writes[0]));
        Assert.Equal("202", ReadValue(stream.Writes[1]));
        Assert.Null(ReadLockKey(stream.Writes[1]));
        Assert.Equal(firstKey, ReadLockKey(stream.Writes[2]));
        Assert.Equal(secondKey, ReadLockKey(stream.Writes[3]));
        Assert.Null(ReadLockKey(stream.Writes[4]));
    }

    /// <summary>
    /// Verifies replacement authenticates with the previous key and installs the new key after success.
    /// </summary>
    [Fact]
    public async Task AcquireTunerLockAsync_WhenReplacingLock_AuthenticatesWithPreviousKey()
    {
        // Arrange
        using var stream = new ScriptedDuplexStream(CreateResponse("ok"), CreateResponse("ok"), CreateResponse("ok"));
        var lockKeys = new Queue<uint>([101, 202]);
        using var control = new HDHomeRunControl(stream, NullLogger<HDHomeRunControl>.Instance, createLockKey: lockKeys.Dequeue);
        var cancellationToken = TestContext.Current.CancellationToken;

        // Act
        var firstKey = await control.AcquireTunerLockAsync(0, cancellationToken: cancellationToken);
        var replacementKey = await control.AcquireTunerLockAsync(0, cancellationToken: cancellationToken);
        await control.SetTunerChannelAsync(0, "auto:7", cancellationToken);

        // Assert
        Assert.Equal(101u, firstKey);
        Assert.Equal(202u, replacementKey);
        Assert.Equal("202", ReadValue(stream.Writes[1]));
        Assert.Equal(101u, ReadLockKey(stream.Writes[1]));
        Assert.Equal(202u, ReadLockKey(stream.Writes[2]));
    }

    /// <summary>
    /// Verifies forced acquisition uses force before acquiring and installing a new key.
    /// </summary>
    [Fact]
    public async Task AcquireTunerLockAsync_WhenForced_SendsForceThenNormalAcquisition()
    {
        // Arrange
        using var stream = new ScriptedDuplexStream(CreateResponse("ok"), CreateResponse("ok"), CreateResponse("ok"), CreateResponse("ok"));
        var lockKeys = new Queue<uint>([101, 202]);
        using var control = new HDHomeRunControl(stream, NullLogger<HDHomeRunControl>.Instance, createLockKey: lockKeys.Dequeue);
        var cancellationToken = TestContext.Current.CancellationToken;

        // Act
        await control.AcquireTunerLockAsync(0, cancellationToken: cancellationToken);
        var forcedKey = await control.AcquireTunerLockAsync(0, force: true, cancellationToken);
        await control.SetTunerChannelAsync(0, "auto:7", cancellationToken);

        // Assert
        Assert.Equal(202u, forcedKey);
        Assert.Equal("force", ReadValue(stream.Writes[1]));
        Assert.Equal(101u, ReadLockKey(stream.Writes[1]));
        Assert.Equal("202", ReadValue(stream.Writes[2]));
        Assert.Null(ReadLockKey(stream.Writes[2]));
        Assert.Equal(202u, ReadLockKey(stream.Writes[3]));
    }

    /// <summary>
    /// Verifies failed replacement preserves the previously acquired tuner key.
    /// </summary>
    [Fact]
    public async Task AcquireTunerLockAsync_WhenReplacementFails_PreservesPreviousKey()
    {
        // Arrange
        using var stream = new ScriptedDuplexStream(CreateResponse("ok"), CreateErrorResponse("busy"), CreateResponse("ok"));
        var lockKeys = new Queue<uint>([101, 202]);
        using var control = new HDHomeRunControl(stream, NullLogger<HDHomeRunControl>.Instance, createLockKey: lockKeys.Dequeue);
        var cancellationToken = TestContext.Current.CancellationToken;

        // Act
        var firstKey = await control.AcquireTunerLockAsync(0, cancellationToken: cancellationToken);
        var replacementKey = await control.AcquireTunerLockAsync(0, cancellationToken: cancellationToken);
        await control.SetTunerChannelAsync(0, "auto:7", cancellationToken);

        // Assert
        Assert.Equal(101u, firstKey);
        Assert.Null(replacementKey);
        Assert.Equal("202", ReadValue(stream.Writes[1]));
        Assert.Equal(101u, ReadLockKey(stream.Writes[1]));
        Assert.Equal(101u, ReadLockKey(stream.Writes[2]));
    }

    /// <summary>
    /// Verifies failed acquisition after force leaves the tuner without an installed key.
    /// </summary>
    [Fact]
    public async Task AcquireTunerLockAsync_WhenPostForceAcquisitionFails_ClearsPreviousKey()
    {
        // Arrange
        using var stream = new ScriptedDuplexStream(CreateResponse("ok"), CreateResponse("ok"), CreateErrorResponse("busy"), CreateResponse("ok"));
        var lockKeys = new Queue<uint>([101, 202]);
        using var control = new HDHomeRunControl(stream, NullLogger<HDHomeRunControl>.Instance, createLockKey: lockKeys.Dequeue);
        var cancellationToken = TestContext.Current.CancellationToken;

        // Act
        await control.AcquireTunerLockAsync(0, cancellationToken: cancellationToken);
        var forcedKey = await control.AcquireTunerLockAsync(0, force: true, cancellationToken);
        await control.SetTunerChannelAsync(0, "auto:7", cancellationToken);

        // Assert
        Assert.Null(forcedKey);
        Assert.Equal("force", ReadValue(stream.Writes[1]));
        Assert.Equal(101u, ReadLockKey(stream.Writes[1]));
        Assert.Equal("202", ReadValue(stream.Writes[2]));
        Assert.Null(ReadLockKey(stream.Writes[2]));
        Assert.Null(ReadLockKey(stream.Writes[3]));
    }

    /// <summary>
    /// Verifies a failed release retains the key so later operations and release retries remain authenticated.
    /// </summary>
    [Fact]
    public async Task ReleaseTunerLockAsync_WhenReleaseFails_PreservesKeyForRetry()
    {
        // Arrange
        using var stream = new ScriptedDuplexStream(CreateResponse("ok"), CreateErrorResponse("temporary failure"), CreateResponse("ok"), CreateResponse("ok"));
        using var control = new HDHomeRunControl(stream, NullLogger<HDHomeRunControl>.Instance, createLockKey: static () => 101);
        var cancellationToken = TestContext.Current.CancellationToken;
        await control.AcquireTunerLockAsync(0, cancellationToken: cancellationToken);

        // Act
        await control.ReleaseTunerLockAsync(0, cancellationToken);
        await control.SetTunerChannelAsync(0, "auto:7", cancellationToken);
        await control.ReleaseTunerLockAsync(0, cancellationToken);

        // Assert
        Assert.Equal(101u, ReadLockKey(stream.Writes[1]));
        Assert.Equal(101u, ReadLockKey(stream.Writes[2]));
        Assert.Equal(101u, ReadLockKey(stream.Writes[3]));
    }

    private static byte[] CreateRequest()
        => new HDHomeRunPacketBuilder()
            .AddTag(HDHomeRunTagType.GetSetName, "/sys/model\0")
            .Build(HDHomeRunPacketType.GetSetRequest);

    private static byte[] CreateResponse(string value)
        => new HDHomeRunPacketBuilder()
            .AddTag(HDHomeRunTagType.GetSetValue, value + "\0")
            .Build(HDHomeRunPacketType.GetSetReply);

    private static byte[] CreateErrorResponse(string error)
        => new HDHomeRunPacketBuilder()
            .AddTag(HDHomeRunTagType.ErrorMessage, error + "\0")
            .Build(HDHomeRunPacketType.GetSetReply);

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

    private sealed class ScriptedDuplexStream(params byte[][] responses) : Stream
    {
        private readonly Queue<byte> _responseBytes = new(responses.SelectMany(static response => response));

        public List<byte[]> Writes { get; } = [];

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = Math.Min(buffer.Length, _responseBytes.Count);
            for (var index = 0; index < count; index++)
            {
                buffer.Span[index] = _responseBytes.Dequeue();
            }

            return ValueTask.FromResult(count);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Writes.Add(buffer.ToArray());
            return ValueTask.CompletedTask;
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ControlledDuplexStream(params byte[][] responses) : Stream
    {
        private TaskCompletionSource _nextChange = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _responseIndex;
        private int _responseOffset;
        private int _releasedResponseCount;
        private int _writeCount;
        private int _readCount;

        public int ReadCount => Volatile.Read(ref _readCount);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public async Task WaitForWriteCountAsync(int count)
        {
            while (true)
            {
                var change = Volatile.Read(ref _nextChange).Task;
                if (Volatile.Read(ref _writeCount) >= count)
                {
                    return;
                }
                await change.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            }
        }

        public void ReleaseResponse()
        {
            Interlocked.Increment(ref _releasedResponseCount);
            SignalChange();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (true)
            {
                var change = Volatile.Read(ref _nextChange).Task;
                if (Volatile.Read(ref _releasedResponseCount) > _responseIndex)
                {
                    break;
                }
                await change.WaitAsync(cancellationToken);
            }

            buffer.Span[0] = responses[_responseIndex][_responseOffset++];
            Interlocked.Increment(ref _readCount);
            if (_responseOffset == responses[_responseIndex].Length)
            {
                _responseIndex++;
                _responseOffset = 0;
            }
            return 1;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _writeCount);
            SignalChange();
            return ValueTask.CompletedTask;
        }

        private void SignalChange()
        {
            var old = Interlocked.Exchange(ref _nextChange, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            old.TrySetResult();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class HangingDuplexStream(byte[] prefix) : Stream
    {
        private int _position;

        /// <summary>Gets the signal that all supplied bytes were consumed and reading is blocked.</summary>
        public TaskCompletionSource ReadBlocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool WasDisposed { get; private set; }

        public int ReadCount => _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position < prefix.Length)
            {
                buffer.Span[0] = prefix[_position++];
                return 1;
            }

            ReadBlocked.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        /// <inheritdoc/>
        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
