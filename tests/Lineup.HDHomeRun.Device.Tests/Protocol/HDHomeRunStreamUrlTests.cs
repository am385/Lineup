using System.Net;
using Lineup.HDHomeRun.Device.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Lineup.HDHomeRun.Device.Tests.Protocol;

/// <summary>
/// Tests HDHomeRun stream URL construction.
/// </summary>
public class HDHomeRunStreamUrlTests
{
    /// <summary>
    /// Verifies public HTTP control operations propagate caller cancellation.
    /// </summary>
    /// <param name="operation">The operation to invoke.</param>
    [Theory]
    [InlineData("discover")]
    [InlineData("statuses")]
    [InlineData("status")]
    [InlineData("tune")]
    [InlineData("lineup")]
    [InlineData("restart")]
    public async Task PublicOperation_WhenCallerCancels_PropagatesCancellation(string operation)
    {
        // Arrange
        using var httpClient = new HttpClient(new CancelableHandler());
        using var control = new HDHomeRunHttpControl("http://192.0.2.10", NullLogger<HDHomeRunHttpControl>.Instance, httpClient);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();

        // Act
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => InvokeAsync(control, operation, cancellation.Token));

        // Assert
        Assert.NotNull(exception);
        Assert.True(cancellation.IsCancellationRequested);
    }

    /// <summary>
    /// Verifies the HTTP control replaces the discovery port and escapes path and query values.
    /// </summary>
    [Fact]
    public void GetStreamUrl_UsesStreamingPort_AndEscapesValues()
    {
        // Arrange
        using var control = new HDHomeRunHttpControl("http://192.0.2.10:80", NullLogger<HDHomeRunHttpControl>.Instance, new HttpClient());

        // Act
        var result = control.GetStreamUrl("7/1 HD", "mobile/low");

        // Assert
        Assert.Equal("http://192.0.2.10:5004/auto/v7%2F1%20HD?transcode=mobile%2Flow", result);
    }

    /// <summary>
    /// Verifies the device helper formats an IPv6 streaming authority correctly.
    /// </summary>
    [Fact]
    public void GetHttpStreamUrl_WithIpv6Address_UsesBracketedStreamingAuthority()
    {
        // Arrange
        var deviceInfo = new HDHomeRunDiscoveredDevice
        {
            IpAddress = IPAddress.Parse("2001:db8::1234"),
            DeviceId = 0x12345678,
            DeviceType = HDHomeRunDeviceType.Tuner,
            BaseUrl = "http://[2001:db8::1234]:80"
        };
        using var loggerFactory = NullLoggerFactory.Instance;
        var httpControlFactory = Substitute.For<IHDHomeRunHttpControlFactory>();
        using var device = new HDHomeRunDevice(deviceInfo, loggerFactory, httpControlFactory);

        // Act
        var result = device.GetHttpStreamUrl("12.3");

        // Assert
        Assert.Equal("http://[2001:db8::1234]:5004/auto/v12.3", result);
    }

    private static async Task InvokeAsync(HDHomeRunHttpControl control, string operation, CancellationToken cancellationToken)
    {
        switch (operation)
        {
            case "discover":
                await control.DiscoverAsync(cancellationToken);
                break;
            case "statuses":
                await control.GetTunerStatusAsync(cancellationToken);
                break;
            case "status":
                await control.GetTunerStatusAsync(0, cancellationToken);
                break;
            case "tune":
                await control.TuneChannelAsync(0, "7.1", cancellationToken);
                break;
            case "lineup":
                await control.GetLineupAsync(cancellationToken);
                break;
            case "restart":
                await control.RestartAsync(cancellationToken);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation));
        }
    }

    private sealed class CancelableHandler : HttpMessageHandler
    {
        /// <inheritdoc/>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        }
    }
}
