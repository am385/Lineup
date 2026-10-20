using Lineup.Core.Settings;
using Lineup.Common.Streaming;
using Lineup.Extensibility.Streaming;
using Lineup.Streaming.FFmpeg;

namespace Lineup.WebPlayer.FFmpeg;

/// <summary>
/// Builds active stream snapshots from probed source metadata and configured output plans.
/// </summary>
public static class ActiveStreamPlanFactory
{
    /// <summary>
    /// Creates a transparent MPEG-TS active stream snapshot.
    /// </summary>
    public static ActiveStreamSnapshot CreateMpegTs(string sessionId, string channel, DateTime startedAtUtc, MediaProbeResult source, LineupSettings settings)
        => CreateMpegTs(sessionId, channel, startedAtUtc, source, settings.Streaming.VirtualDefaults);

    /// <summary>Creates a transparent MPEG-TS snapshot from the effective topology profile.</summary>
    public static ActiveStreamSnapshot CreateMpegTs(
        string sessionId,
        string channel,
        DateTime startedAtUtc,
        MediaProbeResult source,
        VirtualTranscodeProfile profile)
        => CreateMpegTs(sessionId, channel, startedAtUtc, source, source, profile);

    /// <summary>Creates a transparent MPEG-TS snapshot from raw and effective source media.</summary>
    public static ActiveStreamSnapshot CreateMpegTs(
        string sessionId,
        string channel,
        DateTime startedAtUtc,
        MediaProbeResult rawSource,
        MediaProbeResult source,
        VirtualTranscodeProfile profile)
        => CreateMpegTsCore(
            sessionId,
            channel,
            startedAtUtc,
            rawSource,
            source,
            track => profile.ConvertHevcToH264 && string.Equals(track.Codec, "hevc", StringComparison.OrdinalIgnoreCase)
                || SourceDeinterlacePlanner.ShouldDeinterlace(track, profile.Deinterlace),
            profile.MaximumVideoBitRateMbps,
            track =>
            {
                var policy = string.Equals(track.Codec, "ac4", StringComparison.OrdinalIgnoreCase) ? profile.Ac4Audio : profile.Audio;
                return policy switch
                {
                    OutputAudioPolicy.Ac3 => "ac3",
                    OutputAudioPolicy.Eac3 => "eac3",
                    _ => "copy"
                };
            });

    private static ActiveStreamSnapshot CreateMpegTsCore(
        string sessionId,
        string channel,
        DateTime startedAtUtc,
        MediaProbeResult rawSource,
        MediaProbeResult source,
        Func<MediaTrackMetadata, bool> transcodeVideo,
        int maximumVideoBitRateMbps,
        Func<MediaTrackMetadata, string> resolveAudioCodec)
    {
        var tracks = source.Tracks.Select(track =>
        {
            if (track.Type == MediaTrackType.Video)
            {
                var shouldTranscode = transcodeVideo(track);
                return CreateTrack(track, shouldTranscode ? "h264" : "copy", shouldTranscode ? maximumVideoBitRateMbps * 1_000_000L : track.BitRate, track.Channels, track.SampleRate);
            }

            if (track.Type == MediaTrackType.Subtitle)
            {
                var compatible = SubtitleCapabilityPolicy.CanCopyToMpegTs(track.Codec);
                return CreateTrack(track, compatible ? "copy" : "not-mapped", track.BitRate, null, null) with
                {
                    IsSelected = compatible
                };
            }

            var outputCodec = resolveAudioCodec(track);
            var outputBitRate = outputCodec switch
            {
                "ac3" => 448_000,
                "eac3" => 640_000,
                _ => track.BitRate
            };
            var outputChannels = outputCodec == "copy"
                ? track.Channels
                : track.Channels.HasValue
                    ? Math.Min(track.Channels.Value, 6)
                    : null;
            return CreateTrack(track, outputCodec, outputBitRate, outputChannels, track.SampleRate);
        }).ToArray();

        return CreateSnapshot(sessionId, channel, HostedStreamFormat.MpegTs, startedAtUtc, rawSource, source, tracks);
    }

