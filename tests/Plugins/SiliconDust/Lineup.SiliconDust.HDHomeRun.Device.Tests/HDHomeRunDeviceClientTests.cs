using Lineup.SiliconDust.HDHomeRun.Device.Models;
using Microsoft.Extensions.Logging;
using NSubstitute;
using RichardSzalay.MockHttp;
using System.Text.Json;
using Xunit;

namespace Lineup.SiliconDust.HDHomeRun.Device.Tests;

/// <summary>
/// Represents hd home run device client tests.
/// </summary>
public class HDHomeRunDeviceClientTests
{
    private readonly ILogger<HDHomeRunDeviceClient> _logger;
    private readonly MockHttpMessageHandler _mockHttp;
    private readonly HttpClient _httpClient;

    /// <summary>
    /// Initializes a new instance of the <see cref="HDHomeRunDeviceClientTests"/> class.
    /// </summary>
    public HDHomeRunDeviceClientTests()
    {
        _logger = Substitute.For<ILogger<HDHomeRunDeviceClient>>();
        _mockHttp = new MockHttpMessageHandler();
        _httpClient = _mockHttp.ToHttpClient();
        _httpClient.BaseAddress = new Uri("http://hdhomerun.local/");
    }

    /// <summary>
    /// Performs the discover device async_returns device info_when api returns valid response operation.
    /// </summary>
    [Fact]
    public async Task DiscoverDeviceAsync_ReturnsDeviceInfo_WhenApiReturnsValidResponse()
    {
        // Arrange
        var expectedDeviceInfo = new HDHomeRunDeviceInfo
        {
            FriendlyName = "HDHomeRun FLEX 4K",
            ModelNumber = "HDFX-4K",
            FirmwareName = "hdhomerun_dvr",
            FirmwareVersion = "20231015",
            DeviceID = "12345678",
            DeviceAuth = "test-device-auth-token",
            BaseURL = "http://10.0.0.10",
            LineupURL = "http://10.0.0.10/lineup.json",
            TunerCount = 4
        };

        _mockHttp.When("http://hdhomerun.local/discover.json")
            .Respond("application/json", JsonSerializer.Serialize(expectedDeviceInfo));

        var client = new HDHomeRunDeviceClient(_logger, _httpClient);

        // Act
        var result = await client.DiscoverDeviceAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(expectedDeviceInfo.FriendlyName, result.FriendlyName);
        Assert.Equal(expectedDeviceInfo.DeviceAuth, result.DeviceAuth);
    }

