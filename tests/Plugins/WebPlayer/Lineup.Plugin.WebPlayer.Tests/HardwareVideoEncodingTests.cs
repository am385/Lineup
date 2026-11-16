using System.Diagnostics;
using Lineup.Core.Common.Streaming;
using Lineup.Core.Extensibility.Streaming;
using Lineup.Core.Streaming.FFmpeg;
using Lineup.Plugin.WebPlayer.FFmpeg;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lineup.Plugin.WebPlayer.Tests;

/// <summary>Verifies optional GPU encoding preserves copy-first media policy and owns initialization failures.</summary>
public sealed class HardwareVideoEncodingTests
{
    /// <summary>Verifies GPU CMAF encoding avoids redundant A53 insertion without removing selectable caption or text sidecars.</summary>
    [Theory]
    [InlineData(HardwareVideoEncoder.Qsv, "-a53cc:v:0", "0")]
    [InlineData(HardwareVideoEncoder.Vaapi, "-sei:v:0", "-a53_cc")]
    [InlineData(HardwareVideoEncoder.Software, null, null)]
    [InlineData(HardwareVideoEncoder.Nvenc, null, null)]
    public void Cmaf_EncodedCaptions_KeepWebVttWithoutRedundantIntelSei(HardwareVideoEncoder encoder, string? option, string? value)
    {
        // Arrange
        var source = new MediaProbeResult(
        [
            new MediaTrackMetadata(0, MediaTrackType.Video, "h264", null, 1280, 720, null, null),
            new MediaTrackMetadata(2, MediaTrackType.Subtitle, "eia_608", null, null, null, null, null)
            {
                IsEmbeddedClosedCaptions = true,
                SubtitlePresentation = SubtitlePresentation.WebVtt
            },
            new MediaTrackMetadata(3, MediaTrackType.Subtitle, "subrip", null, null, null, null, null) { SubtitlePresentation = SubtitlePresentation.WebVtt }
        ], null);
        var profile = new VirtualTranscodeProfile { AlwaysTranscodeVideo = true, HardwareEncoding = new() { Encoder = encoder } };
        var request = new CmafStreamRequest { PreferredVideo = CmafPreferredVideo.Source };
        var selection = CmafStreamPlanner.SelectTracks(source, request);

        // Act
        var arguments = CmafStreamPlanner.CreateArguments(profile, source, selection, request, "manifest.mpd", new Dictionary<int, string> { [3] = "captions-3.vtt" }).ToList();
        var subtitles = CmafStreamPlanner.GetSelectableSubtitles(source);

        // Assert
        Assert.Equal(HardwareVideoEncodingPlanner.GetEncoderName(encoder), arguments[arguments.IndexOf("-c:v:0") + 1]);
        if (option is not null)
        {
            Assert.Equal(value, arguments[arguments.IndexOf(option) + 1]);
            Assert.Equal(1, arguments.Count(argument => argument == option));
            Assert.True(arguments.IndexOf(option) < arguments.IndexOf("manifest.mpd"));
        }
        else
        {
            Assert.DoesNotContain("-a53cc:v:0", arguments);
            Assert.DoesNotContain("-sei:v:0", arguments);
        }
        Assert.Equal([2, 3], subtitles.Select(track => track.Index));
        Assert.True(subtitles[0].IsEmbeddedClosedCaptions);
        Assert.Equal("captions-3.vtt", arguments[^1]);
        Assert.Equal("webvtt", arguments[arguments.IndexOf("-c:s") + 1]);
        Assert.Contains("0:3", arguments);
    }

    /// <summary>Verifies an explicit source-video request cannot bypass configured forced encoding or its bitrate constraint.</summary>
    [Theory]
    [InlineData(HardwareVideoEncoder.Software, "libx264")]
    [InlineData(HardwareVideoEncoder.Qsv, "h264_qsv")]
    public void Cmaf_ForcedVideo_EncodesSourceWithConfiguredCeiling(HardwareVideoEncoder encoder, string expectedEncoder)
    {
        // Arrange
        var source = new MediaProbeResult([new MediaTrackMetadata(0, MediaTrackType.Video, "h264", 12_000_000, 1920, 1080, null, null)
            { ScanType = VideoScanType.Progressive, FrameRate = new(25, 1) }], null);
        var profile = new VirtualTranscodeProfile
        {
            AlwaysTranscodeVideo = true,
            HardwareEncoding = new() { Encoder = encoder },
            MaximumVideoBitRateMbps = 3
        };
        var request = new CmafStreamRequest
        {
            PreferredVideo = CmafPreferredVideo.Source,
            Overrides = new() { Enabled = true, Video = CmafPreferredVideo.Source }
        };
        var selection = CmafStreamPlanner.SelectTracks(source, request);

        // Act
        var arguments = CmafStreamPlanner.CreateArguments(profile, source, selection, request, "manifest.mpd").ToList();
        var processing = CmafStreamPlanner.CreateVideoProcessing(profile, source, selection, request);
        var snapshot = ActiveStreamPlanFactory.CreateCmaf("forced", "2.3", DateTime.UnixEpoch, source, selection, request, profile);

        // Assert
        Assert.Equal(expectedEncoder, arguments[arguments.IndexOf("-c:v:0") + 1]);
        Assert.Equal("3M", arguments[arguments.IndexOf("-maxrate:v:0") + 1]);
        Assert.Equal("6M", arguments[arguments.IndexOf("-bufsize:v:0") + 1]);
        Assert.NotNull(processing);
        Assert.Equal(processing, snapshot.VideoProcessing);
        Assert.DoesNotContain(arguments, value => value.Contains("bwdif", StringComparison.Ordinal));
        var rendition = Assert.Single(CmafStreamPlanner.CreateVideoRenditions(
            source.Tracks[0], request, false, profile.Deinterlace, profile.AlwaysTranscodeVideo));
        Assert.False(rendition.CopySource);
    }

