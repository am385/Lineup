using System.Collections.Immutable;
using System.Text.Json;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Sinks.OpenTelemetry;

namespace Lineup.Web.Services;

/// <summary>
/// Represents a structured log event safe for display in the Lineup user interface.
/// </summary>
/// <param name="Timestamp">Event timestamp.</param>
/// <param name="Level">Event severity.</param>
/// <param name="Category">Source category.</param>
/// <param name="Message">Rendered event message.</param>
/// <param name="Exception">Rendered exception, when present.</param>
/// <param name="Properties">Redacted structured properties.</param>
public sealed record LineupLogEvent(DateTimeOffset Timestamp, LogEventLevel Level, string Category, string Message, string? Exception, IReadOnlyDictionary<string, string> Properties);

/// <summary>
/// Stores a bounded snapshot of recent application log events.
/// </summary>
public interface ILogEventStore
{
    /// <summary>
    /// Removes all retained events.
    /// </summary>
    void Clear();

    /// <summary>
    /// Returns recent events newest first.
    /// </summary>
    /// <param name="minimumLevel">Minimum included severity.</param>
    /// <param name="category">Optional case-insensitive category text.</param>
    /// <param name="search">Optional case-insensitive message, category, exception, or property text.</param>
    /// <param name="limit">Maximum number of events.</param>
    /// <returns>An immutable event snapshot.</returns>
    IReadOnlyList<LineupLogEvent> GetEvents(LogEventLevel minimumLevel, string? category, string? search, int limit);
}

/// <summary>
/// Captures recent Serilog events in a bounded, thread-safe in-memory collection.
/// </summary>
public sealed class InMemoryLogEventStore : ILogEventStore, ILogEventSink
{
    private const int DefaultCapacity = 2000;
    private readonly int _capacity;
    private readonly Lock _sync = new();
    private readonly Queue<LineupLogEvent> _events = [];

    /// <summary>
    /// Initializes a bounded event store.
    /// </summary>
    /// <param name="capacity">Maximum retained event count.</param>
    public InMemoryLogEventStore(int capacity = DefaultCapacity)
    {
        _capacity = Math.Clamp(capacity, 100, 10000);
    }

    /// <inheritdoc />
    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        var message = logEvent.RenderMessage();
        foreach (var sensitiveValue in logEvent.Properties.SelectMany(pair => SensitiveLogData.GetSensitiveValues(pair.Key, pair.Value)))
        {
            if (!string.IsNullOrEmpty(sensitiveValue))
            {
                message = message.Replace(sensitiveValue, SensitiveLogData.RedactedValue, StringComparison.Ordinal);
            }
        }
        var properties = logEvent.Properties.ToDictionary(
            pair => pair.Key,
            pair => SensitiveLogData.RenderValue(SensitiveLogData.RedactValue(pair.Key, pair.Value)),
            StringComparer.Ordinal);
        var category = properties.TryGetValue("SourceContext", out var source) ? source.Trim('"') : "Lineup";
        var captured = new LineupLogEvent(logEvent.Timestamp, logEvent.Level, category, message, logEvent.Exception?.ToString(), properties.ToImmutableDictionary());

