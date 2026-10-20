using System.Text.Json;
using Lineup.Common.Guides;
using Lineup.Common.Streaming;

namespace Lineup.VirtualOutputs.Protocol;

/// <summary>Defines the versioned local IPC protocol used by isolated virtual-output workers.</summary>
public static class VirtualOutputProtocol
{
    /// <summary>Gets the oldest protocol version implemented by this release.</summary>
    public const int MinimumVersion = 1;

    /// <summary>Gets the newest protocol version implemented by this release.</summary>
    public const int MaximumVersion = 2;

    /// <summary>Gets the maximum serialized control-message size.</summary>
    public const int MaximumControlMessageBytes = 1024 * 1024;

    /// <summary>Gets the maximum serialized guide-response size.</summary>
    public const int MaximumGuideMessageBytes = 32 * 1024 * 1024;

    /// <summary>Gets the maximum accepted channel-key length.</summary>
    public const int MaximumChannelKeyLength = 512;

    /// <summary>Gets the maximum accepted authenticated client-identifier length.</summary>
    public const int MaximumAuthenticatedClientIdLength = 128;

    /// <summary>Gets the maximum accepted authenticated client-name length.</summary>
    public const int MaximumAuthenticatedClientNameLength = 128;

    /// <summary>Gets the maximum accepted worker-identifier length.</summary>
    public const int MaximumWorkerIdLength = 128;

    /// <summary>Gets the maximum accepted worker media-session identifier length.</summary>
    public const int MaximumWorkerMediaSessionIdLength = 128;

    /// <summary>Gets the maximum accepted sanitized stream-failure length.</summary>
    public const int MaximumStreamFailureLength = 2048;

    /// <summary>Gets the maximum accepted track count in one worker stream-status report.</summary>
    public const int MaximumStreamStatusTrackCount = 128;

    /// <summary>Gets the maximum accepted codec-name length in one worker stream-status report.</summary>
    public const int MaximumStreamCodecLength = 128;

    /// <summary>Gets the maximum accepted descriptive media-text length in one worker stream-status report.</summary>
    public const int MaximumStreamMediaTextLength = 256;

    /// <summary>Selects the newest mutually supported protocol version.</summary>
    public static int? Negotiate(int peerMinimumVersion, int peerMaximumVersion)
    {
        if (peerMinimumVersion < 1 || peerMaximumVersion < peerMinimumVersion)
        {
            return null;
        }

        var minimum = Math.Max(MinimumVersion, peerMinimumVersion);
        var maximum = Math.Min(MaximumVersion, peerMaximumVersion);
        return minimum <= maximum ? maximum : null;
    }
}

/// <summary>Identifies the operating-system local transport used by the capability broker.</summary>
public enum VirtualOutputIpcTransport
{
    /// <summary>Windows named pipe.</summary>
    NamedPipe,

    /// <summary>Unix-domain socket.</summary>
    UnixDomainSocket
}

/// <summary>Identifies the local capability-broker endpoint supplied to workers.</summary>
public sealed record VirtualOutputIpcEndpoint
{
    /// <summary>Gets the local transport.</summary>
    public VirtualOutputIpcTransport Transport { get; init; }

    /// <summary>Gets the pipe name or absolute Unix-domain socket path.</summary>
    public required string Address { get; init; }
}

/// <summary>Contains activation-scoped TLS material resolved by the trusted host.</summary>
public sealed record VirtualOutputCertificateMaterial
{
    /// <summary>Gets the opaque certificate reference used by listener policies.</summary>
    public required string Reference { get; init; }

    /// <summary>Gets PKCS#12 certificate bytes.</summary>
    public required byte[] Pkcs12 { get; init; }

    /// <summary>Gets the optional PKCS#12 password.</summary>
    public string? Password { get; init; }
}

/// <summary>Contains the one-time instance-scoped bootstrap descriptor delivered through worker standard input.</summary>
public sealed record VirtualOutputWorkerBootstrap
{
    /// <summary>Gets the local capability-broker endpoint.</summary>
    public required VirtualOutputIpcEndpoint Endpoint { get; init; }

    /// <summary>Gets the assigned virtual device and provider configuration.</summary>
    public required VirtualDeviceDefinition VirtualDevice { get; init; }