    /// <summary>Verifies QSV schedules closed GOPs from the actual progressive output rate without forced timestamps.</summary>
    [Theory]
    [InlineData(25, 1, VideoScanType.Progressive, SourceDeinterlacePolicy.Preserve, 50, false)]
    [InlineData(30000, 1001, VideoScanType.Progressive, SourceDeinterlacePolicy.Preserve, 60, false)]
    [InlineData(60000, 1001, VideoScanType.Progressive, SourceDeinterlacePolicy.SourceFieldRate, 120, false)]
    [InlineData(30000, 1001, VideoScanType.Interlaced, SourceDeinterlacePolicy.SourceFrameRate, 60, false)]
    [InlineData(30000, 1001, VideoScanType.Interlaced, SourceDeinterlacePolicy.SourceFieldRate, 120, false)]
    [InlineData(30000, 1001, VideoScanType.Interlaced, SourceDeinterlacePolicy.Preserve, 120, true)]
    public void Cmaf_QsvCadence_UsesEffectiveFrameRate(int numerator, int denominator, VideoScanType scan, SourceDeinterlacePolicy mode, int gop, bool automatic)
    {
        // Arrange
        var source = new MediaProbeResult([new MediaTrackMetadata(0, MediaTrackType.Video, "mpeg2video", null, 720, 480, null, null)
            { ScanType = scan, FrameRate = new(numerator, denominator) }], null);
        var profile = new VirtualTranscodeProfile { Deinterlace = mode, HardwareEncoding = new() { Encoder = HardwareVideoEncoder.Qsv } };
        var request = new CmafStreamRequest { PreferredVideo = CmafPreferredVideo.Fallback };
        var selection = CmafStreamPlanner.SelectTracks(source, request);

        // Act
        var arguments = CmafStreamPlanner.CreateArguments(profile, source, selection, request, "manifest.mpd").ToList();
        var processing = CmafStreamPlanner.CreateVideoProcessing(profile, source, selection, request);
        var snapshot = ActiveStreamPlanFactory.CreateCmaf("test", "2.3", DateTime.UnixEpoch, source, selection, request, profile);

        // Assert
        Assert.Equal(gop.ToString(), arguments[arguments.IndexOf("-g:v:0") + 1]);
        Assert.Equal(gop.ToString(), arguments[arguments.IndexOf("-keyint_min:v:0") + 1]);
        Assert.Contains("+cgop", arguments);
        Assert.DoesNotContain(arguments, value => value.StartsWith("-force_key_frames", StringComparison.Ordinal));
        Assert.Equal(automatic, processing!.AutomaticDeinterlace);
        Assert.False(processing.UnknownGopRate);
        Assert.Equal(processing, snapshot.VideoProcessing);
        Assert.Equal(mode, profile.Deinterlace);
        if (scan == VideoScanType.Interlaced)
        {
            var expectedMode = mode == SourceDeinterlacePolicy.SourceFrameRate ? "send_frame" : "send_field";
            Assert.Contains(arguments, value => value.Contains($"bwdif=mode={expectedMode}", StringComparison.Ordinal));
            Assert.Contains("-copyts", arguments);
            Assert.Contains("-start_at_zero", arguments);
            Assert.Contains("-fps_mode:v:0", arguments);
        }
        else
        {
            Assert.DoesNotContain(arguments, value => value.Contains("bwdif", StringComparison.Ordinal));
        }
    }

    /// <summary>Verifies unavailable or unrepresentable frame rates retain a finite, explicitly unknown GOP policy.</summary>
    [Theory]
    [InlineData(null, null)]
    [InlineData(30, 0)]
    [InlineData(-30, 1)]
    [InlineData(int.MaxValue, 1)]
    public void Cmaf_QsvUnknownRate_RetainsBoundedGop(int? numerator, int? denominator)
    {
        // Arrange
        var source = new MediaProbeResult([new MediaTrackMetadata(0, MediaTrackType.Video, "mpeg2video", null, 720, 480, null, null)
            { FrameRate = numerator.HasValue ? new(numerator.Value, denominator!.Value) : null }], null);
        var profile = new VirtualTranscodeProfile { HardwareEncoding = new() { Encoder = HardwareVideoEncoder.Qsv } };
        var request = new CmafStreamRequest { PreferredVideo = CmafPreferredVideo.Fallback };
        var selection = CmafStreamPlanner.SelectTracks(source, request);

        // Act
        var arguments = CmafStreamPlanner.CreateArguments(profile, source, selection, request, "manifest.mpd").ToList();
        var processing = CmafStreamPlanner.CreateVideoProcessing(profile, source, selection, request);

        // Assert
        Assert.Equal("120", arguments[arguments.IndexOf("-g:v:0") + 1]);
        Assert.Equal("120", arguments[arguments.IndexOf("-keyint_min:v:0") + 1]);
        Assert.True(processing!.UnknownGopRate);
        Assert.DoesNotContain(arguments, value => value.StartsWith("-force_key_frames", StringComparison.Ordinal));
    }

