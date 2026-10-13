using System.Security.Cryptography;
using Lineup.Common.Streaming;
using Lineup.VirtualOutputs.Protocol;

namespace Lineup.VirtualOutputs.Runtime;

/// <summary>Contains a one-time worker bootstrap credential returned only to the supervisor.</summary>
public sealed record VirtualOutputActivationTicket
{
    /// <summary>Gets the assigned virtual device.</summary>
    public required VirtualDeviceId VirtualDeviceId { get; init; }

    /// <summary>Gets the authorized worker package identifier.</summary>
    public required string WorkerId { get; init; }

    /// <summary>Gets the new activation generation.</summary>
    public required long Generation { get; init; }

    /// <summary>Gets the one-time plaintext bootstrap token.</summary>
    public required string BootstrapToken { get; init; }

    /// <summary>Gets when registration using this ticket expires.</summary>
    public required DateTimeOffset ExpiresUtc { get; init; }
}

/// <summary>Describes the result of authorizing one worker capability request.</summary>
public sealed record VirtualOutputAuthorizationResult
{
    /// <summary>Gets whether the request is authorized.</summary>
    public bool Authorized { get; init; }

    /// <summary>Gets the assigned virtual device when authorized.</summary>
    public VirtualDeviceId? VirtualDeviceId { get; init; }

    /// <summary>Gets a structured failure code when authorization fails.</summary>
    public VirtualOutputFailureCode? FailureCode { get; init; }

    /// <summary>Gets a safe authorization failure description.</summary>
    public string? Failure { get; init; }
}

/// <summary>Creates generation-bound activations and authenticates isolated output worker sessions.</summary>
public interface IVirtualOutputSessionAuthority
{
    /// <summary>Creates a new activation generation and revokes any earlier generation for the virtual device.</summary>
    VirtualOutputActivationTicket CreateActivation(
        VirtualDeviceId virtualDeviceId,
        string workerId,
        IReadOnlySet<VirtualOutputCapability> capabilities,
        TimeSpan bootstrapLifetime,
        TimeSpan leaseLifetime);

    /// <summary>Authenticates a worker using one activation ticket.</summary>
    VirtualOutputWorkerRegistrationResponse Register(VirtualOutputWorkerRegistrationRequest request);

    /// <summary>Renews an authenticated worker lease.</summary>
    VirtualOutputLeaseRenewalResponse Renew(VirtualOutputLeaseRenewalRequest request);

    /// <summary>Authorizes one capability against the authenticated session identity.</summary>
    VirtualOutputAuthorizationResult Authorize(VirtualOutputSessionContext session, VirtualOutputCapability capability);

    /// <summary>Revokes the active generation for one virtual device.</summary>
    bool Revoke(VirtualDeviceId virtualDeviceId, string reason);
}

/// <summary>Thread-safe in-memory authority for worker bootstrap, generation ownership, and leases.</summary>
public sealed class VirtualOutputSessionAuthority : IVirtualOutputSessionAuthority, IDisposable
{
    private readonly Lock _gate = new();
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<VirtualDeviceId, ActivationState> _activations = [];
    private readonly Dictionary<VirtualOutputWorkerSessionId, VirtualDeviceId> _sessions = [];
    private bool _disposed;

