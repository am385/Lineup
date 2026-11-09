using System.Net;
using System.Net.Sockets;
using System.Text;
using Lineup.Plugin.SiliconDust.HDHomeRun.DiscoveryHost;
using Xunit;

namespace Lineup.Plugin.SiliconDust.HDHomeRun.VirtualTuner.Tests;

/// <summary>
/// Verifies SSDP M-SEARCH parsing and response generation.
/// </summary>
public class SsdpDiscoveryProtocolTests
{
    /// <summary>Verifies the worker socket policy permits shared binding and accepts host-local multicast on Windows.</summary>
    [Fact]
    public void BoundSocket_DefaultPolicy_UsesPlatformAppropriateMulticastLoopback()
    {
        // Arrange
        using var listener = SsdpDiscoverySocket.CreateBoundListener(IPAddress.Any, 0);
        var endpoint = Assert.IsType<IPEndPoint>(listener.Client.LocalEndPoint);

        // Act
        using var companion = SsdpDiscoverySocket.CreateBoundListener(IPAddress.Any, endpoint.Port);

        // Assert
        Assert.False(listener.ExclusiveAddressUse);
        Assert.False(companion.ExclusiveAddressUse);
        Assert.Equal(OperatingSystem.IsWindows(), listener.MulticastLoopback);
        Assert.Equal(endpoint.Port, Assert.IsType<IPEndPoint>(companion.Client.LocalEndPoint).Port);
    }

    /// <summary>Verifies a genuinely exclusive owner causes a socket failure rather than a falsely healthy shared listener.</summary>
    [Fact]
    public void BoundSocket_ExclusiveOwner_RejectsSharedContender()
    {
        // Arrange
        using var owner = SsdpDiscoverySocket.CreateBoundListener(IPAddress.Any, 0, allowAddressSharing: false);
        var endpoint = Assert.IsType<IPEndPoint>(owner.Client.LocalEndPoint);

        // Act
        var exception = Assert.Throws<SocketException>(() => SsdpDiscoverySocket.CreateBoundListener(IPAddress.Any, endpoint.Port));

        // Assert
        Assert.Contains(exception.SocketErrorCode, new[] { SocketError.AccessDenied, SocketError.AddressAlreadyInUse });
    }

