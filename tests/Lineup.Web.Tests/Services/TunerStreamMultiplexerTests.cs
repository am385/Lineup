using System.Net;
using Lineup.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Lineup.Web.Tests.Services;

/// <summary>
/// Verifies shared tuner input fan-out.
/// </summary>
public class TunerStreamMultiplexerTests
{
    /// <summary>
    /// Verifies that concurrent subscribers receive the same data through one upstream request.
    /// </summary>
    [Fact]
    public async Task ConcurrentSubscribers_UseOneUpstreamRequest()
    {
        // Arrange
        // Act
        var responseSource = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new ControlledResponseHandler(responseSource.Task);
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("StreamProxy").Returns(new HttpClient(handler, disposeHandler: false));
        var multiplexer = new TunerStreamMultiplexer(httpClientFactory, NullLogger<TunerStreamMultiplexer>.Instance);
        var sourceUri = new Uri("http://tuner/auto/v2.1");
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var first = await multiplexer.SubscribeAsync(sourceUri, cancellationToken);
        await using var second = await multiplexer.SubscribeAsync(sourceUri, cancellationToken);
        var payload = Enumerable.Range(0, 188 * 20).Select(index => (byte)(index % 251)).ToArray();

        responseSource.SetResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload)
        });
        var firstPayload = new byte[payload.Length];
        var secondPayload = new byte[payload.Length];
        await first.ReadExactlyAsync(firstPayload, cancellationToken);
        await second.ReadExactlyAsync(secondPayload, cancellationToken);

        // Assert
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(payload, firstPayload);
        Assert.Equal(payload, secondPayload);
    }

    /// <summary>
    /// Verifies that normal FFmpeg startup buffering does not disconnect a temporarily delayed subscriber.
    /// </summary>
    [Fact]
    public async Task DelayedSubscriber_ReceivesBurstLargerThanLegacyBuffer()
    {
        // Arrange
        // Act
        var responseSource = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new ControlledResponseHandler(responseSource.Task);
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("StreamProxy").Returns(new HttpClient(handler, disposeHandler: false));
        var multiplexer = new TunerStreamMultiplexer(httpClientFactory, NullLogger<TunerStreamMultiplexer>.Instance);
        var payload = Enumerable.Range(0, 8 * 1024 * 1024).Select(index => (byte)(index % 251)).ToArray();
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var subscription = await multiplexer.SubscribeAsync(new Uri("http://tuner/auto/v2.1"), cancellationToken);

        responseSource.SetResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload)
        });
        await Task.Delay(100, cancellationToken);
        var receivedPayload = new byte[payload.Length];
        await subscription.ReadExactlyAsync(receivedPayload, cancellationToken);

        // Assert
        Assert.Equal(payload, receivedPayload);
    }

    /// <summary>
    /// Verifies that a subscriber cannot join a source whose last subscriber has initiated shutdown.
    /// </summary>
    [Fact]
    public async Task SubscribeAfterLastUnsubscribe_CreatesNewUpstreamRequest()
    {
        // Arrange
        var handler = new BlockingCancellationHandler();
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("StreamProxy").Returns(new HttpClient(handler, disposeHandler: false));
        var multiplexer = new TunerStreamMultiplexer(httpClientFactory, NullLogger<TunerStreamMultiplexer>.Instance);
        var sourceUri = new Uri("http://tuner/auto/v2.1");
        var cancellationToken = TestContext.Current.CancellationToken;
        var first = await multiplexer.SubscribeAsync(sourceUri, cancellationToken);
        await handler.WaitForRequestCountAsync(1, cancellationToken);

        // Act
        await first.DisposeAsync();
        await handler.WaitForCancellationAsync(cancellationToken);
        var second = await multiplexer.SubscribeAsync(sourceUri, cancellationToken);
        await handler.WaitForRequestCountAsync(2, cancellationToken);

        // Assert
        Assert.Equal(2, handler.RequestCount);

        handler.AllowCompletion();
        await second.DisposeAsync();
    }

    /// <summary>
    /// Verifies that evicting the final slow subscriber stops and replaces the upstream source.
    /// </summary>
    [Fact]
    public async Task FinalSlowSubscriberEviction_CreatesNewUpstreamRequest()
    {
        // Arrange
        var handler = new EndlessResponseHandler();
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("StreamProxy").Returns(new HttpClient(handler, disposeHandler: false));
        var multiplexer = new TunerStreamMultiplexer(httpClientFactory, NullLogger<TunerStreamMultiplexer>.Instance);
        var sourceUri = new Uri("http://tuner/auto/v2.1");
        var cancellationToken = TestContext.Current.CancellationToken;
        var first = await multiplexer.SubscribeAsync(sourceUri, cancellationToken);

        // Act
        await handler.WaitForCancellationAsync(cancellationToken);
        var second = await multiplexer.SubscribeAsync(sourceUri, cancellationToken);
        await handler.WaitForRequestCountAsync(2, cancellationToken);

        // Assert
        Assert.Equal(2, handler.RequestCount);

        await first.DisposeAsync();
        await second.DisposeAsync();
    }

    private sealed class ControlledResponseHandler(Task<HttpResponseMessage> responseTask) : HttpMessageHandler
    {
        private int _requestCount;

        /// <summary>
        /// Gets request count.
        /// </summary>
        public int RequestCount => _requestCount;

        /// <summary>
        /// Performs the send operation.
        /// </summary>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            return await responseTask.WaitAsync(cancellationToken);
        }
    }

    private sealed class BlockingCancellationHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _cancellationObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _completionAllowed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _requestCount;

        /// <summary>
        /// Gets request count.
        /// </summary>
        public int RequestCount => _requestCount;

        /// <summary>
        /// Allows canceled requests to finish.
        /// </summary>
        public void AllowCompletion() => _completionAllowed.TrySetResult();

        /// <summary>
        /// Waits until the specified number of requests have started.
        /// </summary>
        public async Task WaitForRequestCountAsync(int expectedCount, CancellationToken cancellationToken)
        {
            while (RequestCount < expectedCount)
            {
                await Task.Delay(1, cancellationToken);
            }
        }

        /// <summary>
        /// Waits until an upstream request observes cancellation.
        /// </summary>
        public Task WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            return _cancellationObserved.Task.WaitAsync(cancellationToken);
        }

        /// <summary>
        /// Performs the send operation.
        /// </summary>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _cancellationObserved.TrySetResult();
                await _completionAllowed.Task;
                throw;
            }

            throw new InvalidOperationException("The controlled request unexpectedly completed.");
        }
    }

    private sealed class EndlessResponseHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _cancellationObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _requestCount;

        /// <summary>
        /// Gets request count.
        /// </summary>
        public int RequestCount => _requestCount;

        /// <summary>
        /// Waits until the specified number of requests have started.
        /// </summary>
        public async Task WaitForRequestCountAsync(int expectedCount, CancellationToken cancellationToken)
        {
            while (RequestCount < expectedCount)
            {
                await Task.Delay(1, cancellationToken);
            }
        }

        /// <summary>
        /// Waits until an upstream request observes cancellation.
        /// </summary>
        public Task WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            return _cancellationObserved.Task.WaitAsync(cancellationToken);
        }

        /// <summary>
        /// Performs the send operation.
        /// </summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new EndlessStream(_cancellationObserved))
            };
            return Task.FromResult(response);
        }
    }

    private sealed class EndlessStream(TaskCompletionSource cancellationObserved) : Stream
    {
        private CancellationTokenRegistration _cancellationRegistration;

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
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_cancellationRegistration == default)
            {
                _cancellationRegistration = cancellationToken.Register(() => cancellationObserved.TrySetResult());
            }

            await Task.Yield();
            buffer.Span.Fill(1);
            return buffer.Length;
        }

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _cancellationRegistration.Dispose();
            }

            base.Dispose(disposing);
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
