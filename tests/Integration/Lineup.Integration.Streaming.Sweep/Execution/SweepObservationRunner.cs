namespace Lineup.Integration.Streaming.Sweep;

/// <summary>Shared bounded observation and lifecycle handling for playback, fresh sync and resumed sync.</summary>
internal static class SweepObservationRunner
{
    /// <summary>Retains every lifecycle violation, continuing only after explicit verification or owned deployment recovery.</summary>
    public static async Task<SweepAttempt> RunAsync(
        SweepRun run,
        string artifactRoot,
        Func<int, Task<SweepAttempt>> observe,
        Func<SweepAttempt, Task<bool>> verifyContinuation,
        Func<SweepAttempt, Task> recover,
        Func<Task> checkpoint,
        CancellationToken cancellationToken,
        SweepAttempt? previous = null)
    {
        SweepAttempt? accepted = null;
        for (var number = previous?.Result?.Attempt ?? 1; number <= run.Options.MaximumAttempts; number++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attempt = previous ?? await observe(number);
            previous = null;
            if (attempt.FinishedUtc is null || attempt.Interrupted || attempt.Result is null)
            {
                throw new InvalidOperationException($"Observation {attempt.Id} is incomplete. Evidence: {artifactRoot}");
            }
            var contaminated = attempt.Verdict == SweepVerdict.CapacityContamination
                || attempt.Starts.Any(exchange => exchange.Outcome.Outcome == "capacity-contamination");
            if (attempt.Cleanup is not { VerifiedAbsent: true, Forced: false } || contaminated)
            {
                var evidence = Path.Combine(artifactRoot, attempt.Artifacts ?? string.Empty);
                run.Violations.Add($"Lifecycle/capacity failure: {attempt.Key}, {attempt.Purpose} attempt {attempt.Id}. Evidence: {evidence}");
                await checkpoint();
                cancellationToken.ThrowIfCancellationRequested();
                if (run.Options.ContinueAfterVerifiedCleanup && !contaminated && attempt.Cleanup is { VerifiedAbsent: true, Forced: true }
                    && attempt.BrowserFailures.Count == 0 && attempt.EvidenceFailures.Count == 0 && await verifyContinuation(attempt))
                {
                    attempt.CleanupContinuationVerified = true;
                    await checkpoint();
                    Console.Error.WriteLine($"Continuing after verified emergency cleanup for {attempt.Id}; lifecycle failure is permanent. Evidence: {evidence}");
                }
                else if (!run.Options.ExistingInstance && run.Options.AllowMatrixRecovery && number < run.Options.MaximumAttempts)
                {
                    run.Violations.Add($"Capacity-release deployment recovery required by {attempt.Id}; violation is permanent.");
                    await checkpoint();
                    cancellationToken.ThrowIfCancellationRequested();
                    await recover(attempt);
                    continue;
                }
                else
                {
                    throw new InvalidOperationException($"Stopped at lifecycle/capacity failure: {attempt.Key}, {attempt.Purpose} attempt {attempt.Id}. Evidence: {evidence}");
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            accepted = accepted?.Result?.AvPassed == true ? accepted : attempt;
            if ((attempt.Purpose == "sync" && !run.Options.SyncOnly) || !SweepPolicy.ShouldRetry(attempt.Result))
            {
                return accepted;
            }
            if (number < run.Options.MaximumAttempts)
            {
                await Task.Delay(TimeSpan.FromSeconds(run.Options.RetryDelaySeconds), cancellationToken);
            }
        }
        return accepted ?? throw new InvalidOperationException($"No uncontaminated observation within the attempt limit. Evidence: {artifactRoot}");
    }
}
