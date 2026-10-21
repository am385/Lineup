namespace Lineup.Streaming.Sweep;

/// <summary>Shared classification and invariant policy for live execution and offline tests.</summary>
internal static class SweepPolicy
{
    /// <summary>Classifies structured codes before generic HTTP status.</summary>
    public static string StartupCategory(int status, string? code)
    {
        if (status is >= 200 and < 300)
        {
            return "success";
        }
        return code?.ToLowerInvariant() switch
        {
            "protected_content" => "protected-content",
            "805" or "source_capacity_exceeded" or "all_tuners_in_use" or "tuner_capacity_exhausted" or "capacity_exhausted" => "capacity-contamination",
            "source_no_video_data" or "source_no_data" or "source_no_signal" or "no_signal" or "source_unavailable" => "source-unavailable",
            "cmaf_startup_timeout" => "startup-timeout",
            _ => status switch
            {
                499 => "client-cancelled",
                408 or 504 => "startup-timeout",
                >= 500 => "server-failure",
                _ => "rejected"
            }
        };
    }

    /// <summary>Honors explicit retryability and terminal protected content before generic HTTP fallback.</summary>
    public static bool Retryable(string outcome, int status, bool? explicitRetryable) => outcome is not ("protected-content" or "inferred-protected-content" or "unconfirmed-protected-content")
        && (explicitRetryable ?? (status is 408 or 429 or 499 || status >= 500));

    /// <summary>Classifies final evidence without treating every 502/503 as off-air.</summary>
    public static SweepVerdict Classify(ChannelAttemptResult result, IReadOnlyList<StartupExchange> starts, SweepAttempt? evidence = null)
    {
        var last = starts.LastOrDefault()?.Outcome ?? result.Startup;
        if (last?.Outcome == "capacity-contamination" || starts.Any(exchange => exchange.Outcome.Outcome == "capacity-contamination"))
        {
            return SweepVerdict.CapacityContamination;
        }
        if (last is { Succeeded: false } && starts.Any(exchange => exchange.Outcome.Succeeded))
        {
            return SweepVerdict.FallbackStartFailure;
        }
        if (last?.Outcome is "protected-content" or "inferred-protected-content" or "unconfirmed-protected-content")
        {
            return ProtectionVerdict(last);
        }
        if (result.AvPassed)
        {
            if (evidence?.SyntheticPlayback == true)
            {
                return evidence.SyntheticReason switch
                {
                    "Disabled channel slate" => SweepVerdict.DisabledSlate,
                    "Protected content slate" => last is { Code: "protected_content" } ? ProtectionVerdict(last) : SweepVerdict.UnconfirmedDrm,
                    _ => SweepVerdict.UnknownSynthetic
                };
            }
            return SweepVerdict.StablePlayback;
        }
        return last?.Outcome switch
        {
            "source-unavailable" => SweepVerdict.SourceUnavailable,
            "startup-timeout" => SweepVerdict.StartupTimeout,
            "server-failure" or "rejected" => SweepVerdict.InitialServerFailure,
            "success" when result.Metrics?.MediaErrors.Count > 0 => SweepVerdict.DecoderFailure,
            "success" => SweepVerdict.ObservationTimeout,
            _ => SweepVerdict.InfrastructureFailure
        };
    }

    private static SweepVerdict ProtectionVerdict(StartupOutcome outcome) => outcome.Inferred switch
    {
        true => SweepVerdict.InferredDrm,
        false => SweepVerdict.ConfirmedDrm,
        null => SweepVerdict.UnconfirmedDrm
    };

