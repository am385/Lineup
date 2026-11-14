using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Lineup.Integration.Streaming.Sweep.Tests;

/// <summary>Atomic, versioned and interruption-safe offline artifact coverage.</summary>
public sealed class SweepPersistenceTests
{
    /// <summary>Identical current-policy provenance remains resumable rather than rejecting every measurement generation.</summary>
    [Fact]
    public void CurrentTimestampPolicyCanResumeWithIdenticalProvenance()
    {
        // Arrange
        var run = SweepPolicyTests.CreateRun() with { Options = new() { TimestampEvidenceVersion = 1 } };
        run.Provenance["target"] = "identity";

        // Act
        var error = Record.Exception(() => SweepStore.ValidateResume(run, run.Options, "target", "identity"));

        // Assert
        Assert.Null(error);
    }

    /// <summary>Historical reports stay readable without silently resuming their sequence-only measurement policy.</summary>
    [Fact]
    public async Task LegacySequenceRunCannotResumeAsTimestampCertification()
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        var run = SweepPolicyTests.CreateRun();
        var document = JsonSerializer.SerializeToNode(run, SweepStore.Json)!;
        document["Options"]!.AsObject().Remove("TimestampEvidenceVersion");
        var path = System.IO.Path.Combine(directory.Path, "sweep-run.json");
        await File.WriteAllTextAsync(path, document.ToJsonString(), TestContext.Current.CancellationToken);

        // Act
        var loaded = await SweepStore.LoadAsync(path);
        var error = Record.Exception(() => SweepStore.ValidateResume(loaded, loaded.Options, "target", "identity"));

