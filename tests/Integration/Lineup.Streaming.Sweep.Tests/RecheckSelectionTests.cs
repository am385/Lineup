using Lineup.Streaming.Sweep;
using Xunit;

namespace Lineup.Streaming.Sweep.Tests;

/// <summary>Verifies post-fix retries include fresh synchronization and mandatory-evidence failures.</summary>
public sealed class RecheckSelectionTests
{
    /// <summary>Certification findings on the fresh observation cannot hide behind an earlier playback pass.</summary>
    [Theory]
    [InlineData("aligned", false)]
    [InlineData("source-unavailable", false)]
    [InlineData("misaligned", true)]
    [InlineData("insufficient-samples", true)]
    [InlineData("startup-timeout", true)]
    [InlineData("server-failure", true)]
    [InlineData("fallback-failure", true)]
    [InlineData("decoder-failure", true)]
    [InlineData("observation-timeout", true)]
    [InlineData("missing", true)]
    [InlineData("stale", true)]
    [InlineData("interrupted", true)]
    [InlineData("inflight", true)]
    [InlineData("unverified-cleanup", true)]
    [InlineData("forced-cleanup", true)]
    [InlineData("browser-failure", true)]
    [InlineData("evidence-failure", true)]
    [InlineData("capacity", true)]
    [InlineData("infrastructure", true)]
    public void RecheckChannels_UsesFreshCertificationEvidence(string scenario, bool expected)
    {
        // Arrange
        var run = CompletedRun(scenario);

        // Act
        var selected = SweepPolicy.RecheckChannels(run);
        var certification = SweepPolicy.ExitCode(run);

        // Assert
        Assert.Equal(expected, selected.Contains("1"));
        Assert.Equal(expected ? 2 : 0, certification);
    }

    /// <summary>A newer successful observation supersedes an older misalignment for retry selection.</summary>
    [Fact]
    public void RecheckChannels_NewestFreshEvidenceRecoversAlignment()
    {
        // Arrange
        var run = CompletedRun("misaligned");
        var previous = run.Attempts[1];
        run.Attempts.Add(previous with
        {
            Id = "recovered",
            StartedUtc = previous.FinishedUtc!.Value.AddSeconds(1),
            FinishedUtc = previous.FinishedUtc.Value.AddSeconds(2),
            Synchronization = new("aligned", "v", "a", 2, 2, 0, "fixture")
        });

        // Act
        var selected = SweepPolicy.RecheckChannels(run);

        // Assert
        Assert.Empty(selected);
        Assert.Equal(0, SweepPolicy.ExitCode(run));
    }

