namespace Lineup.Core.Streaming.FFmpeg;

/// <summary>Centralizes subtitle codec support for MPEG-TS and browser-oriented outputs.</summary>
public static class SubtitleCapabilityPolicy
{
    private static readonly HashSet<string> _textCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "ass", "ssa", "subrip", "text", "webvtt", "mov_text", "eia_608", "eia_708"
    };
    private static readonly HashSet<string> _bitmapCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "dvb_subtitle", "dvd_subtitle", "hdmv_pgs_subtitle", "xsub"
    };
    private static readonly HashSet<string> _mpegTsCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "dvb_subtitle", "dvb_teletext"
    };

    /// <summary>Classifies a subtitle codec for browser presentation.</summary>
    public static SubtitlePresentation Classify(string? codec) =>
        codec is not null && _textCodecs.Contains(codec)
            ? SubtitlePresentation.WebVtt
            : codec is not null && _bitmapCodecs.Contains(codec)
                ? SubtitlePresentation.BurnIn
                : SubtitlePresentation.Unsupported;

    /// <summary>Returns whether MPEG-TS can safely copy a standalone subtitle codec.</summary>
    public static bool CanCopyToMpegTs(string? codec) => codec is not null && _mpegTsCodecs.Contains(codec);
}
