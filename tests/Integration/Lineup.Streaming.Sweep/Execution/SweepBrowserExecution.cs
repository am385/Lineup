namespace Lineup.Streaming.Sweep;

internal sealed partial class SweepHarness
{
    private async Task RefreshGuideAsync(IBrowserContext context, string baseUrl, RunJournal journal, string role)
    {
        if (_options.ExistingInstance)
        {
            throw new InvalidOperationException("Existing-instance mode forbids guide refresh.");
        }
        var page = await context.NewPageAsync();
        try
        {
            await page.GotoAsync(baseUrl, new() { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 120_000 });
            await page.Locator("#fetchGuide").WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 120_000 });
            await page.Locator("#fetchGuide").ClickAsync();
            var deadline = DateTimeOffset.UtcNow.AddMinutes(12);
            var observedRunning = false;
            while (DateTimeOffset.UtcNow < deadline)
            {
                var status = await GetStatusAsync(baseUrl);
                var running = status["guide"]?["autoFetch"]?["running"]?.GetValue<bool>() ?? false;
                var count = status["channels"]?["count"]?.GetValue<int>() ?? 0;
                observedRunning |= running;
                if (!running && count > 0 && (observedRunning || status["guide"]?["channelCount"]?.GetValue<int?>() > 0))
                {
                    await journal.WriteEventAsync("guide-ready", new { role, baseUrl, channelCount = count });
                    return;
                }
                await Task.Delay(2_000);
            }
            throw new TimeoutException($"Guide refresh did not complete for {baseUrl}.");
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    private async Task RunCompatibilityAsync(IBrowserContext context, string baseUrl, string outputPath, RunJournal journal)
    {
        var page = await context.NewPageAsync();
        var errors = new List<string>();
        var matrix = new MatrixDefinition("compatibility", 0, null, null, null, HostingMode.Management, false, baseUrl);
        var baseline = await CaptureBaselineAsync(matrix);
        var observation = new SweepAttempt
        {
            Id = Guid.NewGuid().ToString("N"),
            Key = baseUrl,
            Purpose = "compatibility",
            SyntheticPlayback = true
        };
        observation.Baseline = JsonSerializer.SerializeToNode(baseline, _json);
        _run?.Compatibility.Add(observation);
        await CheckpointAsync();
        var tasks = new System.Collections.Concurrent.ConcurrentQueue<Task>();
        var pending = new System.Collections.Concurrent.ConcurrentDictionary<IRequest, StartupRequestEvidence>();
        page.Request += (_, request) =>
        {
            if (request.Url.Contains("/api/stream/watch-test/start", StringComparison.Ordinal))
            {
                var item = new StartupRequestEvidence(Guid.NewGuid().ToString("N"), request.Url, DateTimeOffset.UtcNow, null);
                pending[request] = item;
                tasks.Enqueue(CheckpointAsync(() => observation.Requests.Add(item)));
            }
        };
        page.RequestFailed += (_, request) =>
        {
            if (pending.TryRemove(request, out var item))
            {
                tasks.Enqueue(CheckpointAsync(() => item.Failure = TruncateOutcomeText(request.Failure)));
            }
        };
        page.Response += (_, response) =>
        {
            if (pending.TryGetValue(response.Request, out var item))
            {
                tasks.Enqueue(CaptureStartupAndReleaseAsync(response, observation, journal, item, pending));
            }
        };
        page.Console += (_, message) =>
        {
            if (message.Type == "error")
            {
                errors.Add(message.Text);
            }
        };
        page.PageError += (_, error) =>
        {
            errors.Add(error);
            observation.BrowserFailures.Add(TruncateOutcomeText(error) ?? "Compatibility script failed.");
        };
        page.Crash += (_, _) => observation.BrowserFailures.Add("Compatibility page crashed.");
        try
        {
            await page.GotoAsync($"{baseUrl.TrimEnd('/')}/watch-test", new() { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 120_000 }).WaitAsync(_cancellation);
            await page.EvaluateAsync($"localStorage.removeItem('{CompatibilityStorageKey}')");
            await page.GotoAsync($"{baseUrl.TrimEnd('/')}/watch-test?runAll=true", new() { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 120_000 }).WaitAsync(_cancellation);
            await page.WaitForFunctionAsync(
                $"() => localStorage.getItem('{CompatibilityStorageKey}') !== null",
                null,
                new() { Timeout = 1_800_000, PollingInterval = 1_000 }).WaitAsync(_cancellation);
            await page.Locator("#runAllCompatibilityTests").WaitForAsync(new()
            {
                State = WaitForSelectorState.Visible,
                Timeout = 120_000
            });
            await page.Locator("#cmafTestVideoPlayer").WaitForAsync(new()
            {
                State = WaitForSelectorState.Detached,
                Timeout = 120_000
            });
            var profile = await page.EvaluateAsync<string>($"localStorage.getItem('{CompatibilityStorageKey}')");
            await File.WriteAllTextAsync(outputPath, profile);
            await journal.WriteEventAsync("compatibility-complete", new { baseUrl, errors });
        }
        finally
        {
            try
            {
                observation.Cleanup = await new OwnedPlayback(_http).CleanupAsync(
                    observation.Ownership,
                    async () =>
                    {
                        await page.GotoAsync("about:blank", new() { Timeout = 5_000 });
                        await Task.WhenAll(tasks.ToArray()).WaitAsync(TimeSpan.FromSeconds(_options.StartupSeconds));
                    },
                    TimeSpan.FromSeconds(_options.CleanupSeconds),
                    token => VerifyBaselineAsync(matrix, baseline, pending, token));
                observation.FinishedUtc = DateTimeOffset.UtcNow;
                await CheckpointAsync();
                if (observation.Cleanup.Forced || !observation.Cleanup.VerifiedAbsent)
                {
                    throw new InvalidOperationException("Compatibility setup failed its owned-session cleanup invariant.");
                }
            }
            finally
            {
                await page.CloseAsync();
            }
        }
    }

