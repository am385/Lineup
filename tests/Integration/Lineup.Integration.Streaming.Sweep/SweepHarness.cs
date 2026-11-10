namespace Lineup.Integration.Streaming.Sweep;

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
    private SweepOptions _options = new();
    private CancellationToken _cancellation;
    private SweepRun? _run;
    private SweepAttempt? _attempt;
    private string? _runRoot;
    private string _generation = string.Empty;
    private readonly SemaphoreSlim _checkpointGate = new(1, 1);
    private readonly HashSet<string> _allowedOrigins = new(StringComparer.Ordinal);

    public SweepHarness(string root, string repositoryRoot, string projectRoot)
    {
        _root = root;
        _repositoryRoot = repositoryRoot;
        _faultSourceProjectPath = Path.Combine(
            Path.GetDirectoryName(projectRoot) ?? throw new DirectoryNotFoundException($"Unable to locate the parent of '{projectRoot}'."),
            "Lineup.Integration.Streaming.Sweep.FaultSource",
            "Lineup.Integration.Streaming.Sweep.FaultSource.csproj");
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

    /// <summary>Validates cached tuner targets before generating settings or updating compose definitions under the output-root lock.</summary>
    public async Task GenerateAsync()
    {
        _cancellation.ThrowIfCancellationRequested();
        if (_options.ExistingInstance)
        {
            return;
        }
        _ = GetTunerAddress();
        Directory.CreateDirectory(_root);
        await using var lease = new FileStream(Path.Combine(_root, "sweep.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        await ValidateMatrixInputsAsync(_matrices, allowMissing: true);
        Directory.CreateDirectory(_runsRoot);
        foreach (var matrix in _matrices)
        {
            var matrixRoot = MatrixRoot(matrix);
            Directory.CreateDirectory(matrixRoot);
            Directory.CreateDirectory(Path.Combine(matrixRoot, "templates", "source"));
            Directory.CreateDirectory(Path.Combine(matrixRoot, "templates", "consumer"));
            var sourceSettings = Path.Combine(matrixRoot, "templates", "source", "settings.json");
            var consumerSettings = Path.Combine(matrixRoot, "templates", "consumer", "settings.json");
            if (!File.Exists(sourceSettings) && (!matrix.Remote || !File.Exists(consumerSettings)))
            {
                await GenerateMatrixSettingsAsync(matrix);
            }
            await File.WriteAllTextAsync(Path.Combine(matrixRoot, "compose.yml"), BuildCompose(matrix));
        }
        await File.WriteAllTextAsync(Path.Combine(_root, "matrices.json"), JsonSerializer.Serialize(_matrices, _json));
    }

    /// <summary>Retries failed channels only when their canonical tuner provenance still matches the effective input.</summary>
    public async Task RecheckPostFixFailuresAsync(string inputRun)
    {
        var reference = await SweepStore.LoadAsync(inputRun);
        ValidateRunTuner(reference, inputRun);
        var failedChannels = SweepPolicy.RecheckChannels(reference);
        if (failedChannels.Count == 0)
        {
            throw new InvalidOperationException("Input run has no failed or pending channels.");
        }
        _options = reference.Options with { Include = failedChannels.Order(StringComparer.OrdinalIgnoreCase).ToArray() };
        await GenerateAsync();
        await ExecuteAsync(failedChannels, $"post-fix-failure-recheck-{DateTime.UtcNow:yyyyMMdd-HHmmss}");
    }

    /// <summary>Executes selected channels and matrices only after offline input validation.</summary>
    public async Task ExecuteAsync(IReadOnlySet<string>? channelNumbers = null, string? runId = null, IReadOnlySet<string>? matrixIds = null)
    {
        await ValidateMatrixInputsAsync(_matrices, allowMissing: false);
        _options = _options with
        {
            Include = channelNumbers?.Order(StringComparer.OrdinalIgnoreCase).ToArray() ?? _options.Include,
            Matrices = matrixIds?.Order(StringComparer.OrdinalIgnoreCase).ToArray() ?? _options.Matrices
        };
        await ExecuteRunAsync(runId is null ? GetOrCreateMainRunRoot() : Path.Combine(_runsRoot, runId));
    }
}
