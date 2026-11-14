using Lineup.Core.Common.Streaming;
using Lineup.Core.VirtualOutputs.Protocol;
using Xunit;

namespace Lineup.Core.VirtualOutputs.Runtime.Tests;

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

    /// <summary>Verifies active-session detection follows the requested activation generation.</summary>
    [Fact]
    public void HasActiveSession_RegisteredGeneration_ReturnsTrueUntilReplaced()
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
        _ = Register(authority, firstTicket);

        // Act
        var beforeReplacement = authority.HasActiveSession(deviceId, firstTicket.Generation);
        var secondTicket = authority.CreateActivation(
            deviceId,
            "lineup-link",
            new HashSet<VirtualOutputCapability> { VirtualOutputCapability.ReadManifest },
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(1));
        var afterReplacement = authority.HasActiveSession(deviceId, firstTicket.Generation);

        // Assert
        Assert.True(beforeReplacement);
        Assert.False(afterReplacement);
        Assert.False(authority.HasActiveSession(deviceId, secondTicket.Generation));
    }

    /// <summary>Verifies renewal extends only its owning session using the configured lease lifetime.</summary>
    [Fact]
    public void Renew_ValidSession_ExtendsOnlyOwningLease()
    {
        // Arrange
        var time = new ManualTimeProvider();
        using var authority = new VirtualOutputSessionAuthority(time);
        var startedUtc = time.GetUtcNow();
        var ticket = authority.CreateActivation(VirtualDeviceId.New(), "lineup-link",
            new HashSet<VirtualOutputCapability> { VirtualOutputCapability.ReportHealth }, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(60));
        var otherTicket = authority.CreateActivation(VirtualDeviceId.New(), "lineup-link",
            new HashSet<VirtualOutputCapability> { VirtualOutputCapability.ReportHealth }, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(30));
        var registration = Register(authority, ticket);
        var otherRegistration = Register(authority, otherTicket);
        var session = new VirtualOutputSessionContext { SessionId = registration.SessionId!.Value, Generation = ticket.Generation };
        var otherSession = new VirtualOutputSessionContext { SessionId = otherRegistration.SessionId!.Value, Generation = otherTicket.Generation };
        time.Advance(TimeSpan.FromSeconds(10));

        // Act
        var renewal = authority.Renew(new VirtualOutputLeaseRenewalRequest { Session = session });
        time.Advance(TimeSpan.FromSeconds(20) + TimeSpan.FromTicks(1));
        var owner = authority.Authorize(session, VirtualOutputCapability.ReportHealth);
        var other = authority.Authorize(otherSession, VirtualOutputCapability.ReportHealth);

        // Assert
        Assert.True(renewal.Accepted);
        Assert.False(renewal.Drain);
        Assert.Null(renewal.Reason);
        Assert.Equal(startedUtc.AddSeconds(70), renewal.LeaseExpiresUtc);
        Assert.True(owner.Authorized);
        Assert.False(other.Authorized);
        Assert.Equal("The worker lease has expired.", other.Failure);
    }

    /// <summary>Verifies renewal preserves the exact expiration boundary and cannot revive an expired session.</summary>
    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public void Renew_LeaseExpirationBoundary_PreservesAuthorization(long offsetTicks, bool accepted)
    {
        // Arrange
        var time = new ManualTimeProvider();
        using var authority = new VirtualOutputSessionAuthority(time);
        var ticket = authority.CreateActivation(VirtualDeviceId.New(), "lineup-link",
            new HashSet<VirtualOutputCapability> { VirtualOutputCapability.ReportHealth }, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1));
        var registration = Register(authority, ticket);
        var session = new VirtualOutputSessionContext { SessionId = registration.SessionId!.Value, Generation = ticket.Generation };
        time.Advance(TimeSpan.FromSeconds(1) + TimeSpan.FromTicks(offsetTicks));
        var renewedUtc = time.GetUtcNow();

        // Act
        var renewal = authority.Renew(new VirtualOutputLeaseRenewalRequest { Session = session });
        var authorization = authority.Authorize(session, VirtualOutputCapability.ReportHealth);

        // Assert
        Assert.Equal(accepted, renewal.Accepted);
        Assert.Equal(!accepted, renewal.Drain);
        Assert.Equal(accepted, authorization.Authorized);
        if (accepted)
        {
            Assert.Equal(renewedUtc.AddSeconds(1), renewal.LeaseExpiresUtc);
            Assert.Null(renewal.Reason);
        }
        else
        {
            Assert.Null(renewal.LeaseExpiresUtc);
            Assert.Equal("The worker lease has expired.", renewal.Reason);
            Assert.Equal("The worker session is not active.", authorization.Failure);
        }
    }

    /// <summary>Verifies unknown, stale, revoked and capability-denied renewals retain explicit rejection and drain responses.</summary>
    [Theory]
    [InlineData("unknown", "The worker session is not active.")]
    [InlineData("stale-generation", "The worker generation is stale.")]
    [InlineData("revoked", "The worker session is not active.")]
    [InlineData("capability-denied", "The worker capability was not granted.")]
    public void Renew_UnauthorizedSession_ReturnsRejectionAndDrain(string scenario, string reason)
    {
        // Arrange
        var time = new ManualTimeProvider();
        using var authority = new VirtualOutputSessionAuthority(time);
        var capability = scenario == "capability-denied" ? VirtualOutputCapability.ReadManifest : VirtualOutputCapability.ReportHealth;
        var ticket = authority.CreateActivation(VirtualDeviceId.New(), "lineup-link",
            new HashSet<VirtualOutputCapability> { capability }, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(30));
        var registration = Register(authority, ticket);
        var session = new VirtualOutputSessionContext
        {
            SessionId = scenario == "unknown" ? VirtualOutputWorkerSessionId.New() : registration.SessionId!.Value,
            Generation = scenario == "stale-generation" ? ticket.Generation + 1 : ticket.Generation
        };
        if (scenario == "revoked")
        {
            Assert.True(authority.Revoke(ticket.VirtualDeviceId, "Test worker revoked."));
        }

        // Act
        var renewal = authority.Renew(new VirtualOutputLeaseRenewalRequest { Session = session });

        // Assert
        Assert.False(renewal.Accepted);
        Assert.True(renewal.Drain);
        Assert.Null(renewal.LeaseExpiresUtc);
        Assert.Equal(reason, renewal.Reason);
    }

    /// <summary>Verifies an old session cannot renew pending or registered replacement activations or extend the replacement lease.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Renew_ReplacedSession_DoesNotMutateReplacement(bool registeredReplacement)
    {
        // Arrange
        var time = new ManualTimeProvider();
        using var authority = new VirtualOutputSessionAuthority(time);
        var deviceId = VirtualDeviceId.New();
        var ticket = authority.CreateActivation(deviceId, "lineup-link",
            new HashSet<VirtualOutputCapability> { VirtualOutputCapability.ReportHealth }, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(60));
        var registration = Register(authority, ticket);
        var session = new VirtualOutputSessionContext { SessionId = registration.SessionId!.Value, Generation = ticket.Generation };
        var replacement = authority.CreateActivation(deviceId, "lineup-link",
            new HashSet<VirtualOutputCapability> { VirtualOutputCapability.ReportHealth }, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(30));
        var replacementRegistration = registeredReplacement ? Register(authority, replacement) : null;
        time.Advance(TimeSpan.FromSeconds(10));

        // Act
        var renewal = authority.Renew(new VirtualOutputLeaseRenewalRequest { Session = session });
        replacementRegistration ??= Register(authority, replacement);
        var expectedExpiry = time.GetUtcNow().AddSeconds(registeredReplacement ? 20 : 30);
        time.Advance(TimeSpan.FromSeconds(registeredReplacement ? 20 : 30) + TimeSpan.FromTicks(1));
        var replacementSession = new VirtualOutputSessionContext { SessionId = replacementRegistration.SessionId!.Value, Generation = replacement.Generation };
        var expiredReplacement = authority.Authorize(replacementSession, VirtualOutputCapability.ReportHealth);

        // Assert
        Assert.False(renewal.Accepted);
        Assert.True(renewal.Drain);
        Assert.Null(renewal.LeaseExpiresUtc);
        Assert.Equal("The worker session is not active.", renewal.Reason);
        Assert.True(replacementRegistration.Accepted);
        Assert.Equal(expectedExpiry, replacementRegistration.LeaseExpiresUtc);
        Assert.False(expiredReplacement.Authorized);
        Assert.Equal("The worker lease has expired.", expiredReplacement.Failure);
    }

    /// <summary>Verifies parallel renewal and replacement/revocation never throw dictionary errors or renew another activation's lease.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Renew_ConcurrentLifecycleChanges_PreservesSessionOwnership(bool revoke)
    {
        // Arrange
        var time = new ManualTimeProvider();
        using var authority = new VirtualOutputSessionAuthority(time);
        var deviceId = VirtualDeviceId.New();
        var startedUtc = time.GetUtcNow();
        var renewalAttempts = 0;
        var options = new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = TestContext.Current.CancellationToken };

        // Act
        Parallel.For(0, 20_000, options, index =>
        {
            var lifetime = TimeSpan.FromSeconds(index % 2 == 0 ? 15 : 90);
            var ticket = authority.CreateActivation(deviceId, "lineup-link",
                new HashSet<VirtualOutputCapability> { VirtualOutputCapability.ReportHealth }, TimeSpan.FromMinutes(1), lifetime);
            var registration = Register(authority, ticket);
            if (registration.Accepted)
            {
                Interlocked.Increment(ref renewalAttempts);
                var session = new VirtualOutputSessionContext { SessionId = registration.SessionId!.Value, Generation = ticket.Generation };
                var renewal = authority.Renew(new VirtualOutputLeaseRenewalRequest { Session = session });
                Assert.Equal(!renewal.Accepted, renewal.Drain);
                if (renewal.Accepted)
                {
                    Assert.Equal(startedUtc.Add(lifetime), renewal.LeaseExpiresUtc);
                    Assert.Null(renewal.Reason);
                }
                else
                {
                    Assert.Null(renewal.LeaseExpiresUtc);
                    Assert.Equal("The worker session is not active.", renewal.Reason);
                }
            }
            if (revoke)
            {
                authority.Revoke(deviceId, "Concurrent worker revoked.");
            }
        });

        // Assert
        Assert.True(renewalAttempts > 0);
        var current = authority.CreateActivation(deviceId, "lineup-link",
            new HashSet<VirtualOutputCapability> { VirtualOutputCapability.ReportHealth }, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(45));
        var currentRegistration = Register(authority, current);
        var currentSession = new VirtualOutputSessionContext { SessionId = currentRegistration.SessionId!.Value, Generation = current.Generation };
        var currentRenewal = authority.Renew(new VirtualOutputLeaseRenewalRequest { Session = currentSession });
        Assert.True(currentRenewal.Accepted);
        Assert.Equal(startedUtc.AddSeconds(45), currentRenewal.LeaseExpiresUtc);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _utcTicks = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).UtcTicks;

        /// <inheritdoc />
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);

        /// <summary>Advances the fixed clock without real-time waits.</summary>
        public void Advance(TimeSpan amount) => Interlocked.Add(ref _utcTicks, amount.Ticks);
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
