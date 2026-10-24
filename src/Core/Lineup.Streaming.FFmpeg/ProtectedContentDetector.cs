namespace Lineup.Streaming.FFmpeg;

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
    private const string InferredMessage = "The requested channel is protected and cannot be streamed.";

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

    /// <summary>Determines whether normalized physical-source metadata marks a channel as protected.</summary>
    public static bool IsProtected(IReadOnlyDictionary<string, string> sourceMetadata)
    {
        ArgumentNullException.ThrowIfNull(sourceMetadata);
        return sourceMetadata.TryGetValue("drm", out var drm)
            && (bool.TryParse(drm, out var protectedFlag) && protectedFlag
                || int.TryParse(drm, out var protectedValue) && protectedValue != 0);
    }

    /// <summary>Classifies a resolved route as known protected only when every available equivalent source is marked protected.</summary>
    public static ProtectedContentDetection Detect(ResolvedStreamingRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);
        return route.Candidates.Count > 0 && route.Candidates.All(candidate => IsProtected(candidate.SourceMetadata))
            ? new ProtectedContentDetection(IsProtected: true, IsInferred: true, InferredMessage)
            : new ProtectedContentDetection(IsProtected: false, IsInferred: false, Message: null);
    }

    /// <summary>Creates a provider-neutral protected-content failure without opening a physical stream.</summary>
    public static StreamAcquisitionException CreateAcquisitionException(ResolvedStreamingRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);
        var candidateFailures = route.Candidates.Select(candidate => new StreamAcquisitionCandidateFailure
        {
            DeviceId = candidate.SourceChannelId.PhysicalDeviceId,
            ChannelId = candidate.SourceChannelId,
            Category = PhysicalStreamFailureCategory.ProtectedContent,
            Code = "protected_content",
            Message = "The physical source metadata marks this channel as protected.",
            Retryable = false
        }).ToArray();
        return StreamAcquisitionException.Create(
            candidateFailures,
            candidateFailures.Select(failure => new InvalidOperationException(failure.Message)).ToArray());
    }
}
