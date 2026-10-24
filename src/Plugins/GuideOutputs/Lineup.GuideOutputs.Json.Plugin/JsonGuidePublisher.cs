using Lineup.Common.Guides;
using Lineup.Extensibility.Guides;
using Lineup.GuideOutputs.Json;

namespace Lineup.GuideOutputs.Json.Plugin;

/// <summary>Serializes normalized virtual guide snapshots as JSON.</summary>
public sealed class JsonGuidePublisher : IGuidePublisher
{
    /// <inheritdoc />
    public string Kind => JsonGuideOutputDriver.AdapterKind;

    /// <inheritdoc />
    public Task PublishAsync(VirtualGuideSnapshot snapshot, Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(destination);
        var document = JsonGuideDocument.FromSnapshot(snapshot);
        return JsonGuideSerialization.SerializeAsync(document, destination, cancellationToken);
    }
}
