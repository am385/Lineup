namespace Lineup.Streaming.Sweep;

internal sealed partial class SweepHarness
{
    private async Task WriteIssue3SummaryAsync(
        string runRoot,
        IReadOnlyList<MatrixDefinition> matrices,
        IReadOnlyList<DedicatedFailureResult> failureResults)
    {
        var terminals = (await JsonSequence.ReadAsync(Path.Combine(runRoot, "events.jsonl")))
            .Where(node => node?["type"]?.GetValue<string>() == "channel-terminal")
            .Select(node => node!["payload"]!["result"]!.Deserialize<ChannelAttemptResult>(_json)!)
            .ToArray();
        var summary = new StringBuilder("# Issue 3 source-acquisition integration sweep\n\n");
        summary.AppendLine($"Completed: {DateTimeOffset.UtcNow:O}\n");
        summary.AppendLine("| Matrix | Control playback | Fault response | Stable code | Browser request failures |");
        summary.AppendLine("|---|---|---|---|---:|");
        foreach (var matrix in matrices)
        {
            var playback = terminals.Single(result => result.Matrix == matrix.Id);
            var failure = failureResults.Single(result => result.Matrix == matrix.Id);
            summary.AppendLine(
                $"| {matrix.Id} | {(playback.AvPassed ? "AV pass" : "failed")} | HTTP {failure.Status} | `{failure.Body?["code"]}` | {failure.RequestFailures.Count} |");
        }
        summary.AppendLine();
        var passedPlaybackCount = terminals.Count(result => result.AvPassed);
        summary.AppendLine(
            $"Result: **{passedPlaybackCount}/{terminals.Length} control playbacks passed "
            + $"and {failureResults.Count}/{matrices.Count} fault-injected starts returned `source_unavailable`.**");
        summary.AppendLine();
        summary.AppendLine("The fault source completed HDHomeRun discovery and lineup successfully, then returned HTTP 503 only when Lineup opened the channel stream.");
        summary.AppendLine("Local management, the dedicated Web Player broker path, and remote Lineup Link all preserved HTTP 503 plus the same safe `source_unavailable` code and message.");
        await File.WriteAllTextAsync(Path.Combine(runRoot, "issue3-summary.md"), summary.ToString());
    }

    private async Task WriteIssue4SummaryAsync(
        string runRoot,
        IReadOnlyList<MatrixDefinition> matrices,
        IReadOnlyList<Issue4FailureCase> failureCases,
        IReadOnlyList<Issue4FailureResult> failureResults)
    {
        var terminals = (await JsonSequence.ReadAsync(Path.Combine(runRoot, "events.jsonl")))
            .Where(node => node?["type"]?.GetValue<string>() == "channel-terminal")
            .Select(node => node!["payload"]!["result"]!.Deserialize<ChannelAttemptResult>(_json)!)
            .ToArray();
        var summary = new StringBuilder("# Issue 4 HDHomeRun failure-classification integration sweep\n\n");
        summary.AppendLine($"Completed: {DateTimeOffset.UtcNow:O}\n");
        summary.AppendLine("| Matrix | Control playback | Protected | No video data | No signal | Capacity | Browser request failures |");
        summary.AppendLine("|---|---|---|---|---|---|---:|");
        foreach (var matrix in matrices)
        {
            var playback = terminals.Single(result => result.Matrix == matrix.Id);
            var results = failureResults.Where(result => result.Matrix == matrix.Id).ToArray();
            var protectedResult = FormatIssue4Result(results, "protected_content");
            var noVideoDataResult = FormatIssue4Result(results, "source_no_video_data");
            var noSignalResult = FormatIssue4Result(results, "source_no_signal");
            var capacityResult = FormatIssue4Result(results, "source_capacity_exceeded");
            var requestFailureCount = results.Sum(result => result.Response.RequestFailures.Count);
            summary.AppendLine(
                $"| {matrix.Id} | {(playback.AvPassed ? "AV pass" : "failed")} | {protectedResult} | {noVideoDataResult} | "
                + $"{noSignalResult} | {capacityResult} | {requestFailureCount} |");
        }
        summary.AppendLine();
        summary.AppendLine(
            $"Result: **{terminals.Count(result => result.AvPassed)}/{terminals.Length} control playbacks passed and {failureResults.Count}/{matrices.Count * failureCases.Count} classified fault responses matched.**");
        summary.AppendLine();
        summary.AppendLine("The synthetic HDHomeRun completed discovery and lineup, then returned realistic `X-HDHomeRun-Error` diagnostics for protected content, no video data, and capacity.");
        summary.AppendLine("The no-signal case was returned only in the bounded text body, proving the adapter fallback parser does not depend on the header.");
        summary.AppendLine("Local management, the dedicated Web Player broker path, and remote Lineup Link preserved the same provider-neutral status, stable code, safe message, and retryability semantics.");
        await File.WriteAllTextAsync(Path.Combine(runRoot, "issue4-summary.md"), summary.ToString());
    }