    /// <summary>Verifies the automatic QSV exception never turns a copy plan into a video transcode.</summary>
    [Theory]
    [InlineData(VideoScanType.Interlaced, HardwareVideoEncoder.Qsv)]
    [InlineData(VideoScanType.Progressive, HardwareVideoEncoder.Qsv)]
    [InlineData(VideoScanType.Unknown, HardwareVideoEncoder.Qsv)]
    [InlineData(VideoScanType.Interlaced, HardwareVideoEncoder.Vaapi)]
    [InlineData(VideoScanType.Progressive, HardwareVideoEncoder.Vaapi)]
    [InlineData(VideoScanType.Unknown, HardwareVideoEncoder.Vaapi)]
    public void Cmaf_HardwareSourceCopy_DoesNotEnableEncoding(VideoScanType scan, HardwareVideoEncoder encoder)
    {
        // Arrange
        var source = new MediaProbeResult([new MediaTrackMetadata(0, MediaTrackType.Video, "h264", null, 1920, 1080, null, null)
            { ScanType = scan, FrameRate = new(30000, 1001) }], null);
        var profile = new VirtualTranscodeProfile { HardwareEncoding = new() { Encoder = encoder } };
        var request = new CmafStreamRequest { PreferredVideo = CmafPreferredVideo.Source };
        var selection = CmafStreamPlanner.SelectTracks(source, request);

        // Act
        var arguments = CmafStreamPlanner.CreateArguments(profile, source, selection, request, "manifest.mpd");
        var processing = CmafStreamPlanner.CreateVideoProcessing(profile, source, selection, request);

        // Assert
        Assert.Null(processing);
        Assert.Contains("copy", arguments);
        Assert.DoesNotContain(HardwareVideoEncodingPlanner.GetEncoderName(encoder), arguments);
        Assert.DoesNotContain("-a53cc:v:0", arguments);
        Assert.DoesNotContain("-sei:v:0", arguments);
        Assert.DoesNotContain(arguments, value => value.Contains("bwdif", StringComparison.Ordinal));
    }

    /// <summary>Verifies cadence changes affect sharing while equivalent effective QSV plans share across viewer IDs.</summary>
    [Fact]
    public void Cmaf_QsvEffectiveCadence_CanonicalizesSharing()
    {
        // Arrange
        var video = new MediaTrackMetadata(0, MediaTrackType.Video, "mpeg2video", null, 720, 480, null, null) { ScanType = VideoScanType.Interlaced, FrameRate = new(30000, 1001) };
        var source = new MediaProbeResult([video], null);
        var profile = new VirtualTranscodeProfile { HardwareEncoding = new() { Encoder = HardwareVideoEncoder.Qsv } };
        var request = new CmafStreamRequest { PreferredVideo = CmafPreferredVideo.Fallback, ClientId = "one" };
        var selection = CmafStreamPlanner.SelectTracks(source, request);

        // Act
        var automatic = CmafStreamPlanner.CreatePlanKey(profile, source, selection, request);
        var explicitField = CmafStreamPlanner.CreatePlanKey(profile with { Deinterlace = SourceDeinterlacePolicy.SourceFieldRate }, source, selection, request with { ClientId = "two" });
        var explicitFrame = CmafStreamPlanner.CreatePlanKey(profile with { Deinterlace = SourceDeinterlacePolicy.SourceFrameRate }, source, selection, request);
        var changedRate = CmafStreamPlanner.CreatePlanKey(profile, source with { Tracks = [video with { FrameRate = new(25, 1) }] }, selection, request);

        // Assert
        Assert.Equal(automatic, explicitField);
        Assert.NotEqual(automatic, explicitFrame);
        Assert.NotEqual(automatic, changedRate);
    }

    /// <summary>Verifies automatic QSV filtering precedes scaling and bitmap overlay without changing audio maps.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cmaf_QsvAutomaticDeinterlace_ComposesScalingAndBurnIn(bool burnIn)
    {
        // Arrange
        var source = new MediaProbeResult(
        [
            new MediaTrackMetadata(0, MediaTrackType.Video, "mpeg2video", null, 720, 480, null, null) { ScanType = VideoScanType.Interlaced, FrameRate = new(30000, 1001) },
            new MediaTrackMetadata(1, MediaTrackType.Audio, "aac", null, null, null, 2, 48000),
            new MediaTrackMetadata(2, MediaTrackType.Subtitle, "dvb_subtitle", null, null, null, null, null) { SubtitlePresentation = SubtitlePresentation.BurnIn }
        ], null);
        var selection = new WebPlayerTrackSelection(source.Tracks[1], burnIn ? source.Tracks[2] : null);
        var profile = new VirtualTranscodeProfile { HardwareEncoding = new() { Encoder = HardwareVideoEncoder.Qsv } };
        var request = new CmafStreamRequest { Quality = WebPlayerQuality.Medium };

        // Act
        var arguments = CmafStreamPlanner.CreateArguments(profile, source, selection, request, "manifest.mpd").ToList();
        var filter = arguments[arguments.IndexOf(burnIn ? "-filter_complex" : "-filter:v:0") + 1];

        // Assert
        Assert.True(filter.IndexOf("bwdif=mode=send_field", StringComparison.Ordinal) < filter.IndexOf("scale=", StringComparison.Ordinal));
        if (burnIn)
        {
            Assert.True(filter.IndexOf("bwdif=", StringComparison.Ordinal) < filter.IndexOf("overlay", StringComparison.Ordinal));
        }
        Assert.Contains("0:1", arguments);
        Assert.Contains("-fps_mode:v:0", arguments);
        Assert.Equal("120", arguments[arguments.IndexOf("-g:v:0") + 1]);
    }

    /// <summary>Verifies other encoders keep Preserve and existing timestamp-based CMAF scheduling.</summary>
    [Theory]
    [InlineData(HardwareVideoEncoder.Software)]
    [InlineData(HardwareVideoEncoder.Nvenc)]
    [InlineData(HardwareVideoEncoder.Vaapi)]
    [InlineData(HardwareVideoEncoder.Amf)]
    [InlineData(HardwareVideoEncoder.VideoToolbox)]
    public void Cmaf_NonQsvTranscode_PreservesExistingCadenceAndFiltering(HardwareVideoEncoder encoder)
    {
        // Arrange
        var source = new MediaProbeResult([new MediaTrackMetadata(0, MediaTrackType.Video, "mpeg2video", null, 720, 480, null, null)
            { ScanType = VideoScanType.Interlaced, FrameRate = new(30000, 1001) }], null);
        var profile = new VirtualTranscodeProfile { HardwareEncoding = new() { Encoder = encoder } };
        var request = new CmafStreamRequest { PreferredVideo = CmafPreferredVideo.Fallback };
        var selection = CmafStreamPlanner.SelectTracks(source, request);

        // Act
        var arguments = CmafStreamPlanner.CreateArguments(profile, source, selection, request, "manifest.mpd").ToList();

        // Assert
        Assert.Contains("expr:if(isnan(prev_forced_t),1,gte(t,prev_forced_t+2))", arguments);
        Assert.Equal("120", arguments[arguments.IndexOf("-g") + 1]);
        Assert.DoesNotContain(arguments, value => value.Contains("bwdif", StringComparison.Ordinal));
        Assert.False(CmafStreamPlanner.CreateVideoProcessing(profile, source, selection, request)!.AutomaticDeinterlace);
    }