    /// <summary>
    /// Creates a CMAF active stream snapshot.
    /// </summary>
    public static ActiveStreamSnapshot CreateCmaf(string sessionId, string channel, DateTime startedAtUtc, MediaProbeResult source, long outputVideoBitRate = 10_000_000)
    {
        return CreateFixedTranscode(sessionId, channel, HostedStreamFormat.Cmaf, startedAtUtc, source, outputVideoBitRate, copyVideo: false);
    }

    /// <summary>
    /// Creates metadata for the selected video, audio, and subtitle renditions in a shared CMAF presentation.
    /// </summary>
    public static ActiveStreamSnapshot CreateCmaf(
        string sessionId,
        string channel,
        DateTime startedAtUtc,
        MediaProbeResult source,
        WebPlayerTrackSelection selection,
        CmafStreamRequest request,
        LineupSettings settings)
        => CreateCmaf(
            sessionId,
            channel,
            startedAtUtc,
            source,
            selection,
            request,
            settings,
            settings.Streaming.VirtualDefaults);

    /// <summary>Creates CMAF stream metadata from the complete effective topology plan.</summary>
    public static ActiveStreamSnapshot CreateCmaf(
        string sessionId,
        string channel,
        DateTime startedAtUtc,
        MediaProbeResult source,
        WebPlayerTrackSelection selection,
        CmafStreamRequest request,
        LineupSettings encoderSettings,
        VirtualTranscodeProfile profile)
        => CreateCmaf(sessionId, channel, startedAtUtc, source, source, selection, request, profile);

    /// <summary>Creates CMAF stream metadata from raw source media and the complete effective topology plan.</summary>
    public static ActiveStreamSnapshot CreateCmaf(
        string sessionId,
        string channel,
        DateTime startedAtUtc,
        MediaProbeResult rawSource,
        MediaProbeResult source,
        WebPlayerTrackSelection selection,
        CmafStreamRequest request,
        LineupSettings encoderSettings,
        VirtualTranscodeProfile profile)
        => CreateCmaf(sessionId, channel, startedAtUtc, rawSource, source, selection, request, profile);

