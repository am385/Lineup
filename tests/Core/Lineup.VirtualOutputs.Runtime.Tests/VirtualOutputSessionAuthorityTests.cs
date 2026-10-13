using Lineup.Common.Streaming;
using Lineup.VirtualOutputs.Protocol;
using Xunit;

namespace Lineup.VirtualOutputs.Runtime.Tests;

/// <summary>Verifies generation ownership, bootstrap authentication, leases, and capability authorization.</summary>
public sealed class VirtualOutputSessionAuthorityTests
{
    /// <summary>Verifies a valid one-time bootstrap creates a capability-scoped session.</summary>
    [Fact]
    public void Register_ValidActivation_AuthorizesGrantedCapability()
    {
        // Arrange
        using var authority = new VirtualOutputSessionAuthority();
        var deviceId = VirtualDeviceId.New();
        var ticket = authority.CreateActivation(
            deviceId,
            "lineup-link",
            new HashSet<VirtualOutputCapability> { VirtualOutputCapability.ReadManifest },
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(1));

        // Act
        var registration = Register(authority, ticket);
        var authorization = authority.Authorize(
            new VirtualOutputSessionContext { SessionId = registration.SessionId!.Value, Generation = ticket.Generation },
            VirtualOutputCapability.ReadManifest);

        // Assert
        Assert.True(registration.Accepted);
        Assert.True(authorization.Authorized);
        Assert.Equal(deviceId, authorization.VirtualDeviceId);
    }

    /// <summary>Verifies a second activation revokes the previous generation and session.</summary>
    [Fact]
    public void CreateActivation_ReplacesGeneration_RevokesPriorSession()
    {
        // Arrange
        using var authority = new VirtualOutputSessionAuthority();
        var deviceId = VirtualDeviceId.New();
        var firstTicket = authority.CreateActivation(
            deviceId,
            "lineup-link",
            new HashSet<VirtualOutputCapability> { VirtualOutputCapability.ReadManifest },
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(1));
        var firstRegistration = Register(authority, firstTicket);

        // Act
        var secondTicket = authority.CreateActivation(
            deviceId,
            "lineup-link",
            new HashSet<VirtualOutputCapability> { VirtualOutputCapability.ReadManifest },
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(1));
        var authorization = authority.Authorize(
            new VirtualOutputSessionContext { SessionId = firstRegistration.SessionId!.Value, Generation = firstTicket.Generation },
            VirtualOutputCapability.ReadManifest);

        // Assert
        Assert.Equal(firstTicket.Generation + 1, secondTicket.Generation);
        Assert.False(authorization.Authorized);
        Assert.Equal(VirtualOutputFailureCode.Unauthorized, authorization.FailureCode);
    }

    /// <summary>Verifies a valid session cannot use a capability that was not granted.</summary>
    [Fact]
    public void Authorize_UngrantedCapability_ReturnsCapabilityDenied()
    {
        // Arrange
        using var authority = new VirtualOutputSessionAuthority();
        var ticket = authority.CreateActivation(
            VirtualDeviceId.New(),
            "lineup-link",
            new HashSet<VirtualOutputCapability> { VirtualOutputCapability.ReadManifest },
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(1));
        var registration = Register(authority, ticket);

        // Act
        var authorization = authority.Authorize(
            new VirtualOutputSessionContext { SessionId = registration.SessionId!.Value, Generation = ticket.Generation },
            VirtualOutputCapability.OpenStream);

        // Assert
        Assert.False(authorization.Authorized);
        Assert.Equal(VirtualOutputFailureCode.CapabilityDenied, authorization.FailureCode);
    }

    /// <summary>Verifies a bootstrap token cannot be replayed after registration.</summary>
    [Fact]
    public void Register_ReplayedBootstrapToken_IsRejected()
    {
        // Arrange
        using var authority = new VirtualOutputSessionAuthority();
        var ticket = authority.CreateActivation(
            VirtualDeviceId.New(),
            "lineup-link",
            new HashSet<VirtualOutputCapability> { VirtualOutputCapability.ReadManifest },
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(1));
        _ = Register(authority, ticket);

        // Act
        var replay = Register(authority, ticket);

        // Assert
        Assert.False(replay.Accepted);
    }

    private static VirtualOutputWorkerRegistrationResponse Register(VirtualOutputSessionAuthority authority, VirtualOutputActivationTicket ticket) =>
        authority.Register(new VirtualOutputWorkerRegistrationRequest
        {
            WorkerId = ticket.WorkerId,
            VirtualDeviceId = ticket.VirtualDeviceId,
            Generation = ticket.Generation,
            BootstrapToken = ticket.BootstrapToken,
            MinimumProtocolVersion = VirtualOutputProtocol.MinimumVersion,
            MaximumProtocolVersion = VirtualOutputProtocol.MaximumVersion
        });
}
