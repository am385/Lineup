using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Lineup.Streaming.Sweep;
using Xunit;

namespace Lineup.Streaming.Sweep.Tests;

/// <summary>Deterministic media-clock, manifest-normalization and retained-trace regressions; no browser, process or hardware is required.</summary>
public sealed class FragmentTimelineTests
{
    /// <summary>Independent sequence origins and unequal segment durations do not imply timeline discontinuity or lip-sync error.</summary>
    [Fact]
    public void UnequalSegmentsAndSequenceOriginsHaveContinuousTimelines()
    {
        // Arrange
        var (initialization, fragments, manifest) = Continuous();

        // Act
        var evidence = FragmentTimeline.Evaluate("s", new HashSet<string> { "v", "a" }, initialization, fragments, manifest);

        // Assert
        Assert.Equal("timeline-continuous", evidence.State);
        Assert.Equal("not-measured", evidence.LipSync);
        Assert.All(evidence.Tracks, track => Assert.Equal(6, track.PresentationEndSeconds));
        Assert.All(evidence.Tracks, track => Assert.Empty(track.Findings));
        Assert.Equal(4, Assert.Single(evidence.Tracks, track => track.Representation == "v").MaximumFragmentDurationSeconds);
    }

    /// <summary>Known timestamp offsets and drift are measured against the manifest in milliseconds, not inferred from fragment counts.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InjectedTimestampErrorIsDetected(bool decodeGap)
    {
        // Arrange
        var (initialization, fragments, manifest) = Continuous();
        var sample = fragments[("v", 11)];
        fragments[("v", 11)] = sample with
        {
            DecodeStart = sample.DecodeStart + (decodeGap ? 100 : 0),
            DecodeEnd = sample.DecodeEnd + (decodeGap ? 100 : 0),
            PresentationStart = sample.PresentationStart + 0.1,
            PresentationEnd = sample.PresentationEnd + 0.1
        };

        // Act
        var evidence = FragmentTimeline.Evaluate("s", new HashSet<string> { "v", "a" }, initialization, fragments, manifest);

        // Assert
        Assert.Equal("timeline-discontinuity", evidence.State);
        var video = Assert.Single(evidence.Tracks, track => track.Representation == "v");
        Assert.Equal(100, video.MaximumManifestDifferenceMilliseconds, 6);
        Assert.Equal(decodeGap ? 100 : 0, video.MaximumDecodeGapMilliseconds, 6);
        Assert.NotEmpty(video.Findings);
        Assert.Equal("not-measured", evidence.LipSync);
    }

    /// <summary>Only clock quantization is tolerated; the measurement never substitutes a subjective lip-sync threshold.</summary>
    [Theory]
    [InlineData(1, "timeline-continuous")]
    [InlineData(2, "timeline-discontinuity")]
    public void ManifestAgreementUsesOneClockTick(int ticks, string expectedState)
    {
        // Arrange
        var (initialization, fragments, manifest) = Continuous();
        var sample = fragments[("v", 11)];
        fragments[("v", 11)] = sample with { PresentationStart = sample.PresentationStart + ticks / 1000.0, PresentationEnd = sample.PresentationEnd + ticks / 1000.0 };

        // Act
        var evidence = FragmentTimeline.Evaluate("s", new HashSet<string> { "v", "a" }, initialization, fragments, manifest);

        // Assert
        Assert.Equal(expectedState, evidence.State);
        Assert.Equal(ticks, Assert.Single(evidence.Tracks, track => track.Representation == "v").MaximumManifestDifferenceMilliseconds, 6);
    }

    /// <summary>Fractional video clocks preserve actual two- and four-second cadence measurements without integer-fps rounding.</summary>
    [Theory]
    [InlineData(60, 2.002)]
    [InlineData(119, 3.970633333333333)]
    public void Mp4FractionalFrameDurationsAreMeasuredExactly(int frames, double expectedSeconds)
    {
        // Arrange
        var initialization = FragmentTiming.ReadInitialization(Initialization(30000, 1001));
        var fragment = Fragment(0, new int[frames], duration: 1001);

        // Act
        var timing = FragmentTiming.ReadFragment(fragment, initialization);

        // Assert
        Assert.Equal(expectedSeconds, timing.PresentationEnd, 9);
        Assert.Equal(frames * 1001, timing.DecodeEnd);
    }