    /// <summary>Initializes a worker-session authority.</summary>
    public VirtualOutputSessionAuthority(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public VirtualOutputActivationTicket CreateActivation(
        VirtualDeviceId virtualDeviceId,
        string workerId,
        IReadOnlySet<VirtualOutputCapability> capabilities,
        TimeSpan bootstrapLifetime,
        TimeSpan leaseLifetime)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        ArgumentNullException.ThrowIfNull(capabilities);
        if (virtualDeviceId.Value == Guid.Empty)
        {
            throw new ArgumentException("A virtual-device ID is required.", nameof(virtualDeviceId));
        }
        if (bootstrapLifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(bootstrapLifetime), "Bootstrap lifetime must be positive.");
        }
        if (leaseLifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseLifetime), "Lease lifetime must be positive.");
        }

        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToBase64String(tokenBytes);
        var tokenHash = SHA256.HashData(tokenBytes);
        CryptographicOperations.ZeroMemory(tokenBytes);
        lock (_gate)
        {
            var generation = _activations.TryGetValue(virtualDeviceId, out var current)
                ? checked(current.Generation + 1)
                : 1;
            if (current?.SessionId is { } currentSessionId)
            {
                _sessions.Remove(currentSessionId);
            }
            var expiresUtc = _timeProvider.GetUtcNow().Add(bootstrapLifetime);
            _activations[virtualDeviceId] = new ActivationState(
                workerId,
                generation,
                tokenHash,
                expiresUtc,
                leaseLifetime,
                capabilities.ToHashSet(),
                null,
                null,
                null);
            return new VirtualOutputActivationTicket
            {
                VirtualDeviceId = virtualDeviceId,
                WorkerId = workerId,
                Generation = generation,
                BootstrapToken = token,
                ExpiresUtc = expiresUtc
            };
        }
    }

    /// <inheritdoc />
    public VirtualOutputWorkerRegistrationResponse Register(VirtualOutputWorkerRegistrationRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var validation = VirtualOutputProtocolValidation.Validate(request);
        if (validation.Count > 0)
        {
            return Rejected(string.Join(" ", validation));
        }

        lock (_gate)
        {
            if (!_activations.TryGetValue(request.VirtualDeviceId, out var activation))
            {
                return Rejected("No worker activation is pending for this virtual device.");
            }
            if (request.Generation != activation.Generation)
            {
                return Rejected("The worker activation generation is stale.");
            }
            if (!string.Equals(request.WorkerId, activation.WorkerId, StringComparison.Ordinal))
            {
                return Rejected("The worker package does not match the active assignment.");
            }
            if (_timeProvider.GetUtcNow() > activation.BootstrapExpiresUtc)
            {
                return Rejected("The worker bootstrap credential has expired.");
            }
            if (!VerifyBootstrapToken(request.BootstrapToken, activation.BootstrapTokenHash))
            {
                return Rejected("The worker bootstrap credential is invalid.");
            }

            var protocolVersion = VirtualOutputProtocol.Negotiate(request.MinimumProtocolVersion, request.MaximumProtocolVersion);
            if (protocolVersion is null)
            {
                return Rejected("The worker does not support a compatible protocol version.");
            }
            if (activation.SessionId is { } oldSessionId)
            {
                _sessions.Remove(oldSessionId);
            }

            var sessionId = VirtualOutputWorkerSessionId.New();
            var leaseExpiresUtc = _timeProvider.GetUtcNow().Add(activation.LeaseLifetime);
            CryptographicOperations.ZeroMemory(activation.BootstrapTokenHash);
            _activations[request.VirtualDeviceId] = activation with
            {
                BootstrapTokenHash = [],
                SessionId = sessionId,
                ProtocolVersion = protocolVersion,
                LeaseExpiresUtc = leaseExpiresUtc
            };
            _sessions[sessionId] = request.VirtualDeviceId;
            return new VirtualOutputWorkerRegistrationResponse
            {
                Accepted = true,
                ProtocolVersion = protocolVersion,
                SessionId = sessionId,
                Generation = activation.Generation,
                LeaseExpiresUtc = leaseExpiresUtc,
                Capabilities = activation.Capabilities.Order().ToArray()
            };
        }
    }

    /// <inheritdoc />
    public VirtualOutputLeaseRenewalResponse Renew(VirtualOutputLeaseRenewalRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        var authorization = Authorize(request.Session, VirtualOutputCapability.ReportHealth);
        if (!authorization.Authorized)
        {
            return new VirtualOutputLeaseRenewalResponse
            {
                Accepted = false,
                Drain = true,
                Reason = authorization.Failure
            };
        }

        lock (_gate)
        {
            var deviceId = authorization.VirtualDeviceId!.Value;
            var activation = _activations[deviceId];
            var leaseExpiresUtc = _timeProvider.GetUtcNow().Add(activation.LeaseLifetime);
            _activations[deviceId] = activation with { LeaseExpiresUtc = leaseExpiresUtc };
            return new VirtualOutputLeaseRenewalResponse
            {
                Accepted = true,
                LeaseExpiresUtc = leaseExpiresUtc
            };
        }
    }

    /// <inheritdoc />
    public VirtualOutputAuthorizationResult Authorize(VirtualOutputSessionContext session, VirtualOutputCapability capability)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(session);
        lock (_gate)
        {
            if (!_sessions.TryGetValue(session.SessionId, out var deviceId) ||
                !_activations.TryGetValue(deviceId, out var activation) ||
                activation.SessionId != session.SessionId)
            {
                return Denied(VirtualOutputFailureCode.Unauthorized, "The worker session is not active.");
            }
            if (activation.Generation != session.Generation)
            {
                return Denied(VirtualOutputFailureCode.StaleGeneration, "The worker generation is stale.");
            }
            if (activation.LeaseExpiresUtc is not { } leaseExpiresUtc || _timeProvider.GetUtcNow() > leaseExpiresUtc)
            {
                _sessions.Remove(session.SessionId);
                _activations[deviceId] = activation with { SessionId = null, ProtocolVersion = null, LeaseExpiresUtc = null };
                return Denied(VirtualOutputFailureCode.Unauthorized, "The worker lease has expired.");
            }
            if (!activation.Capabilities.Contains(capability))
            {
                return Denied(VirtualOutputFailureCode.CapabilityDenied, "The worker capability was not granted.");
            }
            return new VirtualOutputAuthorizationResult
            {
                Authorized = true,
                VirtualDeviceId = deviceId
            };
        }
    }

    /// <inheritdoc />
    public bool Revoke(VirtualDeviceId virtualDeviceId, string reason)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        lock (_gate)
        {
            if (!_activations.Remove(virtualDeviceId, out var activation))
            {
                return false;
            }
            if (activation.SessionId is { } sessionId)
            {
                _sessions.Remove(sessionId);
            }
            CryptographicOperations.ZeroMemory(activation.BootstrapTokenHash);
            return true;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        lock (_gate)
        {
            foreach (var activation in _activations.Values)
            {
                CryptographicOperations.ZeroMemory(activation.BootstrapTokenHash);
            }
            _activations.Clear();
            _sessions.Clear();
            _disposed = true;
        }
    }

    private static bool VerifyBootstrapToken(string token, byte[] expectedHash)
    {
        byte[] tokenBytes;
        try
        {
            tokenBytes = Convert.FromBase64String(token);
        }
        catch (FormatException)
        {
            return false;
        }

        try
        {
            var actualHash = SHA256.HashData(tokenBytes);
            return expectedHash.Length == actualHash.Length && CryptographicOperations.FixedTimeEquals(expectedHash, actualHash);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(tokenBytes);
        }
    }

    private static VirtualOutputWorkerRegistrationResponse Rejected(string failure) =>
        new()
        {
            Failure = failure
        };

    private static VirtualOutputAuthorizationResult Denied(VirtualOutputFailureCode code, string failure) =>
        new()
        {
            FailureCode = code,
            Failure = failure
        };

    private sealed record ActivationState(
        string WorkerId,
        long Generation,
        byte[] BootstrapTokenHash,
        DateTimeOffset BootstrapExpiresUtc,
        TimeSpan LeaseLifetime,
        IReadOnlySet<VirtualOutputCapability> Capabilities,
        VirtualOutputWorkerSessionId? SessionId,
        int? ProtocolVersion,
        DateTimeOffset? LeaseExpiresUtc);
}
