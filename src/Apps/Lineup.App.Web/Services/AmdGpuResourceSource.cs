using System.Globalization;

namespace Lineup.App.Web.Services;

/// <summary>Reads AMD's graphics/compute busy percentage; unsupported video-engine metrics remain unavailable.</summary>
internal sealed class AmdGpuResourceSource(string card) : IGpuResourceSource
{
    /// <inheritdoc />
    public GpuResourceSnapshot Read()
    {
        var path = Path.Combine(card, "device", "gpu_busy_percent");
        if (!File.Exists(path)) return new([new(card, $"AMD GPU ({Path.GetFileName(card)})", null, null, null, null, "unsupported")]);
        var value = ParsePercentage(File.ReadAllText(path));
        return new([new(card, $"AMD GPU ({Path.GetFileName(card)})", value, null, null, null)]);
    }

    /// <summary>Validates an operating-system percentage rather than fabricating zero on parse failure.</summary>
    internal static double ParsePercentage(string text) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) && value is >= 0 and <= 100
            ? value : throw new GpuCounterException("The AMD driver returned an invalid GPU busy percentage.");

    /// <inheritdoc />
    public void ResetBaseline()
    {
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }
}