    /// <summary>Verifies all supported backends replace x264 only in actual physical and virtual video transforms.</summary>
    [Theory]
    [InlineData(HardwareVideoEncoder.Qsv, "h264_qsv")]
    [InlineData(HardwareVideoEncoder.Nvenc, "h264_nvenc")]
    [InlineData(HardwareVideoEncoder.Vaapi, "h264_vaapi")]
    [InlineData(HardwareVideoEncoder.Amf, "h264_amf")]
    [InlineData(HardwareVideoEncoder.VideoToolbox, "h264_videotoolbox")]
    public void Planners_VideoConversion_UsesBackendWithoutSoftwareOnlyOptions(HardwareVideoEncoder encoder, string expected)
    {
        // Arrange
        var hardware = new HardwareEncodingSettings { Encoder = encoder };
        var source = new MediaProbeResult([new MediaTrackMetadata(0, MediaTrackType.Video, "hevc", null, 1920, 1080, null, null)], null);
        var physical = new PhysicalTranscodeProfile { ConvertHevcToH264 = true, HardwareEncoding = hardware };
        var profile = new VirtualTranscodeProfile { ConvertHevcToH264 = true, HardwareEncoding = hardware };

        // Act
        var physicalArguments = SourceDeinterlacePlanner.CreateArguments(source, physical);
        var virtualArguments = MpegTsTranscodePlanner.CreateFfmpegArguments(new Uri("http://device/source"), "hevc", [], profile);

        // Assert
        foreach (var arguments in new[] { physicalArguments, virtualArguments })
        {
            Assert.Contains(expected, arguments);
            Assert.DoesNotContain("libx264", arguments);
            Assert.DoesNotContain(arguments, value => value.StartsWith("-crf", StringComparison.Ordinal) || value.StartsWith("-x264-params", StringComparison.Ordinal));
            Assert.Contains("-b:v:0", arguments);
        }
        if (encoder == HardwareVideoEncoder.Vaapi)
        {
            Assert.Contains("format=nv12,hwupload", physicalArguments);
            Assert.True(physicalArguments.ToList().IndexOf("-init_hw_device") < physicalArguments.ToList().IndexOf("-i"));
        }
    }

    /// <summary>Verifies selecting a GPU never changes source-copy or audio-only policy.</summary>
    [Fact]
    public void Planners_CopyAndAudioOnly_DoNotInitializeGpu()
    {
        // Arrange
        var profile = new VirtualTranscodeProfile { HardwareEncoding = new() { Encoder = HardwareVideoEncoder.Vaapi }, Audio = OutputAudioPolicy.Ac3 };

        // Act
        var arguments = MpegTsTranscodePlanner.CreateFfmpegArguments(new Uri("http://device/source"), "h264", [new AudioStreamInfo(1, "aac", 2)], profile);

        // Assert
        Assert.DoesNotContain("-init_hw_device", arguments);
        Assert.DoesNotContain("h264_vaapi", arguments);
        Assert.Contains("ac3", arguments);
    }

    /// <summary>Verifies CMAF encoder/device changes affect the actual media plan and producer sharing key.</summary>
    [Fact]
    public void Cmaf_HardwareSelection_ChangesEncoderAndSharingKeyWithoutLosingKeyframes()
    {
        // Arrange
        var source = new MediaProbeResult([new MediaTrackMetadata(0, MediaTrackType.Video, "h264", null, 1920, 1080, null, null)], null);
        var request = new CmafStreamRequest { PreferredVideo = CmafPreferredVideo.Fallback };
        var selection = CmafStreamPlanner.SelectTracks(source, request);
        var software = new VirtualTranscodeProfile();
        var hardware = software with { HardwareEncoding = new() { Encoder = HardwareVideoEncoder.Nvenc, Device = "1" } };

        // Act
        var arguments = CmafStreamPlanner.CreateArguments(hardware, source, selection, request, "manifest.mpd");
        var softwareKey = CmafStreamPlanner.CreatePlanKey(software, source, selection, request);
        var hardwareKey = CmafStreamPlanner.CreatePlanKey(hardware, source, selection, request);

        // Assert
        Assert.Contains("h264_nvenc", arguments);
        Assert.Contains("expr:if(isnan(prev_forced_t),1,gte(t,prev_forced_t+2))", arguments);
        Assert.NotEqual(softwareKey, hardwareKey);
        Assert.NotEqual(hardwareKey, CmafStreamPlanner.CreatePlanKey(hardware with { HardwareEncoding = hardware.HardwareEncoding with { Device = "0" } }, source, selection, request));
    }

    /// <summary>Verifies advanced values cannot override Lineup-owned inputs, outputs, maps or formats.</summary>
    [Theory]
    [InlineData("i=http://untrusted")]
    [InlineData("map=0")]
    [InlineData("f=mp4")]
    [InlineData("preset=p4\npreset=p5")]
    [InlineData("preset=p4;command")]
    public void Validation_ManagedOrMalformedArguments_ReturnsErrors(string arguments)
    {
        // Arrange
        var settings = new HardwareEncodingSettings { Encoder = HardwareVideoEncoder.Nvenc, AdvancedArguments = arguments };

        // Act
        var errors = HardwareEncodingValidation.Validate(settings);

        // Assert
        Assert.NotEmpty(errors);
    }

