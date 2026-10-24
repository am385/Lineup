using Lineup.Common.Guides;
using Lineup.Extensibility.Guides;
using Lineup.LineupLink.Protocol;

namespace Lineup.LineupLink.Guides;

/// <summary>Maps virtual guide snapshots to the shared management and dedicated Lineup Link wire contract.</summary>
public static class LineupLinkGuideExporter
{
    /// <summary>Exports schedules and one merged root envelope; the first contributing source wins conflicting qualified root attributes.</summary>
    public static LineupLinkGuide Create(VirtualGuideSnapshot snapshot, bool publishProtection = true)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var rootMetadata = snapshot.SupplementalXml.Select(XmltvSupplementalMetadata.Parse).OfType<XmltvSupplementalMetadata>().ToArray();
        return new LineupLinkGuide
        {
            VirtualDeviceId = snapshot.VirtualDeviceId.Value,
            Channels = snapshot.Channels.Select(channel => new LineupLinkGuideChannel
            {
                Key = channel.Number,
                Number = channel.Number,
                Name = channel.Name,
                Affiliate = channel.Affiliate,
                ImageUrl = channel.ImageUrl,
                IsProtected = publishProtection && channel.IsProtected,
                IsFavorite = channel.IsFavorite,
                SupplementalXml = channel.SupplementalXml
            }).ToArray(),
            Programs = snapshot.Channels.SelectMany(channel => channel.Programs.Select(program => new LineupLinkGuideProgram
            {
                ChannelKey = channel.Number,
                StartUtc = program.StartUtc,
                EndUtc = program.EndUtc,
                Title = program.Title,
                Synopsis = program.Synopsis,
                EpisodeTitle = program.EpisodeTitle,
                ImageUrl = program.ImageUrl,
                PosterUrl = program.PosterUrl,
                EpisodeNumber = program.EpisodeNumber,
                OriginalAirdateUtc = program.OriginalAirdateUtc,
                IsFirstAiring = program.IsFirstAiring,
                SeriesId = program.SeriesId,
                Categories = program.Categories,
                Metadata = program.Metadata,
                SupplementalXml = program.SupplementalXml
            })).ToArray(),
            SupplementalXml = XmltvSupplementalMetadata.Create(
                rootMetadata.SelectMany(metadata => metadata.Attributes).DistinctBy(attribute => attribute.Name),
                rootMetadata.SelectMany(metadata => metadata.Elements))
        };
    }
}
