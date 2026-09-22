using Lineup.Web.Services;
using Xunit;

namespace Lineup.Web.Tests.Services;

/// <summary>
/// Verifies active stream registration, media parsing, and output metadata planning.
/// </summary>
public class ActiveStreamServiceTests
{
    /// <summary>
    /// Verifies live and piped probes request decoded frames needed to detect embedded ATSC captions.
    /// </summary>
    [Fact]
    public void MediaProbeArguments_RequestFramesForEmbeddedCaptionDetection()
    {
        // Arrange
        var inputUri = new Uri("http://tuner.local/auto/v2.6");

        // Act
        var liveArguments = MediaProbeParser.CreateArguments(inputUri);
        var pipeArguments = MediaProbeParser.CreatePipeArguments();

        // Assert
        Assert.Contains("-show_frames", liveArguments);
        Assert.Contains("-show_frames", pipeArguments);
        Assert.Equal("%+3", liveArguments[Array.IndexOf(liveArguments.ToArray(), "-read_intervals") + 1]);
        Assert.Equal("%+3", pipeArguments[Array.IndexOf(pipeArguments.ToArray(), "-read_intervals") + 1]);
        Assert.Contains(liveArguments, argument => argument.Contains("frame_side_data=side_data_type", StringComparison.Ordinal));
        Assert.Contains(pipeArguments, argument => argument.Contains("frame_side_data=side_data_type", StringComparison.Ordinal));
    }

    /// <summary>
    /// Verifies that registry snapshots are ordered and isolated from caller mutations.
    /// </summary>
    [Fact]
    public void Registry_ReturnsOrderedImmutableSnapshots()
    {
        // Arrange
        var registry = new ActiveStreamRegistry();
        var mutableTracks = new List<ActiveStreamTrack>
        {
            CreateTrack("hevc", "copy")
        };
        registry.Register(new ActiveStreamSnapshot("later", "5.1", HostedStreamFormat.MpegTs, DateTime.UtcNow, null, mutableTracks));
        registry.Register(new ActiveStreamSnapshot("earlier", "2.1", HostedStreamFormat.FragmentedMp4, DateTime.UtcNow.AddMinutes(-1), null, []));

        mutableTracks.Clear();
        // Act
        var streams = registry.GetActiveStreams();

        // Assert
        Assert.Equal(["earlier", "later"], streams.Select(stream => stream.SessionId));
        Assert.Single(streams[1].Tracks);
    }

    /// <summary>
    /// Verifies that unregister removes only the requested active stream.
    /// </summary>
    [Fact]
    public void Registry_UnregisterRemovesRequestedStream()
    {
        // Arrange
        var registry = new ActiveStreamRegistry();
        registry.Register(new ActiveStreamSnapshot("one", "2.1", HostedStreamFormat.MpegTs, DateTime.UtcNow, null, []));
        registry.Register(new ActiveStreamSnapshot("two", "5.1", HostedStreamFormat.Hls, DateTime.UtcNow, null, []));

        // Act
        var removed = registry.Unregister("one");

        // Assert
        Assert.True(removed);
        Assert.Equal("two", Assert.Single(registry.GetActiveStreams()).SessionId);
    }

    /// <summary>
    /// Verifies that a protected-slate metadata update preserves the stream lifecycle callback.
    /// </summary>
    [Fact]
    public void Registry_ProtectedSlateUpdate_PreservesStopAction()
    {
        // Arrange
        var registry = new ActiveStreamRegistry();
        var stopped = false;
        ActiveStreamSnapshot? notification = null;
        registry.StopRequested += stream => notification = stream;
        var initial = new ActiveStreamSnapshot("one", "2.1", HostedStreamFormat.MpegTs, DateTime.UtcNow, null, []) { ClientId = "watch-one" };
        registry.Register(initial, () => stopped = true);
        registry.Register(ActiveStreamPlanFactory.CreateProtectedSlate("one", "2.1", HostedStreamFormat.MpegTs, initial.StartedAtUtc));

        // Act
        var requested = registry.RequestStop("one");

        // Assert
        Assert.True(requested);
        Assert.True(stopped);
        Assert.Equal("watch-one", notification?.ClientId);
        Assert.Empty(registry.GetActiveStreams());
    }