    /// <summary>Verifies render nodes cannot smuggle initialization options or arbitrary paths through structured settings.</summary>
    [Theory]
    [InlineData("/dev/dri/renderD")]
    [InlineData("/dev/dri/renderD128,driver=other")]
    [InlineData("/dev/dri/renderD128/../../other")]
    [InlineData("/dev/dri/card0")]
    public void Validation_MalformedRenderNode_ReturnsErrors(string device)
    {
        // Arrange
        var settings = new HardwareEncodingSettings { Encoder = HardwareVideoEncoder.Vaapi, Device = device };

        // Act
        var errors = HardwareEncodingValidation.Validate(settings);

        // Assert
        Assert.NotEmpty(errors);
    }

    /// <summary>Verifies software retry classification excludes unrelated media/network failures.</summary>
    [Theory]
    [InlineData("OpenEncodeSessionEx failed: unsupported device", true)]
    [InlineData("Error while opening encoder h264_qsv", true)]
    [InlineData("Connection refused", false)]
    [InlineData("codec ac3 not currently supported in container", false)]
    [InlineData("Error writing trailer: Broken pipe", false)]
    [InlineData(null, false)]
    public void IsInitializationFailure_Diagnostics_RestrictsSoftwareRetry(string? diagnostics, bool expected)
    {
        // Arrange
        var message = diagnostics;

        // Act
        var actual = HardwareEncodingRuntime.IsInitializationFailure(message);

        // Assert
        Assert.Equal(expected, actual);
    }

    /// <summary>Verifies custom tuning overrides backend defaults without losing stream-scoped safety.</summary>
    [Fact]
    public void Apply_AdvancedEncoderOption_ReplacesDefaultAndPreservesManagedOutput()
    {
        // Arrange
        var settings = new HardwareEncodingSettings { Encoder = HardwareVideoEncoder.Nvenc, Device = "1", AdvancedArguments = "preset=p6\ncq=22" };

        // Act
        var arguments = HardwareVideoEncodingPlanner.CreateProbeArguments(settings).ToList();

        // Assert
        Assert.Equal("p6", arguments[arguments.IndexOf("-preset:v:0") + 1]);
        Assert.Equal("1", arguments[arguments.IndexOf("-gpu:v:0") + 1]);
        Assert.Equal("-", arguments[^1]);
        Assert.Equal(1, arguments.Count(value => value == "-preset:v:0"));
    }

    /// <summary>Verifies VA-API avoids assuming Intel preset numbers apply to every Mesa or vendor driver.</summary>
    [Fact]
    public void Apply_VaapiTuning_UsesDriverDefaultUnlessExplicitlyOverridden()
    {
        // Arrange
        var defaults = new HardwareEncodingSettings { Encoder = HardwareVideoEncoder.Vaapi };

        // Act
        var arguments = HardwareVideoEncodingPlanner.CreateProbeArguments(defaults);
        var advanced = HardwareVideoEncodingPlanner.CreateProbeArguments(defaults with { AdvancedArguments = "compression_level=1" }).ToList();
        var errors = HardwareEncodingValidation.Validate(defaults with { Tuning = HardwareEncoderTuning.Speed });

        // Assert
        Assert.DoesNotContain("-compression_level:v:0", arguments);
        Assert.Equal("1", advanced[advanced.IndexOf("-compression_level:v:0") + 1]);
        Assert.NotEmpty(errors);
    }

    /// <summary>Verifies hardware upload follows existing software complex-filter output.</summary>
    [Fact]
    public void Apply_VaapiBurnIn_AppendsUploadAfterOverlay()
    {
        // Arrange
        string[] software = ["-i", "pipe:0", "-filter_complex", "[0:v][0:2]overlay[v]", "-map", "[v]", "-c:v:0", "libx264", "-f", "dash", "manifest.mpd"];

        // Act
        var arguments = HardwareVideoEncodingPlanner.Apply(software, new() { Encoder = HardwareVideoEncoder.Vaapi }).ToList();

        // Assert
        Assert.Equal("[0:v][0:2]overlay[lineup_cpu];[lineup_cpu]format=nv12,hwupload[v]", arguments[arguments.IndexOf("-filter_complex") + 1]);
        Assert.Equal("manifest.mpd", arguments[^1]);
    }