    /// <summary>Creates CMAF stream metadata from raw source media and a worker's complete effective topology plan.</summary>
    public static ActiveStreamSnapshot CreateCmaf(
        string sessionId,
        string channel,
        DateTime startedAtUtc,
        MediaProbeResult rawSource,
        MediaProbeResult source,
        WebPlayerTrackSelection selection,
        CmafStreamRequest request,
        VirtualTranscodeProfile profile)
    {
        var audioRenditions = CmafStreamPlanner.CreatePresentationAudioRenditions(
            source,
            selection.Audio,
            request.PreferredAudio,
            CmafStreamPlanner.ResolveFallbackAudio(request),
            request.CompatibilityProfile,
            request.Overrides.Enabled ? request.Overrides.FallbackAudio : null,
            request.Overrides.Enabled && request.Overrides.Audio.HasValue);
        var sourceVideo = source.Tracks.FirstOrDefault(track => track.Type == MediaTrackType.Video);
        var videoPlans = CmafStreamPlanner.CreateVideoRenditions(
            sourceVideo,
            request,
            selection.SubtitlePresentation == SubtitlePresentation.BurnIn,
            profile.Deinterlace);
        var primaryVideoPlan = videoPlans[0];
        var outputVideoBitRate = WebVideoTranscodePlanner.GetMaximumBitRate(profile, request.Quality);
        var tracks = source.Tracks
            .Where(track => track.Type != MediaTrackType.Audio)
            .Select(track =>
        {
            var selected = track.Type switch
            {
                MediaTrackType.Video => track == sourceVideo,
                MediaTrackType.Subtitle when track.SubtitlePresentation == SubtitlePresentation.WebVtt => true,
                MediaTrackType.Subtitle => track.Index == selection.Subtitle?.Index,
                _ => false
            };
            var outputCodec = track.Type switch
            {
                MediaTrackType.Video when selected => primaryVideoPlan.CopySource ? "copy" : primaryVideoPlan.Codec,
                MediaTrackType.Subtitle when track.SubtitlePresentation == SubtitlePresentation.WebVtt => "webvtt",
                MediaTrackType.Subtitle when selected => "burn-in",
                _ => "not-mapped"
            };
            var outputBitRate = track.Type switch
            {
                MediaTrackType.Video when selected => primaryVideoPlan.CopySource ? track.BitRate : outputVideoBitRate,
                _ => null
            };
            return CreateTrack(
                track,
                outputCodec,
                outputBitRate,
                null,
                null) with
            {
                IsSelected = selected,
                OutputTitle = track.Type switch
                {
                    MediaTrackType.Video when selected => primaryVideoPlan.Title,
                    _ => null
                },
                SubtitlePresentation = track.Type == MediaTrackType.Subtitle ? track.SubtitlePresentation : null
            };
        }).ToList();

        tracks.AddRange(audioRenditions.Select(rendition =>
            CreateTrack(
                rendition.Source,
                rendition.Plan.CopySource ? "copy" : rendition.Plan.Codec,
                rendition.Plan.BitRate,
                rendition.Plan.Channels,
                rendition.Plan.SampleRate) with
            {
                IsSelected = true,
                OutputTitle = rendition.Plan.Title
            }));

        if (videoPlans.Count > 1 && sourceVideo is not null)
        {
            var secondaryVideoPlan = videoPlans[1];
            tracks.Add(CreateTrack(
                sourceVideo,
                secondaryVideoPlan.CopySource ? "copy" : secondaryVideoPlan.Codec,
                secondaryVideoPlan.CopySource ? sourceVideo.BitRate : outputVideoBitRate,
                null,
                null) with
            {
                IsSelected = true,
                OutputTitle = secondaryVideoPlan.Title
            });
        }

        return CreateSnapshot(sessionId, channel, HostedStreamFormat.Cmaf, startedAtUtc, rawSource, source, tracks.OrderBy(track => track.SourceIndex).ToArray());
    }

    /// <summary>
    /// Creates metadata for a synthetic protected-content slate.
    /// </summary>
    public static ActiveStreamSnapshot CreateProtectedSlate(string sessionId, string channel, HostedStreamFormat format, DateTime startedAtUtc)
    {
        return CreateSlate(sessionId, channel, format, startedAtUtc, "protected");
    }

    /// <summary>
    /// Creates metadata for a synthetic disabled-channel slate.
    /// </summary>
    public static ActiveStreamSnapshot CreateDisabledSlate(string sessionId, string channel, HostedStreamFormat format, DateTime startedAtUtc)
    {
        return CreateSlate(sessionId, channel, format, startedAtUtc, "disabled");
    }

    private static ActiveStreamSnapshot CreateSlate(string sessionId, string channel, HostedStreamFormat format, DateTime startedAtUtc, string sourceCodec)
    {
        ActiveStreamTrack[] tracks =
        [
            new(MediaTrackType.Video, sourceCodec, "h264", null, 2_500_000, 1280, 720, null, null, null),
            new(MediaTrackType.Audio, sourceCodec, "aac", null, 128_000, null, null, null, 2, 44_100)
        ];
        var synthetic = new ActiveStreamMediaSnapshot(null, tracks.Select(ToOutputMediaTrack).ToArray());
        return new ActiveStreamSnapshot(sessionId, channel, format, startedAtUtc, null, tracks)
        {
            PhysicalInput = synthetic,
            PhysicalOutput = synthetic,
            VirtualOutput = synthetic
        };
    }

