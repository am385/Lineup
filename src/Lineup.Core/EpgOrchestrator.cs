using Lineup.Core.Storage;
using Lineup.HDHomeRun.Device.Models;
using Microsoft.Extensions.Logging;

namespace Lineup.Core;

/// <summary>
/// Coordinates provider guide imports, normalized storage, and Lineup XMLTV output.
/// </summary>
public class EpgOrchestrator
{
    private readonly ILogger<EpgOrchestrator> _logger;
    private readonly ChannelLineupStore _channelLineupStore;
    private readonly CachedEpgDataProvider _epgDataProvider;
    private readonly IEpgRepository _repository;
    private readonly LineupXmltvWriter _xmltvWriter;
    private readonly IXmltvPublicationStore _publicationStore;

    /// <summary>
    /// Initializes a new instance of the <see cref="EpgOrchestrator"/> class.
    /// </summary>
    public EpgOrchestrator(
        ILogger<EpgOrchestrator> logger,
        ChannelLineupStore channelLineupStore,
        CachedEpgDataProvider epgDataProvider,
        IEpgRepository repository,
        LineupXmltvWriter xmltvWriter,
        IXmltvPublicationStore publicationStore)
    {
        _logger = logger;
        _channelLineupStore = channelLineupStore;
        _epgDataProvider = epgDataProvider;
        _repository = repository;
        _xmltvWriter = xmltvWriter;
        _publicationStore = publicationStore;
    }

    /// <summary>
    /// Downloads and publishes the complete XMLTV guide.
    /// </summary>
    /// <remarks>
    /// The legacy day and interval parameters are retained for command-line compatibility.
    /// SiliconDust determines the available guide duration.
    /// </remarks>
    public async Task GenerateEpgAsync(int days, int hours, string filename)
    {
        await FetchAndStoreEpgAsync(days, force: true);
        await PublishEnabledGuideAsync(days, filename);
    }

    /// <summary>
    /// Downloads the complete XMLTV guide, filters it to current tuner lineups, and replaces the normalized local cache.
    /// </summary>
    /// <remarks>
    /// SiliconDust returns one complete entitlement-based snapshot, so <paramref name="targetDays"/>
    /// and <paramref name="force"/> are retained only for compatibility with existing callers.
    /// </remarks>
    public async Task FetchAndStoreEpgAsync(int targetDays, bool force = false, IProgress<FetchProgressInfo>? progress = null, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Refreshing the complete SiliconDust XMLTV guide");
        var lineupSnapshot = await _channelLineupStore.ReadAsync(cancellationToken)
            ?? throw new InvalidOperationException("No HDHomeRun channel lineup has been saved. Refresh Channels before fetching guide data.");
        var deviceChannels = lineupSnapshot.Channels;
        var deviceChannelsByNumber = deviceChannels
            .Where(channel => !string.IsNullOrWhiteSpace(channel.GuideNumber))
            .GroupBy(channel => channel.GuideNumber.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var rawSegments = await _epgDataProvider.FetchAndStoreRawDataAsync(deviceChannelsByNumber.Values, progress, cancellationToken);

        var enrichedChannels = rawSegments.Select(segment =>
        {
            if (segment.GuideNumber is null ||
                !deviceChannelsByNumber.TryGetValue(segment.GuideNumber, out var deviceChannel))
            {
                return segment;
            }

            return segment with
            {
                GuideName = deviceChannel.GuideName ?? segment.GuideName,
                DRM = deviceChannel.DRM,
                Favorite = deviceChannel.Favorite
            };
        });
        await _repository.StoreChannelsAsync(enrichedChannels);
        await LogCacheStatisticsAsync();
    }

    /// <summary>
    /// Publishes XMLTV generated from authoritative normalized guide data.
    /// </summary>
    /// <param name="days">Placeholder duration for channels without programme data.</param>
    /// <param name="filename">Destination XMLTV file.</param>
    /// <param name="cancellationToken">Cancels publication.</param>
    public Task GenerateEpgFromCacheAsync(int days, string filename, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Publishing Lineup XMLTV from authoritative guide data to {Filename}", filename);
        return PublishEnabledGuideAsync(days, filename, cancellationToken);
    }

    private async Task PublishEnabledGuideAsync(int days, string filename, CancellationToken cancellationToken = default)
    {
        var lineupSnapshot = await _channelLineupStore.ReadAsync(cancellationToken)
            ?? throw new InvalidOperationException("No HDHomeRun channel lineup has been saved. Refresh Channels before publishing guide data.");
        var enabledChannels = lineupSnapshot.Channels
            .Where(channel => lineupSnapshot.IsChannelEnabled(channel.GuideNumber))
            .ToArray();
        var snapshot = await _repository.GetGuideSnapshotAsync(cancellationToken: cancellationToken);
        var start = DateTimeOffset.UtcNow;
        var filteredContent = _xmltvWriter.Write(snapshot, enabledChannels, start, start.AddDays(Math.Max(1, days)));
        await _publicationStore.PublishAsync(
            filename,
            (stream, token) => stream.WriteAsync(filteredContent, token).AsTask(),
            cancellationToken);
    }

    private async Task LogCacheStatisticsAsync()
    {
        var stats = await _repository.GetCacheStatisticsAsync();
        _logger.LogInformation(
            "Guide cache: {Channels} channels and {Programs} programmes spanning {Start:yyyy-MM-dd HH:mm} to {End:yyyy-MM-dd HH:mm} UTC",
            stats.ChannelCount,
            stats.ProgramCount,
            stats.EarliestProgramStart,
            stats.LatestProgramEnd);
    }
}