    /// <summary>Preserves synthetic identity and reason only from the current owned flow.</summary>
    public static void CaptureFlow(SweepAttempt evidence, JsonNode status)
    {
        var currentSession = evidence.Starts.LastOrDefault()?.SessionId;
        var owned = evidence.Ownership.Where(owner => currentSession is null || owner.SessionId == currentSession)
            .Select(owner => owner.SessionId).ToHashSet(StringComparer.Ordinal);
        if (evidence.ActiveStatus?["sessionId"]?.GetValue<string>() is { } brokerSession
            && owned.Contains(evidence.ActiveStatus["workerMediaSessionId"]?.GetValue<string>() ?? string.Empty))
        {
            owned.Add(brokerSession);
        }
        foreach (var item in status["streams"]?["items"]?.AsArray() ?? [])
        {
            if (owned.Contains(item?["workerMediaSessionId"]?.GetValue<string>() ?? string.Empty) && item?["sessionId"]?.GetValue<string>() is { } session)
            {
                owned.Add(session);
            }
        }
        var flow = status["streamingFlows"]?["activePaths"]?.AsArray()
            .FirstOrDefault(path => owned.Contains(path?["sessionId"]?.GetValue<string>() ?? string.Empty));
        evidence.ActiveFlow = flow?.DeepClone();
        evidence.SyntheticPlayback = flow?["isSynthetic"]?.GetValue<bool>();
        evidence.SyntheticReason = evidence.SyntheticPlayback == true
            ? flow?["stages"]?.AsArray().FirstOrDefault(stage => stage?["kind"]?.ToString() is "PhysicalSource" or "physicalSource" or "0")?["label"]?.GetValue<string>()
            : null;
    }

    /// <summary>Finds completed, uninterrupted synchronization evidence fresh relative to accepted playback.</summary>
    public static SweepAttempt? FreshSynchronization(SweepRun run, SweepAttempt playback) => run.Attempts.LastOrDefault(attempt =>
        attempt.Key == playback.Key && attempt.Purpose == "sync" && attempt.FinishedUtc is not null
        && attempt.Synchronization is not null && !attempt.Interrupted
        && (run.Options.SyncOnly || attempt.StartedUtc >= playback.FinishedUtc));

