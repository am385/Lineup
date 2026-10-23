namespace Lineup.Streaming.Sweep;

internal sealed partial class SweepHarness
{
    public async Task VerifyIssue9Async(string? matrixId = null)
    {
        const string channelNumber = "2.1";
        const string missingChannel = "__issue9_missing__";
        var coverageMatrices = _matrices.Where(matrix =>
            matrix.Id is "local-management" or "local-managed-process" or "local-external-process" or "remote-managed-process").ToArray();
        var matrices = string.IsNullOrWhiteSpace(matrixId)
            ? coverageMatrices
            : coverageMatrices.Where(matrix => string.Equals(matrix.Id, matrixId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matrices.Length == 0)
        {
            throw new ArgumentException($"Unknown issue 9 matrix '{matrixId}'.", nameof(matrixId));
        }
        var runRoot = Path.Combine(_runsRoot, $"issue9-{DateTime.UtcNow:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(runRoot);
        var journal = new RunJournal(runRoot, _json);
        var results = new List<Issue9IntegrationResult>();
        await File.WriteAllTextAsync(
            Path.Combine(runRoot, "run.json"),
            JsonSerializer.Serialize(new
            {
                startedAtUtc = DateTimeOffset.UtcNow,
                purpose = "Verify real browser, worker HTTP, broker failure, Lineup Link, and cleanup paths covered by issue 9 integration fixtures",
                image = _image,
                matrices = matrices.Select(matrix => matrix.Id),
                channel = channelNumber,
                deterministicFailureChannel = missingChannel
            }, _json));

        foreach (var matrix in matrices)
        {
            var project = $"lineup-issue9-{matrix.Id}";
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
                var playback = await RunChannelAsync(browser.Context, matrix, channel, 1, matrixRunRoot, journal);
                await journal.WriteTerminalAsync(matrix.Id, channel.Number, playback);
                if (!playback.AvPassed)
                {
                    throw new InvalidOperationException($"{matrix.Id} issue 9 control playback failed: {playback.Failure}.");
                }

                DedicatedFailureResult? failure = null;
                if (!matrix.Remote && matrix.Mode != HostingMode.Management)
                {
                    failure = await VerifyDedicatedFailureAsync(browser.Context, matrix, missingChannel);
                    await journal.WriteEventAsync("structured-broker-failure", failure);
                }
                await WaitForCleanupAsync(matrix.SourceUrl, TimeSpan.FromSeconds(20));
                if (matrix.Remote)
                {
                    await WaitForCleanupAsync(matrix.WatchUrl, TimeSpan.FromSeconds(20));
                }
                if (matrix.Mode != HostingMode.Management)
                {
                    await WaitForBrokerStreamCountAsync(matrix, 0, TimeSpan.FromSeconds(20));
                }
                await EnsureTunerIdleAsync(TimeSpan.FromSeconds(30));
                var status = await GetStatusAsync(matrix.SourceUrl);
                var sourceActiveStreams = status["streams"]?["count"]?.GetValue<int>() ?? -1;
                var activeBrokerStreams = matrix.Mode == HostingMode.Management ? 0 : ReadBrokerStreamCount(status, matrix);
                var result = new Issue9IntegrationResult(
                    matrix.Id,
                    playback.AvPassed,
                    playback.CaptionResult,
                    failure?.Status,
                    failure?.Body?["code"]?.GetValue<string>(),
                    failure?.CorsOrigin,
                    sourceActiveStreams,
                    activeBrokerStreams);
                results.Add(result);
                await journal.WriteEventAsync("integration-coverage", result);
                await journal.WriteEventAsync("matrix-complete", new { matrix = matrix.Id });
            }
            finally
            {
                await SaveComposeLogsAsync(matrix, project, matrixRunRoot);
                await StopMatrixAsync(matrix, project, matrixRunRoot);
            }
        }

        var summary = new StringBuilder("# Issue 9 streaming-host integration coverage sweep\n\n");
        summary.AppendLine($"Completed: {DateTimeOffset.UtcNow:O}\n");
        summary.AppendLine("| Matrix | Browser AV | Captions | Dedicated broker failure | CORS | Source streams | Broker streams |");
        summary.AppendLine("|---|---|---|---|---|---:|---:|");
        foreach (var result in results)
        {
            var failure = result.FailureStatus is null ? "N/A" : $"HTTP {result.FailureStatus} `{result.FailureCode}`";
            summary.AppendLine(
                $"| {result.Matrix} | {(result.AvPassed ? "pass" : "failed")} | {result.CaptionResult} | {failure} | "
                + $"{result.CorsOrigin ?? "N/A"} | {result.SourceActiveStreams} | {result.ActiveBrokerStreams} |");
        }
        summary.AppendLine();
        summary.AppendLine($"Result: **{results.Count(result => result.AvPassed)}/{results.Count} real browser playbacks passed.**");
        summary.AppendLine();
        summary.AppendLine("The matrix covers management hosting, supervised and external dedicated workers, and remote Lineup Link consumption with real FFmpeg and browser playback.");
        summary.AppendLine("Every dedicated Web Player worker returned a browser-readable CORS failure for a deterministic broker rejection, and every matrix ended with zero active source and broker streams.");
        await File.WriteAllTextAsync(Path.Combine(runRoot, "issue9-summary.md"), summary.ToString());
        await journal.WriteEventAsync("run-complete", new { completedAtUtc = DateTimeOffset.UtcNow });
        await File.WriteAllTextAsync(Path.Combine(runRoot, "completed.marker"), DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        Console.WriteLine(runRoot);
    }

    public async Task VerifyIssue10Async(string? matrixId = null)
    {
        const string channelNumber = "122.1";
        var coverageMatrices = _matrices.Where(matrix =>
            matrix.Id is "local-management" or "local-managed-process" or "local-external-process").ToArray();
        var matrices = string.IsNullOrWhiteSpace(matrixId)
            ? coverageMatrices
            : coverageMatrices.Where(matrix => string.Equals(matrix.Id, matrixId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matrices.Length == 0)
        {
            throw new ArgumentException($"Unknown issue 10 matrix '{matrixId}'.", nameof(matrixId));
        }
        var runRoot = Path.Combine(_runsRoot, $"issue10-{DateTime.UtcNow:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(runRoot);
        var journal = new RunJournal(runRoot, _json);
        var results = new List<Issue10IntegrationResult>();
        await File.WriteAllTextAsync(
            Path.Combine(runRoot, "run.json"),
            JsonSerializer.Serialize(new
            {
                startedAtUtc = DateTimeOffset.UtcNow,
                purpose = "Verify AC-4 virtual topology policy produces browser-decodable AC-3 CMAF audio",
                image = _image,
                matrices = matrices.Select(matrix => matrix.Id),
                channel = channelNumber
            }, _json));

        foreach (var matrix in matrices)
        {
            var project = $"lineup-issue10-{matrix.Id}";
            var matrixRunRoot = Path.Combine(runRoot, matrix.Id);
            Directory.CreateDirectory(matrixRunRoot);
            await journal.WriteEventAsync("matrix-start", new { matrix = matrix.Id });
            try
            {
                await PrepareAndStartAsync(matrix, project, journal);
                await using var browser = await OpenBrowserAsync(Path.Combine(matrixRunRoot, "browser"));
                await RunCompatibilityAsync(
                    browser.Context,
                    matrix.WatchUrl,
                    Path.Combine(matrixRunRoot, "compatibility.json"),
                    journal);
                await RefreshGuideAsync(browser.Context, matrix.SourceUrl, journal, "source");
                var inventory = await ReadEnabledChannelsAsync(matrix.WatchUrl);
                var channel = inventory.SingleOrDefault(item => string.Equals(item.Number, channelNumber, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"Channel {channelNumber} was not present in {matrix.Id}.");

                ChannelAttemptResult? playback = null;
                Issue10AudioPlan? audioPlan = null;
                for (var attempt = 1; attempt <= 3; attempt++)
                {
                    playback = await RunChannelAsync(browser.Context, matrix, channel, attempt, matrixRunRoot, journal);
                    await journal.WriteTerminalAsync(matrix.Id, channel.Number, playback);
                    audioPlan = ReadIssue10AudioPlan(matrixRunRoot, channel.Number, playback);
                    if (playback.AvPassed && audioPlan is { SourceCodec: "ac4", OutputCodec: "ac3" })
                    {
                        break;
                    }
                }

                if (playback is null || audioPlan is null)
                {
                    throw new InvalidOperationException($"{matrix.Id} did not produce an active AC-4 stream plan.");
                }
                if (!playback.AvPassed)
                {
                    throw new InvalidOperationException($"{matrix.Id} issue 10 playback failed: {playback.Failure}.");
                }
                if (!string.Equals(audioPlan.SourceCodec, "ac4", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"{matrix.Id} source audio was '{audioPlan.SourceCodec}' instead of AC-4.");
                }
                if (!string.Equals(audioPlan.OutputCodec, "ac3", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"{matrix.Id} output audio was '{audioPlan.OutputCodec}' instead of AC-3.");
                }

                await WaitForCleanupAsync(matrix.SourceUrl, TimeSpan.FromSeconds(20));
                if (matrix.Mode != HostingMode.Management)
                {
                    await WaitForBrokerStreamCountAsync(matrix, 0, TimeSpan.FromSeconds(20));
                }
                await EnsureTunerIdleAsync(TimeSpan.FromSeconds(30));
                var status = await GetStatusAsync(matrix.SourceUrl);
                var sourceActiveStreams = status["streams"]?["count"]?.GetValue<int>() ?? -1;
                var activeBrokerStreams = matrix.Mode == HostingMode.Management ? 0 : ReadBrokerStreamCount(status, matrix);
                var result = new Issue10IntegrationResult(
                    matrix.Id,
                    playback.Attempt,
                    playback.AvPassed,
                    playback.Metrics?.AudioBytesDelta,
                    playback.Metrics?.AudioPeak,
                    audioPlan.SourceCodec,
                    audioPlan.OutputCodec,
                    sourceActiveStreams,
                    activeBrokerStreams);
                results.Add(result);
                await journal.WriteEventAsync("ac4-policy", result);
                await journal.WriteEventAsync("matrix-complete", new { matrix = matrix.Id });
            }
            finally
            {
                await SaveComposeLogsAsync(matrix, project, matrixRunRoot);
                await StopMatrixAsync(matrix, project, matrixRunRoot);
            }
        }

        var summary = new StringBuilder("# Issue 10 AC-4 CMAF policy integration sweep\n\n");
        summary.AppendLine($"Completed: {DateTimeOffset.UtcNow:O}\n");
        summary.AppendLine("| Matrix | Attempt | Browser AV | Audio bytes | Audio peak | Audio plan | Cleanup |");
        summary.AppendLine("|---|---:|---|---:|---:|---|---|");
        foreach (var result in results)
        {
            summary.AppendLine(
                $"| {result.Matrix} | {result.Attempt} | {(result.AvPassed ? "pass" : "failed")} | {result.AudioBytesDelta} | {result.AudioPeak} | "
                + $"{result.SourceAudioCodec} to {result.OutputAudioCodec} | {result.SourceActiveStreams} source, {result.ActiveBrokerStreams} broker |");
        }
        summary.AppendLine();
        summary.AppendLine($"Result: **{results.Count}/{results.Count} AC-4 CMAF playback checks passed.**");
        summary.AppendLine();
        summary.AppendLine("Every hosting path applied the effective virtual AC-4 policy, reported AC-4 source audio transcoded to AC-3, produced browser audio decode evidence, and cleaned up its source and broker streams.");
        await File.WriteAllTextAsync(Path.Combine(runRoot, "issue10-summary.md"), summary.ToString());
        await journal.WriteEventAsync("run-complete", new { completedAtUtc = DateTimeOffset.UtcNow });
        await File.WriteAllTextAsync(Path.Combine(runRoot, "completed.marker"), DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        Console.WriteLine(runRoot);
    }

    /// <summary>Gets the artifact directory shared by an attempt's writer and diagnostic readers.</summary>
    internal static string GetAttemptArtifactRoot(string matrixRunRoot, string channel, string? evidenceId)
    {
        if (string.IsNullOrWhiteSpace(evidenceId) || evidenceId is "." or ".." || Path.IsPathRooted(evidenceId)
            || evidenceId.IndexOfAny(['/', '\\']) >= 0 || evidenceId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidDataException("An attempt requires a nonempty, single-segment evidence identity.");
        }
        var safeChannel = string.Concat(channel.Select(character => char.IsLetterOrDigit(character) ? character : '_'));
        return Path.Combine(matrixRunRoot, "attempts", safeChannel, evidenceId);
    }

    /// <summary>Reads the active diagnostic snapshot for one completed browser attempt.</summary>
    internal static JsonNode? ReadActiveStatus(string matrixRunRoot, string channel, ChannelAttemptResult attempt)
    {
        var path = Path.Combine(GetAttemptArtifactRoot(matrixRunRoot, channel, attempt.EvidenceId), "active-status.json");
        return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) : null;
    }

    private static Issue10AudioPlan? ReadIssue10AudioPlan(string matrixRunRoot, string channel, ChannelAttemptResult attempt)
    {
        var status = ReadActiveStatus(matrixRunRoot, channel, attempt);
        var audio = status?["tracks"]?.AsArray()
            .FirstOrDefault(track => string.Equals(track?["type"]?.GetValue<string>(), "audio", StringComparison.OrdinalIgnoreCase));
        return audio is null
            ? null
            : new Issue10AudioPlan(
                audio["sourceCodec"]?.GetValue<string>() ?? string.Empty,
                audio["outputCodec"]?.GetValue<string>() ?? string.Empty);
    }

}
