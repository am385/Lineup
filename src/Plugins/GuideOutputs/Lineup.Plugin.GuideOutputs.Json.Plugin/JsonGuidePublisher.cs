using Lineup.Core.Common.Guides;
using Lineup.Core.Extensibility.Guides;

namespace Lineup.Plugin.GuideOutputs.Json.Plugin;

/// <summary>Serializes normalized virtual guide snapshots as JSON.</summary>
public sealed class JsonGuidePublisher : IGuidePublisher
{
    /// <summary>Gets the kind.</summary>
    public string Kind => JsonGuideOutputDriver.AdapterKind;

    /// <summary>Executes publish asynchronously.</summary>
    /// <param name="snapshot">The snapshot value.</param>
    /// <param name="destination">The destination value.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>A task that completes when the operation has finished.</returns>
    public Task PublishAsync(VirtualGuideSnapshot snapshot, Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(destination);
        var document = JsonGuideDocument.FromSnapshot(snapshot);
        return JsonGuideSerialization.SerializeAsync(document, destination, cancellationToken);
    }
}