    /// <summary>Verifies Intel caption repacking follows deinterlacing and precedes upload in both MPEG-TS stages.</summary>
    [Theory]
    [InlineData(HardwareVideoEncoder.Qsv, VideoScanType.Progressive, SourceDeinterlacePolicy.Preserve, 60000, 1001)]
    [InlineData(HardwareVideoEncoder.Vaapi, VideoScanType.Progressive, SourceDeinterlacePolicy.Preserve, 60000, 1001)]
    [InlineData(HardwareVideoEncoder.Qsv, VideoScanType.Interlaced, SourceDeinterlacePolicy.SourceFieldRate, 30000, 1001)]
    [InlineData(HardwareVideoEncoder.Vaapi, VideoScanType.Interlaced, SourceDeinterlacePolicy.SourceFieldRate, 30000, 1001)]
    [InlineData(HardwareVideoEncoder.Qsv, VideoScanType.Interlaced, SourceDeinterlacePolicy.SourceFrameRate, 30000, 1001)]
    [InlineData(HardwareVideoEncoder.Vaapi, VideoScanType.Interlaced, SourceDeinterlacePolicy.SourceFrameRate, 30000, 1001)]
    public void MpegTs_IntelCaptions_RepackBeforeEncoding(HardwareVideoEncoder encoder, VideoScanType scan, SourceDeinterlacePolicy deinterlace, int numerator, int denominator)
    {
        // Arrange
        var video = new MediaTrackMetadata(0, MediaTrackType.Video, "mpeg2video", null, 1280, 720, null, null)
        {
            ScanType = scan,
            FrameRate = new(numerator, denominator),
            HasClosedCaptions = true
        };
        var caption = new MediaTrackMetadata(2, MediaTrackType.Subtitle, "eia_608", null, null, null, null, null) { IsEmbeddedClosedCaptions = true };
        var source = new MediaProbeResult([video, caption], null);
        var hardware = new HardwareEncodingSettings { Encoder = encoder };
        var physical = new PhysicalTranscodeProfile { AlwaysTranscodeVideo = true, HardwareEncoding = hardware, Deinterlace = deinterlace };
        var output = new VirtualTranscodeProfile { AlwaysTranscodeVideo = true, HardwareEncoding = hardware, Deinterlace = deinterlace };
        var expectedFilter = scan == VideoScanType.Interlaced ? $"{SourceDeinterlacePlanner.CreateFilter(deinterlace)},ccrepack" : "ccrepack";
        expectedFilter += encoder == HardwareVideoEncoder.Vaapi ? ",format=nv12,hwupload" : "";

        // Act
        var physicalArguments = SourceDeinterlacePlanner.CreateArguments(source, physical).ToList();
        var virtualArguments = MpegTsTranscodePlanner.CreateFfmpegArguments(
            new Uri("http://device/source"), video.Codec, [], output, [caption], scan == VideoScanType.Interlaced, video.FrameRate).ToList();

        // Assert
        foreach (var arguments in new[] { physicalArguments, virtualArguments })
        {
            Assert.Contains(HardwareVideoEncodingPlanner.GetEncoderName(encoder), arguments);
            Assert.Equal(expectedFilter, arguments[arguments.IndexOf("-filter:v:0") + 1]);
            Assert.Equal(1, arguments.Count(argument => argument == "-filter:v:0"));
            Assert.DoesNotContain("-filter:v", arguments);
            Assert.DoesNotContain("0:2", arguments);
            var captionOption = encoder == HardwareVideoEncoder.Qsv ? "-a53cc:v:0" : "-sei:v:0";
            Assert.Equal(encoder == HardwareVideoEncoder.Qsv ? "1" : "+a53_cc", arguments[arguments.IndexOf(captionOption) + 1]);
            Assert.True(arguments.IndexOf(captionOption) > arguments.IndexOf("-i"));
            Assert.True(arguments.IndexOf(captionOption) < arguments.IndexOf("pipe:1"));
            if (encoder == HardwareVideoEncoder.Qsv)
            {
                Assert.Equal("1", arguments[arguments.IndexOf("-async_depth:v:0") + 1]);
            }
            else
            {
                Assert.DoesNotContain("-async_depth:v:0", arguments);
            }
        }
    }

    /// <summary>Verifies caption preservation prevents unsafe concurrent QSV payload submission without changing other QSV streams.</summary>
    [Theory]
    [InlineData(true, "1")]
    [InlineData(true, "4")]
    [InlineData(true, "0")]
    [InlineData(false, "4")]
    public void Apply_QsvCaptionSubmission_RequiresSingleFrameDepth(bool captions, string depth)
    {
        // Arrange
        string[] software = ["-i", "pipe:0", "-c:v:0", "libx264", "-f", "mpegts", "pipe:1"];
        var hardware = new HardwareEncodingSettings { Encoder = HardwareVideoEncoder.Qsv, AdvancedArguments = $"async_depth={depth}" };
        var rejected = captions && depth != "1";
        List<string>? arguments = null;

        // Act
        var error = Record.Exception(() => arguments = HardwareVideoEncodingPlanner.Apply(
            software, hardware, requiresClosedCaptions: captions, closedCaptionFrameRate: new(60000, 1001)).ToList());

        // Assert
        if (rejected)
        {
            var failure = Assert.IsType<NotSupportedException>(error);
            Assert.Contains("async_depth=1", failure.Message);
        }
        else
        {
            Assert.Null(error);
            Assert.NotNull(arguments);
            Assert.Equal(1, arguments.Count(argument => argument == "-async_depth:v:0"));
            Assert.Equal(depth, arguments[arguments.IndexOf("-async_depth:v:0") + 1]);
            Assert.Equal(captions, arguments.Contains("-a53cc:v:0"));
        }
    }

    /// <summary>Verifies field doubling cannot silently pass an unsupported caption rate through ccrepack.</summary>
    [Theory]
    [InlineData(HardwareVideoEncoder.Qsv)]
    [InlineData(HardwareVideoEncoder.Vaapi)]
    public void MpegTs_UnsupportedCaptionFieldRate_RejectsEncoding(HardwareVideoEncoder encoder)
    {
        // Arrange
        var caption = new MediaTrackMetadata(2, MediaTrackType.Subtitle, "eia_608", null, null, null, null, null) { IsEmbeddedClosedCaptions = true };
        var profile = new VirtualTranscodeProfile
        {
            AlwaysTranscodeVideo = true,
            Deinterlace = SourceDeinterlacePolicy.SourceFieldRate,
            HardwareEncoding = new() { Encoder = encoder }
        };

        // Act
        var error = Assert.Throws<NotSupportedException>(() => MpegTsTranscodePlanner.CreateFfmpegArguments(
            new Uri("http://device/source"), "mpeg2video", [], profile, [caption], sourceVideoIsInterlaced: true, sourceVideoFrameRate: new(60000, 1001)));

        // Assert
        Assert.Contains("supported output frame rate", error.Message);
    }

    /// <summary>Verifies copy-only streams do not require a caption normalization rate or initialize a GPU.</summary>
    [Theory]
    [InlineData(HardwareVideoEncoder.Qsv)]
    [InlineData(HardwareVideoEncoder.Vaapi)]
    public void Apply_CopyWithCaptions_PreservesArguments(HardwareVideoEncoder encoder)
    {
        // Arrange
        string[] software = ["-i", "pipe:0", "-c", "copy", "-f", "mpegts", "pipe:1"];

        // Act
        var arguments = HardwareVideoEncodingPlanner.Apply(software, new() { Encoder = encoder }, requiresClosedCaptions: true);

        // Assert
        Assert.Same(software, arguments);
    }