    /// <summary>Consistently timestamped audio can still be semantically out of sync; transport integrity must never certify perceptual sync.</summary>
    [Fact]
    public void ConsistentShiftedTimelineIsNotALipSyncCertificate()
    {
        // Arrange
        var (initialization, fragments, manifest) = Continuous();
        foreach (var key in fragments.Keys.Where(key => key.Representation == "a").ToArray())
        {
            var sample = fragments[key];
            fragments[key] = sample with { PresentationStart = sample.PresentationStart + 0.1, PresentationEnd = sample.PresentationEnd + 0.1 };
            var expected = manifest[key];
            manifest[key] = expected with { Start = expected.Start + 0.1, End = expected.End + 0.1 };
        }

        // Act
        var evidence = FragmentTimeline.Evaluate("s", new HashSet<string> { "v", "a" }, initialization, fragments, manifest);

        // Assert
        Assert.Equal("timeline-continuous", evidence.State);
        Assert.Equal("not-measured", evidence.LipSync);
    }

    /// <summary>Missing fragments, manifest matches or overlapping coverage cannot produce a transport pass.</summary>
    [Theory]
    [InlineData("fragment")]
    [InlineData("manifest")]
    [InlineData("coverage")]
    public void MissingComparableEvidenceIsInconclusive(string missing)
    {
        // Arrange
        var (initialization, fragments, manifest) = Continuous();
        if (missing == "fragment")
        {
            fragments.Remove(("v", 11));
        }
        else if (missing == "manifest")
        {
            manifest.Remove(("v", 11));
        }
        else
        {
            foreach (var key in fragments.Keys.Where(key => key.Representation == "a").ToArray())
            {
                var sample = fragments[key];
                fragments[key] = sample with { PresentationStart = sample.PresentationStart + 100, PresentationEnd = sample.PresentationEnd + 100 };
                var expected = manifest[key];
                manifest[key] = expected with { Start = expected.Start + 100, End = expected.End + 100 };
            }
        }

        // Act
        var evidence = FragmentTimeline.Evaluate("s", new HashSet<string> { "v", "a" }, initialization, fragments, manifest);

        // Assert
        Assert.Equal("insufficient-samples", evidence.State);
        Assert.NotEmpty(evidence.Errors);
    }

    /// <summary>Period offsets, inherited timescales, non-unit sequence origins and repeated timeline entries normalize to presentation seconds.</summary>
    [Fact]
    public void ManifestUsesPeriodStartPtoTimescaleAndTrackLocalNumbers()
    {
        // Arrange
        const string manifest = """
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011"><Period start="PT10S" duration="PT6S"><AdaptationSet>
              <SegmentTemplate timescale="48000" presentationTimeOffset="480000" startNumber="40">
                <SegmentTimeline><S t="480000" d="96000" r="-1"/></SegmentTimeline>
              </SegmentTemplate><Representation id="audio"/>
            </AdaptationSet></Period></MPD>
            """;

        // Act
        var timeline = FragmentTimeline.ParseManifests([manifest]);

        // Assert
        Assert.Equal(3, timeline.Count);
        Assert.Equal(10, timeline[("audio", 40)].Start);
        Assert.Equal(16, timeline[("audio", 42)].End);
        Assert.Equal(0, timeline[("audio", 40)].Offset);
        Assert.Equal(1.0 / 48000, timeline[("audio", 40)].Precision);
    }

    /// <summary>Unbounded repeats, zero clocks and conflicting snapshots are explicit missing evidence, not silently guessed timestamps.</summary>
    [Theory]
    [InlineData("0", "0")]
    [InlineData("1000", "-1")]
    [InlineData("1000", "100000")]
    public void InvalidManifestTimingIsRejected(string scale, string repeat)
    {
        // Arrange
        var manifest = $"<MPD><Period><AdaptationSet><Representation id=\"v\"><SegmentTemplate timescale=\"{scale}\">"
            + $"<SegmentTimeline><S d=\"2000\" r=\"{repeat}\"/></SegmentTimeline></SegmentTemplate></Representation></AdaptationSet></Period></MPD>";

        // Act
        var error = Record.Exception(() => FragmentTimeline.ParseManifests([manifest]));

        // Assert
        Assert.IsType<InvalidDataException>(error);
    }

    /// <summary>A numbered fragment cannot borrow inconsistent metadata from another Period or manifest snapshot.</summary>
    [Fact]
    public void ConflictingManifestSnapshotsAreRejected()
    {
        // Arrange
        const string first = """
            <MPD><Period><AdaptationSet><Representation id="v"><SegmentTemplate>
              <SegmentTimeline><S t="0" d="2"/></SegmentTimeline>
            </SegmentTemplate></Representation></AdaptationSet></Period></MPD>
            """;
        var second = first.Replace("d=\"2\"", "d=\"3\"", StringComparison.Ordinal);

        // Act
        var error = Record.Exception(() => FragmentTimeline.ParseManifests([first, second]));

        // Assert
        Assert.IsType<InvalidDataException>(error);
        Assert.Contains("snapshots disagree", error.Message);
    }

