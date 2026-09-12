namespace Lineup.Web.Services;

/// <summary>
/// Resolves content protection from live tuner diagnostics and cached channel metadata.
/// </summary>
public static class ProtectedContentDetector
{
    /// <summary>
    /// Determines whether a failed stream is protected.
    /// </summary>
    /// <param name="tunerError">The live tuner error, when one was returned.</param>
    /// <param name="cachedDrm">Whether the cached lineup marks the channel as DRM protected.</param>
    /// <returns>
    /// <see langword="true"/> for tuner error 811, or for cached DRM when no live tuner error is available.
    /// </returns>
    public static bool IsProtected(string? tunerError, bool cachedDrm)
    {
        if (!string.IsNullOrWhiteSpace(tunerError))
        {
            return tunerError.StartsWith("811 ", StringComparison.Ordinal);
        }

        return cachedDrm;
    }
}
