using Lineup.Web.Services;
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
        var result = ProtectedContentDetector.IsProtected(tunerError, cachedDrm: true);

        // Assert
        Assert.True(result);
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
        var result = ProtectedContentDetector.IsProtected(tunerError, cachedDrm: true);

        // Assert
        Assert.False(result);
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
        var result = ProtectedContentDetector.IsProtected(tunerError, cachedDrm: false);

        // Assert
        Assert.True(result);
    }
}
