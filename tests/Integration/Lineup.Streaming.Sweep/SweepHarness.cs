namespace Lineup.Streaming.Sweep;

internal sealed partial class SweepHarness
{

    private const string CompatibilityStorageKey = "lineup-watch-cmaf-compatibility-v1";
    private const string FaultSourceAddress = "http://fault-source:8090";
    private readonly string _root;
    private readonly string _repositoryRoot;
    private readonly string _faultSourceProjectPath;
    private readonly string _runsRoot;
    private readonly string _image;
    private readonly string? _tunerAddress;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly MatrixDefinition[] _matrices;

    public SweepHarness(string root, string repositoryRoot, string projectRoot)
    {
        _root = root;
        _repositoryRoot = repositoryRoot;
        _faultSourceProjectPath = Path.Combine(
            Path.GetDirectoryName(projectRoot) ?? throw new DirectoryNotFoundException($"Unable to locate the parent of '{projectRoot}'."),
            "Lineup.Streaming.Sweep.FaultSource",
            "Lineup.Streaming.Sweep.FaultSource.csproj");
        _runsRoot = Path.Combine(root, "runs");
        _image = Environment.GetEnvironmentVariable("LINEUP_SWEEP_IMAGE")?.Trim() is { Length: > 0 } image
            ? image
            : "lineup-sweep:current";
        _tunerAddress = Environment.GetEnvironmentVariable("LINEUP_SWEEP_TUNER_ADDRESS")?.Trim();
        _matrices =
        [
            new("local-management", 18081, null, null, null, HostingMode.Management, false),
            new("local-managed-process", 18082, 18602, null, null, HostingMode.ManagedProcess, false),
            new("local-external-process", 18083, 18603, null, null, HostingMode.ExternalProcess, false),
            new("remote-management", 18101, null, 18201, null, HostingMode.Management, true),
            new("remote-managed-process", 18102, 18702, 18202, null, HostingMode.ManagedProcess, true),
            new("remote-external-process", 18103, 18703, 18203, null, HostingMode.ExternalProcess, true)
        ];
    }

    public async Task GenerateAsync()
    {
        Directory.CreateDirectory(_runsRoot);
        foreach (var matrix in _matrices)
        {
            var matrixRoot = MatrixRoot(matrix);
            Directory.CreateDirectory(matrixRoot);
            Directory.CreateDirectory(Path.Combine(matrixRoot, "templates", "source"));
            Directory.CreateDirectory(Path.Combine(matrixRoot, "templates", "consumer"));
            var sourceSettings = Path.Combine(matrixRoot, "templates", "source", "settings.json");
            var consumerSettings = Path.Combine(matrixRoot, "templates", "consumer", "settings.json");
            if (!File.Exists(sourceSettings) || (matrix.Remote && !File.Exists(consumerSettings)))
            {
                await GenerateMatrixSettingsAsync(matrix);
            }
            await File.WriteAllTextAsync(Path.Combine(matrixRoot, "compose.yml"), BuildCompose(matrix));
        }
        await File.WriteAllTextAsync(Path.Combine(_root, "matrices.json"), JsonSerializer.Serialize(_matrices, _json));
    }

    public async Task RunSmokeAsync()
    {
        var matrix = _matrices[0];
        var runRoot = Path.Combine(_runsRoot, "smoke");
        if (Directory.Exists(runRoot))
        {
            Directory.Delete(runRoot, true);
        }
        Directory.CreateDirectory(runRoot);
        var journal = new RunJournal(runRoot, _json);
        await journal.WriteEventAsync("smoke-start", new { matrix = matrix.Id });
        try
        {
            await PrepareAndStartAsync(matrix, "lineup-sweep-smoke", journal);
            await using var browser = await OpenBrowserAsync(Path.Combine(runRoot, "browser"));
            await RefreshGuideAsync(browser.Context, matrix.SourceUrl, journal, "source");
            var channels = await ReadEnabledChannelsAsync(matrix.SourceUrl);
            if (channels.Count == 0)
            {
                throw new InvalidOperationException("The smoke topology returned no enabled channels.");
            }
            await RunCompatibilityAsync(browser.Context, matrix.WatchUrl, Path.Combine(runRoot, "compatibility.json"), journal);
            var result = await RunChannelAsync(browser.Context, matrix, channels[0], 1, runRoot, journal);
            if (!result.AvPassed)
            {
                throw new InvalidOperationException($"Smoke playback failed: {result.Failure}");
            }
            await journal.WriteEventAsync("smoke-passed", result);
        }
        finally
        {
            await SaveComposeLogsAsync(matrix, "lineup-sweep-smoke", runRoot);
            await StopMatrixAsync(matrix, "lineup-sweep-smoke", runRoot);
        }
    }

    public async Task RecheckPostFixFailuresAsync()
    {
        var referenceResultsPath = Path.Combine(_runsRoot, "run-20261018-234438", "results.json");
        var referenceResults = JsonSerializer.Deserialize<IReadOnlyList<ChannelAttemptResult>>(
            await File.ReadAllTextAsync(referenceResultsPath),
            _json)
            ?? throw new InvalidOperationException($"Unable to read reference results from {referenceResultsPath}.");
        var failedChannels = referenceResults
            .Where(result => !result.AvPassed)
            .Select(result => result.Channel)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        await ExecuteAsync(failedChannels, $"post-fix-failure-recheck-{DateTime.UtcNow:yyyyMMdd-HHmmss}");
    }

