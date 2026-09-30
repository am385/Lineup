using System.Collections.Concurrent;
using Lineup.Web.Services;
using Xunit;

namespace Lineup.Web.Tests.Services;

/// <summary>
/// Verifies active stream registration, media parsing, and output metadata planning.
/// </summary>
public class ActiveStreamServiceTests
{
    /// <summary>
    /// Verifies shared CMAF metadata identifies one source-copy rendition per audio track plus the selected subtitle output.
    /// </summary>
    [Fact]
    public void CreateCmaf_SelectedRenditions_DescribeSingleCopyAndSidecar()
    {
        // Arrange
        var source = new MediaProbeResult(
        [
            new MediaTrackMetadata(0, MediaTrackType.Video, "h264", 8_000_000, 1920, 1080, null, null),
            new MediaTrackMetadata(1, MediaTrackType.Audio, "ac4", 640_000, null, null, 6, 48_000),
            new MediaTrackMetadata(2, MediaTrackType.Audio, "aac", 128_000, null, null, 2, 48_000),
            new MediaTrackMetadata(3, MediaTrackType.Subtitle, "subrip", null, null, null, null, null)
            {
                SubtitlePresentation = SubtitlePresentation.WebVtt
            }
        ], 8_768_000);
        var selection = WebPlayerTrackPlanner.SelectTracks(source, 1, 3);
        var request = new CmafStreamRequest { PreferredAudio = CmafPreferredAudio.Source, FallbackAudio = CmafFallbackAudio.Ac3 };

        // Act
        var snapshot = ActiveStreamPlanFactory.CreateCmaf("session", "7.1", DateTime.UtcNow, source, selection, request, new AppSettings());

        // Assert
        Assert.Equal(HostedStreamFormat.Cmaf, snapshot.Format);
        Assert.Equal([0, 1, 2, 3], snapshot.Tracks.Select(track => track.SourceIndex));
        Assert.Equal("copy", snapshot.Tracks.Single(track => track.SourceIndex == 0).OutputCodec);
        var selectedAudioOutputs = snapshot.Tracks.Where(track => track.SourceIndex == 1).ToArray();
        var selectedSource = Assert.Single(selectedAudioOutputs);
        Assert.Equal("Audio #1 · Source", selectedSource.OutputTitle);
        Assert.Equal("copy", selectedSource.OutputCodec);
        Assert.True(selectedSource.IsSelected);
        var alternateAudioOutputs = snapshot.Tracks.Where(track => track.SourceIndex == 2).ToArray();
        var alternateSource = Assert.Single(alternateAudioOutputs);
        Assert.Equal("Audio #2 · Source", alternateSource.OutputTitle);
        Assert.Equal("copy", alternateSource.OutputCodec);
        Assert.True(alternateSource.IsSelected);
        Assert.Equal("webvtt", snapshot.Tracks.Single(track => track.SourceIndex == 3).OutputCodec);
    }

    /// <summary>
    /// Verifies shared CMAF metadata reports copied HEVC without an unused H.264 fallback encode.
    /// </summary>
    [Fact]
    public void CreateCmaf_HevcSource_DescribesOnlySourceVideo()
    {
        // Arrange
        var source = new MediaProbeResult(
        [
            new MediaTrackMetadata(0, MediaTrackType.Video, "hevc", 12_000_000, 1920, 1080, null, null) { Profile = "Main 10", Level = 123 },
            new MediaTrackMetadata(1, MediaTrackType.Audio, "aac", 128_000, null, null, 2, 48_000)
        ], 12_128_000);
        var selection = WebPlayerTrackPlanner.SelectTracks(source, 1, null);

        // Act
        var snapshot = ActiveStreamPlanFactory.CreateCmaf("session", "105.1", DateTime.UtcNow, source, selection, new CmafStreamRequest(), new AppSettings());

        // Assert
        var videoTrack = Assert.Single(snapshot.Tracks, track => track.Type == MediaTrackType.Video);
        Assert.Equal("Source", videoTrack.OutputTitle);
        Assert.Equal("copy", videoTrack.OutputCodec);
    }

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
        Assert.Contains(liveArguments, argument => argument.Contains("profile,level", StringComparison.Ordinal));
        Assert.Contains(pipeArguments, argument => argument.Contains("profile,level", StringComparison.Ordinal));
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
        registry.Register(new ActiveStreamSnapshot("earlier", "2.1", HostedStreamFormat.Cmaf, DateTime.UtcNow.AddMinutes(-1), null, []));

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
        registry.Register(new ActiveStreamSnapshot("two", "5.1", HostedStreamFormat.Cmaf, DateTime.UtcNow, null, []));

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
        Assert.Equal("one", Assert.Single(registry.GetActiveStreams()).SessionId);
        registry.Unregister("one");
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
    /// Verifies that a client stop request closes every matching stream without affecting other clients.
    /// </summary>
    [Fact]
    public void Registry_RequestStopByClientId_StopsOnlyMatchingStreams()
    {
        // Arrange
        var registry = new ActiveStreamRegistry();
        var stoppedSessions = new List<string>();
        registry.Register(
            new ActiveStreamSnapshot("one", "2.1", HostedStreamFormat.Cmaf, DateTime.UtcNow, null, []) { ClientId = "watch-one" },
            () => stoppedSessions.Add("one"));
        registry.Register(
            new ActiveStreamSnapshot("two", "5.1", HostedStreamFormat.Cmaf, DateTime.UtcNow, null, []) { ClientId = "watch-one" },
            () => stoppedSessions.Add("two"));
        registry.Register(
            new ActiveStreamSnapshot("other", "7.1", HostedStreamFormat.Cmaf, DateTime.UtcNow, null, []) { ClientId = "watch-two" },
            () => stoppedSessions.Add("other"));

        // Act
        var stoppedCount = registry.RequestStopByClientId("watch-one");

        // Assert
        Assert.Equal(2, stoppedCount);
        Assert.Equal(["one", "two"], stoppedSessions.Order());
        Assert.Equal(["one", "other", "two"], registry.GetActiveStreams().Select(stream => stream.SessionId).Order());
        registry.Unregister("one");
        registry.Unregister("two");
        Assert.Equal("other", Assert.Single(registry.GetActiveStreams()).SessionId);
    }