    /// <summary>
    /// Verifies a stop request does not hide a stream that has no lifecycle callback.
    /// </summary>
    [Fact]
    public void Registry_RequestStopWithoutAction_LeavesStreamRegistered()
    {
        // Arrange
        var registry = new ActiveStreamRegistry();
        registry.Register(new ActiveStreamSnapshot("one", "2.1", HostedStreamFormat.MpegTs, DateTime.UtcNow, null, []));

        // Act
        var requested = registry.RequestStop("one");

        // Assert
        Assert.False(requested);
        Assert.Equal("one", Assert.Single(registry.GetActiveStreams()).SessionId);
    }

    /// <summary>
    /// Verifies that metadata updates preserve the client endpoint captured by the initial registration.
    /// </summary>
    [Fact]
    public void Registry_MetadataUpdate_PreservesClientAddress()
    {
        // Arrange
        var registry = new ActiveStreamRegistry();
        var initial = new ActiveStreamSnapshot("one", "2.1", HostedStreamFormat.MpegTs, DateTime.UtcNow, null, [])
        {
            ClientAddress = "192.0.2.10:54321"
        };
        registry.Register(initial);

        // Act
        registry.Register(initial with { ClientAddress = null, SourceBitRate = 8_000_000 });
        var updated = Assert.Single(registry.GetActiveStreams());

        // Assert
        Assert.Equal("192.0.2.10:54321", updated.ClientAddress);
    }

    /// <summary>
    /// Verifies that initial registrations enforce the configured concurrency limit.
    /// </summary>
    [Fact]
    public void Registry_TryRegisterRejectsStreamsBeyondLimit()
    {
        // Arrange
        var registry = new ActiveStreamRegistry();
        // Act
        var first = new ActiveStreamSnapshot("one", "2.1", HostedStreamFormat.MpegTs, DateTime.UtcNow, null, []);
        var second = new ActiveStreamSnapshot("two", "5.1", HostedStreamFormat.FragmentedMp4, DateTime.UtcNow, null, []);

        var firstRegistered = registry.TryRegister(first, 1, () => { });
        var secondRegistered = registry.TryRegister(second, 1, () => { });

        // Assert
        Assert.True(firstRegistered);
        Assert.False(secondRegistered);
        Assert.Equal("one", Assert.Single(registry.GetActiveStreams()).SessionId);
    }

    /// <summary>
    /// Verifies that FFprobe JSON produces typed video, audio, and bitrate metadata.
    /// </summary>
    [Fact]
    public void MediaProbeParser_ParsesTrackAndFormatMetadata()
    {
        // Arrange
        const string json =
            """
            {
              "streams": [
                { "index": 0, "codec_type": "video", "codec_name": "hevc", "bit_rate": "8000000", "width": 1920, "height": 1080 },
                { "index": 1, "codec_type": "audio", "codec_name": "ac4", "channels": 8, "sample_rate": "48000" },
                { "index": 2, "codec_type": "subtitle", "codec_name": "dvb_subtitle" }
              ],
              "format": { "bit_rate": "8500000" }
            }
            """;

        // Act
        var result = MediaProbeParser.Parse(json);

        // Assert
        Assert.Equal(8_500_000, result.BitRate);
        Assert.Collection(
            result.Tracks,
            video =>
            {
                Assert.Equal(MediaTrackType.Video, video.Type);
                Assert.Equal("hevc", video.Codec);
                Assert.Equal(8_000_000, video.BitRate);
                Assert.Equal(1920, video.Width);
                Assert.Equal(1080, video.Height);
            },
            audio =>
            {
                Assert.Equal(MediaTrackType.Audio, audio.Type);
                Assert.Equal("ac4", audio.Codec);
                Assert.Null(audio.BitRate);
                Assert.Equal(8, audio.Channels);
                Assert.Equal(48_000, audio.SampleRate);
            },
            subtitle =>
            {
                Assert.Equal(MediaTrackType.Subtitle, subtitle.Type);
                Assert.Equal("dvb_subtitle", subtitle.Codec);
                Assert.Equal(SubtitlePresentation.BurnIn, subtitle.SubtitlePresentation);
            });
    }

