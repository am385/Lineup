using Lineup.HDHomeRun.Api;
using Lineup.HDHomeRun.Api.Models;
using Lineup.HDHomeRun.Device.Models;
using Microsoft.Extensions.Logging;

namespace Lineup.Core.Storage;

/// <summary>
/// Imports SiliconDust XMLTV guide data into Lineup's normalized local cache.
/// </summary>
public class CachedEpgDataProvider
{
    private readonly ILogger<CachedEpgDataProvider> _logger;
    private readonly HDHomeRunApiClient _apiClient;
    private readonly SiliconDustXmltvParser _parser;
    private readonly XmltvGuideStore _guideStore;
    private readonly IEpgRepository _repository;
    private readonly GuideGenerationCoordinator _generationCoordinator;

    /// <summary>
    /// Initializes a new instance of the <see cref="CachedEpgDataProvider"/> class.
    /// </summary>
    public CachedEpgDataProvider(
        ILogger<CachedEpgDataProvider> logger,
        HDHomeRunApiClient apiClient,
        SiliconDustXmltvParser parser,
        XmltvGuideStore guideStore,
        IEpgRepository repository,
        GuideGenerationCoordinator generationCoordinator)
    {
        _logger = logger;
        _apiClient = apiClient;
        _parser = parser;
        _guideStore = guideStore;
        _repository = repository;
        _generationCoordinator = generationCoordinator;
    }

    /// <summary>
    /// Downloads and validates the SiliconDust XMLTV guide, filters it to available tuner
    /// channels, and atomically replaces the canonical guide and normalized cache.
    /// </summary>
    /// <param name="availableChannels">Channels currently returned by configured tuners.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="cancellationToken">Cancels the refresh before publication.</param>
    /// <returns>The filtered normalized guide segments.</returns>
    public async Task<List<HDHomeRunChannelEpgSegment>> FetchAndStoreRawDataAsync(IEnumerable<HDHomeRunChannel> availableChannels, IProgress<FetchProgressInfo>? progress = null, CancellationToken cancellationToken = default)
    {
        await _repository.EnsureDatabaseCreatedAsync();
        progress?.Report(CreateProgress(FetchStatus.Fetching, "Downloading the SiliconDust XMLTV guide..."));

        var content = await _apiClient.FetchXmltvAsync(cancellationToken);
        var downloadedSegments = _parser.Parse(content);
        if (downloadedSegments.Count == 0 || downloadedSegments.Sum(segment => segment.Guide.Count) == 0)
        {
            throw new InvalidDataException("The SiliconDust XMLTV guide did not contain any usable channel programme data.");
        }

        var filteredContent = _parser.FilterByChannels(content, availableChannels);
        var segments = _parser.Parse(filteredContent).ToList();
        var programmeCount = segments.Sum(segment => segment.Guide.Count);
        progress?.Report(CreateProgress(FetchStatus.Storing, $"Storing {segments.Count} channels and {programmeCount:N0} programmes...", segments.Count, programmeCount));

        await _generationCoordinator.ExecuteAsync(async transitionToken =>
        {
            using var stagedGuide = await _guideStore.StageAsync(filteredContent, transitionToken);
            stagedGuide.Commit();
            await _repository.ReplaceRawEpgDataAsync(segments, transitionToken);
            await _guideStore.RecordGenerationAsync(filteredContent, CancellationToken.None);
        }, cancellationToken);

        _logger.LogInformation("Imported {ChannelCount} of {DownloadedChannelCount} SiliconDust XMLTV channels available in the tuner lineup with {ProgramCount} programmes",
                               segments.Count, downloadedSegments.Count, programmeCount);
        progress?.Report(CreateProgress(FetchStatus.Completed, $"Imported {segments.Count} channels and {programmeCount:N0} programmes", segments.Count, programmeCount));
        return segments;
    }

    /// <summary>
    /// Rebuilds the normalized cache from the authoritative canonical XMLTV guide.
    /// </summary>
    /// <param name="cancellationToken">A token used to cancel reconciliation.</param>
    public async Task ReconcileFromCanonicalGuideAsync(CancellationToken cancellationToken = default)
    {
        await _generationCoordinator.ExecuteAsync(async transitionToken =>
        {
            var content = await _guideStore.ReadAsync(transitionToken);
            if (content == null || await _guideStore.IsGenerationRecordedAsync(content, transitionToken))
            {
                return;
            }

            var segments = _parser.Parse(content).ToList();
            if (segments.Count == 0 || segments.Sum(segment => segment.Guide.Count) == 0)
            {
                throw new InvalidDataException("The canonical XMLTV guide did not contain any usable channel programme data.");
            }

            await _repository.EnsureDatabaseCreatedAsync();
            await _repository.ReplaceRawEpgDataAsync(segments, transitionToken);
            await _guideStore.RecordGenerationAsync(content, CancellationToken.None);
            _logger.LogInformation("Reconciled the normalized guide cache from the authoritative canonical XMLTV guide");
        }, cancellationToken);
    }

    private static FetchProgressInfo CreateProgress(FetchStatus status, string message, int channels = 0, int programmes = 0)
    {
        return new FetchProgressInfo
        {
            Status = status,
            FetchCount = status == FetchStatus.Initializing ? 0 : 1,
            TotalChannelsFetched = channels,
            TotalProgramsFetched = programmes,
            Message = message
        };
    }
}
