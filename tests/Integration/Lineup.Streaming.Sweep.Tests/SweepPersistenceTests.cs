using System.Text.Json;
using System.Text.Json.Nodes;
using Lineup.Streaming.Sweep;
using Xunit;

namespace Lineup.Streaming.Sweep.Tests;

/// <summary>Atomic, versioned and interruption-safe offline artifact coverage.</summary>
public sealed class SweepPersistenceTests
{
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
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lineup-sweep-tests", Guid.NewGuid().ToString("N"));

        internal TemporaryDirectory() => Directory.CreateDirectory(Path);

        public void Dispose() => Directory.Delete(Path, true);
    }
}