    /// <summary>Gets security policies referenced by the assigned listener bindings.</summary>
    public IReadOnlyList<VirtualOutputSecurityPolicyDefinition> SecurityPolicies { get; init; } = [];

    /// <summary>Gets activation-scoped certificate material referenced by assigned HTTPS policies.</summary>
    public IReadOnlyList<VirtualOutputCertificateMaterial> CertificateMaterials { get; init; } = [];

    /// <summary>Gets provider-neutral advertisement snapshots assigned to a shared discovery worker.</summary>
    public IReadOnlyList<VirtualOutputAdvertisementSnapshot> Advertisements { get; init; } = [];

    /// <summary>Gets the assigned worker lifecycle policy.</summary>
    public required VirtualOutputWorkerPolicyDefinition WorkerPolicy { get; init; }

    /// <summary>Gets the authorized worker package identifier.</summary>
    public required string WorkerId { get; init; }

    /// <summary>Gets the activation generation.</summary>
    public required long Generation { get; init; }

    /// <summary>Gets the one-time registration credential.</summary>
    public required string BootstrapToken { get; init; }

    /// <summary>Gets when the one-time registration credential expires.</summary>
    public required DateTimeOffset BootstrapExpiresUtc { get; init; }
}

/// <summary>Describes one virtual output that a shared provider discovery worker may advertise.</summary>
public sealed record VirtualOutputAdvertisementSnapshot
{
    /// <summary>Gets the virtual-device identity.</summary>
    public required VirtualDeviceId VirtualDeviceId { get; init; }

    /// <summary>Gets the virtual adapter kind.</summary>
    public required string AdapterKind { get; init; }

    /// <summary>Gets the safe display name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the externally reachable protocol base URI.</summary>
    public required Uri PublicBaseUri { get; init; }

    /// <summary>Gets the effective tuner count when the advertised protocol exposes one.</summary>
    public int? TunerCount { get; init; }

    /// <summary>Gets bounded provider-specific advertisement metadata.</summary>
    public JsonElement Metadata { get; init; }
}

/// <summary>Identifies one authenticated worker session.</summary>
/// <param name="Value">Opaque session identifier.</param>
public readonly record struct VirtualOutputWorkerSessionId(Guid Value)
{
    /// <summary>Creates a new session identifier.</summary>
    public static VirtualOutputWorkerSessionId New() => new(Guid.NewGuid());

    /// <inheritdoc />
    public override string ToString() => Value.ToString("N");
}

/// <summary>Identifies one brokered stream request.</summary>
/// <param name="Value">Opaque stream-request identifier.</param>
public readonly record struct VirtualOutputStreamRequestId(Guid Value)
{
    /// <summary>Creates a new stream-request identifier.</summary>
    public static VirtualOutputStreamRequestId New() => new(Guid.NewGuid());

    /// <inheritdoc />
    public override string ToString() => Value.ToString("N");
}

/// <summary>Identifies capabilities available to an authenticated output worker.</summary>
public enum VirtualOutputCapability
{
    /// <summary>Read the assigned virtual-device manifest.</summary>
    ReadManifest,

    /// <summary>Read the assigned virtual-device lineup.</summary>
    ReadLineup,

    /// <summary>Read the assigned virtual-device guide.</summary>
    ReadGuide,

    /// <summary>Open an assigned virtual-device media stream.</summary>
    OpenStream,

    /// <summary>Report worker health and listener readiness.</summary>
    ReportHealth
}

/// <summary>Requests an authenticated, generation-bound worker session.</summary>
public sealed record VirtualOutputWorkerRegistrationRequest
{
    /// <summary>Gets the declarative worker package identifier.</summary>
    public required string WorkerId { get; init; }

    /// <summary>Gets the assigned virtual device.</summary>
    public required VirtualDeviceId VirtualDeviceId { get; init; }

    /// <summary>Gets the activation generation supplied by the supervisor.</summary>
    public required long Generation { get; init; }

    /// <summary>Gets the one-time bootstrap credential.</summary>
    public required string BootstrapToken { get; init; }

    /// <summary>Gets the worker's oldest supported protocol version.</summary>
    public int MinimumProtocolVersion { get; init; }

