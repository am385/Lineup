namespace Lineup.Core.Streaming.FFmpeg;

/// <summary>Associates explicitly copied AC-4 output tracks with deterministic MPEG-TS elementary PIDs.</summary>
public sealed class MpegTsAc4CopyPlan
{
    private readonly int[] _audioIndexes;
    private readonly int _videoStreamCount;

    private MpegTsAc4CopyPlan(int[] audioIndexes, int videoStreamCount)
    {
        _audioIndexes = audioIndexes;
        _videoStreamCount = videoStreamCount;
        PacketIds = Array.AsReadOnly(audioIndexes.Select(index => GetPacketId(videoStreamCount + index)).ToArray());
    }

    /// <summary>Gets the copied AC-4 elementary PIDs requiring registration signaling.</summary>
    public IReadOnlyList<int> PacketIds { get; }

    /// <summary>Plans copied AC-4 after the mapped video streams and before standalone subtitles.</summary>
    public static MpegTsAc4CopyPlan Create(IEnumerable<string> audioCodecs, OutputAudioPolicy ac4Policy, int videoStreamCount)
    {
        ArgumentNullException.ThrowIfNull(audioCodecs);
        ArgumentOutOfRangeException.ThrowIfNegative(videoStreamCount);
        var indexes = audioCodecs.Select((codec, index) => (codec, index))
            .Where(item => ac4Policy == OutputAudioPolicy.Copy && string.Equals(item.codec, "ac4", StringComparison.OrdinalIgnoreCase))
            .Select(item => item.index)
            .ToArray();
        return new(indexes, videoStreamCount);
    }

    /// <summary>Adds scoped packet-copy options and explicit output PIDs without changing other codecs.</summary>
    public void AddArguments(List<string> arguments)
    {
        for (var index = 0; index < _audioIndexes.Length; index++)
        {
            arguments.AddRange([
                $"-copyinkf:a:{_audioIndexes[index]}",
                "-streamid", $"{_videoStreamCount + _audioIndexes[index]}:{PacketIds[index]}"
            ]);
        }
    }

    private static int GetPacketId(int outputIndex)
    {
        // Match FFmpeg's elementary PID allocation without colliding with its default PMT PID, 0x1000.
        if (outputIndex >= 0xf00)
        {
            throw new ArgumentOutOfRangeException(nameof(outputIndex), "The copied AC-4 output index exceeds the elementary PID range.");
        }
        return 0x100 + outputIndex;
    }
}
