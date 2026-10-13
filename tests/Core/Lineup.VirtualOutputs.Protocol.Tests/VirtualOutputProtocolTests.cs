using Lineup.Common.Streaming;
using Xunit;

namespace Lineup.VirtualOutputs.Protocol.Tests;

/// <summary>Verifies isolated virtual-output protocol negotiation and boundary validation.</summary>
public sealed class VirtualOutputProtocolTests
{
    /// <summary>Verifies negotiation selects the newest mutually supported version.</summary>
    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(1, 2, 1)]
    [InlineData(2, 3, null)]
    [InlineData(2, 1, null)]
    public void Negotiate_PeerRange_ReturnsNewestCompatibleVersion(int minimum, int maximum, int? expected)
    {
        // Act
        var actual = VirtualOutputProtocol.Negotiate(minimum, maximum);

        // Assert
        Assert.Equal(expected, actual);
    }

    /// <summary>Verifies invalid or incompatible worker registration is rejected before authentication.</summary>
    [Fact]
    public void Validate_InvalidRegistration_ReturnsAllBoundaryErrors()
    {
        // Arrange
        var request = new VirtualOutputWorkerRegistrationRequest
        {
            WorkerId = string.Empty,
            VirtualDeviceId = new VirtualDeviceId(Guid.Empty),
            Generation = 0,
            BootstrapToken = string.Empty,
            MinimumProtocolVersion = 2,
            MaximumProtocolVersion = 1
        };

        // Act
        var errors = VirtualOutputProtocolValidation.Validate(request);

        // Assert
        Assert.Equal(5, errors.Count);
    }

    /// <summary>Verifies stream requests require authenticated identities and a bounded channel key.</summary>
    [Fact]
    public void Validate_InvalidStreamRequest_ReturnsAllBoundaryErrors()
    {
        // Arrange
        var request = new VirtualOutputOpenStreamRequest
        {
            Session = new VirtualOutputSessionContext
            {
                SessionId = new VirtualOutputWorkerSessionId(Guid.Empty),
                Generation = 0
            },
            RequestId = new VirtualOutputStreamRequestId(Guid.Empty),
            ChannelKey = new string('x', VirtualOutputProtocol.MaximumChannelKeyLength + 1),
            Client = new VirtualOutputClientContext
            {
                AuthenticatedClientId = new string('i', VirtualOutputProtocol.MaximumAuthenticatedClientIdLength + 1),
                AuthenticatedClientName = new string('n', VirtualOutputProtocol.MaximumAuthenticatedClientNameLength + 1)
            }
        };

        // Act
        var errors = VirtualOutputProtocolValidation.Validate(request);

        // Assert
        Assert.Equal(6, errors.Count);
    }

    /// <summary>Verifies framed control messages leave following stream bytes available to the caller.</summary>
    [Fact]
    public async Task WireProtocol_ReadControlMessage_PreservesFollowingBody()
    {
        // Arrange
        await using var stream = new MemoryStream();
        var response = VirtualOutputIpcResponse.Success(new VirtualOutputOpenStreamResponse
        {
            Accepted = true,
            ContentType = "video/mp2t",
            StreamSessionId = StreamSessionId.New()
        });
        await VirtualOutputWireProtocol.WriteAsync(stream, response, TestContext.Current.CancellationToken);
        await stream.WriteAsync(new byte[] { 1, 2, 3 }, TestContext.Current.CancellationToken);
        stream.Position = 0;

        // Act
        var actual = await VirtualOutputWireProtocol.ReadAsync<VirtualOutputIpcResponse>(stream, TestContext.Current.CancellationToken);
        var body = new byte[3];
        await stream.ReadExactlyAsync(body, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(actual.Succeeded);
        Assert.Equal([1, 2, 3], body);
    }

    /// <summary>Verifies ordinary control messages remain constrained to the default one-mebibyte boundary.</summary>
    [Fact]
    public async Task WireProtocol_DefaultLimit_RejectsLargeMessage()
    {
        // Arrange
        await using var stream = new MemoryStream();
        var message = new string('x', VirtualOutputProtocol.MaximumControlMessageBytes);

        // Act
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            VirtualOutputWireProtocol.WriteAsync(stream, message, TestContext.Current.CancellationToken));

        // Assert
        Assert.Contains(VirtualOutputProtocol.MaximumControlMessageBytes.ToString(), exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Verifies guide responses may use their larger explicit boundary while remaining framed and bounded.</summary>
    [Fact]
    public async Task WireProtocol_GuideLimit_RoundTripsLargeMessage()
    {
        // Arrange
        await using var stream = new MemoryStream();
        var message = new string('x', VirtualOutputProtocol.MaximumControlMessageBytes + 1);
        await VirtualOutputWireProtocol.WriteAsync(
            stream,
            message,
            VirtualOutputProtocol.MaximumGuideMessageBytes,
            TestContext.Current.CancellationToken);
        stream.Position = 0;

        // Act
        var actual = await VirtualOutputWireProtocol.ReadAsync<string>(
            stream,
            VirtualOutputProtocol.MaximumGuideMessageBytes,
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(message, actual);
    }
}