    /// <summary>Gets the worker's newest supported protocol version.</summary>
    public int MaximumProtocolVersion { get; init; }
}

/// <summary>Returns the result of worker registration and protocol negotiation.</summary>
public sealed record VirtualOutputWorkerRegistrationResponse
{
    /// <summary>Gets whether registration succeeded.</summary>
    public bool Accepted { get; init; }

    /// <summary>Gets the negotiated protocol version when accepted.</summary>
    public int? ProtocolVersion { get; init; }

    /// <summary>Gets the authenticated worker session when accepted.</summary>
    public VirtualOutputWorkerSessionId? SessionId { get; init; }

    /// <summary>Gets the active generation when accepted.</summary>
    public long? Generation { get; init; }

    /// <summary>Gets when the worker must renew its control-plane lease.</summary>
    public DateTimeOffset? LeaseExpiresUtc { get; init; }

    /// <summary>Gets the capabilities granted to the session.</summary>
    public IReadOnlyList<VirtualOutputCapability> Capabilities { get; init; } = [];

    /// <summary>Gets a safe rejection reason.</summary>
    public string? Failure { get; init; }
}

/// <summary>Authenticates one generation-bound capability request.</summary>
public sealed record VirtualOutputSessionContext
{
    /// <summary>Gets the authenticated session.</summary>
    public required VirtualOutputWorkerSessionId SessionId { get; init; }

    /// <summary>Gets the active generation.</summary>
    public required long Generation { get; init; }
}

/// <summary>Renews one worker's control-plane lease.</summary>
public sealed record VirtualOutputLeaseRenewalRequest
{
    /// <summary>Gets the authenticated session context.</summary>
    public required VirtualOutputSessionContext Session { get; init; }

    /// <summary>Gets worker-observed active public connections.</summary>
    public int ActiveConnections { get; init; }

    /// <summary>Gets whether all configured public listeners are ready.</summary>
    public bool Ready { get; init; }
}

/// <summary>Returns one renewed or revoked worker lease.</summary>
public sealed record VirtualOutputLeaseRenewalResponse
{
    /// <summary>Gets whether the lease remains active.</summary>
    public bool Accepted { get; init; }

    /// <summary>Gets the renewed expiration time.</summary>
    public DateTimeOffset? LeaseExpiresUtc { get; init; }

    /// <summary>Gets whether the worker must stop accepting new public requests.</summary>
    public bool Drain { get; init; }

    /// <summary>Gets a safe rejection or drain reason.</summary>
    public string? Reason { get; init; }
}

/// <summary>Requests the manifest for the session's assigned virtual device.</summary>
public sealed record VirtualOutputManifestRequest
{
    /// <summary>Gets the authenticated session context.</summary>
    public required VirtualOutputSessionContext Session { get; init; }
}

/// <summary>Describes the virtual device visible to one isolated worker.</summary>
public sealed record VirtualOutputManifest
{
    /// <summary>Gets the virtual device.</summary>
    public required VirtualDeviceId VirtualDeviceId { get; init; }

    /// <summary>Gets the display name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets finite tuner capacity, or <see langword="null"/> when unbounded.</summary>
    public int? TunerCount { get; init; }

    /// <summary>Gets whether guide publication is available.</summary>
    public bool GuideAvailable { get; init; }
}

/// <summary>Requests the lineup for the session's assigned virtual device.</summary>
public sealed record VirtualOutputLineupRequest
{
    /// <summary>Gets the authenticated session context.</summary>
    public required VirtualOutputSessionContext Session { get; init; }
}

/// <summary>Returns channels published by the assigned virtual device.</summary>
public sealed record VirtualOutputLineupResponse
{
    /// <summary>Gets projected channels.</summary>
    public IReadOnlyList<ResolvedVirtualChannel> Channels { get; init; } = [];
}

/// <summary>Requests the guide for the session's assigned virtual device.</summary>
public sealed record VirtualOutputGuideRequest
{
    /// <summary>Gets the authenticated session context.</summary>
    public required VirtualOutputSessionContext Session { get; init; }
}