    /// <summary>
    /// Performs the discover device async_throws exception_when api returns invalid json operation.
    /// </summary>
    [Fact]
    public async Task DiscoverDeviceAsync_ThrowsException_WhenApiReturnsInvalidJson()
    {
        // Arrange
        _mockHttp.When("http://hdhomerun.local/discover.json")
            .Respond("application/json", "invalid json content");

        var client = new HDHomeRunDeviceClient(_logger, _httpClient);

        // Act
        // Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.DiscoverDeviceAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Performs the fetch channel lineup async_returns channels_when api returns valid response operation.
    /// </summary>
    [Fact]
    public async Task FetchChannelLineupAsync_ReturnsChannels_WhenApiReturnsValidResponse()
    {
        // Arrange
        var expectedChannels = new List<HDHomeRunChannel>
        {
            new() { GuideNumber = "2.1", GuideName = "WFMY-HD", URL = "http://device/auto/v2.1" },
            new() { GuideNumber = "5.1", GuideName = "WRAL-HD", URL = "http://device/auto/v5.1" },
            new() { GuideNumber = "11.1", GuideName = "WTVD-HD", URL = "http://device/auto/v11.1" }
        };

        _mockHttp.When("http://hdhomerun.local/lineup.json")
            .Respond("application/json", JsonSerializer.Serialize(expectedChannels));

        var client = new HDHomeRunDeviceClient(_logger, _httpClient);

        // Act
        var result = await client.FetchChannelLineupAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(3, result.Count);
        Assert.Equal("2.1", result[0].GuideNumber);
        Assert.Equal("WFMY-HD", result[0].GuideName);
    }

    /// <summary>
    /// Performs the fetch channel lineup async_returns empty list_when api returns empty array operation.
    /// </summary>
    [Fact]
    public async Task FetchChannelLineupAsync_ReturnsEmptyList_WhenApiReturnsEmptyArray()
    {
        // Arrange
        _mockHttp.When("http://hdhomerun.local/lineup.json")
            .Respond("application/json", "[]");

        var client = new HDHomeRunDeviceClient(_logger, _httpClient);

        // Act
        var result = await client.FetchChannelLineupAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    /// <summary>
    /// Performs the fetch channel lineup async_throws exception_when api returns invalid json operation.
    /// </summary>
    [Fact]
    public async Task FetchChannelLineupAsync_ThrowsException_WhenApiReturnsInvalidJson()
    {
        // Arrange
        _mockHttp.When("http://hdhomerun.local/lineup.json")
            .Respond("application/json", "not valid json");

        var client = new HDHomeRunDeviceClient(_logger, _httpClient);

        // Act
        // Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.FetchChannelLineupAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Performs the discover device auth async_returns device auth_when device info is valid operation.
    /// </summary>
    [Fact]
    public async Task DiscoverDeviceAuthAsync_ReturnsDeviceAuth_WhenDeviceInfoIsValid()
    {
        // Arrange
        var deviceInfo = new HDHomeRunDeviceInfo
        {
            FriendlyName = "Test Device",
            ModelNumber = "TEST-1",
            FirmwareName = "test",
            FirmwareVersion = "1.0",
            DeviceID = "ABCD1234",
            DeviceAuth = "expected-auth-token",
            BaseURL = "http://10.0.0.1",
            LineupURL = "http://10.0.0.1/lineup.json",
            TunerCount = 2
        };

        _mockHttp.When("http://hdhomerun.local/discover.json")
            .Respond("application/json", JsonSerializer.Serialize(deviceInfo));

        var client = new HDHomeRunDeviceClient(_logger, _httpClient);

        // Act
        var result = await client.DiscoverDeviceAuthAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("expected-auth-token", result);
    }

    /// <summary>
    /// Verifies that caller cancellation promptly cancels device discovery without wrapping it.
    /// </summary>
    [Fact]
    public async Task DiscoverDeviceAsync_CallerCanceled_ThrowsOperationCanceledException()
    {
        // Arrange
        var handler = new BlockingHandler();
        var client = new HDHomeRunDeviceClient(_logger, new HttpClient(handler)
        {
            BaseAddress = new Uri("http://hdhomerun.local/")
        });
        using var cancellation = new CancellationTokenSource();

        // Act
        var discovery = client.DiscoverDeviceAsync(cancellation.Token);
        await handler.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        cancellation.Cancel();

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => discovery);
    }

    /// <summary>
    /// Verifies that caller cancellation promptly cancels lineup retrieval without wrapping it.
    /// </summary>
    [Fact]
    public async Task FetchChannelLineupAsync_CallerCanceled_ThrowsOperationCanceledException()
    {
        // Arrange
        var handler = new BlockingHandler();
        var client = new HDHomeRunDeviceClient(_logger, new HttpClient(handler)
        {
            BaseAddress = new Uri("http://hdhomerun.local/")
        });
        using var cancellation = new CancellationTokenSource();

        // Act
        var fetch = client.FetchChannelLineupAsync(cancellation.Token);
        await handler.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        cancellation.Cancel();

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetch);
    }

    /// <summary>Verifies an HTTP client's own deadline becomes a recoverable device failure instead of caller cancellation.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ControlRequest_HttpTimeout_WrapsIndependentCancellation(bool discovery, bool body)
    {
        // Arrange
        using var http = new HttpClient(body ? new BlockingBodyHandler() : new BlockingHandler())
        {
            BaseAddress = new Uri("http://hdhomerun.local/"),
            Timeout = TimeSpan.FromMilliseconds(100)
        };
        var client = new HDHomeRunDeviceClient(_logger, http);

        // Act
        var exception = await Record.ExceptionAsync(async () =>
        {
            if (discovery)
            {
                await client.DiscoverDeviceAsync(TestContext.Current.CancellationToken);
            }
            else
            {
                await client.FetchChannelLineupAsync(TestContext.Current.CancellationToken);
            }
        });

        // Assert
        var failure = Assert.IsType<InvalidOperationException>(exception);
        Assert.IsAssignableFrom<OperationCanceledException>(failure.InnerException);
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
    }

    /// <summary>Verifies caller cancellation of a response body is not translated into a device timeout.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ControlRequest_BodyCanceled_PropagatesCallerCancellation(bool discovery)
    {
        // Arrange
        var handler = new BlockingBodyHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://hdhomerun.local/") };
        var client = new HDHomeRunDeviceClient(_logger, http);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        Task operation = discovery ? client.DiscoverDeviceAsync(cancellation.Token) : client.FetchChannelLineupAsync(cancellation.Token);
        await handler.Started.Task.WaitAsync(TestContext.Current.CancellationToken);

        // Act
        await cancellation.CancelAsync();
        var exception = await Record.ExceptionAsync(() => operation);

        // Assert
        Assert.IsAssignableFrom<OperationCanceledException>(exception);
        Assert.True(cancellation.IsCancellationRequested);
    }

    private sealed class BlockingBodyHandler : HttpMessageHandler
    {
        /// <summary>Gets the signal raised when JSON body reading begins.</summary>
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <inheritdoc />
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StreamContent(new BlockingBodyStream(Started)) });
    }

    private sealed class BlockingBodyStream(TaskCompletionSource started) : Stream
    {
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
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
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

    private sealed class BlockingHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <inheritdoc/>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The canceled request unexpectedly continued.");
        }
    }
}