    /// <summary>
    /// Verifies that FFprobe preserves language, title, dispositions, captions, and subtitle capabilities.
    /// </summary>
    [Fact]
    public void MediaProbeParser_RichTrackMetadata_IsPreservedAndClassified()
    {
        // Arrange
        const string json =
            """
            {
              "streams": [
                {
                  "index": 0, "codec_type": "video", "codec_name": "h264", "closed_captions": 1
                },
                {
                  "index": 3, "codec_type": "audio", "codec_name": "ac3", "channels": 6,
                  "tags": { "language": "spa", "title": "SAP" },
                  "disposition": { "default": 0, "forced": 0, "hearing_impaired": 1 }
                },
                {
                  "index": 5, "codec_type": "subtitle", "codec_name": "eia_608",
                  "tags": { "language": "eng", "title": "CC" },
                  "disposition": { "default": 1, "forced": 1, "hearing_impaired": 1 }
                }
              ]
            }
            """;

        // Act
        var result = MediaProbeParser.Parse(json);

        // Assert
        Assert.True(result.Tracks[0].HasClosedCaptions);
        Assert.Equal("spa", result.Tracks[1].Language);
        Assert.Equal("SAP", result.Tracks[1].Title);
        Assert.True(result.Tracks[1].IsHearingImpaired);
        Assert.Equal(SubtitlePresentation.WebVtt, result.Tracks[2].SubtitlePresentation);
        Assert.True(result.Tracks[2].IsDefault);
        Assert.True(result.Tracks[2].IsForced);
    }

    /// <summary>
    /// Verifies ATSC captions embedded in video frames become a selectable synthetic subtitle track.
    /// </summary>
    [Fact]
    public void MediaProbeParser_EmbeddedAtscCaptions_CreatesSelectableSubtitleTrack()
    {
        // Arrange
        const string json =
            """
            {
              "streams": [
                { "index": 0, "codec_type": "video", "codec_name": "mpeg2video" },
                { "index": 1, "codec_type": "audio", "codec_name": "ac3", "channels": 2 }
              ],
              "frames": [
                {
                  "media_type": "video",
                  "side_data_list": [
                    { "side_data_type": "ATSC A53 Part 4 Closed Captions" }
                  ]
                }
              ]
            }
            """;

        // Act
        var result = MediaProbeParser.Parse(json);

        // Assert
        Assert.True(result.Tracks[0].HasClosedCaptions);
        var captions = Assert.Single(result.Tracks, track => track.Type == MediaTrackType.Subtitle);
        Assert.Equal(2, captions.Index);
        Assert.Equal("eia_608", captions.Codec);
        Assert.Equal("Closed Captions", captions.Title);
        Assert.True(captions.IsEmbeddedClosedCaptions);
        Assert.Equal(SubtitlePresentation.WebVtt, captions.SubtitlePresentation);
    }

    /// <summary>
    /// Verifies that FFmpeg input descriptions provide source metadata without a second tuner connection.
    /// </summary>
    [Fact]
    public void FfmpegInputMetadataParser_ParsesAtsc3VideoAndAudio()
    {
        // Arrange
        const string videoLine = "  Stream #0:0[0x31]: Video: hevc (Main 10), yuv420p10le, 1920x1080, 59.94 fps";
        const string audioLine = "  Stream #0:1[0x32]: Audio: ac4, 48000 Hz, 5.1, fltp";

        var parsedVideo = FfmpegInputMetadataParser.TryParseTrack(videoLine, out var video);
        // Act
        var parsedAudio = FfmpegInputMetadataParser.TryParseTrack(audioLine, out var audio);

        // Assert
        Assert.True(parsedVideo);
        Assert.Equal(MediaTrackType.Video, video.Type);
        Assert.Equal("hevc", video.Codec);
        Assert.Equal(1920, video.Width);
        Assert.Equal(1080, video.Height);
        Assert.True(parsedAudio);
        Assert.Equal(MediaTrackType.Audio, audio.Type);
        Assert.Equal("ac4", audio.Codec);
        Assert.Equal(6, audio.Channels);
        Assert.Equal(48_000, audio.SampleRate);
    }