/// <summary>Returns normalized guide data for the assigned virtual device.</summary>
public sealed record VirtualOutputGuideResponse
{
    /// <summary>Gets the normalized guide snapshot.</summary>
    public required VirtualGuideSnapshot Snapshot { get; init; }
}

/// <summary>Describes safe external-client metadata attached to one stream request.</summary>
public sealed record VirtualOutputClientContext
{
    /// <summary>Gets the direct or trusted-proxy-resolved client address.</summary>
    public string? Address { get; init; }

    /// <summary>Gets an optional protocol-specific user-agent value.</summary>
    public string? UserAgent { get; init; }

    /// <summary>Gets the stable authenticated external-client identifier.</summary>
    public string? AuthenticatedClientId { get; init; }

    /// <summary>Gets the safe authenticated external-client display name.</summary>
    public string? AuthenticatedClientName { get; init; }
}

/// <summary>Requests one MPEG-TS stream from the assigned virtual device.</summary>
public sealed record VirtualOutputOpenStreamRequest
{
    /// <summary>Gets the authenticated session context.</summary>
    public required VirtualOutputSessionContext Session { get; init; }

    /// <summary>Gets the unique request identifier.</summary>
    public required VirtualOutputStreamRequestId RequestId { get; init; }

    /// <summary>Gets the channel key published in the assigned lineup.</summary>
    public required string ChannelKey { get; init; }

    /// <summary>Gets safe external-client metadata.</summary>
    public VirtualOutputClientContext Client { get; init; } = new();

    /// <summary>Gets the broker stage required by the output worker.</summary>
    public VirtualOutputStreamStage Stage { get; init; }
}

/// <summary>Identifies the MPEG-TS stage exposed to an isolated output worker.</summary>
public enum VirtualOutputStreamStage
{
    /// <summary>Applies both physical and virtual-device transforms before delivery.</summary>
    VirtualOutput,

    /// <summary>Applies physical transforms and lets the worker apply the virtual presentation profile.</summary>
    PhysicalOutput
}

/// <summary>Describes an accepted brokered MPEG-TS stream.</summary>
public sealed record VirtualOutputOpenStreamResponse
{
    /// <summary>Gets whether the stream was accepted.</summary>
    public bool Accepted { get; init; }

    /// <summary>Gets the content type when accepted.</summary>
    public string? ContentType { get; init; }

    /// <summary>Gets the active Lineup stream session when accepted.</summary>
    public StreamSessionId? StreamSessionId { get; init; }

    /// <summary>Gets normalized metadata for the requested brokered MPEG-TS stage.</summary>
    public MediaProbeResult? Source { get; init; }

    /// <summary>Gets normalized metadata for the native physical source before physical-device transforms.</summary>
    public MediaProbeResult? RawSource { get; init; }

    /// <summary>Gets the effective virtual-device profile used to produce the delivered MPEG-TS stream.</summary>
    public VirtualTranscodeProfile? Profile { get; init; }

    /// <summary>Gets a structured rejection code.</summary>
    public VirtualOutputFailureCode? FailureCode { get; init; }

    /// <summary>Gets a safe operator-facing rejection description.</summary>
    public string? Failure { get; init; }
}

/// <summary>Requests deterministic closure of one brokered MPEG-TS stream.</summary>
public sealed record VirtualOutputCloseStreamRequest
{
    /// <summary>Gets the authenticated session context that opened the stream.</summary>
    public required VirtualOutputSessionContext Session { get; init; }

    /// <summary>Gets the original broker request identifier.</summary>
    public required VirtualOutputStreamRequestId RequestId { get; init; }

    /// <summary>Gets the active Lineup stream session returned by the broker.</summary>
    public required StreamSessionId StreamSessionId { get; init; }
}

/// <summary>Describes the result of a deterministic broker-stream close request.</summary>
public sealed record VirtualOutputCloseStreamResponse
{
    /// <summary>Gets whether an active broker stream was found and closed.</summary>
    public bool Closed { get; init; }
}

/// <summary>Identifies safe protocol failures returned to an isolated worker.</summary>
public enum VirtualOutputFailureCode
{
    /// <summary>The worker session is missing, expired, or revoked.</summary>
    Unauthorized,

    /// <summary>The requested capability was not granted.</summary>
    CapabilityDenied,

