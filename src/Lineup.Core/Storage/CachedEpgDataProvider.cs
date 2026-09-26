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
    private readonly IEpgRepository _repository;
    private readonly GuideGenerationCoordinator _generationCoordinator;
    private readonly IEpgRetentionPolicy _retentionPolicy;
    private readonly GuideSnapshotProjector _projector;

    /// <summary>
    /// Initializes a new instance of the <see cref="CachedEpgDataProvider"/> class.
    /// </summary>
    public CachedEpgDataProvider(
        ILogger<CachedEpgDataProvider> logger,
        HDHomeRunApiClient apiClient,
        SiliconDustXmltvParser parser,
        IEpgRepository repository,
        GuideGenerationCoordinator generationCoordinator)
        : this(logger, apiClient, parser, repository, generationCoordinator, new DefaultEpgRetentionPolicy(), new GuideSnapshotProjector())
    {
    }

    /// <summary>
    /// Initializes a normalized guide importer with a configurable history policy.
    /// </summary>
    public CachedEpgDataProvider(
        ILogger<CachedEpgDataProvider> logger,
        HDHomeRunApiClient apiClient,
        SiliconDustXmltvParser parser,
        IEpgRepository repository,
        GuideGenerationCoordinator generationCoordinator,
        IEpgRetentionPolicy retentionPolicy)
        : this(logger, apiClient, parser, repository, generationCoordinator, retentionPolicy, new GuideSnapshotProjector())
    {
    }

    /// <summary>
    /// Initializes a normalized guide importer with configurable retention and lineup projection.
    /// </summary>
    public CachedEpgDataProvider(
        ILogger<CachedEpgDataProvider> logger,
        HDHomeRunApiClient apiClient,
        SiliconDustXmltvParser parser,
        IEpgRepository repository,
        GuideGenerationCoordinator generationCoordinator,
        IEpgRetentionPolicy retentionPolicy,
        GuideSnapshotProjector projector)
    {
        _logger = logger;
        _apiClient = apiClient;
        _parser = parser;
        _repository = repository;
        _generationCoordinator = generationCoordinator;
        _retentionPolicy = retentionPolicy;
        _projector = projector;
    }

    /// <summary>
    /// Downloads and validates the SiliconDust XMLTV guide, projects it onto available tuner
    /// channels, and atomically imports the normalized snapshot.
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
        var downloadedSnapshot = _parser.Parse(content);
        if (downloadedSnapshot.Segments.Count == 0 || downloadedSnapshot.Segments.Sum(segment => segment.Guide.Count) == 0)
        {
            throw new InvalidDataException("The SiliconDust XMLTV guide did not contain any usable channel programme data.");
        }

        var snapshot = _projector.Project(downloadedSnapshot, availableChannels);
        var segments = snapshot.Segments.ToList();
        var programmeCount = segments.Sum(segment => segment.Guide.Count);
        progress?.Report(CreateProgress(FetchStatus.Storing, $"Storing {segments.Count} channels and {programmeCount:N0} programmes...", segments.Count, programmeCount));

        await _generationCoordinator.ExecuteAsync(async transitionToken =>
        {
            await _repository.ImportGuideAsync(snapshot, _retentionPolicy.HistoryRetention, transitionToken);
        }, cancellationToken);

        _logger.LogInformation("Imported {ChannelCount} of {DownloadedChannelCount} SiliconDust XMLTV channels available in the tuner lineup with {ProgramCount} programmes",
                               segments.Count, downloadedSnapshot.Segments.Count, programmeCount);
        progress?.Report(CreateProgress(FetchStatus.Completed, $"Imported {segments.Count} channels and {programmeCount:N0} programmes", segments.Count, programmeCount));
        return segments;
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
