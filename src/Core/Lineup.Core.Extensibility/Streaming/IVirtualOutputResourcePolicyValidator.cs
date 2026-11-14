using Lineup.Core.Common.Streaming;

namespace Lineup.Core.Extensibility.Streaming;

/// <summary>Checks host enforcement capabilities before committing changed output policies.</summary>
public interface IVirtualOutputResourcePolicyValidator
{
    /// <summary>Rejects enabled output policies that this host cannot enforce.</summary>
    void Validate(StreamingConfiguration configuration);
}