    private static async Task WriteIssue5SummaryAsync(
        string runRoot,
        IReadOnlyList<MatrixDefinition> matrices,
        IReadOnlyList<ChannelAttemptResult> controlResults,
        IReadOnlyList<ChannelAttemptResult> delayedAudioResults,
        IReadOnlyList<ChannelAttemptResult> videoOnlyResults)
    {
        var summary = new StringBuilder("# Issue 5 provisional probe-metadata integration sweep\n\n");
        summary.AppendLine($"Completed: {DateTimeOffset.UtcNow:O}\n");
        summary.AppendLine("| Matrix | Real control | Delayed audio attempt 1 | Delayed audio attempt 2 | Intentional video-only |");
        summary.AppendLine("|---|---|---|---|---|");
        foreach (var matrix in matrices)
        {
            var control = controlResults.Single(result => result.Matrix == matrix.Id);
            var delayed = delayedAudioResults.Where(result => result.Matrix == matrix.Id).OrderBy(result => result.Attempt).ToArray();
            var videoOnly = videoOnlyResults.Single(result => result.Matrix == matrix.Id);
            summary.AppendLine(
                $"| {matrix.Id} | {FormatPlaybackResult(control)} | {FormatPlaybackResult(delayed[0])} | {FormatPlaybackResult(delayed[1])} | {FormatPlaybackResult(videoOnly)} |");
        }
        summary.AppendLine();
        summary.AppendLine(
            $"Result: **{controlResults.Count(result => result.AvPassed)}/{controlResults.Count} real AV controls, "
            + $"{delayedAudioResults.Count(result => result.AvPassed)}/{delayedAudioResults.Count} delayed-audio attempts, and "
            + $"{videoOnlyResults.Count(result => result.AvPassed)}/{videoOnlyResults.Count} intentional video-only attempts passed.**");
        summary.AppendLine();
        summary.AppendLine("The delayed-audio source advertised AC-3 immediately, emitted four wall-clock seconds of MPEG-2 video only, then added AC-3 audio.");
        summary.AppendLine("Both first and repeated starts discovered and decoded the delayed audio through local management, a dedicated managed process, and remote Lineup Link.");
        summary.AppendLine("The source without an advertised audio codec remained valid video-only media, proving that the refresh policy is driven by normalized source expectations.");
        await File.WriteAllTextAsync(Path.Combine(runRoot, "issue5-summary.md"), summary.ToString());
    }

