using System.Reflection;
using Bunit;
using Lineup.App.Web.Components.Layout;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Lineup.App.Web.Tests.Components.Layout;

/// <summary>
/// Verifies navigation reacts to application setup state.
/// </summary>
public sealed class NavMenuTests
{
    /// <summary>
    /// Verifies component disposal does not resolve services after the host service provider has shut down.
    /// </summary>
    [Fact]
    public async Task Dispose_ServiceProviderUnavailable_DoesNotResolveServices()
    {
        // Arrange
        await using var context = new BunitContext();
        var settingsService = Substitute.For<ILineupSettingsService>();
        settingsService.Settings.Returns(new LineupSettings());
        context.Services.AddSingleton(settingsService);
        var component = context.Render<NavMenu>();
        var servicesProperty = typeof(NavMenu).GetProperty("Services", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(servicesProperty);
        servicesProperty.SetValue(component.Instance, new DisposedServiceProvider());

        // Act
        var exception = Record.Exception(component.Instance.Dispose);

        // Assert
        Assert.Null(exception);
    }

    /// <summary>
    /// Verifies completing first-run setup reveals application navigation without recreating the layout.
    /// </summary>
    [Fact]
    public async Task SettingsChanged_SetupCompleted_RevealsApplicationPages()
    {
        // Arrange
        await using var context = new BunitContext();
        var settings = new LineupSettings
        {
            IsSetupComplete = false,
            Streaming = new StreamingConfiguration
            {
                VirtualDevices = [WebPlayerVirtualTunerDriver.CreateBuiltIn()]
            }
        };
        var settingsService = Substitute.For<ILineupSettingsService>();
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
            var watchLink = Assert.Single(component.FindAll("a.nav-link"), link => string.Equals(link.GetAttribute("href"), "watch", StringComparison.Ordinal));
            Assert.Equal("Watch", watchLink.TextContent.Trim());
            Assert.DoesNotContain(component.FindAll("a.nav-link"), link => string.Equals(link.GetAttribute("href"), "watch-cmaf", StringComparison.Ordinal));
        }, timeout: LineupWebBunitContext.WaitTimeout);
    }

    /// <summary>
    /// Verifies disabling the built-in Web Player removes both playback pages from navigation.
    /// </summary>
    [Fact]
    public async Task SettingsChanged_WebPlayerDisabled_HidesWatchPages()
    {
        // Arrange
        await using var context = new BunitContext();
        var webPlayer = WebPlayerVirtualTunerDriver.CreateBuiltIn();
        var settings = new LineupSettings
        {
            IsSetupComplete = true,
            Streaming = new StreamingConfiguration
            {
                VirtualDevices = [webPlayer]
            }
        };
        var settingsService = Substitute.For<ILineupSettingsService>();
        settingsService.Settings.Returns(settings);
        context.Services.AddSingleton(settingsService);
        var component = context.Render<NavMenu>();
        Assert.Contains(component.FindAll("a.nav-link"), link => string.Equals(link.GetAttribute("href"), "watch", StringComparison.Ordinal));
        Assert.Contains(component.FindAll("a.nav-link"), link => string.Equals(link.GetAttribute("href"), "watch-test", StringComparison.Ordinal));

        // Act
        settings.Streaming = settings.Streaming with
        {
            VirtualDevices = [webPlayer with { Enabled = false }]
        };
        settingsService.OnSettingsChanged += Raise.Event<Action>();

        // Assert
        component.WaitForAssertion(() =>
        {
            Assert.DoesNotContain(component.FindAll("a.nav-link"), link => string.Equals(link.GetAttribute("href"), "watch", StringComparison.Ordinal));
            Assert.DoesNotContain(component.FindAll("a.nav-link"), link => string.Equals(link.GetAttribute("href"), "watch-test", StringComparison.Ordinal));
        }, timeout: LineupWebBunitContext.WaitTimeout);
    }

    private sealed class DisposedServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => throw new ObjectDisposedException(nameof(IServiceProvider));
    }
}
