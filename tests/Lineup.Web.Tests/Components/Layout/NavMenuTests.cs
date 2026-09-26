using Bunit;
using Lineup.Core;
using Lineup.Web.Components.Layout;
using Lineup.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Lineup.Web.Tests.Components.Layout;

/// <summary>
/// Verifies navigation reacts to application setup state.
/// </summary>
public sealed class NavMenuTests
{
    /// <summary>
    /// Verifies completing first-run setup reveals application navigation without recreating the layout.
    /// </summary>
    [Fact]
    public void SettingsChanged_SetupCompleted_RevealsApplicationPages()
    {
        // Arrange
        using var context = new BunitContext();
        var settings = new AppSettings { IsSetupComplete = false };
        var settingsService = Substitute.For<IAppSettingsService>();
        settingsService.Settings.Returns(settings);
        context.Services.AddSingleton(settingsService);
        var component = context.Render<NavMenu>();
        Assert.DoesNotContain("Channels", component.Markup);

        // Act
        settings.IsSetupComplete = true;
        settingsService.OnSettingsChanged += Raise.Event<Action>();

        // Assert
        component.WaitForAssertion(() =>
        {
            Assert.Contains("Dashboard", component.Markup);
            Assert.Contains("Channels", component.Markup);
            Assert.Contains("Guide", component.Markup);
            Assert.Contains("Watch", component.Markup);
            Assert.Contains("Logs", component.Markup);
        });
    }
}
