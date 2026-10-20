using Xunit;

namespace Lineup.Web.Tests.Services;

/// <summary>
/// Verifies protected-content detection precedence.
/// </summary>
public class ProtectedContentDetectorTests
{
    /// <summary>
    /// Verifies that cached DRM is used when live tuner diagnostics time out.
    /// </summary>
    [Fact]
    public void IsProtected_MissingTunerErrorAndCachedDrm_ReturnsTrue()
    {
        // Arrange
        string? tunerError = null;

        // Act
        var result = ProtectedContentDetector.Detect(tunerError, cachedDrm: true);

        // Assert
        Assert.True(result.IsProtected);
        Assert.True(result.IsInferred);
        Assert.Equal("The requested channel is protected and cannot be streamed.", result.Message);
    }

    /// <summary>
    /// Verifies that an explicit non-DRM tuner error takes precedence over cached metadata.
    /// </summary>
    [Fact]
    public void IsProtected_NonDrmTunerErrorAndCachedDrm_ReturnsFalse()
    {
        // Arrange
        const string tunerError = "807 No Video Data";

        // Act
        var result = ProtectedContentDetector.Detect(tunerError, cachedDrm: true);

        // Assert
        Assert.False(result.IsProtected);
        Assert.False(result.IsInferred);
        Assert.Null(result.Message);
    }

    /// <summary>
    /// Verifies that the tuner's content-protection error is always recognized.
    /// </summary>
    [Fact]
    public void IsProtected_ContentProtectionTunerError_ReturnsTrue()
    {
        // Arrange
        const string tunerError = "811 Content Protection Required";

        // Act
        var result = ProtectedContentDetector.Detect(tunerError, cachedDrm: false);

        // Assert
        Assert.True(result.IsProtected);
        Assert.False(result.IsInferred);
        Assert.Equal(tunerError, result.Message);
    }

    /// <summary>Verifies normalized numeric and Boolean DRM values are recognized.</summary>
    [Theory]
    [InlineData("1")]
    [InlineData("True")]
    public void IsProtected_ProtectedSourceMetadata_ReturnsTrue(string value)
    {
        // Arrange
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["drm"] = value };

        // Act
        var protectedContent = ProtectedContentDetector.IsProtected(metadata);

        // Assert
        Assert.True(protectedContent);
    }
}