    /// <summary>
    /// Verifies decimal bitrates cannot be mistaken for an AC-4 surround layout.
    /// </summary>
    [Fact]
    public void FfmpegInputMetadataParser_BitrateBeforeLayout_ParsesLayoutChannels()
    {
        // Arrange
        const string line = "  Stream #0:1[0x32]: Audio: ac4, 62175.1 kb/s, 48000 Hz, 5.1(side), fltp";

        // Act
        var parsed = FfmpegInputMetadataParser.TryParseTrack(line, out var audio);

        // Assert
        Assert.True(parsed);
        Assert.Equal(MediaTrackType.Audio, audio.Type);
        Assert.Equal(6, audio.Channels);
    }

    /// <summary>
    /// Verifies implausible FFprobe channel counts are discarded instead of reaching stream planning and the UI.
    /// </summary>
    [Fact]
    public void MediaProbeParser_ImplausibleChannelCount_IsDiscarded()
    {
        // Arrange
        const string json =
            """
            {
              "streams": [
                { "index": 1, "codec_type": "audio", "codec_name": "ac4", "channels": 621751, "sample_rate": "48000" }
              ]
            }
            """;

        // Act
        var result = MediaProbeParser.Parse(json);

        // Assert
        Assert.Null(Assert.Single(result.Tracks).Channels);
    }

    /// <summary>
    /// Verifies that MPEG-TS metadata reflects copy and AC-4 override decisions.
    /// </summary>
    [Fact]
    public void MpegTsPlan_DescribesCopyAndAc4Output()
    {
        // Arrange
        var source = new MediaProbeResult(
        [
            new MediaTrackMetadata(0, MediaTrackType.Video, "hevc", 8_000_000, 1920, 1080, null, null),
            new MediaTrackMetadata(1, MediaTrackType.Audio, "aac", 128_000, null, null, 2, 48_000),
            new MediaTrackMetadata(2, MediaTrackType.Audio, "ac4", null, null, null, 8, 48_000)
        ],
        8_500_000);
        var settings = new AppSettings { VirtualTunerVideoMode = VirtualTunerVideoMode.ConvertHevcToH264 };

        // Act
        var stream = ActiveStreamPlanFactory.CreateMpegTs("session", "2.1", DateTime.UtcNow, source, settings);

        // Assert
        Assert.Equal("h264", stream.Tracks[0].OutputCodec);
        Assert.Equal(10_000_000, stream.Tracks[0].OutputBitRate);
        Assert.Equal("copy", stream.Tracks[1].OutputCodec);
        Assert.Equal("ac3", stream.Tracks[2].OutputCodec);
        Assert.Equal(448_000, stream.Tracks[2].OutputBitRate);
        Assert.Equal(6, stream.Tracks[2].OutputChannels);
    }

    /// <summary>
    /// Verifies the fixed browser-compatible fMP4 output metadata.
    /// </summary>
    [Fact]
    public void FragmentedMp4Plan_DescribesConfiguredTargets()
    {
        // Arrange
        var source = new MediaProbeResult(
        [
            new MediaTrackMetadata(0, MediaTrackType.Video, "mpeg2video", null, 1920, 1080, null, null),
            new MediaTrackMetadata(1, MediaTrackType.Audio, "ac3", 384_000, null, null, 6, 48_000)
        ],
        null);

        // Act
        var stream = ActiveStreamPlanFactory.CreateFragmentedMp4("session", "5.1", DateTime.UtcNow, source);

        // Assert
        Assert.Equal("h264", stream.Tracks[0].OutputCodec);
        Assert.Equal(10_000_000, stream.Tracks[0].OutputBitRate);
        Assert.Equal("aac", stream.Tracks[1].OutputCodec);
        Assert.Equal(128_000, stream.Tracks[1].OutputBitRate);
        Assert.Equal(2, stream.Tracks[1].OutputChannels);
        Assert.Equal(44_100, stream.Tracks[1].OutputSampleRate);
    }

