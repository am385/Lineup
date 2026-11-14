using System.Text.Json;
using Lineup.Core.Common.Guides;
using Lineup.Core.Common.Streaming;
using Lineup.Core.Extensibility.Guides;

namespace Lineup.Plugin.GuideOutputs.Xmltv.Plugin;

/// <summary>
/// Publishes provider-neutral virtual guide snapshots with Lineup's existing XMLTV writer.
/// </summary>
public sealed class XmltvGuidePublisher : IConfiguredGuidePublisher
{
    /// <summary>Gets the stable publisher kind.</summary>
    public const string PublisherKind = "xmltv";

    /// <summary>Gets the kind.</summary>
    public string Kind => PublisherKind;

    /// <summary>Executes publish asynchronously.</summary>
    /// <param name="snapshot">The snapshot value.</param>
    /// <param name="destination">The destination value.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>A task that completes when the operation has finished.</returns>
    public Task PublishAsync(VirtualGuideSnapshot snapshot, Stream destination, CancellationToken cancellationToken = default) =>
        XmltvGuideSerialization.SerializeAsync(snapshot, destination, cancellationToken);

    /// <summary>Publishes XMLTV with the selected output's unavailable-program policy.</summary>
    public Task PublishAsync(VirtualGuideSnapshot snapshot, Stream destination, AdapterConfiguration configuration, CancellationToken cancellationToken = default)
    {
        var errors = new XmltvGuideOutputDriver().Validate(configuration);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(" ", errors));
        }
        var typed = configuration.Configuration.Deserialize<XmltvGuideOutputConfiguration>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        return XmltvGuideSerialization.SerializeAsync(snapshot, destination, cancellationToken, typed.IncludeUnavailablePrograms);
    }
}