    private async Task<ChannelAttemptResult> RunChannelAsync(
        IBrowserContext context,
        MatrixDefinition matrix,
        ChannelDefinition channel,
        int attempt,
        string matrixRunRoot,
        RunJournal journal,
        bool requireAudio = true)
    {
        _cancellation.ThrowIfCancellationRequested();
        await EnsureTunerIdleAsync(TimeSpan.FromMinutes(2));
        var safeChannel = string.Concat(channel.Number.Select(character => char.IsLetterOrDigit(character) ? character : '_'));
        var evidence = _attempt ?? new SweepAttempt { Id = Guid.NewGuid().ToString("N"), Key = $"{matrix.Id}|{channel.VirtualDeviceId}|{channel.Number}" };
        var attemptRoot = Path.Combine(matrixRunRoot, "attempts", safeChannel, evidence.Id);
        evidence.Artifacts = Path.GetRelativePath(_runRoot ?? matrixRunRoot, attemptRoot);
        Directory.CreateDirectory(attemptRoot);
        var baseline = await CaptureBaselineAsync(matrix);
        evidence.Baseline = JsonSerializer.SerializeToNode(baseline, _json);
        var tracePath = Path.Combine(attemptRoot, "trace.zip");
        await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = false });
        var page = await context.NewPageAsync();
        if (evidence.Purpose == "sync")
        {
            // Playwright routing disables the HTTP cache for this fresh observation.
            await page.RouteAsync("**/*", route => route.ContinueAsync());
        }
        var console = new List<object>();
        var network = new List<object>();
        var startResponseSource = new TaskCompletionSource<IResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var startFailureSource = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var captureTasks = new System.Collections.Concurrent.ConcurrentQueue<Task>();
        var fragments = new System.Collections.Concurrent.ConcurrentQueue<FragmentSample>();
        var representations = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var pendingStarts = new System.Collections.Concurrent.ConcurrentDictionary<IRequest, StartupRequestEvidence>();
        page.Request += (_, request) =>
        {
            if (IsCmafStartUrl(request.Url))
            {
                var requestEvidence = new StartupRequestEvidence(Guid.NewGuid().ToString("N"), request.Url, DateTimeOffset.UtcNow,
                    request.PostData is { } body ? SafeStartupRequest(body) : null);
                pendingStarts[request] = requestEvidence;
                captureTasks.Enqueue(CheckpointAsync(() => evidence.Requests.Add(requestEvidence)));
            }
        };
        page.Console += (_, message) => console.Add(new { utc = DateTimeOffset.UtcNow, message.Type, message.Text });
        page.PageError += (_, error) =>
        {
            console.Add(new { utc = DateTimeOffset.UtcNow, type = "pageerror", text = error });
            evidence.BrowserFailures.Add(TruncateOutcomeText(error) ?? "Uncaught browser error.");
        };
        page.Crash += (_, _) => evidence.BrowserFailures.Add("Browser page crashed.");
        page.RequestFailed += (_, request) =>
        {
            network.Add(new { utc = DateTimeOffset.UtcNow, type = "failed", request.Url, error = request.Failure });
            if (IsCmafStartUrl(request.Url))
            {
                if (pendingStarts.TryRemove(request, out var requestEvidence))
                {
                    captureTasks.Enqueue(CheckpointAsync(() => requestEvidence.Failure = TruncateOutcomeText(request.Failure)));
                }
                startFailureSource.TrySetResult(request.Failure ?? "The CMAF startup request failed without a response.");
            }
        };
        page.Response += (_, response) =>
        {
            if (response.Status >= 400)
            {
                network.Add(new { utc = DateTimeOffset.UtcNow, type = "response", response.Url, response.Status });
            }
            if (IsCmafStartUrl(response.Url))
            {
                pendingStarts.TryGetValue(response.Request, out var requestEvidence);
                captureTasks.Enqueue(CaptureStartupAndReleaseAsync(response, evidence, journal, requestEvidence, pendingStarts));
                startResponseSource.TrySetResult(response);
            }
            if (response.Status is >= 200 and < 300)
            {
                if (FragmentAlignment.Parse(response.Url, DateTimeOffset.UtcNow) is { } fragment)
                {
                    fragments.Enqueue(fragment);
                }
            }
        };

        var started = Stopwatch.StartNew();
        var startupStartedAtUtc = DateTimeOffset.MinValue;
        string? failure = null;
        string? failurePhase = null;
        JsonNode? activeStatus = null;
        PlayerMetrics? metrics = null;
        StartupOutcome? startupOutcome = null;
        byte[]? failureScreenshot = null;
        string? synchronizationExchange = null;
        string captionResult = "not-applicable";
        try
        {
            await page.GotoAsync($"{matrix.WatchUrl.TrimEnd('/')}/watch", new() { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 120_000 }).WaitAsync(_cancellation);
            var numbers = page.Locator(".cmaf-channel-number");
            await numbers.First.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 120_000 }).WaitAsync(_cancellation);
            ILocator? channelButton = null;
            for (var index = 0; index < await numbers.CountAsync(); index++)
            {
                if (string.Equals((await numbers.Nth(index).InnerTextAsync()).Trim(), channel.Number, StringComparison.OrdinalIgnoreCase))
                {
                    channelButton = numbers.Nth(index).Locator("xpath=..");
                    break;
                }
            }
            if (channelButton is null)
            {
                throw new InvalidOperationException($"Channel {channel.Number} was not present on the Watch page.");
            }
            await Task.Delay(3_000, _cancellation);
            startupStartedAtUtc = DateTimeOffset.UtcNow;
            await channelButton.ClickAsync(new() { Timeout = 30_000 });
            startupOutcome = await WaitForStartupOutcomeAsync(
                startResponseSource.Task,
                startFailureSource.Task,
                startupStartedAtUtc,
                TimeSpan.FromSeconds(_options.StartupSeconds)).WaitAsync(_cancellation);
            await Task.WhenAll(captureTasks.ToArray());
            await File.WriteAllTextAsync(
                Path.Combine(attemptRoot, "startup-outcome.json"),
                JsonSerializer.Serialize(startupOutcome, _json));
            if (!startupOutcome.Succeeded)
            {
                failurePhase = "startup";
                throw new InvalidOperationException(FormatStartupFailure(startupOutcome));
            }
            var observation = Stopwatch.StartNew();
            TimeSpan RemainingObservation() => TimeSpan.FromSeconds(Math.Max(0.001, _options.ObservationSeconds - observation.Elapsed.TotalSeconds));
            failurePhase = "player-attachment";
            await page.Locator("#cmafVideoPlayer").WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 45_000 }).WaitAsync(RemainingObservation(), _cancellation);
            failurePhase = "playback";
            await page.WaitForFunctionAsync(
                "() => { const v=document.querySelector('#cmafVideoPlayer'); return v && v.readyState >= 2 && v.videoWidth > 0 && v.currentTime > 0; }",
                null,
                new() { Timeout = 30_000, PollingInterval = 500 }).WaitAsync(RemainingObservation(), _cancellation);
            activeStatus = await WaitForActiveStreamAsync(matrix.WatchUrl, TimeSpan.FromSeconds(30)).WaitAsync(RemainingObservation(), _cancellation);
            network.Clear();
            await page.EvaluateAsync(
                """
                () => {
                  const v = document.querySelector('#cmafVideoPlayer');
                  window.__lineupSweep = {
                    startedAt: performance.now(),
                    initialTime: v.currentTime,
                    initialVideoBytes: v.webkitVideoDecodedByteCount ?? null,
                    initialAudioBytes: v.webkitAudioDecodedByteCount ?? null,
                    initialFrames: v.webkitDecodedFrameCount ?? null,
                    waiting: 0,
                    stalled: 0,
                    errors: []
                  };
                  v.addEventListener('waiting', () => window.__lineupSweep.waiting++);
                  v.addEventListener('stalled', () => window.__lineupSweep.stalled++);
                  v.addEventListener('error', () => window.__lineupSweep.errors.push(v.error?.message ?? 'media error'));
                  try {
                    const AudioContextType = window.AudioContext || window.webkitAudioContext;
                    const audioContext = new AudioContextType();
                    const source = audioContext.createMediaElementSource(v);
                    const analyser = audioContext.createAnalyser();
                    analyser.fftSize = 256;
                    source.connect(analyser);
                    analyser.connect(audioContext.destination);
                    audioContext.resume();
                    const data = new Uint8Array(analyser.frequencyBinCount);
                    window.__lineupSweep.audioPeak = 0;
                    window.__lineupSweep.audioSamples = 0;
                    window.__lineupSweep.audioTimer = setInterval(() => {
                      analyser.getByteFrequencyData(data);
                      window.__lineupSweep.audioPeak = Math.max(window.__lineupSweep.audioPeak, ...data);
                      window.__lineupSweep.audioSamples++;
                    }, 250);
                  } catch (error) {
                    window.__lineupSweep.audioError = String(error);
                  }
                }
                """);
            var stable = new StablePlayback(_options.StableSeconds, _options.MediaAdvanceSeconds);
            var stablePassed = false;
            while (observation.Elapsed.TotalSeconds < _options.ObservationSeconds)
            {
                _cancellation.ThrowIfCancellationRequested();
                var sample = await page.EvaluateAsync<JsonElement>(
                    """
                    () => {
                      const v = document.querySelector('#cmafVideoPlayer');
                      const s = window.__lineupSweep;
                      return { mediaTime: v?.currentTime ?? 0, ready: !!v && v.readyState >= 2 && v.videoWidth > 0,
                        paused: !v || v.paused, error: !!v?.error, discontinuities: s.waiting + s.stalled,
                        session: v?.currentSrc ?? '' };
                    }
                    """);
                var value = sample.Deserialize<PlaybackSample>(_json) ?? throw new InvalidDataException("Browser stability sample missing.");
                var identity = evidence.Starts.LastOrDefault(exchange => exchange.Outcome.Succeeded)?.SessionId ?? value.Session;
                stablePassed = stable.Observe(observation.Elapsed.TotalSeconds, value with { Session = identity });
                if (stablePassed)
                {
                    break;
                }
                await Task.Delay(500, _cancellation);
            }
            if (!stablePassed)
            {
                throw new TimeoutException("Continuous stability and media advancement did not meet configured thresholds before the observation deadline.");
            }
            evidence.StableDurationSeconds = stable.StableDurationSeconds;
            evidence.StableMediaAdvanceSeconds = stable.MediaAdvanceSeconds;
            var metricsJson = await page.EvaluateAsync<JsonElement>(
                """
                () => {
                  const v = document.querySelector('#cmafVideoPlayer');
                  const s = window.__lineupSweep;
                  if (s.audioTimer) clearInterval(s.audioTimer);
                  const tracks = Array.from(v.textTracks || []).map(t => ({
                    kind: t.kind, label: t.label, language: t.language, mode: t.mode,
                    activeCueCount: t.activeCues ? t.activeCues.length : 0,
                    cueCount: t.cues ? t.cues.length : 0
                  }));
                  return {
                    initialTime: s.initialTime,
                    finalTime: v.currentTime,
                    readyState: v.readyState,
                    videoWidth: v.videoWidth,
                    videoHeight: v.videoHeight,
                    videoBytesDelta: s.initialVideoBytes == null ? null : (v.webkitVideoDecodedByteCount - s.initialVideoBytes),
                    audioBytesDelta: s.initialAudioBytes == null ? null : (v.webkitAudioDecodedByteCount - s.initialAudioBytes),
                    decodedFramesDelta: s.initialFrames == null ? null : (v.webkitDecodedFrameCount - s.initialFrames),
                    droppedFrames: v.webkitDroppedFrameCount ?? null,
                    waitingEvents: s.waiting,
                    stalledEvents: s.stalled,
                    mediaErrors: s.errors,
                    audioPeak: s.audioPeak ?? null,
                    audioSamples: s.audioSamples ?? 0,
                    audioError: s.audioError ?? null,
                    textTracks: tracks
                  };
                }
                """);
            metrics = metricsJson.Deserialize<PlayerMetrics>(_json)
                ?? throw new InvalidOperationException("Browser player metrics were empty.");
            activeStatus = await WaitForActiveStreamAsync(matrix.WatchUrl, TimeSpan.FromSeconds(5));
            evidence.ActiveStatus = activeStatus.DeepClone();
            var flowStatus = await GetStatusAsync(matrix.WatchUrl);
            SweepPolicy.CaptureFlow(evidence, flowStatus);
            var sourceCaptions = HasSourceCaptions(activeStatus);
            var exposedCaptions = metrics.TextTracks.Count > 0;
            captionResult = !sourceCaptions
                ? "not-applicable"
                : !exposedCaptions
                    ? "source-caption-not-exposed"
                    : metrics.TextTracks.Any(track => track.ActiveCueCount > 0)
                        ? "track-and-cue"
                        : "track-no-cue";

            if (metrics.FinalTime - metrics.InitialTime < _options.MediaAdvanceSeconds)
            {
                failure = $"Media time advanced only {metrics.FinalTime - metrics.InitialTime:F1} seconds.";
            }
            else if (metrics.VideoWidth <= 0 || metrics.VideoHeight <= 0 || metrics.ReadyState < 2)
            {
                failure = "Video never reached a stable decoded state.";
            }
            else if (requireAudio && !HasAudio(activeStatus))
            {
                failure = "The active stream did not report an audio track.";
            }
            else if (requireAudio && metrics.AudioBytesDelta is <= 0 && metrics.AudioPeak is <= 0)
            {
                failure = $"No browser audio decode evidence was observed ({metrics.AudioError ?? "no analyser error"}).";
            }
            else if (!requireAudio && HasAudio(activeStatus))
            {
                failure = "The intentional video-only stream unexpectedly reported an audio track.";
            }
            else if (metrics.MediaErrors.Count > 0)
            {
                failure = string.Join("; ", metrics.MediaErrors);
            }
            else if (network.Count > 0)
            {
                failure = "One or more media/network requests failed.";
            }
            else
            {
                var alert = page.Locator(".alert-danger");
                if (await alert.CountAsync() > 0)
                {
                    failure = await alert.InnerTextAsync();
                }
            }
            if (failure is null && evidence.Purpose == "sync")
            {
                failurePhase = "synchronization-metadata";
                await Task.WhenAll(captureTasks.ToArray());
                var current = evidence.Starts.LastOrDefault() ?? throw new InvalidDataException("No startup evidence for synchronization metadata.");
                synchronizationExchange = current.Id;
                using var metadataDeadline = CancellationTokenSource.CreateLinkedTokenSource(_cancellation);
                metadataDeadline.CancelAfter(RemainingObservation());
                try
                {
                    var metadata = await FragmentAlignment.ReadOwnedMetadataAsync(
                        current, evidence.Ownership, url => _http.GetStringAsync(url, metadataDeadline.Token));
                    evidence.SynchronizationManifestUrl = metadata.ManifestUrl;
                    foreach (var pair in metadata.Representations)
                    {
                        representations[pair.Key] = pair.Value;
                    }
                }
                catch (Exception metadataError) when (metadataError is IOException or HttpRequestException or OperationCanceledException)
                {
                    evidence.EvidenceFailures.Add($"Current-session synchronization metadata failed: {metadataError.Message}");
                    throw;
                }
            }
            if (failure is not null || captionResult == "source-caption-not-exposed")
            {
                failureScreenshot = await page.ScreenshotAsync(new() { FullPage = true, Timeout = 5_000 });
            }
        }
        catch (Exception ex) when (ex is PlaywrightException or InvalidOperationException or TimeoutException or HttpRequestException or OperationCanceledException or JsonException)
        {
            failurePhase ??= startupOutcome is null ? "navigation" : startupOutcome.Succeeded ? "playback" : "startup";
            failure = startupOutcome is { Succeeded: false }
                ? FormatStartupFailure(startupOutcome)
                : ex.Message;
            try
            {
                failureScreenshot = await page.ScreenshotAsync(new() { FullPage = true, Timeout = 5_000 });
            }
            catch (PlaywrightException screenshotError)
            {
                console.Add(new { type = "screenshot-error", text = screenshotError.Message });
            }
        }
        finally
        {
            // Cleanup is deliberately outside diagnostics. No screenshot, trace or filesystem error can skip stop.
            try
            {
                evidence.Cleanup = await new OwnedPlayback(_http).CleanupAsync(
                    evidence.Ownership,
                    async () =>
                    {
                        try
                        {
                            var stop = page.Locator("button.btn-danger");
                            if (await stop.CountAsync() > 0)
                            {
                                await stop.First.ClickAsync(new() { Timeout = 5_000 });
                            }
                        }
                        finally
                        {
                            try
                            {
                                await page.GotoAsync("about:blank", new() { Timeout = 5_000 });
                            }
                            finally
                            {
                                await Task.WhenAll(captureTasks.ToArray()).WaitAsync(TimeSpan.FromSeconds(_options.StartupSeconds));
                            }
                        }
                    },
                    TimeSpan.FromSeconds(_options.CleanupSeconds),
                    token => VerifyBaselineAsync(matrix, baseline, pendingStarts, token));
                if (evidence.Cleanup.Forced || !evidence.Cleanup.VerifiedAbsent)
                {
                    failurePhase = "cleanup";
                    failure = $"{failure} Cleanup failed: forced={evidence.Cleanup.Forced}, absent={evidence.Cleanup.VerifiedAbsent}. {string.Join("; ", evidence.Cleanup.Errors)}".Trim();
                }
                await CheckpointAsync();
            }
            finally
            {
                try
                {
                    await File.WriteAllTextAsync(Path.Combine(attemptRoot, "console.json"), JsonSerializer.Serialize(console, _json));
                    await File.WriteAllTextAsync(Path.Combine(attemptRoot, "network.json"), JsonSerializer.Serialize(network, _json));
                    await File.WriteAllTextAsync(Path.Combine(attemptRoot, "fragments.json"), JsonSerializer.Serialize(fragments, _json));
                    await File.WriteAllTextAsync(Path.Combine(attemptRoot, "representations.json"), JsonSerializer.Serialize(representations, _json));
                    if (activeStatus is not null)
                    {
                        await File.WriteAllTextAsync(Path.Combine(attemptRoot, "active-status.json"), activeStatus.ToJsonString(_json));
                    }
                    if (metrics is not null)
                    {
                        await File.WriteAllTextAsync(Path.Combine(attemptRoot, "player-metrics.json"), JsonSerializer.Serialize(metrics, _json));
                    }
                    if (failure is not null || captionResult == "source-caption-not-exposed")
                    {
                        if (failureScreenshot is not null)
                        {
                            await File.WriteAllBytesAsync(Path.Combine(attemptRoot, "failure.png"), failureScreenshot);
                        }
                        await context.Tracing.StopAsync(new() { Path = tracePath });
                    }
                    else
                    {
                        var temporaryTrace = Path.Combine(attemptRoot, "success-trace.zip");
                        await context.Tracing.StopAsync(new() { Path = temporaryTrace });
                        File.Delete(temporaryTrace);
                    }
                }
                finally
                {
                    await page.CloseAsync();
                }
            }
        }

        if (evidence.Starts.LastOrDefault() is { Outcome.Succeeded: false } last)
        {
            startupOutcome = last.Outcome;
            failure = FormatStartupFailure(startupOutcome);
            failurePhase = "startup";
        }
        if (evidence.Purpose == "sync")
        {
            evidence.Synchronization = failure is null
                ? synchronizationExchange != evidence.Starts.LastOrDefault()?.Id
                    ? new("insufficient-samples", null, null, null, null, null, "Startup changed after metadata capture; stale/fallback metadata is not synchronization evidence.")
                    : FragmentAlignment.Evaluate(fragments.ToArray(), representations, _options.MaximumSequenceDelta)
                : new SynchronizationEvidence(
                    startupOutcome?.Outcome == "source-unavailable" ? "unavailable-on-retest"
                        : startupOutcome?.Outcome == "capacity-contamination" ? "capacity-contaminated" : "startup-failure",
                    null, null, null, null, null, failure);
        }
        var result = new ChannelAttemptResult(
            matrix.Id,
            channel.Number,
            channel.Name,
            attempt,
            failure is null,
            captionResult,
            started.Elapsed.TotalSeconds,
            failure,
            metrics,
            startupOutcome,
            attempt,
            failure is null ? null : failurePhase,
            evidence.SyntheticPlayback == true && failure is null ? "synthetic-playback" : ClassifyResult(failure, startupOutcome));
        await File.WriteAllTextAsync(Path.Combine(attemptRoot, "result.json"), JsonSerializer.Serialize(result, _json));
        await journal.WriteEventAsync("channel-attempt", result);
        if (_cancellation.IsCancellationRequested)
        {
            evidence.Interrupted = true;
            evidence.Result = result;
            evidence.FinishedUtc = DateTimeOffset.UtcNow;
            await CheckpointAsync();
            _cancellation.ThrowIfCancellationRequested();
        }
        return result;
    }

    public async Task VerifyOutcomeReportingAsync()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;
        StartupOutcome Startup(int status, string? code = null)
        {
            var outcome = ClassifyStartupHttpOutcome(status, code);
            return new StartupOutcome(
                outcome,
                status,
                code,
                "startup",
                null,
                now,
                now.AddMilliseconds(25),
                25,
                "http://localhost/start",
                status is >= 200 and < 300,
                IsRetryableStartupStatus(status));
        }

        ChannelAttemptResult Result(
            int attempt,
            bool avPassed,
            string captionResult,
            StartupOutcome startup,
            string? failure)
        {
            return new ChannelAttemptResult(
                "matrix",
                "1.1",
                "Channel",
                attempt,
                avPassed,
                captionResult,
                1,
                failure,
                null,
                startup,
                attempt,
                failure is null ? null : startup.Succeeded ? "playback" : "startup",
                ClassifyResult(failure, startup));
        }

        // Act
        var protectedResult = Result(1, false, "not-applicable", Startup(422, "protected_content"), "protected");
        var unavailableResult = Result(1, false, "not-applicable", Startup(503, "source_no_video_data"), "unavailable");
        var successfulAttempt = Result(1, true, "source-caption-not-exposed", Startup(200), null);
        var laterFailure = Result(2, false, "track-and-cue", Startup(504, "cmaf_startup_timeout"), "timeout");
        var terminal = SelectTerminalResult([successfulAttempt, laterFailure]);
        var pendingResponse = new TaskCompletionSource<IResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = await WaitForStartupOutcomeAsync(
            pendingResponse.Task,
            Task.FromResult("net::ERR_ABORTED"),
            now,
            TimeSpan.FromSeconds(1));

        // Assert
        if (protectedResult.Startup?.Outcome != "protected-content" || ShouldRetry(protectedResult))
        {
            throw new InvalidOperationException("Protected-content outcomes must be classified as terminal without retry.");
        }
        if (unavailableResult.Category != "source-availability" || !ShouldRetry(unavailableResult))
        {
            throw new InvalidOperationException("Source availability failures must remain retryable and separately classified.");
        }
        if (!terminal.AvPassed || terminal.Attempt != 1 || terminal.CaptionResult != "track-and-cue" || terminal.AttemptsExecuted != 2)
        {
            throw new InvalidOperationException("Terminal aggregation must preserve the strongest AV and caption evidence independently.");
        }
        if (cancelled.Outcome != "client-cancelled" || cancelled.HttpStatus is not null || !cancelled.Retryable)
        {
            throw new InvalidOperationException("Client-cancelled requests must remain distinct from server startup timeouts.");
        }

        Console.WriteLine("Outcome reporting verification passed.");
    }

    private static bool IsCmafStartUrl(string url)
    {
        return url.Contains("/api/stream/cmaf/start/", StringComparison.OrdinalIgnoreCase)
            || url.Contains("/media/start/", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<StartupOutcome> WaitForStartupOutcomeAsync(
        Task<IResponse> responseTask,
        Task<string> requestFailureTask,
        DateTimeOffset startedAtUtc,
        TimeSpan timeout)
    {
        var timeoutTask = Task.Delay(timeout);
        var completedTask = await Task.WhenAny(responseTask, requestFailureTask, timeoutTask);
        var completedAtUtc = DateTimeOffset.UtcNow;
        if (completedTask == responseTask)
        {
            return await CreateStartupOutcomeAsync(await responseTask, startedAtUtc, completedAtUtc);
        }

        if (completedTask == requestFailureTask)
        {
            var requestFailure = await requestFailureTask;
            var outcome = requestFailure.Contains("abort", StringComparison.OrdinalIgnoreCase)
                || requestFailure.Contains("cancel", StringComparison.OrdinalIgnoreCase)
                    ? "client-cancelled"
                    : "transport-failure";
            return new StartupOutcome(
                outcome,
                null,
                null,
                "request",
                TruncateOutcomeText(requestFailure),
                startedAtUtc,
                completedAtUtc,
                (long)(completedAtUtc - startedAtUtc).TotalMilliseconds,
                null,
                false,
                true);
        }

        return new StartupOutcome(
            "no-response-timeout",
            null,
            null,
            "request",
            $"No CMAF startup response was received within {timeout.TotalSeconds:F0} seconds.",
            startedAtUtc,
            completedAtUtc,
            (long)(completedAtUtc - startedAtUtc).TotalMilliseconds,
            null,
            false,
            true);
    }

    /// <summary>Captures actual HTTP protection evidence without promoting absent inference to confirmation.</summary>
    internal static async Task<StartupOutcome> CreateStartupOutcomeAsync(
        IResponse response,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc)
    {
        string? code = null;
        string? stage = null;
        string? message = null;
        bool? retryable = null;
        bool? inferred = null;
        string? source = null;
        if (response.Status >= 400)
        {
            try
            {
                var responseText = await response.TextAsync();
                try
                {
                    var payload = JsonNode.Parse(responseText);
                    code = ReadOutcomeField(payload, "code");
                    stage = ReadOutcomeField(payload, "stage");
                    retryable = payload?["retryable"]?.GetValue<bool?>();
                    inferred = payload?["inferred"]?.GetValue<bool?>();
                    source = ReadOutcomeField(payload, "source");
                    message = ReadOutcomeField(payload, "message")
                        ?? ReadOutcomeField(payload, "error")
                        ?? ReadOutcomeField(payload, "detail");
                }
                catch (JsonException)
                {
                    message = TruncateOutcomeText(responseText);
                }
            }
            catch (PlaywrightException ex)
            {
                message = TruncateOutcomeText($"Unable to read the response body: {ex.Message}");
            }
        }

        var outcome = ClassifyStartupHttpOutcome(response.Status, code);
        if (outcome == "protected-content")
        {
            outcome = inferred switch { true => "inferred-protected-content", false => "protected-content", null => "unconfirmed-protected-content" };
        }
        return new StartupOutcome(
            outcome,
            response.Status,
            code,
            stage,
            message,
            startedAtUtc,
            completedAtUtc,
            (long)(completedAtUtc - startedAtUtc).TotalMilliseconds,
            response.Url,
            response.Status is >= 200 and < 300,
            SweepPolicy.Retryable(outcome, response.Status, retryable))
        {
            Inferred = inferred,
            Source = source
        };
    }

    private static string ClassifyStartupHttpOutcome(int status, string? code)
    {
        return SweepPolicy.StartupCategory(status, code);
    }

    private static bool IsRetryableStartupStatus(int status)
    {
        return status is 408 or 429 or 499 or 502 or 503 or 504 || status >= 500;
    }

    private static string FormatStartupFailure(StartupOutcome outcome)
    {
        var status = outcome.HttpStatus is null ? string.Empty : $" HTTP {outcome.HttpStatus}";
        var code = string.IsNullOrWhiteSpace(outcome.Code) ? string.Empty : $" ({outcome.Code})";
        var stage = string.IsNullOrWhiteSpace(outcome.Stage) ? string.Empty : $" at {outcome.Stage}";
        var message = string.IsNullOrWhiteSpace(outcome.Message) ? string.Empty : $": {outcome.Message}";
        return $"CMAF startup {outcome.Outcome}{status}{code}{stage}{message}";
    }

    private static string? TruncateOutcomeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= 500 ? normalized : normalized[..500];
    }

    private static string? ReadOutcomeField(JsonNode? payload, string name)
    {
        return payload?[name] is JsonValue value
            ? value.TryGetValue<string>(out var text) ? TruncateOutcomeText(text) : TruncateOutcomeText(value.ToJsonString())
            : null;
    }

    private static bool ShouldRetry(ChannelAttemptResult result)
    {
        if (result.AvPassed && result.CaptionResult != "source-caption-not-exposed")
        {
            return false;
        }

        return result.Startup is null || result.Startup.Succeeded || result.Startup.Retryable;
    }

    private static ChannelAttemptResult SelectTerminalResult(IReadOnlyList<ChannelAttemptResult> attempts)
    {
        if (attempts.Count == 0)
        {
            throw new InvalidOperationException("A terminal channel result requires at least one attempt.");
        }

        var strongestPlayback = attempts
            .OrderByDescending(GetPlaybackResultScore)
            .ThenBy(result => result.Attempt)
            .First();
        var strongestCaption = attempts
            .OrderByDescending(result => GetCaptionResultScore(result.CaptionResult))
            .ThenBy(result => result.Attempt)
            .First();
        return strongestPlayback with
        {
            CaptionResult = strongestCaption.CaptionResult,
            AttemptsExecuted = attempts.Count
        };
    }

    private static int GetPlaybackResultScore(ChannelAttemptResult result)
    {
        if (result.AvPassed)
        {
            return 1_000;
        }

        if (result.Startup?.Succeeded == true)
        {
            return 700;
        }

        if (result.Startup is { Retryable: false })
        {
            return 600;
        }

        if (result.Startup?.HttpStatus is not null)
        {
            return 500;
        }

        if (result.Startup is not null)
        {
            return 300;
        }

        return 100;
    }

    private static int GetCaptionResultScore(string captionResult)
    {
        return captionResult switch
        {
            "track-and-cue" => 3,
            "track-no-cue" => 2,
            "source-caption-not-exposed" => 1,
            _ => 0
        };
    }

    private static string ClassifyResult(string? failure, StartupOutcome? startup)
    {
        if (failure is null)
        {
            return "pass";
        }

        return startup?.Outcome switch
        {
            "protected-content" or "inferred-protected-content" or "unconfirmed-protected-content" => "protected-content",
            "source-unavailable" => "source-availability",
            "capacity-contamination" => "capacity-contamination",
            "transport-failure" or "no-response-timeout" or "client-cancelled" => "harness-failure",
            "success" => "product-playback-defect",
            null => "harness-failure",
            _ => "product-startup-defect"
        };
    }

    private static string GetResultCategory(ChannelAttemptResult result)
    {
        return string.IsNullOrWhiteSpace(result.Category)
            ? ClassifyResult(result.Failure, result.Startup)
            : result.Category;
    }

    private static int GetAttemptsExecuted(ChannelAttemptResult result)
    {
        return result.AttemptsExecuted > 0 ? result.AttemptsExecuted : result.Attempt;
    }
}