    /// <summary>The request uses a stale activation generation.</summary>
    StaleGeneration,

    /// <summary>The requested channel is not assigned to the virtual output.</summary>
    ChannelNotFound,

    /// <summary>A configured concurrency or resource limit was reached.</summary>
    ResourceLimit,

    /// <summary>The control plane cannot currently provide the requested operation.</summary>
    Unavailable,

    /// <summary>The request did not satisfy protocol validation.</summary>
    InvalidRequest,

    /// <summary>Every equivalent physical source is currently unavailable.</summary>
    SourceUnavailable,

    /// <summary>Every equivalent physical source is unable to lock a usable signal.</summary>
    SourceNoSignal,

    /// <summary>Every equivalent physical source is returning no video data.</summary>
    SourceNoData,

    /// <summary>Every equivalent physical source is currently at tuner capacity.</summary>
    SourceCapacityExceeded,

    /// <summary>The requested physical source channel no longer exists.</summary>
    SourceNotFound,

    /// <summary>The physical source rejected the stream request.</summary>
    SourceInvalid,

    /// <summary>The physical source is protected and cannot be streamed.</summary>
    ProtectedContent
}

/// <summary>Identifies the latest startup state of one worker-owned media presentation.</summary>
public enum VirtualOutputStreamStartupStage
{
    /// <summary>The worker is planning or starting the final presentation.</summary>
    Starting,

    /// <summary>The final presentation passed readiness checks and is available to the consumer.</summary>
    Ready,

    /// <summary>The final presentation failed to start or became unavailable.</summary>
    Failed
}

/// <summary>Reports the normalized final media presentation for one brokered stream.</summary>
public sealed record VirtualOutputStreamStatusReport
{
    /// <summary>Gets the authenticated session context that opened the stream.</summary>
    public required VirtualOutputSessionContext Session { get; init; }

    /// <summary>Gets the original broker request identifier.</summary>
    public required VirtualOutputStreamRequestId RequestId { get; init; }

    /// <summary>Gets the active Lineup stream session returned by the broker.</summary>
    public required StreamSessionId StreamSessionId { get; init; }

    /// <summary>Gets the worker-owned media-session identifier.</summary>
    public required string WorkerMediaSessionId { get; init; }

    /// <summary>Gets the final hosted format exposed to the consumer.</summary>
    public HostedStreamFormat Format { get; init; }

    /// <summary>Gets source-to-output track mappings for the worker presentation.</summary>
    public IReadOnlyList<ActiveStreamTrack> Tracks { get; init; } = [];

    /// <summary>Gets the native physical media entering the complete route.</summary>
    public ActiveStreamMediaSnapshot? PhysicalInput { get; init; }

    /// <summary>Gets the media entering the dedicated virtual-output worker.</summary>
    public ActiveStreamMediaSnapshot? PhysicalOutput { get; init; }

    /// <summary>Gets the final media presentation exposed by the dedicated worker.</summary>
    public ActiveStreamMediaSnapshot? VirtualOutput { get; init; }

    /// <summary>Gets the latest worker startup stage.</summary>
    public VirtualOutputStreamStartupStage StartupStage { get; init; }

    /// <summary>Gets a sanitized latest failure description.</summary>
    public string? LastFailure { get; init; }
}

/// <summary>Describes whether a worker stream-status report updated its correlated active stream.</summary>
public sealed record VirtualOutputStreamStatusResponse
{
    /// <summary>Gets whether the correlated active stream was updated.</summary>
    public bool Updated { get; init; }
}

/// <summary>Reports safe worker health and listener readiness.</summary>
public sealed record VirtualOutputWorkerHealthReport
{
    /// <summary>Gets the authenticated session context.</summary>
    public required VirtualOutputSessionContext Session { get; init; }

    /// <summary>Gets whether all configured listeners are ready.</summary>
    public bool Ready { get; init; }

    /// <summary>Gets active public connections.</summary>
    public int ActiveConnections { get; init; }

    /// <summary>Gets safe listener descriptions.</summary>
    public IReadOnlyList<string> Bindings { get; init; } = [];

    /// <summary>Gets a sanitized degraded or failure description.</summary>
    public string? Failure { get; init; }
}
