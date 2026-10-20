namespace Lineup.Streaming.Sweep;

internal sealed partial class SweepHarness
{
    public async Task VerifyIssue1Async()
    {
        string[] requestedChannels = ["2.1", "4.1", "17.1"];
        var matrices = _matrices.Where(matrix => matrix.Id is "local-managed-process" or "local-external-process").ToArray();
        var runRoot = Path.Combine(_runsRoot, $"issue1-{DateTime.UtcNow:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(runRoot);
        var journal = new RunJournal(runRoot, _json);
        await File.WriteAllTextAsync(
            Path.Combine(runRoot, "run.json"),
            JsonSerializer.Serialize(new
            {
                startedAtUtc = DateTimeOffset.UtcNow,
                purpose = "Verify dedicated Web Player CMAF and caption parity fix",
                image = _image,
                matrices = matrices.Select(matrix => matrix.Id),
                channels = requestedChannels
            }, _json));

        foreach (var matrix in matrices)
        {
            var project = $"lineup-issue1-{matrix.Id}";
            var matrixRunRoot = Path.Combine(runRoot, matrix.Id);
            Directory.CreateDirectory(matrixRunRoot);
            await journal.WriteEventAsync("matrix-start", new { matrix = matrix.Id });
            try
            {
                await PrepareAndStartAsync(matrix, project, journal);
                await using var browser = await OpenBrowserAsync(Path.Combine(matrixRunRoot, "browser"));
                await RefreshGuideAsync(browser.Context, matrix.SourceUrl, journal, "source");
                var inventory = await ReadEnabledChannelsAsync(matrix.WatchUrl);
                var channels = requestedChannels
                    .Select(number => inventory.SingleOrDefault(channel => string.Equals(channel.Number, number, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException($"Channel {number} was not present in {matrix.Id}."))
                    .ToArray();
                await File.WriteAllTextAsync(
                    Path.Combine(matrixRunRoot, "channels.json"),
                    JsonSerializer.Serialize(channels, _json));
                await RunCompatibilityAsync(
                    browser.Context,
                    matrix.WatchUrl,
                    Path.Combine(matrixRunRoot, "compatibility.json"),
                    journal);

                foreach (var channel in channels)
                {
                    var result = await RunChannelAsync(browser.Context, matrix, channel, 1, matrixRunRoot, journal);
                    await journal.WriteTerminalAsync(matrix.Id, channel.Number, result);
                }
                await journal.WriteEventAsync("matrix-complete", new { matrix = matrix.Id });
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
        Console.WriteLine(runRoot);
    }

    public async Task VerifyIssue2Async()
    {
        string[] requestedChannels = ["2.1", "4.1"];
        const string missingChannel = "__issue2_missing__";
        var matrices = _matrices.Where(matrix => matrix.Id is "local-managed-process" or "local-external-process").ToArray();
        var runRoot = Path.Combine(_runsRoot, $"issue2-{DateTime.UtcNow:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(runRoot);
        var journal = new RunJournal(runRoot, _json);
        var failureResults = new List<DedicatedFailureResult>();
        await File.WriteAllTextAsync(
            Path.Combine(runRoot, "run.json"),
            JsonSerializer.Serialize(new
            {
                startedAtUtc = DateTimeOffset.UtcNow,
                purpose = "Verify dedicated Web Player startup failures remain browser-readable through CORS",
                image = _image,
                matrices = matrices.Select(matrix => matrix.Id),
                channels = requestedChannels,
                deterministicFailureChannel = missingChannel
            }, _json));

        foreach (var matrix in matrices)
        {
            var project = $"lineup-issue2-{matrix.Id}";
            var matrixRunRoot = Path.Combine(runRoot, matrix.Id);
            Directory.CreateDirectory(matrixRunRoot);
            await journal.WriteEventAsync("matrix-start", new { matrix = matrix.Id });
            try
            {
                await PrepareAndStartAsync(matrix, project, journal);
                await using var browser = await OpenBrowserAsync(Path.Combine(matrixRunRoot, "browser"));
                await RefreshGuideAsync(browser.Context, matrix.SourceUrl, journal, "source");
                var inventory = await ReadEnabledChannelsAsync(matrix.WatchUrl);
                var channels = requestedChannels
                    .Select(number => inventory.SingleOrDefault(channel => string.Equals(channel.Number, number, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException($"Channel {number} was not present in {matrix.Id}."))
                    .ToArray();
                await File.WriteAllTextAsync(
                    Path.Combine(matrixRunRoot, "channels.json"),
                    JsonSerializer.Serialize(channels, _json));
                await RunCompatibilityAsync(
                    browser.Context,
                    matrix.WatchUrl,
                    Path.Combine(matrixRunRoot, "compatibility.json"),
                    journal);

                var failure = await VerifyDedicatedFailureAsync(browser.Context, matrix, missingChannel);
                failureResults.Add(failure);
                await File.WriteAllTextAsync(
                    Path.Combine(matrixRunRoot, "startup-failure.json"),
                    JsonSerializer.Serialize(failure, _json));
                await journal.WriteEventAsync("startup-failure-verification", failure);

                foreach (var channel in channels)
                {
                    var result = await RunChannelAsync(browser.Context, matrix, channel, 1, matrixRunRoot, journal);
                    await journal.WriteTerminalAsync(matrix.Id, channel.Number, result);
                }

                await Task.Delay(500);
                var logResult = await RunProcessAsync(
                    "docker",
                    $"compose -p {project} -f \"{Path.Combine(MatrixRoot(matrix), "compose.yml")}\" logs --no-color --timestamps",
                    MatrixRoot(matrix),
                    false);
                var logs = logResult.Output + logResult.Error;
                var logCount = CountOccurrences(logs, $"startup failed for channel {missingChannel}");
                var expectedLogFields =
                    logs.Contains("worker generation", StringComparison.OrdinalIgnoreCase)
                    && logs.Contains("code channel_not_found", StringComparison.OrdinalIgnoreCase)
                    && logs.Contains("stage broker", StringComparison.OrdinalIgnoreCase);
                if (logCount != 1 || !expectedLogFields)
                {
                    throw new InvalidOperationException(
                        $"{matrix.Id} emitted {logCount} correlated startup log entries; expected exactly one with generation, channel_not_found, and broker stage.");
                }
                await journal.WriteEventAsync("startup-failure-log-verification", new { matrix = matrix.Id, logCount, expectedLogFields });
                await journal.WriteEventAsync("matrix-complete", new { matrix = matrix.Id });
            }
            finally
            {
                await SaveComposeLogsAsync(matrix, project, matrixRunRoot);
                await StopMatrixAsync(matrix, project, matrixRunRoot);
            }
        }

        await BuildReportsAsync(runRoot);
        await File.WriteAllTextAsync(Path.Combine(runRoot, "startup-failures.json"), JsonSerializer.Serialize(failureResults, _json));
        await WriteIssue2SummaryAsync(runRoot, matrices, failureResults);
        await journal.WriteEventAsync("run-complete", new { completedAtUtc = DateTimeOffset.UtcNow });
        await File.WriteAllTextAsync(Path.Combine(runRoot, "completed.marker"), DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        Console.WriteLine(runRoot);
    }

    public async Task VerifyIssue3Async()
    {
        const string channelNumber = "2.1";
        var matrices = _matrices.Where(matrix => matrix.Id is "local-management" or "local-managed-process" or "remote-management").ToArray();
        var runRoot = Path.Combine(_runsRoot, $"issue3-{DateTime.UtcNow:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(runRoot);
        var journal = new RunJournal(runRoot, _json);
        var failureResults = new List<DedicatedFailureResult>();
        await File.WriteAllTextAsync(
            Path.Combine(runRoot, "run.json"),
            JsonSerializer.Serialize(new
            {
                startedAtUtc = DateTimeOffset.UtcNow,
                purpose = "Verify local, dedicated, and Lineup Link source-acquisition failure propagation",
                image = _image,
                matrices = matrices.Select(matrix => matrix.Id),
                channel = channelNumber,
                faultSource = FaultSourceAddress
            }, _json));

        foreach (var matrix in matrices)
        {
            var project = $"lineup-issue3-{matrix.Id}";
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
                var channel = inventory.SingleOrDefault(item => string.Equals(item.Number, channelNumber, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"Channel {channelNumber} was not present in {matrix.Id}.");
                await File.WriteAllTextAsync(
                    Path.Combine(matrixRunRoot, "channels.json"),
                    JsonSerializer.Serialize(new[] { channel }, _json));
                await RunCompatibilityAsync(
                    browser.Context,
                    matrix.WatchUrl,
                    Path.Combine(matrixRunRoot, "compatibility.json"),
                    journal);
                var playback = await RunChannelAsync(browser.Context, matrix, channel, 1, matrixRunRoot, journal);
                await journal.WriteTerminalAsync(matrix.Id, channel.Number, playback);
                if (!playback.AvPassed)
                {
                    throw new InvalidOperationException($"{matrix.Id} successful-source control playback failed: {playback.Failure}");
                }

                await RestartWithFaultSourceAsync(matrix, project, faultContainer);
                var failure = await VerifySourceAcquisitionFailureAsync(
                    browser.Context,
                    matrix,
                    channelNumber,
                    expectedStatus: 503,
                    expectedCode: "source_unavailable",
                    expectedError: "Every equivalent physical source is currently unavailable.");
                failureResults.Add(failure);
                await File.WriteAllTextAsync(
                    Path.Combine(matrixRunRoot, "source-acquisition-failure.json"),
                    JsonSerializer.Serialize(failure, _json));
                await journal.WriteEventAsync("source-acquisition-failure-verification", failure);
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
        await File.WriteAllTextAsync(Path.Combine(runRoot, "source-acquisition-failures.json"), JsonSerializer.Serialize(failureResults, _json));
        await WriteIssue3SummaryAsync(runRoot, matrices, failureResults);
        await journal.WriteEventAsync("run-complete", new { completedAtUtc = DateTimeOffset.UtcNow });
        await File.WriteAllTextAsync(Path.Combine(runRoot, "completed.marker"), DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        Console.WriteLine(runRoot);
    }

    public async Task VerifyIssue4Async()
    {
        const string controlChannelNumber = "2.1";
        var failureCases = new[]
        {
            new Issue4FailureCase("811.1", 422, "protected_content", "The requested channel is protected and cannot be streamed."),
            new Issue4FailureCase("807.1", 503, "source_no_video_data", "Every equivalent physical source is currently returning no video data."),
            new Issue4FailureCase("806.1", 503, "source_no_signal", "Every equivalent physical source is currently unable to lock a usable signal."),
            new Issue4FailureCase("805.1", 503, "source_capacity_exceeded", "Every equivalent physical source is currently at tuner capacity.")
        };
        var matrices = _matrices.Where(matrix => matrix.Id is "local-management" or "local-managed-process" or "remote-management").ToArray();
        var runRoot = Path.Combine(_runsRoot, $"issue4-{DateTime.UtcNow:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(runRoot);
        var journal = new RunJournal(runRoot, _json);
        var failureResults = new List<Issue4FailureResult>();
        await File.WriteAllTextAsync(
            Path.Combine(runRoot, "run.json"),
            JsonSerializer.Serialize(new
            {
                startedAtUtc = DateTimeOffset.UtcNow,
                purpose = "Verify HDHomeRun tuner diagnostics remain classified through local, dedicated, and Lineup Link streaming",
                image = _image,
                matrices = matrices.Select(matrix => matrix.Id),
                controlChannel = controlChannelNumber,
                failureCases,
                faultSource = FaultSourceAddress
            }, _json));

        foreach (var matrix in matrices)
        {
            var project = $"lineup-issue4-{matrix.Id}";
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
                var playback = await RunChannelAsync(browser.Context, matrix, controlChannel, 1, matrixRunRoot, journal);
                await journal.WriteTerminalAsync(matrix.Id, controlChannel.Number, playback);
                if (!playback.AvPassed)
                {
                    throw new InvalidOperationException($"{matrix.Id} successful-source control playback failed: {playback.Failure}");
                }

                await RestartWithFaultSourceAsync(matrix, project, faultContainer);
                await PrimeFaultSourceCatalogAsync(matrix.SourceUrl);
                await RefreshGuideAsync(browser.Context, matrix.SourceUrl, journal, "fault-source");
                if (matrix.Remote)
                {
                    await RefreshGuideAsync(browser.Context, matrix.WatchUrl, journal, "fault-consumer");
                }
                foreach (var failureCase in failureCases)
                {
                    var response = await VerifySourceAcquisitionFailureAsync(
                        browser.Context,
                        matrix,
                        failureCase.Channel,
                        failureCase.Status,
                        failureCase.Code,
                        failureCase.Error);
                    var result = new Issue4FailureResult(matrix.Id, failureCase.Channel, failureCase.Code, response);
                    failureResults.Add(result);
                    await journal.WriteEventAsync("hdhomerun-failure-verification", result);
                }
                await File.WriteAllTextAsync(
                    Path.Combine(matrixRunRoot, "hdhomerun-failures.json"),
                    JsonSerializer.Serialize(failureResults.Where(result => result.Matrix == matrix.Id), _json));
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
        await File.WriteAllTextAsync(Path.Combine(runRoot, "hdhomerun-failures.json"), JsonSerializer.Serialize(failureResults, _json));
        await WriteIssue4SummaryAsync(runRoot, matrices, failureCases, failureResults);
        await journal.WriteEventAsync("run-complete", new { completedAtUtc = DateTimeOffset.UtcNow });
        await File.WriteAllTextAsync(Path.Combine(runRoot, "completed.marker"), DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        Console.WriteLine(runRoot);
    }

    public async Task VerifyIssue5Async()
    {
        const string controlChannelNumber = "2.1";
        const string delayedAudioChannelNumber = "900.1";
        const string videoOnlyChannelNumber = "901.1";
        var matrices = _matrices.Where(matrix => matrix.Id is "local-management" or "local-managed-process" or "remote-management").ToArray();
        var runRoot = Path.Combine(_runsRoot, $"issue5-{DateTime.UtcNow:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(runRoot);
        var journal = new RunJournal(runRoot, _json);
        var controlResults = new List<ChannelAttemptResult>();
        var delayedAudioResults = new List<ChannelAttemptResult>();
        var videoOnlyResults = new List<ChannelAttemptResult>();
        await File.WriteAllTextAsync(
            Path.Combine(runRoot, "run.json"),
            JsonSerializer.Serialize(new
            {
                startedAtUtc = DateTimeOffset.UtcNow,
                purpose = "Verify provisional media probes refresh advertised delayed audio without rejecting intentional video-only sources",
                image = _image,
                matrices = matrices.Select(matrix => matrix.Id),
                controlChannel = controlChannelNumber,
                delayedAudioChannel = delayedAudioChannelNumber,
                videoOnlyChannel = videoOnlyChannelNumber,
                faultSource = FaultSourceAddress
            }, _json));

        foreach (var matrix in matrices)
        {
            var project = $"lineup-issue5-{matrix.Id}";
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
                await RefreshGuideAsync(browser.Context, matrix.SourceUrl, journal, "fault-source");
                if (matrix.Remote)
                {
                    await RefreshGuideAsync(browser.Context, matrix.WatchUrl, journal, "fault-consumer");
                }
                var syntheticInventory = await ReadEnabledChannelsAsync(matrix.WatchUrl);
                var delayedAudioChannel = syntheticInventory.SingleOrDefault(
                    item => string.Equals(item.Number, delayedAudioChannelNumber, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"Channel {delayedAudioChannelNumber} was not present in {matrix.Id}.");
                var videoOnlyChannel = syntheticInventory.SingleOrDefault(
                    item => string.Equals(item.Number, videoOnlyChannelNumber, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"Channel {videoOnlyChannelNumber} was not present in {matrix.Id}.");

                for (var attempt = 1; attempt <= 2; attempt++)
                {
                    var delayedAudio = await RunChannelAsync(browser.Context, matrix, delayedAudioChannel, attempt, matrixRunRoot, journal);
                    delayedAudioResults.Add(delayedAudio);
                    await journal.WriteEventAsync("delayed-audio-playback", delayedAudio);
                    if (!delayedAudio.AvPassed)
                    {
                        throw new InvalidOperationException(
                            $"{matrix.Id} delayed-audio playback attempt {attempt} failed: {delayedAudio.Failure}");
                    }
                }

                var videoOnly = await RunChannelAsync(
                    browser.Context,
                    matrix,
                    videoOnlyChannel,
                    1,
                    matrixRunRoot,
                    journal,
                    requireAudio: false);
                videoOnlyResults.Add(videoOnly);
                await journal.WriteEventAsync("video-only-playback", videoOnly);
                if (!videoOnly.AvPassed)
                {
                    throw new InvalidOperationException($"{matrix.Id} intentional video-only playback failed: {videoOnly.Failure}");
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
        await WriteIssue5SummaryAsync(runRoot, matrices, controlResults, delayedAudioResults, videoOnlyResults);
        await journal.WriteEventAsync("run-complete", new { completedAtUtc = DateTimeOffset.UtcNow });
        await File.WriteAllTextAsync(Path.Combine(runRoot, "completed.marker"), DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        Console.WriteLine(runRoot);
    }

}
