using Lineup.Common.Guides;
using Lineup.Extensibility.Guides;

namespace Lineup.Core;

/// <summary>
/// Projects normalized provider data onto the current physical lineup.
/// </summary>
public sealed class GuideSnapshotProjector
{
    private const string PlaceholderTitle = "Not Available";

    /// <summary>
    /// Retains requested physical channels while preserving guide-level metadata.
    /// </summary>
    /// <param name="providerSnapshot">Normalized provider snapshot.</param>
    /// <param name="channels">Physical channels to retain.</param>
    /// <returns>A projected snapshot containing every requested channel.</returns>
    public XmltvGuideSnapshot Project(XmltvGuideSnapshot providerSnapshot, IEnumerable<TunerChannel> channels)
    {
        ArgumentNullException.ThrowIfNull(providerSnapshot);
        return providerSnapshot with { Segments = Project(providerSnapshot.Segments, channels) };
    }

    /// <summary>
    /// Retains requested physical channels and supplies full-range placeholders when provider data is unavailable.
    /// </summary>
    /// <param name="providerSegments">Normalized data from one or more guide providers.</param>
    /// <param name="channels">Physical channels to retain.</param>
    /// <returns>A normalized snapshot containing every requested channel.</returns>
    public IReadOnlyList<GuideChannelSegment> Project(IEnumerable<GuideChannelSegment> providerSegments, IEnumerable<TunerChannel> channels)
    {
        ArgumentNullException.ThrowIfNull(providerSegments);
        ArgumentNullException.ThrowIfNull(channels);

        var segments = providerSegments
            .Where(segment => !string.IsNullOrWhiteSpace(segment.GuideNumber))
            .GroupBy(segment => segment.GuideNumber!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => MergeSegments(group.Key, group), StringComparer.OrdinalIgnoreCase);
        var programRange = segments.Values
            .SelectMany(segment => segment.Guide)
            .Where(program => program.EndTime > program.StartTime)
            .Aggregate(
                (HasValue: false, Start: 0L, End: 0L),
                (range, program) => !range.HasValue
                    ? (true, program.StartTime, program.EndTime)
                    : (true, Math.Min(range.Start, program.StartTime), Math.Max(range.End, program.EndTime)));
        if (!programRange.HasValue)
        {
            throw new InvalidDataException("The normalized guide has no usable program range for placeholder data.");
        }

        return channels
            .Where(channel => !string.IsNullOrWhiteSpace(channel.GuideNumber))
            .DistinctBy(channel => channel.GuideNumber.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderBy(channel => channel.GuideNumber, ChannelNumberComparer.Instance)
            .Select(channel => ProjectChannel(channel, segments, programRange.Start, programRange.End))
            .ToArray();
    }

    private static GuideChannelSegment MergeSegments(string guideNumber, IEnumerable<GuideChannelSegment> segments)
    {
        var primary = segments.First();
        return primary with
        {
            GuideNumber = guideNumber,
            Guide = segments
                .SelectMany(segment => segment.Guide)
                .GroupBy(program => new { program.StartTime, program.EndTime, program.Title })
                .Select(group => group.First() with { GuideNumber = guideNumber })
                .OrderBy(program => program.StartTime)
                .ToList()
        };
    }

    private static GuideChannelSegment ProjectChannel(
        TunerChannel channel,
        IReadOnlyDictionary<string, GuideChannelSegment> segments,
        long placeholderStart,
        long placeholderEnd)
    {
        var guideNumber = channel.GuideNumber.Trim();
        if (segments.TryGetValue(guideNumber, out var segment) && segment.Guide.Count > 0)
        {
            return segment;
        }

        return new GuideChannelSegment
        {
            GuideNumber = guideNumber,
            GuideName = segment?.GuideName ?? channel.GuideName,
            Affiliate = segment?.Affiliate,
            ImageURL = segment?.ImageURL,
            SupplementalXml = segment?.SupplementalXml,
            Guide =
            [
                new GuideCacheProgram
                {
                    GuideNumber = guideNumber,
                    Title = PlaceholderTitle,
                    StartTime = placeholderStart,
                    EndTime = placeholderEnd
                }
            ]
        };
    }
}
