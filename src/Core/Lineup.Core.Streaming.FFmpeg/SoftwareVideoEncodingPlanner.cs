namespace Lineup.Core.Streaming.FFmpeg;

/// <summary>Provides the shared low-latency software H.264 encoding policy.</summary>
public static class SoftwareVideoEncodingPlanner
{
    /// <summary>Retains zero-lookahead tuning while enabling automatically sized frame threading for one encoded video output.</summary>
    /// <param name="outputIndex">The video output index, independent of the input stream index.</param>
    /// <param name="repeatHeaders">Whether to retain repeated H.264 headers for MPEG-TS output.</param>
    /// <returns>Stream-scoped libx264 tuning options, without an explicit thread count.</returns>
    public static IReadOnlyList<string> CreateLowLatencyArguments(int outputIndex, bool repeatHeaders = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(outputIndex);
        return [
            $"-tune:v:{outputIndex}", "zerolatency",
            $"-x264-params:v:{outputIndex}", repeatHeaders ? "sliced-threads=0:repeat-headers=1" : "sliced-threads=0"
        ];
    }
}