    /// <summary>Track edits account for AAC priming, while signed composition offsets and wide decode times remain intact.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Mp4TimingHandlesEditsCompositionAndWideClocks(bool wide)
    {
        // Arrange
        var init = FragmentTiming.ReadInitialization(Initialization(1000, 1000, 100));
        var start = wide ? 4_294_967_296L : 1000L;
        var fragment = Fragment(start, [1000, -1000], wide);

        // Act
        var timing = FragmentTiming.ReadFragment(fragment, init);

        // Assert
        Assert.Equal(start, timing.DecodeStart);
        Assert.Equal(start + 2000, timing.DecodeEnd);
        Assert.Equal(start / 1000.0 - 0.1, timing.PresentationStart, 6);
        Assert.Equal(start / 1000.0 + 1.9, timing.PresentationEnd, 6);
        Assert.Equal(0, timing.MaximumDecodeGap);
    }

    /// <summary>Duration defaults in initialization remain valid when fragments omit per-sample durations.</summary>
    [Fact]
    public void Mp4UsesInitializationDurationDefaults()
    {
        // Arrange
        var init = FragmentTiming.ReadInitialization(Initialization(48000, 1024));
        var fragment = Box("moof", Box("traf", Box("tfhd", Fields(0, 1)), Box("tfdt", Fields(0, 0)), Box("trun", Fields(0, 2))));

        // Act
        var timing = FragmentTiming.ReadFragment(fragment, init);

        // Assert
        Assert.Equal(2048, timing.DecodeEnd);
        Assert.Equal(2048.0 / 48000, timing.PresentationEnd, 9);
    }

    /// <summary>Truncated boxes, absent decode clocks and absent durations cannot masquerade as zero-based successful timing.</summary>
    [Theory]
    [InlineData("truncated")]
    [InlineData("clock")]
    [InlineData("duration")]
    public void MissingMp4TimingIsRejected(string missing)
    {
        // Arrange
        var init = FragmentTiming.ReadInitialization(Initialization(1000, missing == "duration" ? 0u : 1000u));
        var fragment = missing switch
        {
            "truncated" => new byte[] { 0, 0, 0, 20, 109, 111, 111, 102 },
            "clock" => Box("moof", Box("traf", Box("tfhd", Fields(0, 1)), Box("trun", Fields(0, 2)))),
            _ => Box("moof", Box("traf", Box("tfhd", Fields(0, 1)), Box("tfdt", Fields(0, 0)), Box("trun", Fields(0, 2))))
        };

        // Act
        var error = Record.Exception(() => FragmentTiming.ReadFragment(fragment, init));

        // Assert
        Assert.IsType<InvalidDataException>(error);
    }

    /// <summary>HLS observations use their explicitly retained owned DASH manifest; missing trace bodies remain inconclusive.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RetainedTraceProvidesRealTimestampEvidenceWithoutBrowserDashRequests(bool missingResource)
    {
        // Arrange
        using var directory = new TraceDirectory();
        var trace = Path.Combine(directory.Path, "trace.zip");
        const string manifest = """
            <MPD><Period><AdaptationSet contentType="video"><Representation id="v">
              <SegmentTemplate timescale="1000"><SegmentTimeline><S d="2000" r="1"/></SegmentTimeline></SegmentTemplate>
            </Representation></AdaptationSet><AdaptationSet contentType="audio"><Representation id="a">
              <SegmentTemplate timescale="1000"><SegmentTimeline><S d="2000" r="1"/></SegmentTimeline></SegmentTemplate>
            </Representation></AdaptationSet></Period></MPD>
            """;
        var metadata = new FragmentMetadata("s", "http://localhost/media/s/manifest.mpd",
            new Dictionary<string, string> { ["v"] = "video", ["a"] = "audio" }, manifest);
        WriteTrace(trace, missingResource);

        // Act
        var evidence = FragmentTimeline.ReadTrace(trace, metadata, new HashSet<string> { "v", "a" });

        // Assert
        Assert.Equal(missingResource ? "insufficient-samples" : "timeline-continuous", evidence.State);
        Assert.Equal("not-measured", evidence.LipSync);
        if (missingResource)
        {
            Assert.Contains(evidence.Errors, error => error.Contains("resource is missing", StringComparison.Ordinal));
        }
        else
        {
            Assert.Equal(2, evidence.Tracks.Count);
            Assert.All(evidence.Tracks, track => Assert.Equal(2, track.ManifestMatches));
        }
        Assert.True(File.Exists(trace));
    }

