namespace Lineup.Web.Services;

/// <summary>
/// Requests a restart-safe factory reset of Lineup-owned persistent state.
/// </summary>
public interface IFactoryResetService
{
    /// <summary>
    /// Persists a reset request and begins graceful application shutdown.
    /// </summary>
    /// <param name="cancellationToken">Cancels marker creation before shutdown begins.</param>
    Task RequestResetAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Coordinates factory-reset requests with the application host lifecycle.
/// </summary>
public sealed class FactoryResetService : IFactoryResetService
{
    private readonly IAppDataStore _appDataStore;
    private readonly ILineupSettingsService _settings;
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly ILogger<FactoryResetService> _logger;

    /// <summary>
    /// Initializes a factory-reset service.
    /// </summary>
    /// <param name="appDataStore">Lineup persistent application-data store.</param>
    /// <param name="settings">Current persisted application settings.</param>
    /// <param name="applicationLifetime">Application shutdown controller.</param>
    /// <param name="logger">Reset diagnostics logger.</param>
    public FactoryResetService(IAppDataStore appDataStore, ILineupSettingsService settings, IHostApplicationLifetime applicationLifetime, ILogger<FactoryResetService> logger)
    {
        _appDataStore = appDataStore;
        _settings = settings;
        _applicationLifetime = applicationLifetime;
        _logger = logger;
    }

    /// <summary>
    /// Initializes a factory-reset service from an application-data path for direct tests.
    /// </summary>
    internal FactoryResetService(string appDataPath, ILineupSettingsService settings, IHostApplicationLifetime applicationLifetime, ILogger<FactoryResetService> logger)
        : this(new AppDataStore(appDataPath), settings, applicationLifetime, logger)
    {
    }

    /// <inheritdoc />
    public async Task RequestResetAsync(CancellationToken cancellationToken = default)
    {
        var request = new FactoryResetRequest
        {
            PersistedXmltvOutputPath = _settings.Settings.XmltvOutputPath,
            PersistedGuideOutputPaths = _settings.Settings.Streaming.GuideOutputs
                .Select(output => output.Adapter.Configuration)
                .Where(configuration => configuration.ValueKind == System.Text.Json.JsonValueKind.Object)
                .Select(configuration => configuration.TryGetProperty("OutputPath", out var path) ? path.GetString() : null)
                .OfType<string>()
                .ToArray()
        };
        await FactoryResetCoordinator.WriteRequestAsync(_appDataStore, request, cancellationToken);
        _logger.LogWarning("Factory reset requested; Lineup will erase owned persistent state during the next startup");
        _applicationLifetime.StopApplication();
    }
}
