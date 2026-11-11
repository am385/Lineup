using Lineup.Core.Common.Streaming;
using Lineup.Core.Extensibility.Streaming;

namespace Lineup.Core.VirtualOutputs.Runtime;

/// <summary>Preflights enabled output policies against the current host rather than silently running without requested limits.</summary>
public sealed class VirtualOutputResourcePolicyValidator : IVirtualOutputResourcePolicyValidator
{
    /// <inheritdoc />
    public void Validate(StreamingConfiguration configuration)
    {
        foreach (var device in configuration.VirtualDevices.Where(device => device.Enabled && device.Hosting is not null))
        {
            var hosting = device.Hosting!;
            var policy = configuration.VirtualOutputWorkerPolicies.Single(policy => string.Equals(policy.Key, hosting.WorkerPolicyKey, StringComparison.OrdinalIgnoreCase));
            if (hosting.ExecutionMode == VirtualOutputExecutionMode.ExternalProcess && (policy.MaximumMemoryMib > 0 || policy.MaximumCpuPercent > 0))
            {
                throw new VirtualOutputResourcePolicyException("Host CPU/memory limits cannot be enforced on externally launched workers. Enforce them in the external supervisor and leave the host policy limits at zero.");
            }
            VirtualOutputProcessLimits.Preflight(policy);
        }
    }
}

/// <summary>Identifies a nontransient worker enforcement/deployment failure requiring operator action.</summary>
public sealed class VirtualOutputResourcePolicyException : InvalidOperationException
{
    /// <summary>Initializes an actionable resource-policy failure.</summary>
    public VirtualOutputResourcePolicyException(string message, Exception? innerException = null) : base(message, innerException)
    {
    }
}
