using Lineup.Core.Common.Guides;

namespace Lineup.Plugin.GuideOutputs.Xmltv;

/// <summary>Serializes normalized virtual guide snapshots as XMLTV.</summary>
public static class XmltvGuideSerialization
{
    /// <summary>Creates XMLTV, retaining channels without programs and optionally adding unavailable placeholders.</summary>
    public static byte[] Serialize(VirtualGuideSnapshot snapshot, bool includeUnavailablePrograms = false)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var channels = snapshot.Channels.Select(channel => new TunerChannel
        {
            GuideNumber = channel.Number,
            GuideName = channel.Name,
            URL = string.Empty
        }).ToArray();
        var segments = snapshot.Channels.Select(channel => new GuideChannelSegment
        {
            GuideNumber = channel.Number,
            GuideName = channel.Name,
            Affiliate = channel.Affiliate,
            ImageURL = channel.ImageUrl,
            DRM = channel.IsProtected,
            Favorite = channel.IsFavorite,
            SupplementalXml = channel.SupplementalXml,
            Guide = channel.Programs.Select(program => new GuideCacheProgram
            {
                GuideNumber = channel.Number,
                StartTime = program.StartUtc.ToUnixTimeSeconds(),
                EndTime = program.EndUtc.ToUnixTimeSeconds(),
                Title = program.Title,
                EpisodeTitle = program.EpisodeTitle,
                Synopsis = program.Synopsis,
                ImageURL = program.ImageUrl,
                PosterURL = program.PosterUrl,
                EpisodeNumber = program.EpisodeNumber,
                OriginalAirdate = program.OriginalAirdateUtc?.ToUnixTimeSeconds(),
                First = program.IsFirstAiring switch
                {
                    true => 1,
                    false => 0,
                    null => null
                },
                SeriesID = program.SeriesId,
                Filter = program.Categories.ToList(),
                SupplementalXml = program.SupplementalXml,
                Metadata = program.Metadata
            }).ToList()
        }).ToArray();
        var programs = snapshot.Channels.SelectMany(channel => channel.Programs).ToArray();
        var placeholderStart = programs.Length == 0 ? DateTimeOffset.UtcNow : programs.Min(program => program.StartUtc);
        var placeholderStop = programs.Length == 0 ? placeholderStart.AddDays(1) : programs.Max(program => program.EndUtc);
        return new LineupXmltvWriter().Write(
            new XmltvGuideSnapshot
            {
                Segments = segments,
                SupplementalXml = MergeRootSupplemental(snapshot.SupplementalXml)
            },
            channels,
            placeholderStart,
            placeholderStop,
            includeUnavailablePrograms);
    }

    /// <summary>Writes an XMLTV document to the supplied destination.</summary>
    public static Task SerializeAsync(VirtualGuideSnapshot snapshot, Stream destination, CancellationToken cancellationToken = default, bool includeUnavailablePrograms = false)
    {
        ArgumentNullException.ThrowIfNull(destination);
        return destination.WriteAsync(Serialize(snapshot, includeUnavailablePrograms), cancellationToken).AsTask();
    }

    private static string? MergeRootSupplemental(IReadOnlyList<string> values)
    {
        var supplemental = values.Select(XmltvSupplementalMetadata.Parse).OfType<XmltvSupplementalMetadata>().ToArray();
        return XmltvSupplementalMetadata.Create(
            supplemental.SelectMany(item => item.Attributes).DistinctBy(attribute => attribute.Name),
            supplemental.SelectMany(item => item.Elements));
    }
}