    /// <summary>
    /// Verifies that fixed transcodes retain their known output plan when source probing fails.
    /// </summary>
    [Fact]
    public void FragmentedMp4Plan_ProbeFailureStillDescribesConfiguredTargets()
    {
        // Arrange
        var source = new MediaProbeResult([], null);

        // Act
        var stream = ActiveStreamPlanFactory.CreateFragmentedMp4("session", "104.1", DateTime.UtcNow, source);

        // Assert
        Assert.Collection(
            stream.Tracks,
            video =>
            {
                Assert.Equal(MediaTrackType.Video, video.Type);
                Assert.Equal("unknown", video.SourceCodec);
                Assert.Equal("h264", video.OutputCodec);
                Assert.Equal(10_000_000, video.OutputBitRate);
            },
            audio =>
            {
                Assert.Equal(MediaTrackType.Audio, audio.Type);
                Assert.Equal("unknown", audio.SourceCodec);
                Assert.Equal("aac", audio.OutputCodec);
                Assert.Equal(128_000, audio.OutputBitRate);
            });
    }

    /// <summary>
    /// Verifies 7.1 fMP4 audio metadata describes the retained source channel count and scaled AAC bitrate.
    /// </summary>
    [Fact]
    public void FragmentedMp4Plan_UpTo7Point1Audio_DescribesMultichannelOutput()
    {
        // Arrange
        var source = new MediaProbeResult(
        [
            new MediaTrackMetadata(0, MediaTrackType.Video, "h264", null, 1920, 1080, null, null),
            new MediaTrackMetadata(1, MediaTrackType.Audio, "ac3", 384_000, null, null, 6, 48_000)
        ],
        null);
        var selection = WatchStreamPlanner.SelectTracks(source, 1, null);

        // Act
        var stream = ActiveStreamPlanFactory.CreateFragmentedMp4("session", "5.1", DateTime.UtcNow, source, selection: selection, audioOutput: WatchAudioOutput.UpTo7Point1);

        // Assert
        var audio = Assert.Single(stream.Tracks, track => track.Type == MediaTrackType.Audio);
        Assert.Equal("aac", audio.OutputCodec);
        Assert.Equal(384_000, audio.OutputBitRate);
        Assert.Equal(6, audio.OutputChannels);
        Assert.Equal(48_000, audio.OutputSampleRate);
    }

    /// <summary>
    /// Verifies 7.1 fMP4 audio metadata reports the eight-channel limit for a 7.1.4 source.
    /// </summary>
    [Fact]
    public void FragmentedMp4Plan_UpTo7Point1Audio_LimitsOutputMetadataToEightChannels()
    {
        // Arrange
        var source = new MediaProbeResult(
        [
            new MediaTrackMetadata(0, MediaTrackType.Video, "hevc", null, 1920, 1080, null, null),
            new MediaTrackMetadata(1, MediaTrackType.Audio, "ac4", null, null, null, 12, 46_034)
        ],
        null);
        var selection = WatchStreamPlanner.SelectTracks(source, 1, null);

        // Act
        var stream = ActiveStreamPlanFactory.CreateFragmentedMp4("session", "105.1", DateTime.UtcNow, source, selection: selection, audioOutput: WatchAudioOutput.UpTo7Point1);

        // Assert
        var audio = Assert.Single(stream.Tracks, track => track.Type == MediaTrackType.Audio);
        Assert.Equal(12, audio.SourceChannels);
        Assert.Equal(8, audio.OutputChannels);
        Assert.Equal(512_000, audio.OutputBitRate);
        Assert.Equal(48_000, audio.OutputSampleRate);
    }

    private static ActiveStreamTrack CreateTrack(string sourceCodec, string outputCodec)
    {
        return new ActiveStreamTrack(MediaTrackType.Video, sourceCodec, outputCodec, null, null, 1920, 1080, null, null, null);
    }
}