    private static (Dictionary<string, FragmentInitialization>, Dictionary<(string Representation, long Sequence), FragmentTimingSample>,
        Dictionary<(string Representation, long Sequence), ManifestTiming>) Continuous()
    {
        var initialization = new Dictionary<string, FragmentInitialization> { ["v"] = new(1, 1000, 1000, 0), ["a"] = new(1, 1000, 1000, 0) };
        var fragments = new Dictionary<(string Representation, long Sequence), FragmentTimingSample>
        {
            [("v", 10)] = new(0, 2000, 0, 2, 0),
            [("v", 11)] = new(2000, 6000, 2, 6, 0),
            [("a", 50)] = new(0, 2000, 0, 2, 0),
            [("a", 51)] = new(2000, 4000, 2, 4, 0),
            [("a", 52)] = new(4000, 6000, 4, 6, 0)
        };
        var manifest = fragments.ToDictionary(pair => pair.Key, pair => new ManifestTiming(pair.Value.PresentationStart, pair.Value.PresentationEnd, 0, 0.001));
        return (initialization, fragments, manifest);
    }

    private static byte[] Initialization(uint timescale, uint duration, int? edit = null)
    {
        var editBox = edit is null ? [] : Box("edts", Box("elst", Fields(0, 1, 0, (uint)edit.Value, 0x10000)));
        return Box("moov", Box("mvhd", Fields(0, 0, 0, 1000, 0)),
            Box("trak", Box("tkhd", Fields(0, 0, 0, 1, 0)), Box("mdia", Box("mdhd", Fields(0, 0, 0, timescale, 0))), editBox),
            Box("mvex", Box("trex", Fields(0, 1, 1, duration, 0, 0))));
    }

    private static byte[] Fragment(long decode, int[] composition, bool wide = false, uint duration = 1000)
    {
        var clock = wide ? Fields(0x1000000, (uint)(decode >> 32), (uint)decode) : Fields(0, (uint)decode);
        var values = new List<uint> { 0x1000900, (uint)composition.Length };
        foreach (var offset in composition)
        {
            values.Add(duration);
            values.Add(unchecked((uint)offset));
        }
        return Box("moof", Box("traf", Box("tfhd", Fields(8, 1, duration)), Box("tfdt", clock), Box("trun", Fields(values.ToArray()))));
    }

    private static byte[] Fields(params uint[] values)
    {
        var bytes = new byte[values.Length * 4];
        for (var index = 0; index < values.Length; index++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(index * 4, 4), values[index]);
        }
        return bytes;
    }

    private static byte[] Box(string type, params byte[][] contents)
    {
        var payload = contents.SelectMany(bytes => bytes).ToArray();
        return [.. Fields((uint)payload.Length + 8), .. Encoding.ASCII.GetBytes(type), .. payload];
    }

    private static void WriteTrace(string path, bool missingResource)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var records = new List<string>();
        foreach (var representation in new[] { "v", "a" })
        {
            Add($"init-{representation}.mp4", Initialization(1000, 1000));
            Add($"chunk-{representation}-00001.m4s", Fragment(0, [0, 0]));
            Add($"chunk-{representation}-00002.m4s", Fragment(2000, [0, 0]));
        }
        using var writer = new StreamWriter(archive.CreateEntry("trace.network").Open());
        writer.Write(string.Join("\n", records));

        void Add(string name, byte[] bytes)
        {
            var hash = name + ".bin";
            if (!missingResource || name != "chunk-a-00002.m4s")
            {
                using var output = archive.CreateEntry("resources/" + hash).Open();
                output.Write(bytes);
            }
            records.Add(JsonSerializer.Serialize(new
            {
                snapshot = new { request = new { url = "http://localhost/media/s/" + name }, response = new { status = 200, content = new { _sha1 = hash } } }
            }));
        }
    }

    private sealed class TraceDirectory : IDisposable
    {
        /// <summary>Gets this test's uniquely owned trace directory.</summary>
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lineup-timestamps", Guid.NewGuid().ToString("N"));

        /// <summary>Creates a unique directory for this test only.</summary>
        public TraceDirectory() => Directory.CreateDirectory(Path);

        /// <summary>Removes only this test's owned artifacts.</summary>
        public void Dispose() => Directory.Delete(Path, true);
    }
}
