namespace Lineup.Extensibility.Streaming;

/// <summary>Interprets normalized physical-source protection flags consistently during topology projection and playback.</summary>
public static class ProtectedContentMetadata
{
    /// <summary>Determines whether normalized metadata explicitly marks a source as protected.</summary>
    public static bool IsProtected(IReadOnlyDictionary<string, string> sourceMetadata)
    {
        ArgumentNullException.ThrowIfNull(sourceMetadata);
        return sourceMetadata.TryGetValue("drm", out var drm)
            && (bool.TryParse(drm, out var protectedFlag) && protectedFlag
                || int.TryParse(drm, out var protectedValue) && protectedValue != 0);
    }
}