    /// <summary>Verifies unsupported or unknown caption rates are rejected instead of reaching unsafe Intel SEI handling.</summary>
    [Theory]
    [InlineData(HardwareVideoEncoder.Qsv, null, null)]
    [InlineData(HardwareVideoEncoder.Vaapi, null, null)]
    [InlineData(HardwareVideoEncoder.Qsv, 25, 1)]
    [InlineData(HardwareVideoEncoder.Qsv, 15, 1)]
    [InlineData(HardwareVideoEncoder.Vaapi, 50, 1)]
    [InlineData(HardwareVideoEncoder.Qsv, 30, 0)]
    [InlineData(HardwareVideoEncoder.Vaapi, -30, 1)]
    public void Apply_UnsupportedClosedCaptionRate_RejectsEncoding(HardwareVideoEncoder encoder, int? numerator, int? denominator)
    {
        // Arrange
        string[] software = ["-i", "pipe:0", "-c:v:0", "libx264", "-f", "mpegts", "pipe:1"];
        var rate = numerator.HasValue ? new MediaFrameRate(numerator.Value, denominator!.Value) : null;

        // Act
        var error = Assert.Throws<NotSupportedException>(() => HardwareVideoEncodingPlanner.Apply(
            software, new() { Encoder = encoder }, requiresClosedCaptions: true, closedCaptionFrameRate: rate));

        // Assert
        Assert.Contains("supported output frame rate", error.Message);
    }

    /// <summary>Verifies unverified caption backends remain rejected instead of silently losing captions.</summary>
    [Theory]
    [InlineData(HardwareVideoEncoder.Amf)]
    [InlineData(HardwareVideoEncoder.VideoToolbox)]
    public void Apply_UnverifiedClosedCaptionBackend_RejectsEncoding(HardwareVideoEncoder encoder)
    {
        // Arrange
        string[] software = ["-i", "pipe:0", "-c:v:0", "libx264", "-f", "mpegts", "pipe:1"];

        // Act
        var error = Assert.Throws<NotSupportedException>(() => HardwareVideoEncodingPlanner.Apply(software, new() { Encoder = encoder }, requiresClosedCaptions: true));

        // Assert
        Assert.Contains("Closed-caption", error.Message);
    }

