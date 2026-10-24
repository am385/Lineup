using Lineup.Streaming.Sweep;
using Xunit;

namespace Lineup.Streaming.Sweep.Tests;

/// <summary>Fresh fragment parsing, representation mapping and alignment thresholds.</summary>
public sealed class FragmentAlignmentTests
{
    /// <summary>Emergency cleanup does not replace valid alignment or successful startup with a fabricated media failure.</summary>
    [Theory]
    [InlineData(0, "aligned")]
    [InlineData(3, "misaligned")]
    public void CleanupFailurePreservesSynchronizationEvidence(int delta, string expected)
    {
        // Arrange
        var (start, owner) = CurrentStartup(false);
        var attempt = new SweepAttempt
        {
            Id = "sync",
            Key = "target|player|1",
            Purpose = "sync",
            Starts = [start],
            Ownership = [owner],
            Cleanup = new(true, true, true, [owner], [])
        };
        var result = new ChannelAttemptResult("target", "1", "One", 1, true, "track-and-cue", 30, null, null, start.Outcome, 1, null, "pass");
        var now = DateTimeOffset.UnixEpoch;
        FragmentSample[] samples = [new("current", "v", 10, now), new("current", "v", 11, now.AddSeconds(2)),
            new("current", "a", 10 + delta, now), new("current", "a", 11 + delta, now.AddSeconds(2))];

        // Act
        var sync = FragmentAlignment.EvaluateObservation(attempt, result, start.Id, samples, new Dictionary<string, string> { ["v"] = "video", ["a"] = "audio" }, 1);
        var verdict = SweepPolicy.Classify(result, attempt.Starts, attempt);

        // Assert
        Assert.Equal(expected, sync.State);
        Assert.Equal(delta, sync.Delta);
        Assert.Equal(SweepVerdict.StablePlayback, verdict);
        Assert.Equal(200, result.Startup?.HttpStatus);
        Assert.True(result.AvPassed);
        Assert.Null(result.Failure);
        Assert.True(attempt.Cleanup.Forced);
    }

    /// <summary>Missing or stale metadata cannot borrow otherwise valid fragments even after verified cleanup.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("earlier-exchange")]
    public void SynchronizationStillRejectsStaleMetadata(string? metadataExchange)
    {
        // Arrange
        var (start, _) = CurrentStartup(false);
        var attempt = new SweepAttempt { Id = "sync", Key = "key", Starts = [start], Cleanup = new(true, true, true, [], []) };
        var result = new ChannelAttemptResult("target", "1", "One", 1, true, "not-applicable", 30, null, null, start.Outcome, 1, null, "pass");

        // Act
        var sync = FragmentAlignment.EvaluateObservation(attempt, result, metadataExchange, [], new Dictionary<string, string>(), 1);

        // Assert
        Assert.Equal("insufficient-samples", sync.State);
        Assert.Null(sync.Delta);
    }

    /// <summary>Post-start playback and metadata failures are not mislabeled as server startup failures.</summary>
    [Theory]
    [InlineData("success", "playback", "playback-failure")]
    [InlineData("success", "synchronization-metadata", "metadata-failure")]
    [InlineData("server-failure", "startup", "startup-failure")]
    [InlineData("source-unavailable", "startup", "unavailable-on-retest")]
    [InlineData("capacity-contamination", "startup", "capacity-contaminated")]
    public void SynchronizationFailureUsesAuthoritativeStage(string outcome, string phase, string expected)
    {
        // Arrange
        var (start, _) = CurrentStartup(false);
        var startup = start.Outcome with { Outcome = outcome, Succeeded = outcome == "success", HttpStatus = outcome == "success" ? 200 : 503 };
        var attempt = new SweepAttempt { Id = "sync", Key = "key", Starts = [start with { Outcome = startup }] };
        var result = new ChannelAttemptResult("target", "1", "One", 1, false, "not-applicable", 30, "failed", null, startup, 1, phase, "failure");

        // Act
        var sync = FragmentAlignment.EvaluateObservation(attempt, result, start.Id, [], new Dictionary<string, string>(), 1);

        // Assert
        Assert.Equal(expected, sync.State);
        Assert.Equal("failed", sync.Detail);
    }

