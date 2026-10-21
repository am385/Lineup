namespace Lineup.Streaming.Sweep;

internal sealed partial class SweepHarness
{
    /// <summary>Sets validated common policy before any deployment operation.</summary>
    public void Configure(SweepOptions options, CancellationToken cancellationToken)
    {
        options.Validate();
        _options = options with
        {
            BrowserExecutable = options.BrowserExecutable ?? Environment.GetEnvironmentVariable("LINEUP_SWEEP_BROWSER_EXECUTABLE")?.Trim()
        };
        _cancellation = cancellationToken;
    }

    /// <summary>Resumes an explicit canonical run without silently adopting legacy artifacts.</summary>
    public Task ResumeAsync(string inputRun) => ExecuteRunAsync(Path.GetDirectoryName(Path.GetFullPath(inputRun))!, inputRun);

    private async Task CheckpointAsync(Action? mutation = null)
    {
        await _checkpointGate.WaitAsync();
        try
        {
            mutation?.Invoke();
            if (_run is not null && _runRoot is not null)
            {
                await SweepStore.SaveAsync(_runRoot, _run);
            }
        }
        finally
        {
            _checkpointGate.Release();
        }
    }

    private async Task ExecuteRunAsync(string root, string? inputRun = null)
    {
        _options.Validate();
        Directory.CreateDirectory(root);
        // Exclusive across processes using this output root, held throughout playback and teardown.
        Directory.CreateDirectory(_root);
        await using var lease = new FileStream(Path.Combine(_root, "sweep.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        await using var runLease = SweepStore.AcquireRunLease(root);
        _runRoot = root;
        var canonicalPath = inputRun ?? Path.Combine(root, "sweep-run.json");
        if (File.Exists(canonicalPath))
        {
            _run = await SweepStore.LoadAsync(canonicalPath);
            if (_run.State == "complete")
            {
                throw new InvalidOperationException("This run is already complete. Start an explicit new run.");
            }
            if (JsonSerializer.Serialize(_options, SweepStore.Json) != JsonSerializer.Serialize(_run.Options, SweepStore.Json))
            {
                throw new InvalidDataException("Effective resume options changed; use a new run.");
            }
            _run.State = "running";
            _run.FinishedUtc = null;
        }
        else
        {
            if (File.Exists(Path.Combine(root, "events.jsonl")))
            {
                throw new InvalidDataException("Legacy run cannot be resumed without a canonical checkpoint. Use execute-fresh.");
            }
            _run = new SweepRun { Id = Path.GetFileName(root), Options = _options };
            _run.Environment["os"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
            _run.Environment["harness"] = typeof(SweepHarness).Assembly.GetName().Version?.ToString() ?? "unknown";
            _run.Environment["image"] = _options.ExistingInstance ? "existing deployment; image unavailable" : _image;
        }
        var journal = new RunJournal(root, _json);
        await journal.ReadTerminalKeysAsync(requireVersion: true); // Validate/repair only a torn final journal record.
        var matrices = _options.ExistingInstance
            ? [new MatrixDefinition("existing", 0, null, null, null, HostingMode.Management, false, _options.BaseUrl)]
            : _matrices.Where(matrix => _options.Matrices.Length == 0 || _options.Matrices.Contains(matrix.Id, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (matrices.Length == 0 || (!_options.ExistingInstance && _options.Matrices.Except(_matrices.Select(matrix => matrix.Id), StringComparer.OrdinalIgnoreCase).Any()))
        {
            throw new ArgumentException("Unknown or empty matrix selection.");
        }
        await CheckpointAsync();
        try
        {
            // Browser dependencies are checked before Docker or physical tuner work.
            await using (var probe = await OpenBrowserAsync(Path.Combine(root, "browser-preflight")))
            {
                var version = probe.Context.Browser?.Version ?? "persistent Chromium";
                if (_run.Environment.TryGetValue("browser", out var previousBrowser) && previousBrowser != version)
                {
                    throw new InvalidDataException("Browser version changed since checkpoint; use a new run.");
                }
                _run.Environment["browser"] = version;
            }
            if (!_options.ExistingInstance)
            {
                var image = await RunProcessAsync("docker", $"image inspect {_image} --format {{{{.Id}}}}", _repositoryRoot);
                var identity = image.Output.Trim();
                if (_run.Environment.TryGetValue("imageId", out var previous) && previous != identity)
                {
                    throw new InvalidDataException("Docker image identity changed since checkpoint.");
                }
                _run.Environment["imageId"] = identity;
            }
            foreach (var matrix in matrices)
            {
                _cancellation.ThrowIfCancellationRequested();
                var matrixRoot = Path.Combine(root, matrix.Id);
                Directory.CreateDirectory(matrixRoot);
                var project = $"lineup-sweep-{matrix.Id}";
                if (!_options.ExistingInstance && _run.Attempts.Concat(_run.Compatibility).Any(attempt => attempt.FinishedUtc is null
                    && (attempt.Key.StartsWith(matrix.Id + "|", StringComparison.Ordinal) || attempt.Key == matrix.WatchUrl)))
                {
                    var previousStatus = await GetStatusAsync(matrix.WatchUrl);
                    _generation = RuntimeIdentity(previousStatus);
                    await ReconcileInterruptedAsync(matrix);
                }
                try
                {
                    if (!_options.ExistingInstance)
                    {
                        await PrepareAndStartAsync(matrix, project, journal);
                    }
                    await using var browser = await OpenBrowserAsync(_options.BrowserProfile ?? Path.Combine(matrixRoot, "browser"));
                    if (!_options.ExistingInstance)
                    {
                        await RefreshGuideAsync(browser.Context, matrix.SourceUrl, journal, "source");
                        if (matrix.Remote)
                        {
                            await RefreshGuideAsync(browser.Context, matrix.WatchUrl, journal, "consumer");
                        }
                    }
                    var channels = _options.Select(await ReadEnabledChannelsAsync(matrix.WatchUrl));
                    await PreflightAsync(matrix, browser.Context, matrixRoot, journal);
                    var frozen = _run.Selected.Where(row => row.Target == matrix.Id).ToArray();
                    var selected = channels.Select(channel => new SelectedChannel($"{matrix.Id}|{channel.VirtualDeviceId}|{channel.Number}", matrix.Id, channel)).ToArray();
                    if (frozen.Length > 0 && JsonSerializer.Serialize(frozen, SweepStore.Json) != JsonSerializer.Serialize(selected, SweepStore.Json))
                    {
                        throw new InvalidDataException($"Projected inventory changed for {matrix.Id}. Start a new run.");
                    }
                    if (frozen.Length == 0)
                    {
                        _run.Selected.AddRange(selected);
                    }
                    await CheckpointAsync();
                    await ReconcileInterruptedAsync(matrix);
                    foreach (var row in selected)
                    {
                        _cancellation.ThrowIfCancellationRequested();
                        if (_run.Accepted.ContainsKey(row.Key))
                        {
                            var previousAttempt = _run.Attempts.Single(item => item.Id == _run.Accepted[row.Key]);
                            if (previousAttempt.Verdict == SweepVerdict.StablePlayback)
                            {
                                var resumedSync = SweepPolicy.FreshSynchronization(_run, previousAttempt)
                                    ?? await ObserveAsync(browser.Context, matrix, row, 1, matrixRoot, journal, "sync");
                                previousAttempt.Synchronization = resumedSync.Synchronization;
                                await CheckpointAsync();
                                if (resumedSync.Cleanup is not { VerifiedAbsent: true, Forced: false } || resumedSync.Verdict == SweepVerdict.CapacityContamination)
                                {
                                    throw new InvalidOperationException("Resumed synchronization contaminated capacity or failed cleanup.");
                                }
                            }
                            continue;
                        }
                        SweepAttempt? accepted = null;
                        for (var number = 1; number <= _options.MaximumAttempts; number++)
                        {
                            _cancellation.ThrowIfCancellationRequested();
                            var attempt = await ObserveAsync(browser.Context, matrix, row, number, matrixRoot, journal, _options.SyncOnly ? "sync" : "playback");
                            if (attempt.Cleanup is not { VerifiedAbsent: true, Forced: false } || attempt.Verdict == SweepVerdict.CapacityContamination)
                            {
                                _run.Violations.Add($"First contamination/lifecycle trigger: {row.Key}, attempt {attempt.Id}.");
                                await CheckpointAsync();
                                if (!_options.ExistingInstance && _options.AllowMatrixRecovery && number < _options.MaximumAttempts)
                                {
                                    _run.Violations.Add($"Capacity-release deployment recovery required by {attempt.Id}; violation is permanent.");
                                    var recovery = Path.Combine(matrixRoot, "recoveries", attempt.Id);
                                    await SaveComposeLogsAsync(matrix, project, recovery);
                                    await StopMatrixAsync(matrix, project, recovery);
                                    await PrepareAndStartAsync(matrix, project, journal);
                                    await RefreshGuideAsync(browser.Context, matrix.SourceUrl, journal, "source-recovery");
                                    if (matrix.Remote)
                                    {
                                        await RefreshGuideAsync(browser.Context, matrix.WatchUrl, journal, "consumer-recovery");
                                    }
                                    await PreflightAsync(matrix, browser.Context, matrixRoot, journal, recovery: true);
                                    continue;
                                }
                                throw new InvalidOperationException($"Stopped at contamination: {row.Key}. Evidence: {root}");
                            }
                            accepted = accepted?.Result?.AvPassed == true ? accepted : attempt;
                            if (attempt.Result is not null && !ShouldRetry(attempt.Result))
                            {
                                break;
                            }
                            await Task.Delay(TimeSpan.FromSeconds(_options.RetryDelaySeconds), _cancellation);
                        }
                        if (accepted is null)
                        {
                            throw new InvalidOperationException($"No uncontaminated attempt for {row.Key}.");
                        }
                        _run.Accepted[row.Key] = accepted.Id;
                        _run.CaptionAccepted[row.Key] = _run.Attempts.Where(item => item.Key == row.Key && item.Purpose == "playback" && item.Result is not null
                            && !item.Interrupted && item.Cleanup is { VerifiedAbsent: true, Forced: false } && item.Verdict != SweepVerdict.CapacityContamination)
                            .OrderByDescending(item => GetCaptionResultScore(item.Result!.CaptionResult)).Select(item => item.Id).FirstOrDefault() ?? accepted.Id;
                        if (!_options.SyncOnly && accepted.Verdict == SweepVerdict.StablePlayback)
                        {
                            var sync = await ObserveAsync(browser.Context, matrix, row, 1, matrixRoot, journal, "sync");
                            accepted.Synchronization = sync.Synchronization;
                            if (sync.Cleanup is not { VerifiedAbsent: true, Forced: false } || sync.Verdict == SweepVerdict.CapacityContamination)
                            {
                                _run.Violations.Add($"Synchronization lifecycle failed at {row.Key}.");
                                throw new InvalidOperationException("Fresh synchronization contaminated capacity or failed cleanup.");
                            }
                        }
                        await journal.WriteTerminalAsync(matrix.Id, row.Channel.Number, accepted.Result!);
                        await SweepStore.ReportsAsync(root, _run);
                        Console.WriteLine($"{matrix.Id}: {row.Channel.Number}: {accepted.Verdict}; {_run.Accepted.Count}/{_run.Selected.Count} accepted. Artifacts: {root}");
                    }
                }
                finally
                {
                    _attempt = null;
                    if (!_options.ExistingInstance)
                    {
                        try
                        {
                            await SaveComposeLogsAsync(matrix, project, matrixRoot);
                        }
                        finally
                        {
                            await StopMatrixAsync(matrix, project, matrixRoot);
                        }
                    }
                }
            }
            _run.State = "complete";
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            _run.State = "cancelled";
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or HttpRequestException or PlaywrightException or JsonException or TimeoutException)
        {
            _run.State = "inconclusive";
            _run.Violations.Add($"{ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine(ex);
        }
        finally
        {
            _run.FinishedUtc = DateTimeOffset.UtcNow;
            await SweepStore.ReportsAsync(root, _run);
            Environment.ExitCode = SweepPolicy.ExitCode(_run);
            Console.WriteLine($"Run state: {_run.State}; exit: {Environment.ExitCode}; canonical results: {Path.Combine(root, "sweep-run.json")}");
        }
    }

    private async Task<SweepAttempt> ObserveAsync(
        IBrowserContext context, MatrixDefinition matrix, SelectedChannel row, int number, string matrixRoot, RunJournal journal, string purpose)
    {
        var attempt = new SweepAttempt { Id = Guid.NewGuid().ToString("N"), Key = row.Key, Purpose = purpose };
        _run!.Attempts.Add(attempt);
        _attempt = attempt;
        await CheckpointAsync();
        try
        {
            attempt.Result = await RunChannelAsync(context, matrix, row.Channel, number, matrixRoot, journal);
            attempt.Verdict = SweepPolicy.Classify(attempt.Result, attempt.Starts, attempt);
            attempt.FinishedUtc = DateTimeOffset.UtcNow;
        }
        finally
        {
            await CheckpointAsync();
            _attempt = null;
        }
        return attempt;
    }

    private async Task ReconcileInterruptedAsync(MatrixDefinition matrix)
    {
        foreach (var attempt in _run!.Attempts.Concat(_run.Compatibility).Where(item => item.FinishedUtc is null
            && (item.Key.StartsWith(matrix.Id + "|", StringComparison.Ordinal) || item.Key == matrix.WatchUrl)))
        {
            if (attempt.Ownership.Any(owner => owner.Generation != _generation))
            {
                throw new InvalidDataException("Interrupted ownership belongs to another runtime generation; do not stop reused identifiers.");
            }
            var baseline = attempt.Baseline?.Deserialize<PlaybackBaseline>(_json);
            attempt.Cleanup = await new OwnedPlayback(_http).CleanupAsync(
                attempt.Ownership, () => Task.CompletedTask, TimeSpan.FromSeconds(_options.CleanupSeconds),
                baseline is null ? null : token => VerifyBaselineAsync(matrix, baseline, new(), token));
            attempt.Interrupted = true;
            attempt.FinishedUtc = DateTimeOffset.UtcNow;
            _run.Limitations.Add($"Interrupted operation {attempt.Id} reconciled; original playback remains unresolved and requires a fresh attempt.");
            if (!attempt.Cleanup.VerifiedAbsent)
            {
                throw new InvalidOperationException("Interrupted owned sessions could not be reconciled.");
            }
        }
        await CheckpointAsync();
    }

    private async Task PreflightAsync(MatrixDefinition matrix, IBrowserContext context, string matrixRoot, RunJournal journal, bool recovery = false)
    {
        _cancellation.ThrowIfCancellationRequested();
        var status = await GetStatusAsync(matrix.WatchUrl);
        if (status["application"]?["startupStatus"]?.GetValue<string>() != "ready" || status["application"]?["setupComplete"]?.GetValue<bool>() != true)
        {
            throw new InvalidDataException("Lineup is not ready or setup is incomplete.");
        }
        var devices = status["configuration"]!["streaming"]!["virtualDevices"]!.AsArray();
        var player = devices.Single(item => item?["enabled"]?.GetValue<bool>() == true && item["adapter"]?["kind"]?.GetValue<string>() == "web-player")!;
        var virtualId = player["virtualDeviceId"]!.GetValue<string>();
        var worker = status["virtualOutputWorkers"]?.AsArray().SingleOrDefault(item => item?["virtualDeviceId"]?.GetValue<string>() == virtualId);
        var executionMode = player["hosting"]?["executionMode"]?.GetValue<string>();
        if (executionMode is not null && !executionMode.Equals("management", StringComparison.OrdinalIgnoreCase)
            && !executionMode.Equals("inProcess", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(worker?["state"]?.GetValue<string>(), "Healthy", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The selected Web Player worker is not healthy.");
        }
        _generation = RuntimeIdentity(status);
        var fingerprint = JsonSerializer.Serialize(new
        {
            matrix.WatchUrl,
            version = status["application"]!["version"]!.GetValue<string>(),
            topology = status["configuration"]!["streaming"],
            runtime = _options.ExistingInstance ? _generation : "owned deployment; generations recorded independently"
        }, _json);
        if (!recovery && _run!.Provenance.ContainsKey(matrix.Id))
        {
            SweepStore.ValidateResume(_run, _options, matrix.Id, fingerprint);
        }
        _run!.Provenance[matrix.Id] = fingerprint;
        await ReconcileInterruptedAsync(matrix);
        _run.Environment[$"{matrix.Id}:generation:{_generation}"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        _run.Environment[$"{matrix.Id}:application"] = status["application"]!.ToJsonString();
        _allowedOrigins.Clear();
        _allowedOrigins.Add(new Uri(matrix.WatchUrl).GetLeftPart(UriPartial.Authority));
        if (player["hosting"] is not null)
        {
            var binding = player["hosting"]?["bindings"]?.AsArray().FirstOrDefault();
            if (!Uri.TryCreate(binding?["publicBaseUri"]?.GetValue<string>(), UriKind.Absolute, out var uri)
                || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.AbsolutePath != "/")
            {
                throw new InvalidDataException("Watch's first dedicated listener has no usable, non-redacted public origin.");
            }
            _allowedOrigins.Add(uri.GetLeftPart(UriPartial.Authority));
        }
        foreach (var origin in _allowedOrigins)
        {
            var inventory = origin == new Uri(matrix.WatchUrl).GetLeftPart(UriPartial.Authority) ? "/api/stream/cmaf/sessions" : "/media/sessions";
            await new OwnedPlayback(_http).InventoryAsync(origin + inventory, _cancellation);
        }
        var page = await context.NewPageAsync();
        try
        {
            await page.GotoAsync(matrix.WatchUrl.TrimEnd('/') + "/watch", new() { Timeout = (float)(_options.StartupSeconds * 1000) });
            await page.Locator(".cmaf-channel-number").First.WaitForAsync(new() { Timeout = (float)(_options.StartupSeconds * 1000) });
            if (_options.SetupCompatibility || !_options.ExistingInstance)
            {
                await RunCompatibilityAsync(context, matrix.WatchUrl, Path.Combine(matrixRoot, "compatibility.json"), journal);
            }
            var profileText = await page.EvaluateAsync<string?>($"localStorage.getItem('{CompatibilityStorageKey}')");
            var profileJson = new JsonSerializerOptions(_json) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
            var profile = profileText is null ? null : JsonSerializer.Deserialize<CmafCompatibilityProfile>(profileText, profileJson);
            if (!CmafCompatibilityProfilePolicy.IsValid(profile))
            {
                throw new InvalidDataException("A complete current compatibility profile is required; use --setup-compatibility explicitly on existing instances.");
            }
            var browserIdentity = await page.EvaluateAsync<string>("() => `${navigator.userAgent}|${navigator.platform || ''}`");
            if (profile!.BrowserIdentity != browserIdentity)
            {
                throw new InvalidDataException("Compatibility browser identity does not match the current browser.");
            }
            var profileFingerprint = JsonSerializer.Serialize(profile with { CompletedAtUtc = DateTimeOffset.UnixEpoch }, profileJson);
            var profileKey = matrix.Id + ":compatibility";
            if (!recovery && _run.Provenance.TryGetValue(profileKey, out var previousProfile) && previousProfile != profileFingerprint)
            {
                throw new InvalidDataException("Compatibility capabilities changed since checkpoint; use a new run.");
            }
            _run.Provenance[profileKey] = profileFingerprint;
            _run.Environment[$"{matrix.Id}:profile"] = profileText!;
            var storage = await page.EvaluateAsync<string>(
                $"() => JSON.stringify(Object.fromEntries(Object.entries(localStorage).filter(([k]) => k.startsWith('lineup-watch') && k !== '{CompatibilityStorageKey}').sort(([a],[b]) => a.localeCompare(b))))");
            var storageKey = matrix.Id + ":preferences";
            if (!recovery && _run.Provenance.TryGetValue(storageKey, out var previousStorage) && previousStorage != storage)
            {
                throw new InvalidDataException("Watch preferences/overrides changed since checkpoint.");
            }
            _run.Provenance[storageKey] = storage;
            _run.Environment[$"{matrix.Id}:storage"] = storage;
            _run.Limitations.Add("Optional decoded byte counters, source codec diagnostics and caption cues may be unavailable; missing values are not success evidence.");
            await CheckpointAsync();
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    private static string RuntimeIdentity(JsonNode status)
    {
        var player = status["configuration"]!["streaming"]!["virtualDevices"]!.AsArray()
            .Single(item => item?["enabled"]?.GetValue<bool>() == true && item["adapter"]?["kind"]?.GetValue<string>() == "web-player")!;
        var worker = status["virtualOutputWorkers"]?.AsArray().SingleOrDefault(item => item?["virtualDeviceId"]?.GetValue<string>() == player["virtualDeviceId"]?.GetValue<string>());
        return $"{status["application"]!["instanceId"]}|{status["application"]!["startedAtUtc"]}|{worker?["generation"]}";
    }
}
