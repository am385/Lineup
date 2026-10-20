namespace Lineup.Common.Streaming;

/// <summary>Identifies how one virtual-output worker is supervised.</summary>
public enum VirtualOutputExecutionMode
{
    /// <summary>Lineup starts and supervises a child worker process.</summary>
    ManagedProcess,

    /// <summary>An external service manager starts the worker and it registers with Lineup.</summary>
    ExternalProcess
}

/// <summary>Identifies the transport used by one public virtual-output listener.</summary>
public enum VirtualOutputTransport
{
    /// <summary>Unencrypted HTTP.</summary>
    Http,

    /// <summary>HTTP protected by TLS.</summary>
    Https
}

/// <summary>Declares host-neutral networking capabilities for one virtual adapter kind.</summary>
public sealed record VirtualOutputCapabilities
{
    /// <summary>Gets whether the adapter exposes network-facing output listeners.</summary>
    public bool RequiresExternalHosting { get; init; }

    /// <summary>Gets supported public listener transports.</summary>
    public IReadOnlySet<VirtualOutputTransport> SupportedTransports { get; init; } = new HashSet<VirtualOutputTransport>();

    /// <summary>Gets whether the adapter requires a separately supervised shared discovery broker.</summary>
    public bool RequiresSharedDiscoveryBroker { get; init; }

    /// <summary>Gets protocol-relative paths used for safe setup previews.</summary>
    public IReadOnlyList<string> ProtocolPaths { get; init; } = [];
}

/// <summary>Controls client-certificate requirements for one output security policy.</summary>
public enum VirtualOutputClientCertificateMode
{
    /// <summary>Do not request a client certificate.</summary>
    None,

    /// <summary>Accept a valid client certificate when one is supplied.</summary>
    Allow,

    /// <summary>Require a valid client certificate.</summary>
    Require
}

/// <summary>Configures one network binding owned by a virtual output.</summary>
public sealed record VirtualOutputBindingDefinition
{
    /// <summary>Gets the listener transport.</summary>
    public VirtualOutputTransport Transport { get; init; }

    /// <summary>Gets the explicit IP addresses or host-supported wildcard tokens to bind.</summary>
    public IReadOnlyList<string> Addresses { get; init; } = [];

    /// <summary>Gets the TCP port.</summary>
    public int Port { get; init; }

    /// <summary>Gets the externally advertised root URI.</summary>
    public string PublicBaseUri { get; init; } = string.Empty;

    /// <summary>Gets the security policy key applied before provider endpoint dispatch.</summary>
    public string SecurityPolicyKey { get; init; } = string.Empty;
}

/// <summary>Configures isolated hosting for one virtual output.</summary>
public sealed record VirtualOutputHostingDefinition
{
    /// <summary>Gets how the output worker is launched and supervised.</summary>
    public VirtualOutputExecutionMode ExecutionMode { get; init; }

    /// <summary>Gets the public listeners owned by this output.</summary>
    public IReadOnlyList<VirtualOutputBindingDefinition> Bindings { get; init; } = [];

    /// <summary>Gets the worker resource and lifecycle policy key.</summary>
    public string WorkerPolicyKey { get; init; } = string.Empty;

}

/// <summary>Defines reusable network and TLS policy for virtual-output listeners.</summary>
public sealed record VirtualOutputSecurityPolicyDefinition
{
    /// <summary>Gets the stable code-safe policy key.</summary>
    public required string Key { get; init; }

    /// <summary>Gets the display name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets a reference resolved by the trusted host to a TLS certificate.</summary>
    public string? CertificateReference { get; init; }

    /// <summary>Gets the client-certificate requirement.</summary>
    public VirtualOutputClientCertificateMode ClientCertificateMode { get; init; }

    /// <summary>Gets the allowed client network CIDRs. An empty collection does not add a network restriction.</summary>
    public IReadOnlyList<string> AllowedNetworks { get; init; } = [];

    /// <summary>Gets the maximum concurrent public connections, or zero to use the host default.</summary>
    public int MaximumConnections { get; init; }

