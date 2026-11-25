using System.Globalization;

namespace Lineup.Core.Streaming.FFmpeg;

/// <summary>Plans output sample rates and timestamp compensation for encoded audio.</summary>
public static class AudioResamplePlanner
{
    /// <summary>Returns 48 kHz for AC-4 transcodes without rewriting the probed source rate.</summary>
    public static int? GetOutputSampleRate(string sourceCodec, int? sampleRate) =>
        string.Equals(sourceCodec, "ac4", StringComparison.OrdinalIgnoreCase) ? 48_000 : sampleRate;

    /// <summary>Adds per-output resampling options for encoded audio, leaving unspecified non-AC-4 rates to FFmpeg.</summary>
    public static void AddArguments(List<string> arguments, string sourceCodec, int outputIndex, bool ac4TimestampCompensation, int? sampleRate = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (ac4TimestampCompensation && string.Equals(sourceCodec, "ac4", StringComparison.OrdinalIgnoreCase))
        {
            // Opt-in filling/trimming targets timestamp disagreement, not the nonstandard decoded rate itself.
            arguments.AddRange([$"-filter:a:{outputIndex}", "aresample=async=1"]);
        }
        var outputSampleRate = GetOutputSampleRate(sourceCodec, sampleRate);
        if (outputSampleRate.HasValue)
        {
            arguments.AddRange([$"-ar:a:{outputIndex}", outputSampleRate.Value.ToString(CultureInfo.InvariantCulture)]);
        }
    }
}
