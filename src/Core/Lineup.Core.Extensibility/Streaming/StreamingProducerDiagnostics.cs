using Lineup.Core.Common.Streaming;

namespace Lineup.Core.Extensibility.Streaming;

/// <summary>Contributes plugin-owned producer state to host-neutral streaming diagnostics.</summary>
public interface IStreamingProducerDiagnosticsContributor
{
    /// <summary>Gets the contributor's current producer state.</summary>
    StreamingProducerDiagnostics GetProducerDiagnostics();
}