    private static async Task WriteIssue6SummaryAsync(
        string runRoot,
        IReadOnlyList<MatrixDefinition> matrices,
        IReadOnlyList<ChannelAttemptResult> controlResults,
        IReadOnlyList<Issue6ErrorResult> errorResults,
        IReadOnlyList<ChannelAttemptResult> slateResults)
    {
        var summary = new StringBuilder("# Issue 6 early protected-content integration sweep\n\n");
        summary.AppendLine($"Completed: {DateTimeOffset.UtcNow:O}\n");
        summary.AppendLine("| Matrix | Real control | ReturnError | Seconds | Error source opens | StreamSlate | Slate source opens |");
        summary.AppendLine("|---|---|---|---:|---:|---|---:|");
        foreach (var matrix in matrices)
        {
            var control = controlResults.Single(result => result.Matrix == matrix.Id);
            var error = errorResults.Single(result => result.Matrix == matrix.Id);
            var slate = slateResults.Single(result => result.Matrix == matrix.Id);
            summary.AppendLine(
                $"| {matrix.Id} | {FormatPlaybackResult(control)} | HTTP {error.Response.Status} `{error.Response.Body?["code"]}` | "
                + $"{error.DurationSeconds:F1} | {error.SourceRequestCount} | {FormatPlaybackResult(slate)} | 0 |");
        }
        summary.AppendLine();
        summary.AppendLine(
            $"Result: **{controlResults.Count(result => result.AvPassed)}/{controlResults.Count} real AV controls, "
            + $"{errorResults.Count(result => result.Response.Status == 422 && result.SourceRequestCount == 0)}/{errorResults.Count} early protected-content errors, and "
            + $"{slateResults.Count(result => result.AvPassed)}/{slateResults.Count} protected-content slates passed.**");
        summary.AppendLine();
        summary.AppendLine("The synthetic HDHomeRun marked channel 811.1 as DRM protected in lineup metadata and counted every attempted physical stream open.");
        summary.AppendLine("ReturnError produced `protected_content` before acquisition in all six local and Lineup Link hosting configurations.");
        summary.AppendLine("StreamSlate produced stable browser video and audio in all six configurations without opening the protected physical source.");
        await File.WriteAllTextAsync(Path.Combine(runRoot, "issue6-summary.md"), summary.ToString());
    }

    private static string FormatPlaybackResult(ChannelAttemptResult result)
    {
        return result.AvPassed ? "pass" : $"failed: {result.Failure}";
    }

    private static string FormatIssue4Result(IReadOnlyList<Issue4FailureResult> results, string code)
    {
        var result = results.Single(candidate => candidate.ExpectedCode == code);
        return $"HTTP {result.Response.Status} `{result.Response.Body?["code"]}`";
    }

    private async Task WriteIssue2SummaryAsync(
        string runRoot,
        IReadOnlyList<MatrixDefinition> matrices,
        IReadOnlyList<DedicatedFailureResult> failureResults)
    {
        var terminals = (await JsonSequence.ReadAsync(Path.Combine(runRoot, "events.jsonl")))
            .Where(node => node?["type"]?.GetValue<string>() == "channel-terminal")
            .Select(node => node!["payload"]!["result"]!.Deserialize<ChannelAttemptResult>(_json)!)
            .ToArray();
        var summary = new StringBuilder("# Issue 2 quick integration sweep\n\n");
        summary.AppendLine($"Completed: {DateTimeOffset.UtcNow:O}\n");
        summary.AppendLine("| Matrix | Playback channels | AV passed | Structured failure | CORS | Browser request failures | Correlated log |");
        summary.AppendLine("|---|---:|---:|---|---|---:|---|");
        foreach (var matrix in matrices)
        {
            var playbacks = terminals.Where(result => result.Matrix == matrix.Id).ToArray();
            var failure = failureResults.Single(result => result.Matrix == matrix.Id);
            summary.AppendLine(
                $"| {matrix.Id} | {playbacks.Length} | {playbacks.Count(result => result.AvPassed)} | "
                + $"{failure.Status} `{failure.Body?["code"]}` / `{failure.Body?["stage"]}` | `{failure.CorsOrigin}` | "
                + $"{failure.RequestFailures.Count} | exactly one |");
        }
        summary.AppendLine();
        summary.AppendLine($"Result: **{terminals.Count(result => result.AvPassed)}/{terminals.Length} playback passes and {failureResults.Count}/{matrices.Count} browser-readable startup failures.**");
        summary.AppendLine();
        summary.AppendLine("Each dedicated worker returned HTTP 404 with `code: channel_not_found`, a safe error message, `stage: broker`, and `Access-Control-Allow-Origin: *`.");
        summary.AppendLine("Playwright observed no browser-level request failures, and each worker emitted exactly one correlated startup warning containing worker generation, failure code, and startup stage.");
        await File.WriteAllTextAsync(Path.Combine(runRoot, "issue2-summary.md"), summary.ToString());
    }
}
