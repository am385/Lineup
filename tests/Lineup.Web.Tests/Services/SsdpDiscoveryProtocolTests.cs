using System.Net;
using System.Net.Sockets;
using System.Text;
using Lineup.Web.Services;
using Xunit;

namespace Lineup.Web.Tests.Services;

/// <summary>
/// Verifies SSDP M-SEARCH parsing and response generation.
/// </summary>
public class SsdpDiscoveryProtocolTests
{
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
        var physicalDevice = new Lineup.HDHomeRun.Device.Models.HDHomeRunDeviceInfo
        {
            FriendlyName = "Tuner",
            ModelNumber = "HDHR",
            FirmwareName = "hdhomerun",
            FirmwareVersion = "1",
            DeviceID = "12345678",
            DeviceAuth = string.Empty,
            BaseURL = "http://tuner.local",
            LineupURL = "http://tuner.local/lineup.json",
            TunerCount = 4
        };
        return new HdHomeRunAdvertisedDevice
        {
            Profile = new HdHomeRunProxyProfileSnapshot
            {
                Settings = new HdHomeRunProxyProfileSettings { PhysicalAddress = "tuner.local" },
                PhysicalDevice = physicalDevice,
                IsPrimary = true,
                DeviceId = 0x12345678,
                DeviceAuth = "virtual-auth",
                TunerCount = 4,
                FriendlyName = "Lineup Tuner",
                PhysicalBaseUri = new Uri("http://tuner.local/")
            },
            BaseUri = new Uri("http://lineup.local:8080/")
        };
    }
}
