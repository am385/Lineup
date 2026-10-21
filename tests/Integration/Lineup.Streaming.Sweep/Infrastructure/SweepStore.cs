namespace Lineup.Streaming.Sweep;

/// <summary>Atomic canonical checkpoints and deterministic reports.</summary>
internal static class SweepStore
{
    /// <summary>Gets canonical serializer settings.</summary>
    public static JsonSerializerOptions Json { get; } = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    /// <summary>Atomically replaces a file on the same filesystem, flushing content before replacement.</summary>
    public static async Task WriteAtomicAsync(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                var bytes = Encoding.UTF8.GetBytes(content);
                await stream.WriteAsync(bytes);
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    /// <summary>Writes the authoritative checkpoint.</summary>
    public static Task SaveAsync(string root, SweepRun run) =>
        WriteAtomicAsync(Path.Combine(root, "sweep-run.json"), JsonSerializer.Serialize(run, Json));

    /// <summary>Excludes every writer or regeneration targeting the canonical run, regardless of configured output root.</summary>
    public static FileStream AcquireRunLease(string root)
    {
        Directory.CreateDirectory(root);
        return new FileStream(Path.Combine(root, "canonical.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    /// <summary>Regenerates under canonical exclusion, never replacing the authoritative input with a loaded snapshot.</summary>
    public static async Task<SweepRun> RegenerateReportsAsync(string input, string outputRoot)
    {
        var inputRoot = Path.GetDirectoryName(Path.GetFullPath(input))!;
        var sameRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputRoot))
            .Equals(Path.TrimEndingDirectorySeparator(inputRoot), StringComparison.OrdinalIgnoreCase);
        await using var inputLease = AcquireRunLease(inputRoot);
        await using var outputLease = sameRoot ? null : AcquireRunLease(outputRoot);
        var run = await LoadAsync(input);
        await ReportsAsync(outputRoot, run, saveCheckpoint: !sameRoot);
        return run;
    }

    /// <summary>Loads only a deliberately versioned canonical object, never legacy result arrays.</summary>
    public static async Task<SweepRun> LoadAsync(string path)
    {
        var text = await File.ReadAllTextAsync(path);
        var node = JsonNode.Parse(text) as JsonObject ?? throw new InvalidDataException("Canonical run must be a JSON object.");
        var version = node.FirstOrDefault(pair => pair.Key.Equals("schemaVersion", StringComparison.OrdinalIgnoreCase)).Value?.GetValue<int>();
        if (version != SweepRun.CurrentSchema)
        {
            throw new InvalidDataException($"Unsupported or absent run schema '{version}'. Start a new run; legacy journals are not canonical checkpoints.");
        }
        foreach (var field in new[] { "Id", "Options", "State", "StartedUtc", "Selected", "Attempts", "Accepted", "Violations", "Provenance" })
        {
            if (!node.Any(pair => pair.Key.Equals(field, StringComparison.OrdinalIgnoreCase) && pair.Value is not null))
            {
                throw new InvalidDataException($"Canonical run omitted required field '{field}'.");
            }
        }
        var run = JsonSerializer.Deserialize<SweepRun>(text, Json) ?? throw new InvalidDataException("Canonical run was null.");
        run.Options.Validate();
        if (run.Selected.Select(row => row.Key).Distinct(StringComparer.Ordinal).Count() != run.Selected.Count
            || run.Attempts.Select(row => row.Id).Distinct(StringComparer.Ordinal).Count() != run.Attempts.Count
            || run.Accepted.Any(pair => !run.Attempts.Any(attempt => attempt.Id == pair.Value && attempt.Key == pair.Key))
            || run.CaptionAccepted.Any(pair => !run.Attempts.Any(attempt => attempt.Id == pair.Value && attempt.Key == pair.Key)))
        {
            throw new InvalidDataException("Canonical references or identities are corrupt.");
        }
        return run;
    }

    /// <summary>Rejects resume policy changes instead of mixing incompatible evidence.</summary>
    public static void ValidateResume(SweepRun run, SweepOptions options, string target, string fingerprint)
    {
        if (JsonSerializer.Serialize(run.Options, Json) != JsonSerializer.Serialize(options, Json)
            || (run.Provenance.TryGetValue(target, out var previous) && previous != fingerprint))
        {
            throw new InvalidDataException($"Resume provenance/options changed for '{target}'. Use an explicit new run.");
        }
    }

    /// <summary>Derives every report from the checkpoint and validates completion before emitting a marker.</summary>
    public static async Task ReportsAsync(string root, SweepRun run, bool saveCheckpoint = true)
    {
        var marker = Path.Combine(root, "completed.marker");
        if (File.Exists(marker))
        {
            File.Delete(marker);
        }
        if (saveCheckpoint)
        {
            await SaveAsync(root, run);
        }
        await WriteAtomicAsync(Path.Combine(root, "results.json"), JsonSerializer.Serialize(run, Json));
        await WriteAtomicAsync(Path.Combine(root, "results.csv"), Csv(run));
        await WriteAtomicAsync(Path.Combine(root, "summary.md"), Markdown(run));
        if (run.State == "complete" && SweepPolicy.Evaluate(run, true).Count == 0)
        {
            await WriteAtomicAsync(marker, JsonSerializer.Serialize(new { run.Id, run.FinishedUtc, exitCode = 0 }, Json));
        }
    }

    /// <summary>Generates complete uniquely keyed selected rows, including pending channels.</summary>
    public static string Markdown(SweepRun run)
    {
        var violations = SweepPolicy.Evaluate(run, run.State == "complete");
        var text = new StringBuilder("# Lineup streaming sweep\n\n");
        text.AppendLine($"Run: `{Escape(run.Id)}`. State: **{Escape(run.State)}**. Recommendation: **{(SweepPolicy.ExitCode(run) == 0 ? "PASS" : "FAIL / INCONCLUSIVE")}**.\n");
        text.AppendLine($"Started: {run.StartedUtc:O}. Finished: {run.FinishedUtc:O}. Selected: {run.Selected.Count}. Accepted: {run.Accepted.Count}. Attempts: {run.Attempts.Count}.");
        text.AppendLine($"Broadcast playback passes: {run.Aggregates.PlaybackPassed}; synthetic transport is excluded.\n");
        text.AppendLine("Canonical JSON: `sweep-run.json` (also exported as `results.json`).\n");
        text.AppendLine("## Environment and effective options\n");
        foreach (var pair in run.Environment.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            text.AppendLine($"- {Escape(pair.Key)}: {Escape(pair.Value)}");
        }
        text.AppendLine($"\n```json\n{JsonSerializer.Serialize(run.Options, Json)}\n```\n");
        text.AppendLine("## Selected channels\n");
        text.AppendLine("| Key | Name | Verdict | Attempts | Caption | Video / frames dropped | Sync | Cleanup | Media kind |");
        text.AppendLine("|---|---|---|---:|---|---|---|---|---|");
        foreach (var row in run.Selected)
        {
            var attempts = run.Attempts.Where(attempt => attempt.Key == row.Key).ToArray();
            var accepted = run.Accepted.TryGetValue(row.Key, out var id) ? attempts.Single(attempt => attempt.Id == id) : null;
            var metrics = accepted?.Result?.Metrics;
            var caption = run.CaptionAccepted.TryGetValue(row.Key, out var captionId) ? attempts.Single(item => item.Id == captionId) : accepted;
            var sync = attempts.LastOrDefault(attempt => attempt.Purpose == "sync")?.Synchronization;
            var video = metrics is null ? "unavailable" : $"{metrics.VideoWidth}x{metrics.VideoHeight} / {metrics.DroppedFrames?.ToString(CultureInfo.InvariantCulture) ?? "unavailable"}";
            text.AppendLine($"| {Escape(row.Key)} | {Escape(row.Channel.Name)} | {accepted?.Verdict.ToString() ?? "pending"} | {attempts.Length} "
                + $"| {Escape(caption?.Result?.CaptionResult ?? "unavailable")} | {video} | {Escape(sync?.State ?? "not-observed")} "
                + $"| {(accepted?.Cleanup?.VerifiedAbsent == true ? "owned sessions absent" : "unverified")} "
                + $"| {Escape(accepted?.SyntheticPlayback is true ? $"synthetic: {accepted.SyntheticReason ?? "unknown reason"}"
                    : accepted?.SyntheticPlayback is false ? "broadcast" : "unconfirmed")} |");
        }
        text.AppendLine("\n## Availability and product findings\n");
        foreach (var group in run.Accepted.Values.Select(id => run.Attempts.Single(attempt => attempt.Id == id)).GroupBy(attempt => attempt.Verdict).OrderBy(group => group.Key))
        {
            text.AppendLine($"- {group.Key}: {group.Count()} ({(SweepPolicy.IsProductFailure(group.Key) ? "product finding" : "playback / availability")})");
        }
        text.AppendLine("\n## Lifecycle and integrity findings\n");
        text.AppendLine(violations.Count == 0 ? "No established violations; incomplete observations remain inconclusive." : string.Join("\n", violations.Select(value => $"- {Escape(value)}")));
        text.AppendLine("\n## Attempt evidence\n");
        foreach (var attempt in run.Attempts)
        {
            text.AppendLine($"- `{attempt.Id}` `{Escape(attempt.Key)}` {attempt.Purpose}: {attempt.Verdict}; "
                + $"cleanup forced={attempt.Cleanup?.Forced}, absent={attempt.Cleanup?.VerifiedAbsent}; artifacts `{Escape(attempt.Artifacts ?? "unavailable")}`; "
                + $"{Escape(attempt.Result?.Failure ?? attempt.Synchronization?.Detail ?? "in flight")}");
            text.AppendLine($"  Stable interval: {attempt.StableDurationSeconds:F1}s / media {attempt.StableMediaAdvanceSeconds:F1}s. "
                + $"Caption provenance: `{run.CaptionAccepted.GetValueOrDefault(attempt.Key) ?? "unavailable"}`. "
                + $"Sync delta: {attempt.Synchronization?.Delta?.ToString(CultureInfo.InvariantCulture) ?? "unavailable"}. "
                + $"Media: {Escape(attempt.ActiveStatus?["virtualOutput"]?.ToJsonString() ?? "unavailable")}.");
            text.AppendLine($"  Protection inferred: {attempt.Result?.Startup?.Inferred?.ToString() ?? "unknown"}; "
                + $"source: {Escape(attempt.Result?.Startup?.Source ?? "unknown")}; metadata manifest: `{Escape(attempt.SynchronizationManifestUrl ?? "unavailable")}`.");
        }
        text.AppendLine("\n## Evidence limitations\n");
        text.AppendLine("Fragment sequence alignment is transport evidence, not a human audible lip-sync assessment. Missing optional counters are unavailable measurements.");
        text.AppendLine("Codec and track details, network activity and media progression are retained in attempt artifacts and canonical metrics. Protected/disabled synthetic playback is not broadcast success.");
        foreach (var limitation in run.Limitations.Distinct(StringComparer.Ordinal))
        {
            text.AppendLine($"- {Escape(limitation)}");
        }
        return text.ToString();
    }

    private static string Csv(SweepRun run)
    {
        static string Quote(string? value) => $"\"{(value ?? string.Empty).Replace("\"", "\"\"")}\"";
        var text = new StringBuilder("Matrix,Channel,Name,Attempts,AV,Caption,Category,StartupOutcome,HttpStatus,Code,Stage,StartupDurationMilliseconds,"
            + "FailurePhase,DurationSeconds,Failure,Key,Disposition,AttemptId,Purpose,Verdict,ForcedCleanup,VerifiedAbsent,Sync,Inferred,ProtectionSource,Synthetic,SyntheticReason\n");
        foreach (var row in run.Selected)
        {
            var attempts = run.Attempts.Where(attempt => attempt.Key == row.Key).ToArray();
            if (attempts.Length == 0)
            {
                text.AppendLine(string.Join(",", Quote(row.Target), Quote(row.Channel.Number), Quote(row.Channel.Name),
                    "0", "", "", "", "", "", "", "", "", "", "", "", Quote(row.Key), "pending", "", "", "", "", "", "", "", "", "", ""));
            }
            foreach (var attempt in attempts)
            {
                var accepted = run.Accepted.GetValueOrDefault(row.Key) == attempt.Id;
                var result = attempt.Result;
                text.AppendLine(string.Join(",", Quote(row.Target), Quote(row.Channel.Number), Quote(row.Channel.Name), result?.AttemptsExecuted,
                    result?.AvPassed, Quote(result?.CaptionResult), Quote(result?.Category), Quote(result?.Startup?.Outcome), result?.Startup?.HttpStatus,
                    Quote(result?.Startup?.Code), Quote(result?.Startup?.Stage), result?.Startup?.DurationMilliseconds, Quote(result?.FailurePhase),
                    result?.DurationSeconds.ToString("F1", CultureInfo.InvariantCulture), Quote(result?.Failure), Quote(row.Key), accepted ? "accepted" : "retained",
                    Quote(attempt.Id), attempt.Purpose, attempt.Verdict, attempt.Cleanup?.Forced, attempt.Cleanup?.VerifiedAbsent, Quote(attempt.Synchronization?.State),
                    attempt.Result?.Startup?.Inferred, Quote(attempt.Result?.Startup?.Source), attempt.SyntheticPlayback, Quote(attempt.SyntheticReason)));
            }
        }
        return text.ToString();
    }

    private static string Escape(string value) => value.Replace("|", "\\|", StringComparison.Ordinal).ReplaceLineEndings(" ").Replace("`", "'", StringComparison.Ordinal);
}
