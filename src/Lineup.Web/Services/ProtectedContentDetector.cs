namespace Lineup.Web.Services;

/// <summary>
/// Describes how a failed stream was classified as protected content.
/// </summary>
/// <param name="IsProtected">Whether the failure represents protected content.</param>
/// <param name="IsInferred">Whether protection was inferred from cached lineup metadata rather than confirmed by the tuner.</param>
/// <param name="Message">The user-facing failure message.</param>
public sealed record ProtectedContentDetection(bool IsProtected, bool IsInferred, string? Message);

/// <summary>
/// Resolves content protection from live tuner diagnostics and cached channel metadata.
/// </summary>
public static class ProtectedContentDetector
{
    private const string InferredMessage = "811 DRM content inferred from channel lineup metadata after playback startup failed.";

    /// <summary>
    /// Determines whether a failed stream is protected.
    /// </summary>
    /// <param name="tunerError">The live tuner error, when one was returned.</param>
    /// <param name="cachedDrm">Whether the cached lineup marks the channel as DRM protected.</param>
    /// <returns>
    /// <see langword="true"/> for tuner error 811, or for cached DRM when no live tuner error is available.
    /// </returns>
    public static bool IsProtected(string? tunerError, bool cachedDrm) => Detect(tunerError, cachedDrm).IsProtected;

    /// <summary>
    /// Classifies a failed stream using live tuner diagnostics first and cached lineup metadata only when diagnostics are unavailable.
    /// </summary>
    /// <param name="tunerError">The live tuner error, when one was returned.</param>
    /// <param name="cachedDrm">Whether the cached lineup marks the channel as DRM protected.</param>
    /// <returns>The protected-content classification and its source.</returns>
    public static ProtectedContentDetection Detect(string? tunerError, bool cachedDrm)
    {
        if (!string.IsNullOrWhiteSpace(tunerError))
        {
            var confirmed = tunerError.StartsWith("811 ", StringComparison.Ordinal);
            return new ProtectedContentDetection(confirmed, IsInferred: false, confirmed ? tunerError : null);
        }

        return cachedDrm
            ? new ProtectedContentDetection(IsProtected: true, IsInferred: true, InferredMessage)
            : new ProtectedContentDetection(IsProtected: false, IsInferred: false, Message: null);
    }
}
