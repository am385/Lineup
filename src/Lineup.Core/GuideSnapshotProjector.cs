using Lineup.HDHomeRun.Api.Models;
using Lineup.HDHomeRun.Device.Models;

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
    public XmltvGuideSnapshot Project(XmltvGuideSnapshot providerSnapshot, IEnumerable<HDHomeRunChannel> channels)
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
    public IReadOnlyList<HDHomeRunChannelEpgSegment> Project(IEnumerable<HDHomeRunChannelEpgSegment> providerSegments, IEnumerable<HDHomeRunChannel> channels)
    {
        ArgumentNullException.ThrowIfNull(providerSegments);
        ArgumentNullException.ThrowIfNull(channels);

        var segments = providerSegments
            .Where(segment => !string.IsNullOrWhiteSpace(segment.GuideNumber))
            .GroupBy(segment => segment.GuideNumber!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => MergeSegments(group.Key, group), StringComparer.OrdinalIgnoreCase);
        var programmeRange = segments.Values
            .SelectMany(segment => segment.Guide)
            .Where(programme => programme.EndTime > programme.StartTime)
            .Aggregate(
                (HasValue: false, Start: 0L, End: 0L),
                (range, programme) => !range.HasValue
                    ? (true, programme.StartTime, programme.EndTime)
                    : (true, Math.Min(range.Start, programme.StartTime), Math.Max(range.End, programme.EndTime)));
        if (!programmeRange.HasValue)
        {
            throw new InvalidDataException("The normalized guide has no usable programme range for placeholder data.");
        }

        return channels
            .Where(channel => !string.IsNullOrWhiteSpace(channel.GuideNumber))
            .DistinctBy(channel => channel.GuideNumber.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderBy(channel => channel.GuideNumber, ChannelNumberComparer.Instance)
            .Select(channel => ProjectChannel(channel, segments, programmeRange.Start, programmeRange.End))
            .ToArray();
    }

    private static HDHomeRunChannelEpgSegment MergeSegments(string guideNumber, IEnumerable<HDHomeRunChannelEpgSegment> segments)
    {
        var primary = segments.First();
        return primary with
        {
            GuideNumber = guideNumber,
            Guide = segments
                .SelectMany(segment => segment.Guide)
                .GroupBy(programme => new { programme.StartTime, programme.EndTime, programme.Title })
                .Select(group => group.First() with { GuideNumber = guideNumber })
                .OrderBy(programme => programme.StartTime)
                .ToList()
        };
    }

    private static HDHomeRunChannelEpgSegment ProjectChannel(
        HDHomeRunChannel channel,
        IReadOnlyDictionary<string, HDHomeRunChannelEpgSegment> segments,
        long placeholderStart,
        long placeholderEnd)
    {
        var guideNumber = channel.GuideNumber.Trim();
        if (segments.TryGetValue(guideNumber, out var segment) && segment.Guide.Count > 0)
        {
            return segment;
        }

        return new HDHomeRunChannelEpgSegment
        {
            GuideNumber = guideNumber,
            GuideName = segment?.GuideName ?? channel.GuideName,
            Affiliate = segment?.Affiliate,
            ImageURL = segment?.ImageURL,
            Guide =
            [
                new HDHomeRunProgram
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