    /// <summary>Checks references, uniqueness, ownership and completion. Incomplete runs are never success-shaped.</summary>
    public static IReadOnlyList<string> Evaluate(SweepRun run, bool requireComplete)
    {
        var violations = new List<string>(run.Violations);
        if (run.SchemaVersion != SweepRun.CurrentSchema)
        {
            violations.Add("Unsupported canonical schema.");
        }
        var selected = run.Selected.Select(row => row.Key).ToHashSet(StringComparer.Ordinal);
        if (selected.Count == 0 || selected.Count != run.Selected.Count)
        {
            violations.Add("Expected inventory is empty or contains duplicate keys.");
        }
        if (run.Attempts.Select(attempt => attempt.Id).Distinct(StringComparer.Ordinal).Count() != run.Attempts.Count)
        {
            violations.Add("Attempt identities are not unique.");
        }
        foreach (var attempt in run.Attempts)
        {
            if (!Enum.IsDefined(attempt.Verdict) || attempt.Purpose is not ("playback" or "sync"))
            {
                violations.Add($"Invalid attempt disposition for {attempt.Id}.");
            }
            if (!selected.Contains(attempt.Key))
            {
                violations.Add($"Attempt {attempt.Id} has an unknown selected key.");
            }
            if (attempt.Cleanup is { Forced: true } || attempt.Cleanup is { VerifiedAbsent: false })
            {
                violations.Add($"Lifecycle invariant failed for {attempt.Id}.");
            }
            if (attempt.Verdict == SweepVerdict.CapacityContamination
                || attempt.Starts.Any(exchange => exchange.Outcome.Outcome == "capacity-contamination"))
            {
                violations.Add($"Capacity contamination in {attempt.Id}.");
            }
            if (attempt.FinishedUtc is not null && attempt.Cleanup is null)
            {
                violations.Add($"Cleanup evidence missing for {attempt.Id}.");
            }
            if (attempt.Verdict == SweepVerdict.InfrastructureFailure && attempt.FinishedUtc is not null && !attempt.Interrupted)
            {
                violations.Add($"Infrastructure failure in {attempt.Id}.");
            }
            if (attempt.BrowserFailures.Count > 0)
            {
                violations.Add($"Browser failure in {attempt.Id}: {string.Join("; ", attempt.BrowserFailures)}");
            }
            if (attempt.EvidenceFailures.Count > 0)
            {
                violations.Add($"Mandatory evidence failed in {attempt.Id}: {string.Join("; ", attempt.EvidenceFailures)}");
            }
        }
        foreach (var (key, id) in run.Accepted)
        {
            var attempt = run.Attempts.FirstOrDefault(item => item.Id == id);
            if (!selected.Contains(key) || attempt is null || attempt.Key != key || attempt.FinishedUtc is null || attempt.Result is null
                || attempt.Interrupted || attempt.Verdict == SweepVerdict.CapacityContamination || attempt.Cleanup is not { VerifiedAbsent: true, Forced: false }
                || attempt is { Verdict: SweepVerdict.StablePlayback, SyntheticPlayback: true })
            {
                violations.Add($"Invalid accepted result for {key}.");
            }
        }
        if (requireComplete && (run.Accepted.Count != selected.Count || run.Attempts.Any(attempt => attempt.FinishedUtc is null)))
        {
            violations.Add("Selected inventory is incomplete or operations remain in flight.");
        }
        var productFailures = run.Accepted.Values.Select(id => run.Attempts.FirstOrDefault(attempt => attempt.Id == id)).OfType<SweepAttempt>()
            .Count(attempt => IsProductFailure(attempt.Verdict)
                || FreshSynchronization(run, attempt) is { } sync && IsProductFailure(sync.Verdict)
                || (FreshSynchronization(run, attempt)?.Synchronization ?? attempt.Synchronization)?.State == "misaligned");
        if (run.Options.MaximumProductFailures is int limit && productFailures > limit)
        {
            violations.Add($"Product failures {productFailures} exceeded configured maximum {limit}.");
        }
        if (requireComplete)
        {
            foreach (var attempt in run.Accepted.Values.Select(id => run.Attempts.FirstOrDefault(item => item.Id == id)).OfType<SweepAttempt>())
            {
                var synchronization = FreshSynchronization(run, attempt);
                if (attempt.Verdict == SweepVerdict.StablePlayback && synchronization is null)
                {
                    violations.Add($"Fresh synchronization observation missing for {attempt.Key}.");
                }
                if ((synchronization?.Synchronization ?? attempt.Synchronization)?.State == "insufficient-samples")
                {
                    violations.Add($"Mandatory synchronization evidence is insufficient for {attempt.Key}.");
                }
            }
        }
        foreach (var (key, id) in run.CaptionAccepted)
        {
            if (!run.Attempts.Any(attempt => attempt.Id == id && attempt.Key == key && attempt.Result is not null && attempt.FinishedUtc is not null))
            {
                violations.Add($"Caption evidence has invalid provenance for {key}.");
            }
        }
        foreach (var compatibility in run.Compatibility)
        {
            if (compatibility.FinishedUtc is null || compatibility.Cleanup is not { VerifiedAbsent: true, Forced: false }
                || compatibility.EvidenceFailures.Count > 0 || compatibility.BrowserFailures.Count > 0)
            {
                violations.Add($"Compatibility ownership unresolved or forcibly stopped: {compatibility.Id}.");
            }
        }
        return violations.Distinct(StringComparer.Ordinal).ToArray();
    }

    /// <summary>Gets the process exit policy: 0 valid, 2 invariant, 3 incomplete, 130 cancelled.</summary>
    public static int ExitCode(SweepRun run) => run.State == "cancelled" ? 130
        : run.State != "complete" ? 3 : Evaluate(run, true).Count == 0 ? 0 : 2;

    /// <summary>Identifies product findings separately from expected unavailable/protected channels.</summary>
    public static bool IsProductFailure(SweepVerdict verdict) => verdict is SweepVerdict.StartupTimeout or SweepVerdict.InitialServerFailure
        or SweepVerdict.FallbackStartFailure or SweepVerdict.DecoderFailure or SweepVerdict.ObservationTimeout or SweepVerdict.SynchronizationMisalignment;
}