    private static ActiveStreamSnapshot CreateFixedTranscode(
        string sessionId,
        string channel,
        HostedStreamFormat format,
        DateTime startedAtUtc,
        MediaProbeResult source,
        long outputVideoBitRate,
        bool copyVideo)
    {
        var video = source.Tracks.FirstOrDefault(track => track.Type == MediaTrackType.Video)
            ?? new MediaTrackMetadata(0, MediaTrackType.Video, "unknown", null, null, null, null, null);
        var audio = source.Tracks.FirstOrDefault(track => track.Type == MediaTrackType.Audio)
            ?? new MediaTrackMetadata(1, MediaTrackType.Audio, "unknown", null, null, null, null, null);
        ActiveStreamTrack[] tracks =
        [
            CreateTrack(video, copyVideo ? "copy" : "h264", copyVideo ? video.BitRate : outputVideoBitRate, null, null),
            CreateTrack(audio, "aac", 128_000, 2, 44_100)
        ];
        return CreateSnapshot(sessionId, channel, format, startedAtUtc, source, source, tracks);
    }

    private static ActiveStreamSnapshot CreateSnapshot(
        string sessionId,
        string channel,
        HostedStreamFormat format,
        DateTime startedAtUtc,
        MediaProbeResult rawSource,
        MediaProbeResult source,
        IReadOnlyList<ActiveStreamTrack> tracks) =>
        new(sessionId, channel, format, startedAtUtc, source.BitRate, tracks)
        {
            PhysicalInput = ToMediaSnapshot(rawSource),
            PhysicalOutput = ToMediaSnapshot(source),
            VirtualOutput = new ActiveStreamMediaSnapshot(
                GetOutputBitRate(source, tracks),
                tracks.Where(IsMappedOutput).Select(ToOutputMediaTrack).ToArray())
        };

    private static long? GetOutputBitRate(MediaProbeResult source, IReadOnlyList<ActiveStreamTrack> tracks)
    {
        var selected = tracks.Where(IsMappedOutput).ToArray();
        if (selected.All(track => string.Equals(track.OutputCodec, "copy", StringComparison.OrdinalIgnoreCase)))
        {
            return source.BitRate;
        }
        return selected.All(track => track.OutputBitRate.HasValue)
            ? selected.Sum(track => track.OutputBitRate!.Value)
            : null;
    }

    private static bool IsMappedOutput(ActiveStreamTrack track) =>
        track.IsSelected && !string.Equals(track.OutputCodec, "not-mapped", StringComparison.OrdinalIgnoreCase);

    private static ActiveStreamMediaSnapshot ToMediaSnapshot(MediaProbeResult source) =>
        new(source.BitRate, source.Tracks.Select(track => new ActiveStreamMediaTrack(
            track.Index,
            track.Type,
            track.Codec,
            track.BitRate,
            track.Width,
            track.Height,
            track.Channels,
            track.SampleRate)
        {
            Language = track.Language,
            Title = track.Title
        }).ToArray());

    private static ActiveStreamMediaTrack ToOutputMediaTrack(ActiveStreamTrack track) =>
        new(
            track.SourceIndex,
            track.Type,
            string.Equals(track.OutputCodec, "copy", StringComparison.OrdinalIgnoreCase) ? track.SourceCodec : track.OutputCodec,
            track.OutputBitRate,
            track.SourceWidth,
            track.SourceHeight,
            track.OutputChannels,
            track.OutputSampleRate)
        {
            Language = track.Language,
            Title = track.Title,
            RenditionTitle = track.OutputTitle
        };

    private static ActiveStreamTrack CreateTrack(MediaTrackMetadata source, string outputCodec, long? outputBitRate, int? outputChannels, int? outputSampleRate)
    {
        return new ActiveStreamTrack(source.Type, source.Codec, outputCodec, source.BitRate, outputBitRate, source.Width, source.Height, source.Channels, outputChannels, outputSampleRate)
        {
            SourceIndex = source.Index,
            Language = source.Language,
            Title = source.Title,
            IsDefault = source.IsDefault,
            IsForced = source.IsForced,
            IsHearingImpaired = source.IsHearingImpaired,
            HasClosedCaptions = source.HasClosedCaptions,
            IsEmbeddedClosedCaptions = source.IsEmbeddedClosedCaptions,
            SubtitlePresentation = source.Type == MediaTrackType.Subtitle ? source.SubtitlePresentation : null
        };
    }
}