    /// <summary>Verifies unavailable encoder detection fails by default and selects software only with explicit permission.</summary>
    /// <param name="fallback">Whether software fallback is explicitly allowed.</param>
    /// <param name="exitBeforeReturn">Whether the probe has already exited when the factory returns it.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task VerifyAsync_UnavailableEncoder_RequiresExplicitFallback(bool fallback, bool exitBeforeReturn)
    {
        // Arrange
        var factory = new MissingEncoderFactory(exitBeforeReturn);
        var settings = new HardwareEncodingSettings { Encoder = HardwareVideoEncoder.Nvenc, AllowSoftwareFallback = fallback };

        // Act
        if (fallback)
        {
            var hardware = await HardwareEncodingRuntime.VerifyAsync(settings, factory, NullLogger.Instance, TestContext.Current.CancellationToken);

            // Assert
            Assert.False(hardware);
        }
        else
        {
            var error = await Assert.ThrowsAsync<HardwareEncodingException>(() => HardwareEncodingRuntime.VerifyAsync(settings, factory, NullLogger.Instance, TestContext.Current.CancellationToken));

            // Assert
            Assert.Contains("encoder", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        Assert.All(factory.ProcessExits, exit => Assert.True(exit.IsCompletedSuccessfully));
    }

    /// <summary>Verifies cancellation cannot initialize a GPU or become permission for software fallback.</summary>
    [Fact]
    public async Task VerifyAsync_AlreadyCanceled_DoesNotStartMediaOrFallback()
    {
        // Arrange
        var factory = new MissingEncoderFactory();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var settings = new HardwareEncodingSettings { Encoder = HardwareVideoEncoder.Nvenc, AllowSoftwareFallback = true };

        // Act
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HardwareEncodingRuntime.VerifyAsync(settings, factory, NullLogger.Instance, cancellation.Token));

        // Assert
        Assert.Empty(factory.ProcessExits);
    }

    /// <summary>Verifies an audio-only physical transform reports copied video without probing or falsely reporting GPU fallback.</summary>
    [Fact]
    public async Task StartAsync_PhysicalAudioConversion_ReportsCopiedVideoWithoutGpuUse()
    {
        // Arrange
        var hardware = new HardwareEncodingSettings { Encoder = HardwareVideoEncoder.Nvenc };
        var source = new MediaProbeResult(
        [
            new MediaTrackMetadata(0, MediaTrackType.Video, "h264", null, 1920, 1080, null, null),
            new MediaTrackMetadata(1, MediaTrackType.Audio, "ac4", null, null, null, 6, 48_000)
        ], null);
        var profile = new PhysicalTranscodeProfile { Ac4Audio = OutputAudioPolicy.Ac3, HardwareEncoding = hardware };
        var arguments = SourceDeinterlacePlanner.CreateArguments(source, profile);
        var factory = new ScriptedEncoderFactory(hardware.Encoder);
        var transform = new FfmpegStreamTransform(arguments, factory, NullLogger.Instance, "audio-only", hardware);
        using var input = new MemoryStream();

        // Act
        await using (var lease = await transform.StartAsync(input, TestContext.Current.CancellationToken))
        {
            // Assert
            Assert.Equal(new VideoEncodingSnapshot("copy", HardwareVideoEncoder.Nvenc, false), lease.VideoEncoding);
            _ = Assert.Single(factory.ProcessExits);
        }
        Assert.All(factory.ProcessExits, exit => Assert.True(exit.IsCompletedSuccessfully));
    }

    /// <summary>Verifies hardware readiness retains the first transport bytes instead of dropping the prefix before publication.</summary>
    [Fact]
    public async Task StartAsync_HardwareReadiness_RetainsOutputPrefixAndReleasesProbes()
    {
        // Arrange
        var encoder = OperatingSystem.IsMacOS() ? HardwareVideoEncoder.VideoToolbox : HardwareVideoEncoder.Nvenc;
        var hardware = new HardwareEncodingSettings { Encoder = encoder };
        var factory = new ScriptedEncoderFactory(encoder);
        var arguments = HardwareVideoEncodingPlanner.Apply(["-i", "pipe:0", "-c:v:0", "libx264", "-f", "mpegts", "pipe:1"], hardware);
        var transform = new FfmpegStreamTransform(arguments, factory, NullLogger.Instance, "hardware-test", hardware);
        using var input = new MemoryStream();

        // Act
        await using (var lease = await transform.StartAsync(input, TestContext.Current.CancellationToken))
        {
            using var reader = new StreamReader(lease.Stream, leaveOpen: true);
            var output = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal("transport-prefix", output);
            Assert.Equal(3, factory.ProcessExits.Count);
            Assert.Equal(new VideoEncodingSnapshot(HardwareVideoEncodingPlanner.GetEncoderName(encoder), encoder, false), lease.VideoEncoding);
            Assert.True(input.CanRead);
        }
        Assert.All(factory.ProcessExits, exit => Assert.True(exit.IsCompletedSuccessfully));
    }

    /// <summary>Verifies a real startup error cannot trigger an unapproved retry or overlap the preceding process.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartAsync_HardwareInitializationFails_RetriesOnlyWithPermissionAfterCleanup(bool fallback)
    {
        // Arrange
        var encoder = OperatingSystem.IsMacOS() ? HardwareVideoEncoder.VideoToolbox : HardwareVideoEncoder.Nvenc;
        var hardware = new HardwareEncodingSettings { Encoder = encoder, AllowSoftwareFallback = fallback };
        var factory = new ScriptedEncoderFactory(encoder, failPlayback: true);
        string[] software = ["-i", "pipe:0", "-c:v:0", "libx264", "-f", "mpegts", "pipe:1"];
        var arguments = HardwareVideoEncodingPlanner.Apply(software, hardware);
        var transform = new FfmpegStreamTransform(arguments, factory, NullLogger.Instance, "hardware-test", hardware, software);
        using var input = new MemoryStream();

        // Act
        if (fallback)
        {
            await using var lease = await transform.StartAsync(input, TestContext.Current.CancellationToken);
            using var reader = new StreamReader(lease.Stream, leaveOpen: true);
            var output = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal("transport-prefix", output);
            Assert.True(factory.HardwareExitedBeforeSoftware);
            Assert.Equal(4, factory.ProcessExits.Count);
            Assert.Equal(new VideoEncodingSnapshot("libx264", encoder, true), lease.VideoEncoding);
        }
        else
        {
            var error = await Assert.ThrowsAsync<MediaProcessException>(() => transform.StartAsync(input, TestContext.Current.CancellationToken).AsTask());

            // Assert
            Assert.True(HardwareEncodingRuntime.IsInitializationFailure(error.Diagnostics));
            Assert.Equal(3, factory.ProcessExits.Count);
        }
        Assert.All(factory.ProcessExits, exit => Assert.True(exit.IsCompletedSuccessfully));
        Assert.True(input.CanRead);
    }

    private static Task ObserveExit(Process process)
    {
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Exited += (_, _) => exited.TrySetResult();
        process.EnableRaisingEvents = true;
        if (process.HasExited)
        {
            exited.TrySetResult();
        }
        return exited.Task;
    }

    private sealed class ScriptedEncoderFactory(HardwareVideoEncoder encoder, bool failPlayback = false) : IMediaProcessFactory
    {
        /// <summary>Gets exit signals retained independently of the probes' and transform's process wrappers.</summary>
        public List<Task> ProcessExits { get; } = [];

        /// <summary>Gets whether the failed actual encoder was already terminated when software was launched.</summary>
        public bool HardwareExitedBeforeSoftware { get; private set; }

        /// <inheritdoc />
        public Process Start(MediaProcessRequest request)
        {
            var configuration = new DirectoryInfo(AppContext.BaseDirectory).Name;
            var executable = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "Lineup.TestSupport.FakeFFmpeg", configuration, "Lineup.TestSupport.FakeFFmpeg.dll"));
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add(executable);
            if (failPlayback && !request.Arguments.Contains("libx264"))
            {
                start.Environment["LINEUP_FAKE_FFMPEG_HARDWARE_MODE"] = "probe-passes-playback-fails";
                foreach (var argument in request.Arguments)
                {
                    start.ArgumentList.Add(argument);
                }
            }
            else
            {
                HardwareExitedBeforeSoftware = failPlayback && ProcessExits[^1].IsCompletedSuccessfully;
                start.ArgumentList.Add("probe-json");
                start.ArgumentList.Add(request.Arguments.Contains("-encoders") ? $"V {HardwareVideoEncodingPlanner.GetEncoderName(encoder)}" :
                    request.Arguments.Contains("testsrc2=size=128x128:rate=25") ? string.Empty : "transport-prefix");
            }
            var process = OwnedMediaProcess.Start(start);
            ProcessExits.Add(ObserveExit(process));
            return process;
        }
    }

    private sealed class MissingEncoderFactory(bool exitBeforeReturn = false) : IMediaProcessFactory
    {
        /// <summary>Gets exit signals retained after runtime verification disposes each original process wrapper.</summary>
        public List<Task> ProcessExits { get; } = [];

        /// <inheritdoc />
        public Process Start(MediaProcessRequest request)
        {
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("--list-runtimes");
            var process = Process.Start(start) ?? throw new InvalidOperationException("The owned test process could not start.");
            if (exitBeforeReturn && !process.WaitForExit(10_000))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                process.Dispose();
                throw new TimeoutException("The encoder-listing test process did not exit.");
            }
            ProcessExits.Add(ObserveExit(process));
            return process;
        }
    }
}
