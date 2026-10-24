namespace Lineup.Streaming.Sweep;

internal sealed partial class SweepHarness
{
    public async Task VerifyIssue6Async(string? matrixId = null)
    {
        const string controlChannelNumber = "2.1";
        const string protectedChannelNumber = "811.1";
        var matrices = string.IsNullOrWhiteSpace(matrixId)
            ? _matrices
            : _matrices.Where(matrix => string.Equals(matrix.Id, matrixId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matrices.Length == 0)
        {
            throw new ArgumentException($"Unknown issue 6 matrix '{matrixId}'.", nameof(matrixId));
        }
        var runRoot = Path.Combine(_runsRoot, $"issue6-{DateTime.UtcNow:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(runRoot);
        var journal = new RunJournal(runRoot, _json);
        var controlResults = new List<ChannelAttemptResult>();
        var errorResults = new List<Issue6ErrorResult>();
        var slateResults = new List<ChannelAttemptResult>();
        await File.WriteAllTextAsync(
            Path.Combine(runRoot, "run.json"),
            JsonSerializer.Serialize(new
            {
                startedAtUtc = DateTimeOffset.UtcNow,
                purpose = "Verify protected-content metadata returns or slates before physical acquisition across every hosting topology",
                image = _image,
                matrices = matrices.Select(matrix => matrix.Id),
                controlChannel = controlChannelNumber,
                protectedChannel = protectedChannelNumber,
                faultSource = FaultSourceAddress
            }, _json));

        foreach (var matrix in matrices)
        {
            var project = $"lineup-issue6-{matrix.Id}";
            var faultContainer = $"{project}-fault-source";
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
                var inventory = await ReadEnabledChannelsAsync(matrix.WatchUrl);
                var controlChannel = inventory.SingleOrDefault(item => string.Equals(item.Number, controlChannelNumber, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"Channel {controlChannelNumber} was not present in {matrix.Id}.");
                await RunCompatibilityAsync(
                    browser.Context,
                    matrix.WatchUrl,
                    Path.Combine(matrixRunRoot, "compatibility.json"),
                    journal);
                var control = await RunChannelAsync(browser.Context, matrix, controlChannel, 1, matrixRunRoot, journal);
                controlResults.Add(control);
                await journal.WriteTerminalAsync(matrix.Id, controlChannel.Number, control);
                if (!control.AvPassed)
                {
                    throw new InvalidOperationException($"{matrix.Id} successful-source control playback failed: {control.Failure}");
                }

                await RestartWithFaultSourceAsync(matrix, project, faultContainer);
                await PrimeFaultSourceCatalogAsync(matrix.SourceUrl);
                await RefreshGuideAsync(browser.Context, matrix.SourceUrl, journal, "fault-source-error");
                if (matrix.Remote)
                {
                    await RefreshGuideAsync(browser.Context, matrix.WatchUrl, journal, "fault-consumer-error");
                }
                var timer = Stopwatch.StartNew();
                var response = await VerifySourceAcquisitionFailureAsync(
                    browser.Context,
                    matrix,
                    protectedChannelNumber,
                    expectedStatus: 422,
                    expectedCode: "protected_content",
                    expectedError: "The requested channel is protected and cannot be streamed.");
                timer.Stop();
                var errorRequestCount = await ReadFaultSourceRequestCountAsync(protectedChannelNumber);
                var errorResult = new Issue6ErrorResult(matrix.Id, timer.Elapsed.TotalSeconds, errorRequestCount, response);
                errorResults.Add(errorResult);
                await journal.WriteEventAsync("protected-content-error", errorResult);
                if (timer.Elapsed > TimeSpan.FromSeconds(10) || errorRequestCount != 0)
                {
                    throw new InvalidOperationException(
                        $"{matrix.Id} protected-content error was not early: elapsed {timer.Elapsed.TotalSeconds:F1}s, source requests {errorRequestCount}.");
                }

                await RemoveContainerAsync(faultContainer);
                await RestartWithFaultSourceAsync(matrix, project, faultContainer, protectedContentMode: "StreamSlate");
                await PrimeFaultSourceCatalogAsync(matrix.SourceUrl);
                await RefreshGuideAsync(browser.Context, matrix.SourceUrl, journal, "fault-source-slate");
                if (matrix.Remote)
                {
                    await RefreshGuideAsync(browser.Context, matrix.WatchUrl, journal, "fault-consumer-slate");
                }
                var syntheticInventory = await ReadEnabledChannelsAsync(matrix.WatchUrl);
                var protectedChannel = syntheticInventory.SingleOrDefault(
                    item => string.Equals(item.Number, protectedChannelNumber, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"Channel {protectedChannelNumber} was not present in {matrix.Id}.");
                var slate = await RunChannelAsync(browser.Context, matrix, protectedChannel, 1, matrixRunRoot, journal);
                slateResults.Add(slate);
                await journal.WriteEventAsync("protected-content-slate", slate);
                var slateRequestCount = await ReadFaultSourceRequestCountAsync(protectedChannelNumber);
                if (!slate.AvPassed || slateRequestCount != 0)
                {
                    throw new InvalidOperationException(
                        $"{matrix.Id} protected-content slate failed or opened the physical source: {slate.Failure ?? "source request observed"}.");
                }
                await journal.WriteEventAsync("matrix-complete", new { matrix = matrix.Id });
            }
            finally
            {
                await SaveComposeLogsAsync(matrix, project, matrixRunRoot);
                await RemoveContainerAsync(faultContainer);
                await StopMatrixAsync(matrix, project, matrixRunRoot);
            }
        }

        await BuildReportsAsync(runRoot);
        await WriteIssue6SummaryAsync(runRoot, matrices, controlResults, errorResults, slateResults);
        await journal.WriteEventAsync("run-complete", new { completedAtUtc = DateTimeOffset.UtcNow });
        await File.WriteAllTextAsync(Path.Combine(runRoot, "completed.marker"), DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        Console.WriteLine(runRoot);
    }

    public async Task VerifyIssue7Async(string? matrixId = null)
    {
        const string channelNumber = "2.1";
        var dedicatedMatrices = _matrices.Where(matrix => matrix.Mode != HostingMode.Management).ToArray();
        var matrices = string.IsNullOrWhiteSpace(matrixId)
            ? dedicatedMatrices
            : dedicatedMatrices.Where(matrix => string.Equals(matrix.Id, matrixId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matrices.Length == 0)
        {
            throw new ArgumentException($"Unknown dedicated issue 7 matrix '{matrixId}'.", nameof(matrixId));
        }
        var runRoot = Path.Combine(_runsRoot, $"issue7-{DateTime.UtcNow:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(runRoot);
        var journal = new RunJournal(runRoot, _json);
        var results = new List<Issue7TeardownResult>();
        await File.WriteAllTextAsync(
            Path.Combine(runRoot, "run.json"),
            JsonSerializer.Serialize(new
            {
                startedAtUtc = DateTimeOffset.UtcNow,
                purpose = "Verify dedicated worker teardown explicitly closes Core broker streams and handles abrupt browser disconnects",
                image = _image,
                matrices = matrices.Select(matrix => matrix.Id),
                channel = channelNumber
            }, _json));

        foreach (var matrix in matrices)
        {
            var project = $"lineup-issue7-{matrix.Id}";
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
                var inventory = await ReadEnabledChannelsAsync(matrix.WatchUrl);
                var channel = inventory.SingleOrDefault(item => string.Equals(item.Number, channelNumber, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"Channel {channelNumber} was not present in {matrix.Id}.");
                await RunCompatibilityAsync(
                    browser.Context,
                    matrix.WatchUrl,
                    Path.Combine(matrixRunRoot, "compatibility.json"),
                    journal);

                var explicitStop = await VerifyDedicatedTeardownAsync(browser.Context, matrix, channel, abrupt: false);
                results.Add(explicitStop);
                await journal.WriteEventAsync("explicit-stop", explicitStop);
                var abruptDisconnect = await VerifyDedicatedTeardownAsync(browser.Context, matrix, channel, abrupt: true);
                results.Add(abruptDisconnect);
                await journal.WriteEventAsync("abrupt-disconnect", abruptDisconnect);
                await journal.WriteEventAsync("matrix-complete", new { matrix = matrix.Id });
            }
            finally
            {
                await SaveComposeLogsAsync(matrix, project, matrixRunRoot);
                await StopMatrixAsync(matrix, project, matrixRunRoot);
            }
        }

        var summary = new StringBuilder("# Issue 7 dedicated broker-stream teardown integration sweep\n\n");
        summary.AppendLine($"Completed: {DateTimeOffset.UtcNow:O}\n");
        summary.AppendLine("| Matrix | Teardown | Cleanup seconds | Source streams | Broker streams |");
        summary.AppendLine("|---|---|---:|---:|---:|");
        foreach (var result in results)
        {
            summary.AppendLine($"| {result.Matrix} | {result.Teardown} | {result.CleanupSeconds:F1} | {result.SourceActiveStreams} | {result.ActiveBrokerStreams} |");
        }
        summary.AppendLine();
        summary.AppendLine($"Result: **{results.Count}/{results.Count} dedicated teardown checks passed.**");
        summary.AppendLine();
        summary.AppendLine("Each explicit stop and abrupt browser disconnect removed the source active-stream registration and the correlated Core broker stream before the cleanup deadline.");
        await File.WriteAllTextAsync(Path.Combine(runRoot, "issue7-summary.md"), summary.ToString());
        await journal.WriteEventAsync("run-complete", new { completedAtUtc = DateTimeOffset.UtcNow });
        await File.WriteAllTextAsync(Path.Combine(runRoot, "completed.marker"), DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        Console.WriteLine(runRoot);
    }

    public async Task VerifyIssue8Async(string? matrixId = null)
    {
        const string channelNumber = "2.1";
        var dedicatedMatrices = _matrices.Where(matrix => !matrix.Remote && matrix.Mode != HostingMode.Management).ToArray();
        var matrices = string.IsNullOrWhiteSpace(matrixId)
            ? dedicatedMatrices
            : dedicatedMatrices.Where(matrix => string.Equals(matrix.Id, matrixId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matrices.Length == 0)
        {
            throw new ArgumentException($"Unknown local dedicated issue 8 matrix '{matrixId}'.", nameof(matrixId));
        }
        var runRoot = Path.Combine(_runsRoot, $"issue8-{DateTime.UtcNow:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(runRoot);
        var journal = new RunJournal(runRoot, _json);
        var results = new List<Issue8PresentationResult>();
        await File.WriteAllTextAsync(
            Path.Combine(runRoot, "run.json"),
            JsonSerializer.Serialize(new
            {
                startedAtUtc = DateTimeOffset.UtcNow,
                purpose = "Verify dedicated Web Player status reports the final CMAF browser presentation rather than only brokered MPEG-TS input",
                image = _image,
                matrices = matrices.Select(matrix => matrix.Id),
                channel = channelNumber
            }, _json));

        foreach (var matrix in matrices)
        {
            var project = $"lineup-issue8-{matrix.Id}";
            var matrixRunRoot = Path.Combine(runRoot, matrix.Id);
            Directory.CreateDirectory(matrixRunRoot);
            await journal.WriteEventAsync("matrix-start", new { matrix = matrix.Id });
            try
            {
                await PrepareAndStartAsync(matrix, project, journal);
                await using var browser = await OpenBrowserAsync(Path.Combine(matrixRunRoot, "browser"));
                await RefreshGuideAsync(browser.Context, matrix.SourceUrl, journal, "source");
                var inventory = await ReadEnabledChannelsAsync(matrix.WatchUrl);
                var channel = inventory.SingleOrDefault(item => string.Equals(item.Number, channelNumber, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"Channel {channelNumber} was not present in {matrix.Id}.");
                await RunCompatibilityAsync(
                    browser.Context,
                    matrix.WatchUrl,
                    Path.Combine(matrixRunRoot, "compatibility.json"),
                    journal);
                var result = await VerifyDedicatedPresentationStatusAsync(browser.Context, matrix, channel);
                results.Add(result);
                await journal.WriteEventAsync("presentation-status", result);
                await journal.WriteEventAsync("matrix-complete", new { matrix = matrix.Id });
            }
            finally
            {
                await SaveComposeLogsAsync(matrix, project, matrixRunRoot);
                await StopMatrixAsync(matrix, project, matrixRunRoot);
            }
        }

        var summary = new StringBuilder("# Issue 8 dedicated presentation-status integration sweep\n\n");
        summary.AppendLine($"Completed: {DateTimeOffset.UtcNow:O}\n");
        summary.AppendLine("| Matrix | Broker input | Status presentation | Manifest codecs | Browser text tracks | Status text tracks | Cleanup |");
        summary.AppendLine("|---|---|---|---|---:|---:|---|");
        foreach (var result in results)
        {
            summary.AppendLine(
                $"| {result.Matrix} | {result.BrokerVideoCodec}/{result.BrokerAudioCodec} | {result.PresentationVideoCodec}/{result.PresentationAudioCodec} | "
                + $"{result.ManifestVideoCodec}/{result.ManifestAudioCodec} | {result.BrowserTextTracks} | {result.StatusTextTracks} | "
                + $"{result.SourceActiveStreams} source, {result.ActiveBrokerStreams} broker |");
        }
        summary.AppendLine();
        summary.AppendLine($"Result: **{results.Count}/{results.Count} dedicated presentation-status checks passed.**");
        summary.AppendLine();
        summary.AppendLine("Each worker correlated its media session with the Core broker stream, reported `ready`, and replaced the brokered MPEG-2/AC-3 status with the H.264/AAC CMAF presentation consumed by the browser.");
        summary.AppendLine("The status subtitle count matched the browser text-track count, so caption exposure was reported from the final presentation rather than inherited from the MPEG-TS input.");
        await File.WriteAllTextAsync(Path.Combine(runRoot, "issue8-summary.md"), summary.ToString());
        await journal.WriteEventAsync("run-complete", new { completedAtUtc = DateTimeOffset.UtcNow });
        await File.WriteAllTextAsync(Path.Combine(runRoot, "completed.marker"), DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        Console.WriteLine(runRoot);
    }

    private async Task<Issue8PresentationResult> VerifyDedicatedPresentationStatusAsync(
        IBrowserContext context,
        MatrixDefinition matrix,
        ChannelDefinition channel)
    {
        await EnsureTunerIdleAsync(TimeSpan.FromMinutes(2));
        var page = await context.NewPageAsync();
        try
        {
            await page.GotoAsync($"{matrix.WatchUrl.TrimEnd('/')}/watch", new() { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 120_000 });
            var numbers = page.Locator(".cmaf-channel-number");
            await numbers.First.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 120_000 });
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
            await Task.Delay(3_000);
            await channelButton.ClickAsync(new() { Timeout = 30_000 });
            await page.Locator("#cmafVideoPlayer").WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 45_000 });
            await page.WaitForFunctionAsync(
                "() => { const v=document.querySelector('#cmafVideoPlayer'); return v && v.readyState >= 2 && v.videoWidth > 0 && v.currentTime > 0; }",
                null,
                new() { Timeout = 30_000, PollingInterval = 500 });
            var initialTime = await page.Locator("#cmafVideoPlayer").EvaluateAsync<double>("v => v.currentTime");
            await Task.Delay(TimeSpan.FromSeconds(1));
            var finalTime = await page.Locator("#cmafVideoPlayer").EvaluateAsync<double>("v => v.currentTime");
            if (finalTime - initialTime < 0.25)
            {
                throw new InvalidOperationException($"{matrix.Id} browser media advanced only {finalTime - initialTime:F1} seconds.");
            }
            var browser = await page.EvaluateAsync<JsonElement>(
                """
                async () => {
                  const video = document.querySelector('#cmafVideoPlayer');
                  const urls = performance.getEntriesByType('resource').map(entry => entry.name);
                  const manifestUrl = urls.find(url => url.includes('manifest.mpd'));
                  if (!manifestUrl) throw new Error('No DASH manifest resource was observed.');
                  const manifest = await fetch(manifestUrl).then(response => response.text());
                  return {
                    manifestUrl,
                    manifest,
                    textTracks: Array.from(video.textTracks || []).length
                  };
                }
                """);
            var manifest = browser.GetProperty("manifest").GetString() ?? string.Empty;
            var browserTextTracks = browser.GetProperty("textTracks").GetInt32();
            var activeStatus = await WaitForReadyWorkerStreamAsync(matrix.SourceUrl, TimeSpan.FromSeconds(30));
            var physicalTracks = activeStatus["physicalOutput"]?["tracks"]?.AsArray()
                ?? throw new InvalidOperationException($"{matrix.Id} did not report broker input media.");
            var presentationTracks = activeStatus["virtualOutput"]?["tracks"]?.AsArray()
                ?? throw new InvalidOperationException($"{matrix.Id} did not report worker presentation media.");
            var brokerVideo = ReadCodec(physicalTracks, "video");
            var brokerAudio = ReadCodec(physicalTracks, "audio");
            var presentationVideo = ReadCodec(presentationTracks, "video");
            var presentationAudio = ReadCodec(presentationTracks, "audio");
            var statusTextTracks = presentationTracks.Count(track =>
                string.Equals(track?["type"]?.GetValue<string>(), "subtitle", StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(activeStatus["brokerRequestId"]?.GetValue<string>())
                || string.IsNullOrWhiteSpace(activeStatus["workerMediaSessionId"]?.GetValue<string>())
                || !string.Equals(activeStatus["workerStartupStage"]?.GetValue<string>(), "ready", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(presentationVideo, "h264", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(presentationAudio, "aac", StringComparison.OrdinalIgnoreCase)
                || !manifest.Contains("avc1", StringComparison.OrdinalIgnoreCase)
                || !manifest.Contains("mp4a", StringComparison.OrdinalIgnoreCase)
                || browserTextTracks != statusTextTracks)
            {
                throw new InvalidOperationException(
                    $"{matrix.Id} presentation status did not match the browser manifest: {activeStatus.ToJsonString(_json)}");
            }

            await page.Locator("button.btn-danger").First.ClickAsync(new() { Timeout = 10_000 });
            await WaitForCleanupAsync(matrix.SourceUrl, TimeSpan.FromSeconds(15));
            await WaitForBrokerStreamCountAsync(matrix, 0, TimeSpan.FromSeconds(15));
            await EnsureTunerIdleAsync(TimeSpan.FromSeconds(30));
            var cleaned = await GetStatusAsync(matrix.SourceUrl);
            var sourceActiveStreams = cleaned["streams"]?["count"]?.GetValue<int>() ?? -1;
            var activeBrokerStreams = ReadBrokerStreamCount(cleaned, matrix);
            return new Issue8PresentationResult(
                matrix.Id,
                brokerVideo,
                brokerAudio,
                presentationVideo,
                presentationAudio,
                "avc1",
                "mp4a",
                browserTextTracks,
                statusTextTracks,
                sourceActiveStreams,
                activeBrokerStreams);
        }
        finally
        {
            if (!page.IsClosed)
            {
                await page.CloseAsync();
            }
        }
    }

    private async Task<Issue7TeardownResult> VerifyDedicatedTeardownAsync(
        IBrowserContext context,
        MatrixDefinition matrix,
        ChannelDefinition channel,
        bool abrupt)
    {
        await EnsureTunerIdleAsync(TimeSpan.FromMinutes(2));
        var page = await context.NewPageAsync();
        try
        {
            await page.GotoAsync($"{matrix.WatchUrl.TrimEnd('/')}/watch", new() { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 120_000 });
            var numbers = page.Locator(".cmaf-channel-number");
            await numbers.First.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 120_000 });
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
            await Task.Delay(3_000);
            await channelButton.ClickAsync(new() { Timeout = 30_000 });
            await page.Locator("#cmafStreamLoading").WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10_000 });
            await page.Locator("#cmafVideoPlayer").WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 45_000 });
            await page.WaitForFunctionAsync(
                "() => { const v=document.querySelector('#cmafVideoPlayer'); return v && v.readyState >= 2 && v.videoWidth > 0 && v.currentTime > 0; }",
                null,
                new() { Timeout = 30_000, PollingInterval = 500 });
            _ = await WaitForActiveStreamAsync(matrix.SourceUrl, TimeSpan.FromSeconds(30));
            await WaitForBrokerStreamCountAsync(matrix, 1, TimeSpan.FromSeconds(30));
            await Task.Delay(TimeSpan.FromSeconds(5));

            var cleanup = Stopwatch.StartNew();
            if (abrupt)
            {
                await page.CloseAsync();
            }
            else
            {
                var stop = page.Locator("button.btn-danger");
                await stop.First.ClickAsync(new() { Timeout = 10_000 });
            }
            var cleanupTimeout = abrupt ? TimeSpan.FromSeconds(50) : TimeSpan.FromSeconds(15);
            await WaitForCleanupAsync(matrix.SourceUrl, cleanupTimeout);
            if (!string.Equals(matrix.SourceUrl, matrix.WatchUrl, StringComparison.OrdinalIgnoreCase))
            {
                await WaitForCleanupAsync(matrix.WatchUrl, cleanupTimeout);
            }
            await WaitForBrokerStreamCountAsync(matrix, 0, cleanupTimeout);
            await EnsureTunerIdleAsync(TimeSpan.FromSeconds(30));
            cleanup.Stop();
            var sourceStatus = await GetStatusAsync(matrix.SourceUrl);
            var sourceActiveStreams = sourceStatus["streams"]?["count"]?.GetValue<int>() ?? -1;
            var activeBrokerStreams = ReadBrokerStreamCount(sourceStatus, matrix);
            if (sourceActiveStreams != 0 || activeBrokerStreams != 0)
            {
                throw new InvalidOperationException(
                    $"{matrix.Id} retained source streams ({sourceActiveStreams}) or broker streams ({activeBrokerStreams}) after {(abrupt ? "abrupt disconnect" : "stop")}.");
            }
            return new Issue7TeardownResult(
                matrix.Id,
                abrupt ? "abrupt browser disconnect" : "explicit stop",
                cleanup.Elapsed.TotalSeconds,
                sourceActiveStreams,
                activeBrokerStreams);
        }
        finally
        {
            if (!page.IsClosed)
            {
                await page.CloseAsync();
            }
        }
    }

}