    /// <summary>
    /// Verifies that metadata updates cannot resurrect a stream while its asynchronous cleanup is running.
    /// </summary>
    [Fact]
    public void Registry_MetadataUpdateAfterStopRequest_RemainsStoppingUntilUnregistered()
    {
        // Arrange
        var registry = new ActiveStreamRegistry();
        var initial = new ActiveStreamSnapshot("one", "2.1", HostedStreamFormat.Cmaf, DateTime.UtcNow, null, []);
        registry.Register(initial, () => { });

        // Act
        var requested = registry.RequestStop("one");
        registry.Register(initial with { SourceBitRate = 8_000_000 });
        var repeatedRequest = registry.RequestStop("one");

        // Assert
        Assert.True(requested);
        Assert.False(repeatedRequest);
        Assert.Equal(8_000_000, Assert.Single(registry.GetActiveStreams()).SourceBitRate);
        registry.Unregister("one");
        Assert.Empty(registry.GetActiveStreams());
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
        var second = new ActiveStreamSnapshot("two", "5.1", HostedStreamFormat.Cmaf, DateTime.UtcNow, null, []);

        var firstRegistered = registry.TryRegister(first, 1, () => { });
        var secondRegistered = registry.TryRegister(second, 1, () => { });

        // Assert
        Assert.True(firstRegistered);
        Assert.False(secondRegistered);
        Assert.Equal("one", Assert.Single(registry.GetActiveStreams()).SessionId);
    }

    /// <summary>
    /// Verifies shutdown requests every active stream and waits for their cleanup.
    /// </summary>
    [Fact]
    public async Task Registry_StopAllAsync_StopsAndWaitsForEveryStream()
    {
        // Arrange
        var registry = new ActiveStreamRegistry();
        var stoppedSessions = new ConcurrentBag<string>();
        var stopCount = 0;
        var stopsRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        registry.Register(new ActiveStreamSnapshot("one", "2.1", HostedStreamFormat.MpegTs, DateTime.UtcNow, null, []), () => RecordStop("one"));
        registry.Register(new ActiveStreamSnapshot("two", "5.1", HostedStreamFormat.Cmaf, DateTime.UtcNow, null, []), () => RecordStop("two"));

        // Act
        var stopTask = registry.StopAllAsync(TestContext.Current.CancellationToken);
        await stopsRequested.Task.WaitAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(["one", "two"], stoppedSessions.Order());
        Assert.False(stopTask.IsCompleted);
        Assert.False(registry.TryRegister(new ActiveStreamSnapshot("three", "7.1", HostedStreamFormat.MpegTs, DateTime.UtcNow, null, []), 0, () => { }));
        registry.Unregister("one");
        Assert.False(stopTask.IsCompleted);
        registry.Unregister("two");
        await stopTask;

        void RecordStop(string sessionId)
        {
            stoppedSessions.Add(sessionId);
            if (Interlocked.Increment(ref stopCount) == 2)
            {
                stopsRequested.TrySetResult();
            }
        }
    }