    /// <summary>Queries do not affect current representation/sequence parsing.</summary>
    [Fact]
    public void ParseUsesCurrentFragmentShape()
    {
        // Arrange
        var url = "http://localhost/media/session/chunk-audio-main-00042.m4s?cache=1";
        // Act
        var sample = FragmentAlignment.Parse(url, DateTimeOffset.UnixEpoch);
        // Assert
        Assert.NotNull(sample);
        Assert.Equal("session", sample.Session);
        Assert.Equal("audio-main", sample.Representation);
        Assert.Equal(42, sample.Sequence);
    }

    /// <summary>Two distinct samples per stream are mandatory, even if requests repeat.</summary>
    [Fact]
    public void RepeatedFragmentDoesNotCountTwice()
    {
        // Arrange
        var now = DateTimeOffset.UnixEpoch;
        FragmentSample[] samples = [new("s", "v", 10, now), new("s", "v", 10, now), new("s", "a", 10, now), new("s", "a", 11, now)];
        // Act
        var result = FragmentAlignment.Evaluate(samples, new Dictionary<string, string> { ["v"] = "video", ["a"] = "audio" }, 1);
        // Assert
        Assert.Equal("insufficient-samples", result.State);
    }

    /// <summary>Actual active representation types come from the manifest, not numeric assumptions.</summary>
    [Theory]
    [InlineData(0, "aligned")]
    [InlineData(1, "aligned")]
    [InlineData(2, "misaligned")]
    public void DeltaUsesConfiguredInclusiveThreshold(int delta, string expected)
    {
        // Arrange
        var manifest = "<MPD xmlns=\"urn:mpeg:dash:schema:mpd:2011\"><Period><AdaptationSet contentType=\"video\"><Representation id=\"7\"/></AdaptationSet>"
            + "<AdaptationSet mimeType=\"audio/mp4\"><Representation id=\"2\"/></AdaptationSet></Period></MPD>";
        var now = DateTimeOffset.UnixEpoch;
        FragmentSample[] samples = [new("s", "7", 10, now), new("s", "7", 11, now.AddSeconds(2)),
            new("s", "2", 10 + delta, now), new("s", "2", 11 + delta, now.AddSeconds(2))];
        // Act
        var result = FragmentAlignment.Evaluate(samples, FragmentAlignment.Representations(manifest), 1);
        // Assert
        Assert.Equal(expected, result.State);
        Assert.Equal(delta, result.Delta);
    }

    /// <summary>Old sessions and rendition switches cannot be combined as fresh A/V evidence.</summary>
    [Fact]
    public void MixedSessionsAreInsufficient()
    {
        // Arrange
        var now = DateTimeOffset.UnixEpoch;
        FragmentSample[] samples = [new("old", "v", 10, now), new("old", "v", 11, now), new("new", "a", 10, now), new("new", "a", 11, now)];
        // Act
        var result = FragmentAlignment.Evaluate(samples, new Dictionary<string, string> { ["v"] = "video", ["a"] = "audio" }, 1);
        // Assert
        Assert.Equal("insufficient-samples", result.State);
    }

    /// <summary>HLS-only playback obtains accurate representation types from the current owned public DASH metadata.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HlsPlaybackUsesOwnedPublicMetadata(bool dedicated)
    {
        // Arrange
        var (start, owner) = CurrentStartup(dedicated);
        var manifest = "<MPD><Period><AdaptationSet contentType=\"video\"><Representation id=\"video-main\"/></AdaptationSet>"
            + "<AdaptationSet contentType=\"audio\"><Representation id=\"audio-main\"/></AdaptationSet></Period></MPD>";
        string? fetched = null;
        var now = DateTimeOffset.UnixEpoch;
        FragmentSample[] samples = [new("current", "video-main", 10, now), new("current", "video-main", 11, now.AddSeconds(2)),
            new("current", "audio-main", 10, now), new("current", "audio-main", 11, now.AddSeconds(2))];
        // Act
        var metadata = await FragmentAlignment.ReadOwnedMetadataAsync(start, [owner], url => { fetched = url; return Task.FromResult(manifest); });
        var result = FragmentAlignment.Evaluate(samples, metadata.Representations, 1);
        // Assert
        Assert.EndsWith(".m3u8", start.HlsUrl);
        Assert.Equal(start.DashUrl, fetched);
        Assert.Equal("current", metadata.Session);
        Assert.Equal("aligned", result.State);
        Assert.Equal("audio-main", result.AudioRepresentation);
        Assert.Equal("video-main", result.VideoRepresentation);
    }