    /// <summary>Verifies shared multicast reaches both listeners and the worker answers a same-host search in either bind order.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReceiveAsync_SharedMulticastSearch_RepliesToLocalClient(bool workerBindsFirst)
    {
        // Arrange
        var group = IPAddress.Parse("239.255.255.250");
        using var first = SsdpDiscoverySocket.CreateBoundListener(IPAddress.Any, 0);
        var port = Assert.IsType<IPEndPoint>(first.Client.LocalEndPoint).Port;
        using var second = SsdpDiscoverySocket.CreateBoundListener(IPAddress.Any, port);
        var listener = workerBindsFirst ? first : second;
        var companion = workerBindsFirst ? second : first;
        listener.JoinMulticastGroup(group, IPAddress.Loopback);
        companion.JoinMulticastGroup(group, IPAddress.Loopback);
        using var sender = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        sender.MulticastLoopback = true;
        sender.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, IPAddress.Loopback.GetAddressBytes());
        sender.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 0);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromSeconds(5));
        var loop = SsdpDiscoveryListener.ReceiveAsync(listener, [CreateDevice()], _ => true, cancellation.Token);
        var request = Encoding.ASCII.GetBytes(CreateRequest(SsdpDiscoveryProtocol.RootDeviceTarget));
        try
        {
            // Act
            await sender.SendAsync(request, new IPEndPoint(group, port), cancellation.Token);
            var receivedResponse = await sender.ReceiveAsync(cancellation.Token);
            var companionRequest = await companion.ReceiveAsync(cancellation.Token);

            // Assert
            Assert.Equal(request, companionRequest.Buffer);
            var response = Encoding.ASCII.GetString(receivedResponse.Buffer);
            Assert.StartsWith("HTTP/1.1 200 OK\r\n", response);
            Assert.Contains("LOCATION: http://lineup.local:8080/device.xml\r\n", response, StringComparison.Ordinal);
            Assert.Contains($"ST: {SsdpDiscoveryProtocol.RootDeviceTarget}\r\n", response, StringComparison.Ordinal);
        }
        finally
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop);
        }
    }

    /// <summary>Verifies notifications, responses, malformed searches, and unsupported targets never consume response permits.</summary>
    [Theory]
    [InlineData("alive")]
    [InlineData("byebye")]
    [InlineData("response")]
    [InlineData("malformed")]
    [InlineData("unsupported")]
    public async Task ReceiveAsync_NonSearchTraffic_DoesNotConsumeResponsePermits(string traffic)
    {
        // Arrange
        using var listener = SsdpDiscoverySocket.CreateBoundListener(IPAddress.Loopback, 0);
        using var sender = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var endpoint = Assert.IsType<IPEndPoint>(listener.Client.LocalEndPoint);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromSeconds(5));
        var permitAttempts = 0;
        var loop = SsdpDiscoveryListener.ReceiveAsync(listener, [CreateDevice()], _ =>
        {
            Interlocked.Increment(ref permitAttempts);
            return true;
        }, cancellation.Token);
        var noise = traffic switch
        {
            "alive" => SsdpDiscoveryProtocol.FormatNotification(SsdpDiscoveryProtocol.RootDeviceTarget, CreateDevice(), alive: true),
            "byebye" => SsdpDiscoveryProtocol.FormatNotification(SsdpDiscoveryProtocol.RootDeviceTarget, CreateDevice(), alive: false),
            "response" => "HTTP/1.1 200 OK\r\nST: upnp:rootdevice\r\n\r\n",
            "unsupported" => CreateRequest("urn:example:unsupported"),
            _ => "M-SEARCH * HTTP/1.1\r\nST: ssdp:all\r\n\r\n"
        };
        try
        {
            // Act
            for (var index = 0; index < 12; index++)
            {
                await sender.SendAsync(Encoding.ASCII.GetBytes(noise), endpoint, cancellation.Token);
            }
            await sender.SendAsync(Encoding.ASCII.GetBytes(CreateRequest(SsdpDiscoveryProtocol.RootDeviceTarget)), endpoint, cancellation.Token);
            var response = await sender.ReceiveAsync(cancellation.Token);

            // Assert
            Assert.StartsWith("HTTP/1.1 200 OK\r\n", Encoding.ASCII.GetString(response.Buffer));
            Assert.Equal(1, Volatile.Read(ref permitAttempts));
        }
        finally
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop);
        }
    }

    /// <summary>Verifies supported searches still require a response permit and cancellation stops the actual receive loop.</summary>
    [Fact]
    public async Task ReceiveAsync_DeniedSearch_DoesNotReplyBeforeNextAllowedSearch()
    {
        // Arrange
        using var listener = SsdpDiscoverySocket.CreateBoundListener(IPAddress.Loopback, 0);
        using var sender = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var endpoint = Assert.IsType<IPEndPoint>(listener.Client.LocalEndPoint);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromSeconds(5));
        var permitAttempts = 0;
        var loop = SsdpDiscoveryListener.ReceiveAsync(listener, [CreateDevice()], _ => Interlocked.Increment(ref permitAttempts) > 1, cancellation.Token);
        try
        {
            // Act
            await sender.SendAsync(Encoding.ASCII.GetBytes(CreateRequest(SsdpDiscoveryProtocol.RootDeviceTarget)), endpoint, cancellation.Token);
            await sender.SendAsync(Encoding.ASCII.GetBytes(CreateRequest(SsdpDiscoveryProtocol.MediaServerTarget)), endpoint, cancellation.Token);
            var response = await sender.ReceiveAsync(cancellation.Token);

            // Assert
            Assert.Equal(2, Volatile.Read(ref permitAttempts));
            Assert.Contains($"ST: {SsdpDiscoveryProtocol.MediaServerTarget}\r\n", Encoding.ASCII.GetString(response.Buffer), StringComparison.Ordinal);
        }
        finally
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop);
        }
    }

    /// <summary>
    /// Verifies that the runtime SSDP socket receives and answers a loopback M-SEARCH datagram.
    /// </summary>
    [Fact]
    public async Task BoundSocket_LoopbackSearch_RoundTripsResponse()
    {
        // Arrange
        using var listener = SsdpDiscoverySocket.CreateBoundListener(IPAddress.Loopback, 0);
        using var sender = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        // Act
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        // Assert
        var listenerEndpoint = Assert.IsType<IPEndPoint>(listener.Client.LocalEndPoint);
        var request = CreateRequest(SsdpDiscoveryProtocol.RootDeviceTarget);

        await sender.SendAsync(Encoding.ASCII.GetBytes(request), listenerEndpoint, timeout.Token);
        var receivedRequest = await listener.ReceiveAsync(timeout.Token);
        var response = Assert.Single(SsdpDiscoveryProtocol.CreateSearchResponses(Encoding.ASCII.GetString(receivedRequest.Buffer), CreateDevice()));
        await listener.SendAsync(Encoding.ASCII.GetBytes(response), receivedRequest.RemoteEndPoint, timeout.Token);
        var receivedResponse = await sender.ReceiveAsync(timeout.Token);

        Assert.False(listener.ExclusiveAddressUse);
        Assert.StartsWith("HTTP/1.1 200 OK", Encoding.ASCII.GetString(receivedResponse.Buffer));
    }

    /// <summary>
    /// Verifies that an exclusive listener can own an available Windows-style SSDP endpoint deterministically.
    /// </summary>
    [Fact]
    public void BoundSocket_ExclusiveMode_DisablesAddressSharing()
    {

        // Arrange
        // Act
        using var listener = SsdpDiscoverySocket.CreateBoundListener(IPAddress.Loopback, 0, allowAddressSharing: false);

        // Assert
        Assert.True(listener.ExclusiveAddressUse);
        Assert.NotNull(listener.Client.LocalEndPoint);
    }

    /// <summary>
    /// Verifies that a root-device search receives a device-description response.
    /// </summary>
    [Fact]
    public void CreateSearchResponses_RootDeviceSearch_FormatsResponse()
    {
        // Arrange
        var request = CreateRequest(SsdpDiscoveryProtocol.RootDeviceTarget);

        // Act
        var responses = SsdpDiscoveryProtocol.CreateSearchResponses(request, CreateDevice());

        // Assert
        var response = Assert.Single(responses);
        Assert.StartsWith("HTTP/1.1 200 OK\r\n", response);
        Assert.Contains("LOCATION: http://lineup.local:8080/device.xml\r\n", response);
        Assert.Contains("ST: upnp:rootdevice\r\n", response);
        Assert.Contains("USN: uuid:12345678::upnp:rootdevice\r\n", response);
        Assert.EndsWith("\r\n\r\n", response);
    }

    /// <summary>
    /// Verifies that ssdp:all produces advertisements for each supported device target.
    /// </summary>
    [Fact]
    public void CreateSearchResponses_AllSearch_ReturnsSupportedTargets()
    {
        // Arrange
        var request = CreateRequest("ssdp:all");

        // Act
        var responses = SsdpDiscoveryProtocol.CreateSearchResponses(request, CreateDevice());

        // Assert
        Assert.Equal(2, responses.Count);
        Assert.Contains(responses, response => response.Contains($"ST: {SsdpDiscoveryProtocol.RootDeviceTarget}\r\n"));
        Assert.Contains(responses, response => response.Contains($"ST: {SsdpDiscoveryProtocol.MediaServerTarget}\r\n"));
    }

    /// <summary>
    /// Verifies that unsupported and malformed searches do not produce responses.
    /// </summary>
    [Theory]
    [InlineData("urn:example:unsupported")]
    [InlineData("")]
    public void CreateSearchResponses_UnsupportedSearch_ReturnsNoResponses(string target)
    {
        // Arrange
        var request = CreateRequest(target);

        // Act
        var responses = SsdpDiscoveryProtocol.CreateSearchResponses(request, CreateDevice());

        // Assert
        Assert.Empty(responses);
    }

    /// <summary>
    /// Verifies that malformed and non-search datagrams fail cheap SSDP prevalidation.
    /// </summary>
    [Theory]
    [InlineData("NOTIFY * HTTP/1.1\r\n\r\n")]
    [InlineData("M-SEARCH * HTTP/1.1\r\nST: ssdp:all\r\n\r\n")]
    [InlineData("not-httpu")]
    public void TryParseSearch_MalformedOrNonSearchDatagram_ReturnsFalse(string request)
    {
        // Arrange
        // Act
        var valid = SsdpDiscoveryProtocol.TryParseSearch(request, out var target);

        // Assert
        Assert.False(valid);
        Assert.Empty(target);
    }

    private static string CreateRequest(string target)
    {
        return $"M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nST: {target}\r\nMX: 2\r\n\r\n";
    }

    private static HdHomeRunAdvertisedDevice CreateDevice()
    {
        return new HdHomeRunAdvertisedDevice
        {
            DeviceId = 0x12345678,
            DeviceAuth = "virtual-auth",
            TunerCount = 4,
            BaseUri = new Uri("http://lineup.local:8080/")
        };
    }
}