    /// <summary>
    /// Verifies shutdown waiting honors cancellation when a stream cannot unregister.
    /// </summary>
    [Fact]
    public async Task Registry_StopAllAsync_StreamDoesNotUnregister_HonorsCancellation()
    {
        // Arrange
        var registry = new ActiveStreamRegistry();
        registry.Register(new ActiveStreamSnapshot("one", "2.1", HostedStreamFormat.MpegTs, DateTime.UtcNow, null, []), () => { });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        // Act
        var exception = await Record.ExceptionAsync(() => registry.StopAllAsync(cancellation.Token));

        // Assert
        Assert.IsAssignableFrom<OperationCanceledException>(exception);
        Assert.Equal("one", Assert.Single(registry.GetActiveStreams()).SessionId);
    }

    /// <summary>
    /// Verifies shutdown waiting remains bounded when a synchronous lifecycle callback blocks.
    /// </summary>
    [Fact]
    public async Task Registry_StopAllAsync_StopActionBlocks_HonorsCancellation()
    {
        // Arrange
        var registry = new ActiveStreamRegistry();
        using var releaseStop = new ManualResetEventSlim();
        registry.Register(
            new ActiveStreamSnapshot("one", "2.1", HostedStreamFormat.Cmaf, DateTime.UtcNow, null, []),
            () => releaseStop.Wait(TestContext.Current.CancellationToken));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        // Act
        var exception = await Record.ExceptionAsync(() => registry.StopAllAsync(cancellation.Token));
        releaseStop.Set();

        // Assert
        Assert.IsAssignableFrom<OperationCanceledException>(exception);
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
                { "index": 0, "codec_type": "video", "codec_name": "hevc", "profile": "Main 10", "level": 123, "bit_rate": "8000000", "width": 1920, "height": 1080, "field_order": "tt" },
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
                Assert.Equal("Main 10", video.Profile);
                Assert.Equal(123, video.Level);
                Assert.Equal(VideoScanType.Interlaced, video.ScanType);
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
    /// Verifies FFprobe progressive and unknown field orders remain distinct from confirmed interlaced video.
    /// </summary>
    [Fact]
    public void MediaProbeParser_FieldOrder_ClassifiesProgressiveAndUnknownVideo()
    {
        // Arrange
        const string json =
            """
            {
              "streams": [
                { "index": 0, "codec_type": "video", "codec_name": "h264", "field_order": "progressive" },
                { "index": 1, "codec_type": "video", "codec_name": "h264", "field_order": "unknown" }
              ]
            }
            """;

        // Act
        var result = MediaProbeParser.Parse(json);

        // Assert
        Assert.Equal(VideoScanType.Progressive, result.Tracks[0].ScanType);
        Assert.Equal(VideoScanType.Unknown, result.Tracks[1].ScanType);
    }

    /// <summary>
    /// Verifies sampled frame flags detect interlacing when stream-level field order is unknown.
    /// </summary>
    [Fact]
    public void MediaProbeParser_InterlacedFrame_OverridesUnknownFieldOrder()
    {
        // Arrange
        const string json =
            """
            {
              "streams": [
                { "index": 0, "codec_type": "video", "codec_name": "mpeg2video", "field_order": "unknown" }
              ],
              "frames": [
                { "media_type": "video", "stream_index": 0, "interlaced_frame": 1 }
              ]
            }
            """;

        // Act
        var result = MediaProbeParser.Parse(json);

        // Assert
        Assert.Equal(VideoScanType.Interlaced, Assert.Single(result.Tracks).ScanType);
    }

    /// <summary>
    /// Verifies sampled interlacing evidence applies only to the video stream that emitted the frame.
    /// </summary>
    [Fact]
    public void MediaProbeParser_InterlacedFrame_ClassifiesMatchingVideoOnly()
    {
        // Arrange
        const string json =
            """
            {
              "streams": [
                { "index": 0, "codec_type": "video", "codec_name": "h264", "field_order": "progressive" },
                { "index": 3, "codec_type": "video", "codec_name": "mpeg2video", "field_order": "unknown" }
              ],
              "frames": [
                { "media_type": "video", "stream_index": 3, "interlaced_frame": 1 }
              ]
            }
            """;

        // Act
        var result = MediaProbeParser.Parse(json);

        // Assert
        Assert.Equal(VideoScanType.Progressive, result.Tracks[0].ScanType);
        Assert.Equal(VideoScanType.Interlaced, result.Tracks[1].ScanType);
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

    private static ActiveStreamTrack CreateTrack(string sourceCodec, string outputCodec)
    {
        return new ActiveStreamTrack(MediaTrackType.Video, sourceCodec, outputCodec, null, null, 1920, 1080, null, null, null);
    }
}
