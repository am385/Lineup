using Lineup.Web.Services;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Lineup.Web.Tests.Services;

/// <summary>
/// Verifies request routing while Lineup is awaiting a factory reset.
/// </summary>
public sealed class FactoryResetRecoveryRequestPolicyTests
{
    /// <summary>
    /// Verifies the runtime endpoint remains available to the browser restart watcher.
    /// </summary>
    [Fact]
    public void GetAction_RuntimeEndpoint_Continues()
    {
        // Arrange
        var path = new PathString("/api/runtime");

        // Act
        var action = FactoryResetRecoveryRequestPolicy.GetAction(path);

        // Assert
        Assert.Equal(FactoryResetRecoveryRequestAction.Continue, action);
    }

    /// <summary>
    /// Verifies unavailable APIs return a recovery response instead of rendering a page.
    /// </summary>
    [Fact]
    public void GetAction_UnavailableApi_RejectsApi()
    {
        // Arrange
        var path = new PathString("/api/xmltv");

        // Act
        var action = FactoryResetRecoveryRequestPolicy.GetAction(path);

        // Assert
        Assert.Equal(FactoryResetRecoveryRequestAction.RejectApi, action);
    }

    /// <summary>
    /// Verifies non-recovery pages redirect to Settings.
    /// </summary>
    [Fact]
    public void GetAction_ApplicationPage_RedirectsToSettings()
    {
        // Arrange
        var path = new PathString("/dashboard");

        // Act
        var action = FactoryResetRecoveryRequestPolicy.GetAction(path);

        // Assert
        Assert.Equal(FactoryResetRecoveryRequestAction.RedirectToSettings, action);
    }
}
