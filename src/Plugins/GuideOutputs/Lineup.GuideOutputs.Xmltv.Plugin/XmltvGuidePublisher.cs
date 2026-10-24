using Lineup.Common.Guides;
using Lineup.Extensibility.Guides;
using Lineup.GuideOutputs.Xmltv;

namespace Lineup.GuideOutputs.Xmltv.Plugin;

/// <summary>
/// Publishes provider-neutral virtual guide snapshots with Lineup's existing XMLTV writer.
/// </summary>
public sealed class XmltvGuidePublisher : IGuidePublisher
{
    /// <summary>Gets the stable publisher kind.</summary>
    public const string PublisherKind = "xmltv";

    /// <inheritdoc />
    public string Kind => PublisherKind;

    /// <inheritdoc />
    public Task PublishAsync(VirtualGuideSnapshot snapshot, Stream destination, CancellationToken cancellationToken = default) =>
        XmltvGuideSerialization.SerializeAsync(snapshot, destination, cancellationToken);
}
