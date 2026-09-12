using Lineup.Core.Storage;
using Lineup.HDHomeRun.Device;
using Microsoft.Extensions.Logging;

namespace Lineup.Core;

/// <summary>
/// Coordinates canonical XMLTV downloads, normalized guide imports, and public guide output.
/// </summary>
public class EpgOrchestrator
{
    private readonly ILogger<EpgOrchestrator> _logger;
    private readonly HDHomeRunDeviceClient _deviceService;
    private readonly CachedEpgDataProvider _epgDataProvider;
    private readonly IEpgRepository _repository;
    private readonly XmltvGuideStore _guideStore;

    /// <summary>
    /// Initializes a new instance of the <see cref="EpgOrchestrator"/> class.
    /// </summary>
    public EpgOrchestrator(ILogger<EpgOrchestrator> logger, HDHomeRunDeviceClient deviceService, CachedEpgDataProvider epgDataProvider, IEpgRepository repository, XmltvGuideStore guideStore)
    {
        _logger = logger;
        _deviceService = deviceService;
        _epgDataProvider = epgDataProvider;
        _repository = repository;
        _guideStore = guideStore;
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
        await _guideStore.CopyToAsync(filename);
    }

    /// <summary>
    /// Downloads the complete XMLTV guide and replaces the normalized local cache.
    /// </summary>
    /// <remarks>
    /// SiliconDust returns one complete entitlement-based snapshot, so <paramref name="targetDays"/>
    /// and <paramref name="force"/> are retained only for compatibility with existing callers.
    /// </remarks>
    public async Task FetchAndStoreEpgAsync(int targetDays, bool force = false, IProgress<FetchProgressInfo>? progress = null, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Refreshing the complete SiliconDust XMLTV guide");
        var rawSegments = await _epgDataProvider.FetchAndStoreRawDataAsync(progress, cancellationToken);
        var deviceChannels = await _deviceService.FetchChannelLineupAsync(cancellationToken);
        var deviceChannelsByNumber = deviceChannels
            .Where(channel => !string.IsNullOrWhiteSpace(channel.GuideNumber))
            .GroupBy(channel => channel.GuideNumber, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

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
                DRM = deviceChannel.DRM
            };
        });
        await _repository.StoreChannelsAsync(enrichedChannels);
        await LogCacheStatisticsAsync();
    }

    /// <summary>
    /// Rebuilds the normalized guide cache from the authoritative canonical XMLTV guide.
    /// </summary>
    /// <param name="cancellationToken">A token used to cancel reconciliation.</param>
    public Task ReconcileCacheAsync(CancellationToken cancellationToken = default)
    {
        return _epgDataProvider.ReconcileFromCanonicalGuideAsync(cancellationToken);
    }

    /// <summary>
    /// Copies the last downloaded canonical XMLTV guide to an output file.
    /// </summary>
    /// <remarks>SiliconDust determines the available guide duration; <paramref name="days"/> is retained for compatibility.</remarks>
    public Task GenerateEpgFromCacheAsync(int days, string filename)
    {
        _logger.LogInformation("Publishing the cached canonical XMLTV guide to {Filename}", filename);
        return _guideStore.CopyToAsync(filename);
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