    /// <summary>Failures are qualified by projection key, not another channel's passing observation.</summary>
    [Fact]
    public void RecheckChannels_DoesNotUseAnotherProjectionSynchronization()
    {
        // Arrange
        var run = CompletedRun("aligned");
        var row = new SelectedChannel("other|player|2", "other", new("2", "Two", "source", "device") { VirtualDeviceId = "player" });
        run.Selected.Add(row);
        var playback = run.Attempts[0] with { Id = "other-playback", Key = row.Key };
        run.Attempts.Add(playback);
        run.Accepted[row.Key] = playback.Id;
        run.Attempts.Add(run.Attempts[1] with
        {
            Id = "other-sync",
            Key = row.Key,
            Synchronization = new("misaligned", "v", "a", 2, 6, 4, "fixture")
        });

        // Act
        var selected = SweepPolicy.RecheckChannels(run);

        // Assert
        Assert.Equal(["2"], selected.Order(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Pending projections retain case-insensitive number deduplication across targets.</summary>
    [Fact]
    public void RecheckChannels_PendingProjections_AreDeduplicated()
    {
        // Arrange
        var run = SweepPolicyTests.CreateRun();
        run.Selected.Add(new("other|player|1", "other", run.Selected[0].Channel));

        // Act
        var selected = SweepPolicy.RecheckChannels(run);

        // Assert
        Assert.Equal(["1"], selected);
    }

    /// <summary>Existing non-playback dispositions remain selected rather than being silently reclassified.</summary>
    [Fact]
    public void RecheckChannels_NonPlaybackVerdicts_RemainSelected()
    {
        // Arrange
        var run = CompletedRun("aligned");

        // Act
        var results = Enum.GetValues<SweepVerdict>().Where(verdict => verdict != SweepVerdict.StablePlayback)
            .Select(verdict =>
            {
                run.Attempts[0].Verdict = verdict;
                return SweepPolicy.RecheckChannels(run).Contains("1");
            }).ToArray();

        // Assert
        Assert.All(results, Assert.True);
    }

    /// <summary>Sync-only runs evaluate the accepted observation itself instead of demanding a later retune.</summary>
    [Theory]
    [InlineData("aligned", false)]
    [InlineData("misaligned", true)]
    public void RecheckChannels_SyncOnly_UsesAcceptedSynchronization(string state, bool expected)
    {
        // Arrange
        var run = CompletedRun("aligned") with { Options = new() { SyncOnly = true, MaximumProductFailures = 0 } };
        run.Attempts.RemoveAt(1);
        var observation = run.Attempts[0] with { Purpose = "sync" };
        observation.Synchronization = new(state, "v", "a", 2, state == "aligned" ? 2 : 6, state == "aligned" ? 0 : 4, "fixture");
        run.Attempts[0] = observation;

        // Act
        var selected = SweepPolicy.RecheckChannels(run);

        // Assert
        Assert.Equal(expected, selected.Contains("1"));
        Assert.Equal(expected ? 2 : 0, SweepPolicy.ExitCode(run));
    }

    /// <summary>An invalid accepted reference is not a completed passing channel for retry selection.</summary>
    [Theory]
    [InlineData("inflight")]
    [InlineData("interrupted")]
    [InlineData("missing-result")]
    [InlineData("unverified-cleanup")]
    [InlineData("forced-cleanup")]
    [InlineData("synthetic")]
    [InlineData("browser-failure")]
    [InlineData("evidence-failure")]
    public void RecheckChannels_InvalidAcceptedPlayback_IsSelected(string scenario)
    {
        // Arrange
        var run = CompletedRun("aligned");
        var playback = run.Attempts[0];
        if (scenario == "inflight")
        {
            playback.FinishedUtc = null;
        }
        playback.Interrupted = scenario == "interrupted";
        if (scenario == "missing-result")
        {
            playback.Result = null;
        }
        playback.Cleanup = new(true, scenario == "forced-cleanup", scenario != "unverified-cleanup", [], []);
        playback.SyntheticPlayback = scenario == "synthetic";
        if (scenario == "browser-failure")
        {
            playback.BrowserFailures.Add("Synthetic browser failure.");
        }
        if (scenario == "evidence-failure")
        {
            playback.EvidenceFailures.Add("Synthetic capture failure.");
        }

        // Act
        var selected = SweepPolicy.RecheckChannels(run);

        // Assert
        Assert.Contains("1", selected);
        Assert.Equal(2, SweepPolicy.ExitCode(run));
    }

    private static SweepRun CompletedRun(string scenario)
    {
        var run = SweepPolicyTests.CreateRun() with { Options = new() { MaximumProductFailures = 0 } };
        run.State = "complete";
        var playback = new SweepAttempt
        {
            Id = "playback",
            Key = run.Selected[0].Key,
            StartedUtc = DateTimeOffset.UnixEpoch,
            FinishedUtc = DateTimeOffset.UnixEpoch.AddSeconds(1),
            Verdict = SweepVerdict.StablePlayback,
            Result = new("target", "1", "One", 1, true, "not-applicable", 30, null, null, null, 1, null, "pass"),
            Cleanup = new(true, false, true, [], []),
            Synchronization = new("aligned", "v", "a", 2, 2, 0, "old attached copy")
        };
        run.Attempts.Add(playback);
        run.Accepted[playback.Key] = playback.Id;
        if (scenario == "missing")
        {
            return run;
        }
        var verdict = scenario switch
        {
            "startup-timeout" => SweepVerdict.StartupTimeout,
            "server-failure" => SweepVerdict.InitialServerFailure,
            "fallback-failure" => SweepVerdict.FallbackStartFailure,
            "decoder-failure" => SweepVerdict.DecoderFailure,
            "observation-timeout" => SweepVerdict.ObservationTimeout,
            "source-unavailable" => SweepVerdict.SourceUnavailable,
            "capacity" => SweepVerdict.CapacityContamination,
            "infrastructure" => SweepVerdict.InfrastructureFailure,
            _ => SweepVerdict.StablePlayback
        };
        var state = scenario switch
        {
            "misaligned" => "misaligned",
            "insufficient-samples" => "insufficient-samples",
            "source-unavailable" => "unavailable-on-retest",
            _ => verdict == SweepVerdict.StablePlayback ? "aligned" : "startup-failure"
        };
        var sync = new SweepAttempt
        {
            Id = "sync",
            Key = playback.Key,
            Purpose = "sync",
            StartedUtc = scenario == "stale" ? DateTimeOffset.UnixEpoch : DateTimeOffset.UnixEpoch.AddSeconds(2),
            FinishedUtc = scenario == "inflight" ? null : DateTimeOffset.UnixEpoch.AddSeconds(3),
            Interrupted = scenario == "interrupted",
            Verdict = verdict,
            Cleanup = new(true, scenario == "forced-cleanup", scenario != "unverified-cleanup", [], []),
            Synchronization = new(state, "v", "a", 2, state == "misaligned" ? 6 : 2, state == "misaligned" ? 4 : 0, "fixture")
        };
        if (scenario == "browser-failure")
        {
            sync.BrowserFailures.Add("Synthetic page crash.");
        }
        if (scenario == "evidence-failure")
        {
            sync.EvidenceFailures.Add("Synthetic startup evidence failure.");
        }
        run.Attempts.Add(sync);
        return run;
    }
}