        lock (_sync)
        {
            _events.Enqueue(captured);
            while (_events.Count > _capacity)
            {
                _events.Dequeue();
            }
        }
    }

    /// <inheritdoc />
    public void Clear()
    {
        lock (_sync)
        {
            _events.Clear();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<LineupLogEvent> GetEvents(LogEventLevel minimumLevel, string? category, string? search, int limit)
    {
        LineupLogEvent[] snapshot;
        lock (_sync)
        {
            snapshot = [.. _events];
        }

        var boundedLimit = Math.Clamp(limit, 10, 500);
        return snapshot
            .Reverse()
            .Where(logEvent => logEvent.Level >= minimumLevel)
            .Where(logEvent => string.IsNullOrWhiteSpace(category) || logEvent.Category.Contains(category, StringComparison.OrdinalIgnoreCase))
            .Where(logEvent => MatchesSearch(logEvent, search))
            .Take(boundedLimit)
            .ToArray();
    }

    private static bool MatchesSearch(LineupLogEvent logEvent, string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return true;
        }

        return logEvent.Message.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            logEvent.Category.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            (logEvent.Exception?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
            logEvent.Properties.Any(pair => pair.Key.Contains(search, StringComparison.OrdinalIgnoreCase) || pair.Value.Contains(search, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Describes an external logging target without exposing its configuration values.
/// </summary>
/// <param name="Name">Display name.</param>
/// <param name="Status">Configuration status.</param>
/// <param name="Message">Non-sensitive status detail.</param>
public sealed record ExternalLogTargetStatus(string Name, string Status, string Message);

/// <summary>
/// Describes the logging pipeline selected at application startup.
/// </summary>
public sealed class LoggingRuntimeState
{
    /// <summary>
    /// Initializes logging runtime state.
    /// </summary>
    /// <param name="appDataStore">Persistent application-data store.</param>
    /// <param name="fileSettings">Active file logging settings.</param>
    /// <param name="externalTargets">External target statuses.</param>
    /// <param name="fileManager">Optional runtime-managed file sink.</param>
    /// <param name="applicationFilter">Optional runtime-managed application filter.</param>
    public LoggingRuntimeState(
        AppDataStore appDataStore,
        FileLoggingSettings fileSettings,
        IReadOnlyList<ExternalLogTargetStatus> externalTargets,
        FileLogManager? fileManager = null,
        ApplicationLogFilter? applicationFilter = null)
    {
        AppDataStore = appDataStore;
        LogDirectory = appDataStore.LogDirectoryPath;
        InitialFileSettings = fileSettings;
        ExternalTargets = externalTargets;
        FileManager = fileManager;
        ApplicationFilter = applicationFilter;
    }

    /// <summary>
    /// Initializes logging runtime state from an application-data path for direct tests.
    /// </summary>
    internal LoggingRuntimeState(
        string appDataPath,
        FileLoggingSettings fileSettings,
        IReadOnlyList<ExternalLogTargetStatus> externalTargets,
        FileLogManager? fileManager = null,
        ApplicationLogFilter? applicationFilter = null)
        : this(new AppDataStore(appDataPath), fileSettings, externalTargets, fileManager, applicationFilter)
    {
    }

    /// <summary>
    /// Application-owned log directory.
    /// </summary>
    public string LogDirectory { get; }

    /// <summary>
    /// Persistent application-data store used by logging services.
    /// </summary>
    internal AppDataStore AppDataStore { get; }

    /// <summary>
    /// File settings active for this process.
    /// </summary>
    public FileLoggingSettings FileSettings => FileManager?.Settings ?? InitialFileSettings;

    /// <summary>
    /// Read-only external target statuses.
    /// </summary>
    public IReadOnlyList<ExternalLogTargetStatus> ExternalTargets { get; }

    /// <summary>
    /// Application filter set loaded from startup configuration.
    /// </summary>
    public ApplicationLogFilterSettings StartupApplicationFilter =>
        ApplicationFilter?.StartupSettings ?? new ApplicationLogFilterSettings("Information", []);

    /// <summary>
    /// Application filter set currently active across all sinks.
    /// </summary>
    public ApplicationLogFilterSettings ActiveApplicationFilter =>
        ApplicationFilter?.ActiveSettings ?? StartupApplicationFilter;

    /// <summary>
    /// Applies persisted application logging overrides or restores startup filters.
    /// </summary>
    /// <param name="settings">Persisted application settings.</param>
    public void ApplyApplicationFilter(AppSettings settings)
    {
        var filter = ApplicationFilter ?? throw new InvalidOperationException("The runtime application log filter is unavailable.");
        filter.ApplySettings(settings);
    }

    /// <summary>
    /// File logging settings loaded during startup.
    /// </summary>
    internal FileLoggingSettings InitialFileSettings { get; }

    /// <summary>
    /// Runtime-managed rolling file sink.
    /// </summary>
    internal FileLogManager? FileManager { get; }

    /// <summary>
    /// Runtime-managed application log filter.
    /// </summary>
    internal ApplicationLogFilter? ApplicationFilter { get; }
}

/// <summary>
/// Represents file logging settings active for a Lineup process.
/// </summary>
/// <param name="Enabled">Whether rolling files are enabled.</param>
/// <param name="MinimumLevel">Minimum file severity.</param>
/// <param name="RetentionDays">Number of retained daily periods.</param>
/// <param name="FileSizeLimitMb">Maximum size of each rolling file.</param>
public sealed record FileLoggingSettings(bool Enabled, LogEventLevel MinimumLevel, int RetentionDays, int FileSizeLimitMb);

/// <summary>
/// Owns the rolling file sink so retained files can be cleared safely while logging remains active.
/// </summary>
public sealed class FileLogManager : ILogEventSink, IDisposable
{
    private readonly Lock _sync = new();
    private readonly string _logDirectory;
    private FileLoggingSettings _settings;
    private readonly string _outputTemplate;
    private Serilog.Core.Logger? _logger;
    private DateTimeOffset _minimumTimestamp;

    /// <summary>
    /// Initializes an active rolling file sink.
    /// </summary>
    /// <param name="logDirectory">Application-owned log directory.</param>
    /// <param name="settings">Active file settings.</param>
    /// <param name="outputTemplate">Text output template.</param>
    public FileLogManager(string logDirectory, FileLoggingSettings settings, string outputTemplate)
    {
        _logDirectory = logDirectory;
        _settings = settings;
        _outputTemplate = outputTemplate;
        _minimumTimestamp = settings.Enabled ? DateTimeOffset.MinValue : DateTimeOffset.MaxValue;
        _logger = CreateLogger(settings);
    }

    /// <summary>
    /// File settings currently active in this process.
    /// </summary>
    public FileLoggingSettings Settings
    {
        get
        {
            lock (_sync)
            {
                return _settings;
            }
        }
    }

    /// <inheritdoc />
    public void Emit(LogEvent logEvent)
    {
        lock (_sync)
        {
            if (logEvent.Timestamp >= _minimumTimestamp)
            {
                _logger?.Write(logEvent);
            }
        }
    }

    /// <summary>
    /// Closes the active file, deletes all retained Lineup logs, and starts a fresh file sink.
    /// </summary>
    /// <returns>The number of existing files removed.</returns>
    public int DeleteFiles()
    {
        lock (_sync)
        {
            (_logger as IDisposable)?.Dispose();
            _logger = null;
            var deleted = DeleteExistingFiles();
            _logger = CreateLogger(_settings);
            return deleted;
        }
    }

    /// <summary>
    /// Enables, disables, or rebuilds the rolling file sink with new settings.
    /// </summary>
    /// <param name="settings">Settings to apply immediately.</param>
    public void ApplySettings(FileLoggingSettings settings)
    {
        lock (_sync)
        {
            var previousSettings = _settings;
            var previousMinimumTimestamp = _minimumTimestamp;
            (_logger as IDisposable)?.Dispose();
            _logger = null;
            _settings = settings;
            _minimumTimestamp = settings.Enabled
                ? previousSettings.Enabled ? _minimumTimestamp : DateTimeOffset.UtcNow
                : DateTimeOffset.MaxValue;
            try
            {
                _logger = CreateLogger(settings);
            }
            catch
            {
                _settings = previousSettings;
                _minimumTimestamp = previousMinimumTimestamp;
                _logger = CreateLogger(previousSettings);
                throw;
            }
        }
    }

    private Serilog.Core.Logger? CreateLogger(FileLoggingSettings settings)
    {
        if (!settings.Enabled)
        {
            return null;
        }

        Directory.CreateDirectory(_logDirectory);
        return new LoggerConfiguration()
            .MinimumLevel.Is(settings.MinimumLevel)
            .WriteTo.File(
                Path.Combine(_logDirectory, "lineup-.log"),
                outputTemplate: _outputTemplate,
                rollingInterval: RollingInterval.Day,
                rollOnFileSizeLimit: true,
                fileSizeLimitBytes: settings.FileSizeLimitMb * 1024L * 1024L,
                retainedFileCountLimit: null,
                retainedFileTimeLimit: TimeSpan.FromDays(settings.RetentionDays),
                shared: true)
            .CreateLogger();
    }

    private int DeleteExistingFiles()
    {
        if (!Directory.Exists(_logDirectory))
        {
            return 0;
        }

        var paths = Directory.EnumerateFiles(_logDirectory, "lineup-*.log", SearchOption.TopDirectoryOnly).ToArray();
        foreach (var path in paths)
        {
            File.Delete(path);
        }
        return paths.Length;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_sync)
        {
            (_logger as IDisposable)?.Dispose();
            _logger = null;
        }
    }
}

/// <summary>
/// Lists and opens only application-owned rolling log files.
/// </summary>
public sealed class LogFileService
{
    private readonly LoggingRuntimeState _runtimeState;

    /// <summary>
    /// Initializes the log file service.
    /// </summary>
    /// <param name="runtimeState">Active logging state.</param>
    public LogFileService(LoggingRuntimeState runtimeState)
    {
        _runtimeState = runtimeState;
    }

    /// <summary>
    /// Lists retained Lineup log files newest first.
    /// </summary>
    /// <returns>Retained file metadata.</returns>
    public IReadOnlyList<LogFileInfo> GetFiles()
    {
        if (!_runtimeState.AppDataStore.DirectoryExists(_runtimeState.LogDirectory))
        {
            return [];
        }

        return _runtimeState.AppDataStore.EnumerateFiles(_runtimeState.LogDirectory, "lineup-*.log")
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Select(file => new LogFileInfo(file.Name, file.Length, file.LastWriteTimeUtc))
            .ToArray();
    }

    /// <summary>
    /// Opens a retained Lineup log file by allowlisted base filename.
    /// </summary>
    /// <param name="fileName">Base filename returned by <see cref="GetFiles"/>.</param>
    /// <returns>A readable stream, or <see langword="null"/> when the name is not allowlisted.</returns>
    public Stream? OpenRead(string fileName)
    {
        if (!string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal) ||
            !fileName.StartsWith("lineup-", StringComparison.Ordinal) ||
            !fileName.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return _runtimeState.AppDataStore.OpenRead(Path.Combine(_runtimeState.LogDirectory, fileName));
    }

    /// <summary>
    /// Deletes all retained application log files.
    /// </summary>
    /// <returns>The number of existing files removed.</returns>
    public int DeleteFiles()
    {
        if (_runtimeState.FileManager != null)
        {
            return _runtimeState.FileManager.DeleteFiles();
        }

        if (!_runtimeState.AppDataStore.DirectoryExists(_runtimeState.LogDirectory))
        {
            return 0;
        }

        var paths = _runtimeState.AppDataStore.EnumerateFiles(_runtimeState.LogDirectory, "lineup-*.log");
        foreach (var path in paths)
        {
            _runtimeState.AppDataStore.DeleteFile(path);
        }
        return paths.Count;
    }

    /// <summary>
    /// Applies persisted file logging settings to the running process.
    /// </summary>
    /// <param name="settings">Persisted application settings.</param>
    public void ApplySettings(AppSettings settings)
    {
        var manager = _runtimeState.FileManager ?? throw new InvalidOperationException("The runtime file logger is unavailable.");
        var level = Enum.TryParse<LogEventLevel>(settings.FileLogLevel, ignoreCase: true, out var parsed) ? parsed : LogEventLevel.Information;
        manager.ApplySettings(new FileLoggingSettings(settings.EnableFileLogging, level, settings.FileLogRetentionDays, settings.FileLogSizeLimitMb));
    }
}

/// <summary>
/// Describes a retained application log file.
/// </summary>
/// <param name="Name">Base filename.</param>
/// <param name="Size">File size in bytes.</param>
/// <param name="LastModifiedUtc">Last modification time.</param>
public sealed record LogFileInfo(string Name, long Size, DateTime LastModifiedUtc);

/// <summary>
/// Creates the application logging pipeline.
/// </summary>
public static class LoggingBootstrapper
{
    private const string OutputTemplate = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

    /// <summary>
    /// Configures Serilog and returns services needed by the diagnostics user interface.
    /// </summary>
    /// <param name="builder">Web application builder.</param>
    /// <param name="appDataStore">Persistent application-data store.</param>
    /// <returns>The active logging services.</returns>
    public static LoggingBootstrapResult Configure(WebApplicationBuilder builder, AppDataStore appDataStore)
    {
        var persistedSettings = LoadPersistedSettings(appDataStore);
        var fileSettings = new FileLoggingSettings(persistedSettings.EnableFileLogging, ParseLevel(persistedSettings.FileLogLevel), persistedSettings.FileLogRetentionDays, persistedSettings.FileLogSizeLimitMb);
        var eventStore = new InMemoryLogEventStore();
        var statuses = new List<ExternalLogTargetStatus>();
        var logDirectory = appDataStore.LogDirectoryPath;
        var fileManager = new FileLogManager(logDirectory, fileSettings, OutputTemplate);
        var applicationFilter = ApplicationLogFilter.FromConfiguration(builder.Configuration);
        applicationFilter.ApplySettings(persistedSettings);
        var loggerConfiguration = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .Filter.ByIncludingOnly(applicationFilter.IsEnabled)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", "Lineup")
            .Enrich.WithProperty("RuntimeInstance", Guid.NewGuid().ToString("N"))
            .Enrich.With(new SensitiveLogPropertyEnricher())
            .WriteTo.Console(outputTemplate: OutputTemplate)
            .WriteTo.Sink(eventStore)
            .WriteTo.Async(sink => sink.Sink(fileManager), bufferSize: 5000, blockWhenFull: false);

        ConfigureOpenTelemetry(loggerConfiguration, builder.Configuration, statuses);

        var logger = loggerConfiguration.CreateLogger();
        builder.Host.UseSerilog(logger, dispose: true);
        return new LoggingBootstrapResult(eventStore, new LoggingRuntimeState(appDataStore, fileSettings, statuses, fileManager, applicationFilter));
    }

    /// <summary>
    /// Configures logging from an application-data path for direct tests.
    /// </summary>
    internal static LoggingBootstrapResult Configure(WebApplicationBuilder builder, string appDataPath) =>
        Configure(builder, new AppDataStore(appDataPath));

    private static AppSettings LoadPersistedSettings(AppDataStore appDataStore)
    {
        foreach (var path in new[] { appDataStore.SettingsPath, appDataStore.SettingsBackupPath })
        {
            try
            {
                if (!appDataStore.FileExists(path))
                {
                    continue;
                }

                var settings = JsonSerializer.Deserialize<AppSettings>(appDataStore.ReadAllText(path));
                if (settings != null)
                {
                    return settings;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                Console.Error.WriteLine($"Unable to read logging settings from '{path}': {ex.Message}");
            }
        }

        return new AppSettings();
    }

    private static void ConfigureOpenTelemetry(LoggerConfiguration logger, ConfigurationManager configuration, List<ExternalLogTargetStatus> statuses)
    {
        var endpoint = configuration["Lineup:Logging:OpenTelemetry:Endpoint"];
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            statuses.Add(new ExternalLogTargetStatus("OpenTelemetry (OTLP)", "Disabled", "No endpoint configured."));
            return;
        }

        if (!IsHttpUri(endpoint))
        {
            statuses.Add(new ExternalLogTargetStatus("OpenTelemetry (OTLP)", "Invalid", "The configured endpoint is invalid."));
            return;
        }

        var protocolName = configuration["Lineup:Logging:OpenTelemetry:Protocol"];
        var protocol = string.Equals(protocolName, "grpc", StringComparison.OrdinalIgnoreCase) ? OtlpProtocol.Grpc : OtlpProtocol.HttpProtobuf;
        var headers = configuration.GetSection("Lineup:Logging:OpenTelemetry:Headers").Get<Dictionary<string, string>>() ?? [];
        logger.WriteTo.OpenTelemetry(options =>
        {
            options.Endpoint = endpoint;
            options.Protocol = protocol;
            options.Headers = headers;
            options.ResourceAttributes = new Dictionary<string, object> { ["service.name"] = "Lineup" };
        });
        statuses.Add(new ExternalLogTargetStatus("OpenTelemetry (OTLP)", "Enabled", $"Configured at startup using {protocol}."));
    }

    private static bool IsHttpUri(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    private static LogEventLevel ParseLevel(string level) => Enum.TryParse<LogEventLevel>(level, ignoreCase: true, out var parsed) ? parsed : LogEventLevel.Information;
}

/// <summary>
/// Applies standard .NET logging levels and runtime UI overrides before events reach sinks.
/// </summary>
public sealed class ApplicationLogFilter
{
    private readonly Lock _sync = new();
    private ApplicationLogFilterSettings _activeSettings;

    private ApplicationLogFilter(ApplicationLogFilterSettings startupSettings)
    {
        StartupSettings = startupSettings;
        _activeSettings = startupSettings;
    }

    /// <summary>
    /// Filter set loaded from startup configuration.
    /// </summary>
    public ApplicationLogFilterSettings StartupSettings { get; }

    /// <summary>
    /// Filter set currently applied to all logging sinks.
    /// </summary>
    public ApplicationLogFilterSettings ActiveSettings
    {
        get
        {
            lock (_sync)
            {
                return _activeSettings;
            }
        }
    }

    /// <summary>
    /// Creates a runtime filter from the effective standard .NET logging configuration.
    /// </summary>
    /// <param name="configuration">Merged application configuration.</param>
    /// <returns>A filter initialized with the startup default and category levels.</returns>
    public static ApplicationLogFilter FromConfiguration(IConfiguration configuration)
    {
        var levels = configuration.GetSection("Logging:LogLevel").GetChildren()
            .Select(section => new LogCategoryLevelSetting { Category = section.Key, Level = ApplicationLogLevelNames.Normalize(section.Value) })
            .ToArray();
        var configuredDefault = levels.FirstOrDefault(level => string.Equals(level.Category, "Default", StringComparison.OrdinalIgnoreCase));
        var defaultLevel = configuredDefault?.Level ?? "Information";
        var categories = levels
            .Where(level => !string.Equals(level.Category, "Default", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(level => level.Category.Length)
            .ToArray();
        return new ApplicationLogFilter(new ApplicationLogFilterSettings(defaultLevel, categories));
    }

    /// <summary>
    /// Determines whether an event satisfies the active application filter.
    /// </summary>
    internal bool IsEnabled(LogEvent logEvent)
    {
        ApplicationLogFilterSettings settings;
        settings = ActiveSettings;

        var category = logEvent.Properties.TryGetValue("SourceContext", out var sourceContext) && sourceContext is ScalarValue { Value: string value }
            ? value
            : "";
        var configuredLevel = settings.CategoryLevels.FirstOrDefault(level => CategoryMatches(category, level.Category))?.Level ?? settings.DefaultLevel;
        var minimumLevel = ParseLevel(configuredLevel);
        return minimumLevel.HasValue && logEvent.Level >= minimumLevel.Value;
    }

    /// <summary>
    /// Applies persisted application logging settings.
    /// </summary>
    internal void ApplySettings(AppSettings settings)
    {
        var activeSettings = settings.OverrideLoggingDefaults
            ? CreateSettings(settings.ApplicationLogLevel, settings.LogCategoryOverrides)
            : StartupSettings;
        lock (_sync)
        {
            _activeSettings = activeSettings;
        }
    }

    private static ApplicationLogFilterSettings CreateSettings(string defaultLevel, IEnumerable<LogCategoryLevelSetting> categoryLevels)
    {
        var categories = categoryLevels
            .OfType<LogCategoryLevelSetting>()
            .Select(level => new LogCategoryLevelSetting { Category = level.Category, Level = level.Level })
            .Where(level => !string.IsNullOrWhiteSpace(level.Category))
            .GroupBy(level => level.Category, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .OrderByDescending(level => level.Category.Length)
            .ToArray();
        return new ApplicationLogFilterSettings(defaultLevel, categories);
    }

    private static bool CategoryMatches(string category, string configuredCategory) =>
        category.Equals(configuredCategory, StringComparison.OrdinalIgnoreCase) ||
        (category.StartsWith(configuredCategory, StringComparison.OrdinalIgnoreCase) &&
            category.Length > configuredCategory.Length &&
            category[configuredCategory.Length] == '.');

    private static LogEventLevel? ParseLevel(string? value) => value?.ToUpperInvariant() switch
    {
        "TRACE" => LogEventLevel.Verbose,
        "DEBUG" => LogEventLevel.Debug,
        "INFORMATION" => LogEventLevel.Information,
        "WARNING" => LogEventLevel.Warning,
        "ERROR" => LogEventLevel.Error,
        "CRITICAL" => LogEventLevel.Fatal,
        "NONE" => null,
        _ => LogEventLevel.Information
    };

}

/// <summary>
/// Represents a complete application-level logging filter set.
/// </summary>
/// <param name="DefaultLevel">Default minimum level.</param>
/// <param name="CategoryLevels">Category-prefix minimum levels.</param>
public sealed record ApplicationLogFilterSettings(string DefaultLevel, IReadOnlyList<LogCategoryLevelSetting> CategoryLevels);

/// <summary>
/// Contains services created while bootstrapping logging.
/// </summary>
/// <param name="EventStore">In-memory event store.</param>
/// <param name="RuntimeState">Active logging state.</param>
public sealed record LoggingBootstrapResult(InMemoryLogEventStore EventStore, LoggingRuntimeState RuntimeState);

/// <summary>
/// Detects and redacts sensitive structured logging values.
/// </summary>
internal static class SensitiveLogData
{
    /// <summary>
    /// Replacement text used for sensitive values.
    /// </summary>
    internal const string RedactedValue = "[REDACTED]";
    private static readonly string[] SensitiveFragments = ["authorization", "apikey", "api_key", "token", "secret", "password", "connectionstring", "deviceauth"];

    /// <summary>
    /// Determines whether a property name denotes sensitive data.
    /// </summary>
    internal static bool IsSensitiveName(string name) => SensitiveFragments.Any(fragment => name.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Renders a structured logging value as text.
    /// </summary>
    internal static string RenderValue(LogEventPropertyValue value) => value is ScalarValue { Value: string text } ? text : value.ToString();

    /// <summary>
    /// Recursively redacts sensitive values from a structured logging value.
    /// </summary>
    internal static LogEventPropertyValue RedactValue(string name, LogEventPropertyValue value)
    {
        if (IsSensitiveName(name))
        {
            return new ScalarValue(RedactedValue);
        }

        return value switch
        {
            StructureValue structure => new StructureValue(structure.Properties.Select(property => new LogEventProperty(property.Name, RedactValue(property.Name, property.Value))), structure.TypeTag),
            SequenceValue sequence => new SequenceValue(sequence.Elements.Select(element => RedactValue(string.Empty, element))),
            DictionaryValue dictionary => new DictionaryValue(dictionary.Elements.Select(pair =>
                new KeyValuePair<ScalarValue, LogEventPropertyValue>(pair.Key, RedactValue(pair.Key.Value?.ToString() ?? string.Empty, pair.Value)))),
            _ => value
        };
    }

    /// <summary>
    /// Enumerates sensitive source values for leak detection.
    /// </summary>
    internal static IEnumerable<string> GetSensitiveValues(string name, LogEventPropertyValue value)
    {
        if (IsSensitiveName(name))
        {
            yield return value.ToString().Trim('"');
            yield break;
        }

        if (value is StructureValue structure)
        {
            foreach (var sensitiveValue in structure.Properties.SelectMany(property => GetSensitiveValues(property.Name, property.Value)))
            {
                yield return sensitiveValue;
            }
        }
        else if (value is DictionaryValue dictionary)
        {
            foreach (var sensitiveValue in dictionary.Elements.SelectMany(pair => GetSensitiveValues(pair.Key.Value?.ToString() ?? string.Empty, pair.Value)))
            {
                yield return sensitiveValue;
            }
        }
        else if (value is SequenceValue sequence)
        {
            foreach (var sensitiveValue in sequence.Elements.SelectMany(element => GetSensitiveValues(string.Empty, element)))
            {
                yield return sensitiveValue;
            }
        }
    }
}

/// <summary>
/// Redacts sensitive properties before log events reach configured sinks.
/// </summary>
internal sealed class SensitiveLogPropertyEnricher : ILogEventEnricher
{
    /// <inheritdoc />
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        foreach (var property in logEvent.Properties.ToArray())
        {
            logEvent.AddOrUpdateProperty(new LogEventProperty(property.Key, SensitiveLogData.RedactValue(property.Key, property.Value)));
        }
    }
}