    public async Task ExecuteAsync(IReadOnlySet<string>? channelNumbers = null, string? runId = null, IReadOnlySet<string>? matrixIds = null)
    {
        var runRoot = runId is null
            ? GetOrCreateMainRunRoot()
            : Path.Combine(_runsRoot, runId);
        Directory.CreateDirectory(runRoot);
        var journal = new RunJournal(runRoot, _json);
        if (!File.Exists(Path.Combine(runRoot, "run.json")))
        {
            await WriteRunManifestAsync(runRoot);
        }
        var completed = await journal.ReadTerminalKeysAsync();
        IReadOnlyList<ChannelDefinition>? canonicalChannels = null;

        foreach (var matrix in _matrices.Where(matrix => matrixIds is null || matrixIds.Contains(matrix.Id)))
        {
            var project = $"lineup-sweep-{matrix.Id}";
            var matrixRunRoot = Path.Combine(runRoot, matrix.Id);
            Directory.CreateDirectory(matrixRunRoot);
            await journal.WriteEventAsync("matrix-start", new { matrix = matrix.Id });
            try
            {
                await PrepareAndStartAsync(matrix, project, journal);
                await using var browser = await OpenBrowserAsync(Path.Combine(matrixRunRoot, "browser"));
                await RefreshGuideAsync(browser.Context, matrix.SourceUrl, journal, "source");
                if (matrix.Remote)
                {
                    await RefreshGuideAsync(browser.Context, matrix.WatchUrl, journal, "consumer");
                }

                var channels = await ReadEnabledChannelsAsync(matrix.WatchUrl);
                canonicalChannels ??= channelNumbers is null
                    ? channels
                    : channels.Where(channel => channelNumbers.Contains(channel.Number)).ToArray();
                await File.WriteAllTextAsync(
                    Path.Combine(matrixRunRoot, "channels.json"),
                    JsonSerializer.Serialize(channels, _json));
                await File.WriteAllTextAsync(
                    Path.Combine(runRoot, "canonical-channels.json"),
                    JsonSerializer.Serialize(canonicalChannels, _json));
                await journal.WriteEventAsync("channel-inventory", new
                {
                    matrix = matrix.Id,
                    count = channels.Count,
                    canonicalCount = canonicalChannels.Count,
                    differences = CompareChannels(canonicalChannels, channels)
                });
                if (channels.Count == 0)
                {
                    throw new InvalidOperationException($"{matrix.Id} returned no enabled channels.");
                }

                await RunCompatibilityAsync(
                    browser.Context,
                    matrix.WatchUrl,
                    Path.Combine(matrixRunRoot, "compatibility.json"),
                    journal);

                foreach (var channel in canonicalChannels)
                {
                    var terminalKey = TerminalKey(matrix.Id, channel.Number);
                    if (completed.Contains(terminalKey))
                    {
                        continue;
                    }

                    var attempts = new List<ChannelAttemptResult>();
                    for (var attempt = 1; attempt <= 3; attempt++)
                    {
                        var attemptResult = await RunChannelAsync(browser.Context, matrix, channel, attempt, matrixRunRoot, journal);
                        attempts.Add(attemptResult);
                        if (attemptResult.Failure?.Contains("Cleanup failed:", StringComparison.Ordinal) == true)
                        {
                            var recoveryRoot = Path.Combine(
                                matrixRunRoot,
                                "recoveries",
                                channel.Number.Replace('.', '_'),
                                attempt.ToString(CultureInfo.InvariantCulture));
                            await SaveComposeLogsAsync(matrix, project, recoveryRoot);
                            await StopMatrixAsync(matrix, project, recoveryRoot);
                            await PrepareAndStartAsync(matrix, project, journal);
                            await RefreshGuideAsync(browser.Context, matrix.SourceUrl, journal, "source-recovery");
                            if (matrix.Remote)
                            {
                                await RefreshGuideAsync(browser.Context, matrix.WatchUrl, journal, "consumer-recovery");
                            }
                        }
                        if (!ShouldRetry(attemptResult))
                        {
                            break;
                        }
                    }
                    var final = SelectTerminalResult(attempts);
                    await journal.WriteTerminalAsync(matrix.Id, channel.Number, final!);
                    completed.Add(terminalKey);
                }
                await journal.WriteEventAsync("matrix-complete", new { matrix = matrix.Id });
            }
            catch (Exception ex)
            {
                await journal.WriteEventAsync("matrix-failed", new { matrix = matrix.Id, error = ex.ToString() });
                throw;
            }
            finally
            {
                await SaveComposeLogsAsync(matrix, project, matrixRunRoot);
                await StopMatrixAsync(matrix, project, matrixRunRoot);
            }
        }

        await BuildReportsAsync(runRoot);
        await journal.WriteEventAsync("run-complete", new { completedAtUtc = DateTimeOffset.UtcNow });
        await File.WriteAllTextAsync(Path.Combine(runRoot, "completed.marker"), DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
    }
}