    /// <summary>Stale fallback manifests and undeclared origins are rejected before any metadata request.</summary>
    [Theory]
    [InlineData("http://localhost/api/stream/cmaf/old/manifest.mpd")]
    [InlineData("http://other/api/stream/cmaf/current/manifest.mpd")]
    public async Task MetadataMustMatchCurrentOwnedStartup(string url)
    {
        // Arrange
        var (start, owner) = CurrentStartup(false);
        var fetched = false;
        // Act
        var error = await Record.ExceptionAsync(() => FragmentAlignment.ReadOwnedMetadataAsync(
            start with { DashUrl = url }, [owner], _ => { fetched = true; return Task.FromResult("<MPD/>"); }));
        // Assert
        Assert.IsType<InvalidDataException>(error);
        Assert.False(fetched);
    }

    /// <summary>A failed or replaced fallback cannot borrow an earlier successful source's manifest.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrReplacedFallbackCannotReuseMetadata(bool replaced)
    {
        // Arrange
        var (start, owner) = CurrentStartup(false);
        start = replaced ? start with { SessionId = "replacement" } : start with { Outcome = start.Outcome with { Succeeded = false } };
        var fetched = false;
        // Act
        var error = await Record.ExceptionAsync(() => FragmentAlignment.ReadOwnedMetadataAsync(
            start, [owner], _ => { fetched = true; return Task.FromResult("<MPD/>"); }));
        // Assert
        Assert.IsType<InvalidDataException>(error);
        Assert.False(fetched);
    }

    /// <summary>Video-only metadata or observed rendition changes never manufacture an aligned audio/video pair.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HlsNoAudioAndRenditionSwitchRemainInsufficient(bool renditionSwitch)
    {
        // Arrange
        var (start, owner) = CurrentStartup(false);
        var manifest = "<MPD><Period><AdaptationSet contentType=\"video\"><Representation id=\"v\"/><Representation id=\"v2\"/></AdaptationSet>"
            + (renditionSwitch ? "<AdaptationSet contentType=\"audio\"><Representation id=\"a\"/></AdaptationSet>" : "") + "</Period></MPD>";
        var now = DateTimeOffset.UnixEpoch;
        List<FragmentSample> samples = [new("current", "v", 10, now), new("current", "v", 11, now)];
        if (renditionSwitch)
        {
            samples.AddRange([new("current", "v2", 12, now), new("current", "a", 10, now), new("current", "a", 11, now)]);
        }
        // Act
        var metadata = await FragmentAlignment.ReadOwnedMetadataAsync(start, [owner], _ => Task.FromResult(manifest));
        var result = FragmentAlignment.Evaluate(samples, metadata.Representations, 1);
        // Assert
        Assert.Equal("insufficient-samples", result.State);
    }

    private static (StartupExchange Start, OwnedSession Owner) CurrentStartup(bool dedicated)
    {
        var endpoint = dedicated ? "http://localhost/media/start/1" : "http://localhost/api/stream/cmaf/start/1";
        var prefix = dedicated ? "http://localhost/media/current/" : "http://localhost/api/stream/cmaf/current/";
        var stop = dedicated ? "http://localhost/media/stop/current" : null;
        var now = DateTimeOffset.UnixEpoch;
        var outcome = new StartupOutcome("success", 200, null, null, null, now, now, 0, endpoint, true, false);
        return (new("exchange", outcome, "current", stop, prefix + "manifest.mpd", prefix + "playlist.m3u8", null),
            OwnedPlayback.CreateOwner(endpoint, "current", stop, "generation"));
    }
}
