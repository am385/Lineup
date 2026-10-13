using Lineup.Core.Settings;

namespace Lineup.Core.Guides;

/// <summary>
/// Reads the active union guide projection for Lineup's presentation surfaces.
/// </summary>
public interface ILineupGuideReader
{
    /// <summary>Gets active guide channels and their programs in an optional UTC window.</summary>
    Task<IReadOnlyList<GuideChannelSegment>> GetChannelsAsync(
        DateTimeOffset? startUtc = null,
        DateTimeOffset? endUtc = null,
        CancellationToken cancellationToken = default);

    /// <summary>Gets active programs in an optional UTC window.</summary>
    Task<IReadOnlyList<GuideCacheProgram>> GetProgramsAsync(
        DateTimeOffset? startUtc = null,
        DateTimeOffset? endUtc = null,
        CancellationToken cancellationToken = default);

    /// <summary>Gets statistics for the active union guide projection.</summary>
    Task<GuideStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Default guide reader backed by the host-neutral guide query service.
/// </summary>
public sealed class LineupGuideReader : ILineupGuideReader
{
    private readonly IGuideQueryService _guides;
    private readonly ILineupSettingsService _settings;

    /// <summary>Initializes a guide reader.</summary>
    public LineupGuideReader(IGuideQueryService guides, ILineupSettingsService settings)
    {
        _guides = guides;
        _settings = settings;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GuideChannelSegment>> GetChannelsAsync(
        DateTimeOffset? startUtc = null,
        DateTimeOffset? endUtc = null,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await _guides.BuildUnionSnapshotAsync(_settings.Settings.Streaming, startUtc, endUtc, cancellationToken);
        return snapshot.Channels.Select(MapChannel).ToArray();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GuideCacheProgram>> GetProgramsAsync(
        DateTimeOffset? startUtc = null,
        DateTimeOffset? endUtc = null,
        CancellationToken cancellationToken = default)
    {
        var channels = await GetChannelsAsync(startUtc, endUtc, cancellationToken);
        return channels.SelectMany(channel => channel.Guide).OrderBy(program => program.StartTime).ToArray();
    }

    /// <inheritdoc />
    public Task<GuideStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default) =>
        _guides.GetStatisticsAsync(_settings.Settings.Streaming, cancellationToken);

    private static GuideChannelSegment MapChannel(VirtualGuideChannel channel) =>
        new()
        {
            GuideNumber = channel.Number,
            GuideName = channel.Name,
            Affiliate = channel.Affiliate,
            ImageURL = channel.ImageUrl,
            DRM = channel.IsProtected,
            Favorite = channel.IsFavorite,
            SupplementalXml = channel.SupplementalXml,
            Guide = channel.Programs.Select(program => MapProgram(channel.Number, program)).ToList()
        };

    private static GuideCacheProgram MapProgram(string channelNumber, GuideProgram program) =>
        new()
        {
            GuideNumber = channelNumber,
            Title = program.Title,
            EpisodeTitle = program.EpisodeTitle,
            Synopsis = program.Synopsis,
            StartTime = program.StartUtc.ToUnixTimeSeconds(),
            EndTime = program.EndUtc.ToUnixTimeSeconds(),
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
        };
}