    /// <summary>Gets the maximum requests accepted per minute from one client, or zero to disable this limit.</summary>
    public int RequestsPerMinutePerClient { get; init; }

    /// <summary>Gets whether forwarded headers may be honored from explicitly trusted proxies.</summary>
    public bool AllowForwardedHeaders { get; init; }

    /// <summary>Gets trusted proxy addresses or CIDRs when forwarded headers are enabled.</summary>
    public IReadOnlyList<string> TrustedProxies { get; init; } = [];
}

/// <summary>Defines reusable lifecycle and resource policy for virtual-output workers.</summary>
public sealed record VirtualOutputWorkerPolicyDefinition
{
    /// <summary>Gets the stable code-safe policy key.</summary>
    public required string Key { get; init; }

    /// <summary>Gets the display name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the maximum worker restarts allowed within the restart window.</summary>
    public int MaximumRestarts { get; init; } = 5;

    /// <summary>Gets the restart accounting window in seconds.</summary>
    public int RestartWindowSeconds { get; init; } = 300;

    /// <summary>Gets the graceful drain timeout in seconds.</summary>
    public int DrainTimeoutSeconds { get; init; } = 15;

    /// <summary>Gets the control-plane lease grace period in seconds.</summary>
    public int LeaseGracePeriodSeconds { get; init; } = 15;

    /// <summary>Gets the maximum concurrent broker capability calls.</summary>
    public int MaximumOutstandingBrokerCalls { get; init; } = 32;

    /// <summary>Gets the maximum memory in mebibytes, or zero when no process limit is requested.</summary>
    public int MaximumMemoryMib { get; init; }

    /// <summary>Gets the maximum CPU percentage, or zero when no process limit is requested.</summary>
    public int MaximumCpuPercent { get; init; }
}

/// <summary>Identifies desired-versus-applied runtime state for one virtual output.</summary>
public enum VirtualOutputRuntimeState
{
    /// <summary>The virtual output is disabled.</summary>
    Disabled,

    /// <summary>The virtual output requires valid hosting configuration.</summary>
    RequiresConfiguration,

    /// <summary>The worker is starting and has not passed readiness checks.</summary>
    Starting,

    /// <summary>The worker is ready and its listeners are active.</summary>
    Healthy,

    /// <summary>The worker remains partially available but requires operator attention.</summary>
    Degraded,

    /// <summary>The worker is draining existing connections.</summary>
    Draining,

    /// <summary>The worker could not be activated or exceeded its restart policy.</summary>
    Failed
}

/// <summary>Describes the applied runtime state of one isolated virtual output.</summary>
public sealed record VirtualOutputRuntimeSnapshot
{
    /// <summary>Gets the virtual device.</summary>
    public required VirtualDeviceId VirtualDeviceId { get; init; }

    /// <summary>Gets the current runtime state.</summary>
    public VirtualOutputRuntimeState State { get; init; }

    /// <summary>Gets the active worker generation when a worker has registered.</summary>
    public long? Generation { get; init; }

    /// <summary>Gets the worker process identifier when known.</summary>
    public int? ProcessId { get; init; }

    /// <summary>Gets the negotiated IPC protocol version when connected.</summary>
    public int? ProtocolVersion { get; init; }

    /// <summary>Gets safe public listener descriptions.</summary>
    public IReadOnlyList<string> Bindings { get; init; } = [];

    /// <summary>Gets the number of active public connections.</summary>
    public int ActiveConnections { get; init; }

    /// <summary>Gets the number of active Core broker streams owned by this worker.</summary>
    public int ActiveBrokerStreams { get; init; }

    /// <summary>Gets the restart count within the active policy window.</summary>
    public int RestartCount { get; init; }

    /// <summary>Gets when the current generation became ready.</summary>
    public DateTimeOffset? ReadySinceUtc { get; init; }

    /// <summary>Gets a sanitized operator-facing failure description.</summary>
    public string? LastFailure { get; init; }
}
