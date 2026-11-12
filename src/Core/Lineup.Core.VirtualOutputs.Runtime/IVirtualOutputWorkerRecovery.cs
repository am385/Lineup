using Lineup.Core.Common.Streaming;

namespace Lineup.Core.VirtualOutputs.Runtime;

/// <summary>Provides explicit operator recovery of unavailable isolated outputs without changing saved configuration.</summary>
public interface IVirtualOutputWorkerRecovery
{
    /// <summary>Revalidates and retries one failed output, leaving running workers and automatic restart policy unchanged.</summary>
    /// <remarks>Cancellation prevents acceptance; an accepted activation is owned by the host rather than the requesting UI.</remarks>
    Task<VirtualOutputWorkerRecoveryResult> RetryAsync(VirtualDeviceId virtualDeviceId, CancellationToken cancellationToken = default);
}

/// <summary>Reports whether a failed worker was retried and its resulting availability.</summary>
/// <param name="Accepted">Whether the supervisor accepted the recovery attempt.</param>
/// <param name="Snapshot">The current worker status, including any retained prior failure.</param>
public sealed record VirtualOutputWorkerRecoveryResult(bool Accepted, VirtualOutputRuntimeSnapshot? Snapshot);