        // Assert
        Assert.Equal(0, loaded.Options.TimestampEvidenceVersion);
        Assert.IsType<InvalidDataException>(error);
        Assert.Contains("Canonical JSON", SweepStore.Markdown(loaded));
    }

    /// <summary>Timestamp measurements, diagnostics and the absence of lip-sync certification survive canonical and tabular export.</summary>
    [Fact]
    public async Task TimestampEvidenceIsRetainedInAllReports()
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        var run = SweepPolicyTests.CreateRun();
        var timestamps = new TimestampEvidence("s", "timeline-continuous",
            [new("v", 2, 1, 2, 2, 6, 0, 0, []), new("a", 3, 2, 3, 1.99, 6.01, 0, 0, [])], []);
        run.Attempts.Add(new()
        {
            Id = "timing",
            Key = run.Selected[0].Key,
            Purpose = "sync",
            Synchronization = new("timeline-continuous", "v", "a", 2, 50, 48, "fixture") { Timestamps = timestamps, SequenceDiagnosticExceeded = true }
        });

        // Act
        await SweepStore.ReportsAsync(directory.Path, run);
        var loaded = await SweepStore.LoadAsync(System.IO.Path.Combine(directory.Path, "sweep-run.json"));
        var markdown = await File.ReadAllTextAsync(System.IO.Path.Combine(directory.Path, "summary.md"), TestContext.Current.CancellationToken);
        using var parser = new Microsoft.VisualBasic.FileIO.TextFieldParser(System.IO.Path.Combine(directory.Path, "results.csv"));
        parser.SetDelimiters(",");
        var header = parser.ReadFields()!;
        var row = parser.ReadFields()!;

        // Assert
        Assert.Equal("timeline-continuous", loaded.Attempts[0].Synchronization?.State);
        Assert.Equal(2, loaded.Attempts[0].Synchronization?.Timestamps?.Tracks.Count);
        Assert.Contains("rendered/audible lip-sync: not-measured", markdown);
        Assert.Contains("Sequence delta (diagnostic only): 48", markdown);
        Assert.Equal(header.Length, row.Length);
        Assert.Equal("media-timeline-v1", row[Array.IndexOf(header, "TimingMeasurement")]);
        Assert.Equal("not-measured", row[Array.IndexOf(header, "LipSync")]);
        Assert.Equal("True", row[Array.IndexOf(header, "SequenceDiagnosticExceeded")]);
    }

    /// <summary>Canonical reports keep startup/media/sync evidence separate from permanent cleanup failure and never emit a success marker.</summary>
    [Fact]
    public async Task ContinuedCleanupFailureIsPersistedAndReportedSeparately()
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        var run = SweepPolicyTests.CreateRun() with { Options = new() { ContinueAfterVerifiedCleanup = true, SyncOnly = true } };
        run.State = "complete";
        var now = DateTimeOffset.UnixEpoch;
        var startup = new StartupOutcome("success", 200, null, null, null, now, now, 0, null, true, false);
        var attempt = new SweepAttempt
        {
            Id = "sync",
            Key = run.Selected[0].Key,
            Purpose = "sync",
            FinishedUtc = now.AddSeconds(30),
            Verdict = SweepVerdict.StablePlayback,
            Cleanup = new(true, true, true, [], ["Normal stop did not release the viewer."]),
            CleanupContinuationVerified = true,
            Result = new("target", "1", "One", 1, true, "track-and-cue", 30, null, null, startup, 1, null, "pass"),
            Synchronization = new("misaligned", "v", "a", 10, 13, 3, "Fresh transport sequences.")
        };
        run.Attempts.Add(attempt);
        run.Accepted[attempt.Key] = attempt.Id;
        run.Selected.Add(new("target|player|2", "target", run.Selected[0].Channel with { Number = "2" }));

        // Act
        await SweepStore.ReportsAsync(directory.Path, run);
        var loaded = await SweepStore.LoadAsync(System.IO.Path.Combine(directory.Path, "sweep-run.json"));
        var markdown = await File.ReadAllTextAsync(System.IO.Path.Combine(directory.Path, "summary.md"), TestContext.Current.CancellationToken);
        using var parser = new Microsoft.VisualBasic.FileIO.TextFieldParser(System.IO.Path.Combine(directory.Path, "results.csv"));
        parser.SetDelimiters(",");
        var header = parser.ReadFields()!;
        var observed = parser.ReadFields()!;
        var pending = parser.ReadFields()!;
        var mismatch = Record.Exception(() => SweepStore.ValidateResume(loaded, loaded.Options with { ContinueAfterVerifiedCleanup = false }, "target", "identity"));

        // Assert
        Assert.True(loaded.Options.ContinueAfterVerifiedCleanup);
        Assert.True(loaded.Attempts[0].CleanupContinuationVerified);
        Assert.True(loaded.Attempts[0].Result!.AvPassed);
        Assert.Null(loaded.Attempts[0].Result!.Failure);
        Assert.Equal("misaligned", loaded.Attempts[0].Synchronization?.State);
        Assert.Contains("lifecycle failure; see attempt evidence", markdown);
        Assert.Contains("Normal stop did not release the viewer.", markdown);
        Assert.DoesNotContain("startup-failure", markdown);
        Assert.Equal(header.Length, observed.Length);
        Assert.Equal(header.Length, pending.Length);
        Assert.Equal("success", observed[Array.IndexOf(header, "StartupOutcome")]);
        Assert.Equal("True", observed[Array.IndexOf(header, "AV")]);
        Assert.Equal("", observed[Array.IndexOf(header, "Failure")]);
        Assert.Equal("True", observed[Array.IndexOf(header, "ForcedCleanup")]);
        Assert.Equal("True", observed[Array.IndexOf(header, "CleanupContinuationVerified")]);
        Assert.Equal("misaligned", observed[Array.IndexOf(header, "Sync")]);
        Assert.IsType<InvalidDataException>(mismatch);
        Assert.False(File.Exists(System.IO.Path.Combine(directory.Path, "completed.marker")));
    }

    /// <summary>Additive continuation fields default to disabled/unverified for older schema-one checkpoints.</summary>
    [Fact]
    public async Task LegacyCheckpointCannotImplicitlyOptIntoContinuation()
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        var run = SweepPolicyTests.CreateRun();
        run.Attempts.Add(new() { Id = "attempt", Key = run.Selected[0].Key, Cleanup = new(true, true, true, [], []) });
        var document = JsonSerializer.SerializeToNode(run, SweepStore.Json)!;
        document["Options"]!.AsObject().Remove("ContinueAfterVerifiedCleanup");
        document["Attempts"]![0]!.AsObject().Remove("CleanupContinuationVerified");
        var path = System.IO.Path.Combine(directory.Path, "sweep-run.json");
        await File.WriteAllTextAsync(path, document.ToJsonString(), TestContext.Current.CancellationToken);

        // Act
        var loaded = await SweepStore.LoadAsync(path);

        // Assert
        Assert.False(loaded.Options.ContinueAfterVerifiedCleanup);
        Assert.False(loaded.Attempts[0].CleanupContinuationVerified);
        Assert.False(SweepPolicy.HasAcceptableCleanup(loaded, loaded.Attempts[0]));
    }

    /// <summary>A malformed final record is corruption, not a provably torn interruption.</summary>
    [Fact]
    public async Task MalformedTailIsNotRecovered()
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        var path = System.IO.Path.Combine(directory.Path, "events.jsonl");
        await File.WriteAllTextAsync(path, "{\"type\":\"complete\"}\n{\"bad\":]}", TestContext.Current.CancellationToken);
        // Act
        var error = await Record.ExceptionAsync(() => JsonSequence.ReadAsync(path));
        // Assert
        Assert.IsAssignableFrom<JsonException>(error);
    }

    /// <summary>Canonical round trips retain pending operations and reject incompatible policy.</summary>
    [Fact]
    public async Task CheckpointRetainsInflightOwnership()
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        var run = SweepPolicyTests.CreateRun();
        run.Attempts.Add(new SweepAttempt
        {
            Id = "attempt",
            Key = run.Selected[0].Key,
            Ownership = [new("http://localhost", "generation", "session", "http://localhost/stop/session", "http://localhost/sessions")]
        });
        run.Provenance["target"] = "identity";
        // Act
        await SweepStore.SaveAsync(directory.Path, run);
        var loaded = await SweepStore.LoadAsync(System.IO.Path.Combine(directory.Path, "sweep-run.json"));
        var mismatch = Record.Exception(() => SweepStore.ValidateResume(loaded, loaded.Options with { StableSeconds = 45 }, "target", "identity"));
        // Assert
        Assert.Equal(SweepRun.CurrentSchema, loaded.SchemaVersion);
        Assert.Null(loaded.Attempts[0].FinishedUtc);
        Assert.Equal("session", loaded.Attempts[0].Ownership[0].SessionId);
        Assert.IsType<InvalidDataException>(mismatch);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    /// <summary>Old arrays and absent/unknown versions are not guessed into canonical runs.</summary>
    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"SchemaVersion\":99}")]
    public async Task LoadRejectsUnsupportedSchema(string json)
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        var path = System.IO.Path.Combine(directory.Path, "run.json");
        await File.WriteAllTextAsync(path, json, TestContext.Current.CancellationToken);
        // Act
        var error = await Record.ExceptionAsync(() => SweepStore.LoadAsync(path));
        // Assert
        Assert.IsType<InvalidDataException>(error);
    }

    /// <summary>A partial trailing journal record is discarded; durable preceding records survive.</summary>
    [Fact]
    public async Task TornTailIsRecovered()
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        var path = System.IO.Path.Combine(directory.Path, "events.jsonl");
        await File.WriteAllTextAsync(path, "{\"type\":\"complete\"}\n{\"type\":", TestContext.Current.CancellationToken);
        // Act
        var records = await JsonSequence.ReadAsync(path);
        var secondRead = await JsonSequence.ReadAsync(path);
        // Assert
        Assert.Single(records);
        Assert.Single(secondRead);
        Assert.Equal("{\"type\":\"complete\"}\n", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    /// <summary>Interior journal corruption is fatal rather than skipped.</summary>
    [Fact]
    public async Task InteriorCorruptionIsRejected()
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        var path = System.IO.Path.Combine(directory.Path, "events.jsonl");
        await File.WriteAllTextAsync(path, "{\"type\":\"complete\"}\n{bad}\n{\"tail\":", TestContext.Current.CancellationToken);
        // Act
        var error = await Record.ExceptionAsync(() => JsonSequence.ReadAsync(path));
        // Assert
        Assert.IsAssignableFrom<JsonException>(error);
    }

    /// <summary>Offline regeneration does not require deployment/browser/tuner access and is deterministic.</summary>
    [Fact]
    public async Task OfflineReportsAreDeterministicAndNeverMarkIncompleteSuccess()
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        var run = SweepPolicyTests.CreateRun();
        // Act
        await SweepStore.ReportsAsync(directory.Path, run);
        var first = await File.ReadAllTextAsync(System.IO.Path.Combine(directory.Path, "summary.md"), TestContext.Current.CancellationToken);
        await SweepStore.ReportsAsync(directory.Path, run);
        var second = await File.ReadAllTextAsync(System.IO.Path.Combine(directory.Path, "summary.md"), TestContext.Current.CancellationToken);
        var exported = JsonNode.Parse(await File.ReadAllTextAsync(System.IO.Path.Combine(directory.Path, "results.json"), TestContext.Current.CancellationToken));
        // Assert
        Assert.Equal(first, second);
        Assert.Single(exported!["Selected"]!.AsArray());
        Assert.False(File.Exists(System.IO.Path.Combine(directory.Path, "completed.marker")));
    }

    /// <summary>Changing configured output cannot bypass the active canonical run's lease or overwrite ownership.</summary>
    [Fact]
    public async Task LockedCanonicalRunRejectsRegenerationAcrossOutputRoots()
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        using var output = new TemporaryDirectory();
        var run = SweepPolicyTests.CreateRun();
        var path = System.IO.Path.Combine(directory.Path, "sweep-run.json");
        await SweepStore.SaveAsync(directory.Path, run);
        var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        using var lease = SweepStore.AcquireRunLease(directory.Path);
        // Act
        var secondLease = Record.Exception(() => { using var competing = SweepStore.AcquireRunLease(directory.Path); });
        var sameOutput = await Record.ExceptionAsync(() => SweepStore.RegenerateReportsAsync(path, directory.Path));
        var changedOutput = await Record.ExceptionAsync(() => SweepStore.RegenerateReportsAsync(path, output.Path));
        // Assert
        Assert.IsType<IOException>(secondLease);
        Assert.IsType<IOException>(sameOutput);
        Assert.IsType<IOException>(changedOutput);
        Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(System.IO.Path.Combine(output.Path, "results.json")));
    }

    /// <summary>Idle offline regeneration preserves the authoritative checkpoint bytes and produces deterministic reports.</summary>
    [Fact]
    public async Task RegenerationNeverRewritesItsInputCheckpoint()
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        var path = System.IO.Path.Combine(directory.Path, "sweep-run.json");
        await SweepStore.SaveAsync(directory.Path, SweepPolicyTests.CreateRun());
        await File.AppendAllTextAsync(path, "\n ", TestContext.Current.CancellationToken);
        var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        // Act
        await SweepStore.RegenerateReportsAsync(path, directory.Path);
        var first = await File.ReadAllBytesAsync(System.IO.Path.Combine(directory.Path, "summary.md"), TestContext.Current.CancellationToken);
        await SweepStore.RegenerateReportsAsync(path, directory.Path);
        var second = await File.ReadAllBytesAsync(System.IO.Path.Combine(directory.Path, "summary.md"), TestContext.Current.CancellationToken);
        // Assert
        Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal(first, second);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        /// <summary>Gets the path.</summary>
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lineup-sweep-tests", Guid.NewGuid().ToString("N"));

        /// <summary>Initializes the temporary directory.</summary>
        internal TemporaryDirectory() => Directory.CreateDirectory(Path);

        /// <inheritdoc />
        public void Dispose() => Directory.Delete(Path, true);
    }
}
