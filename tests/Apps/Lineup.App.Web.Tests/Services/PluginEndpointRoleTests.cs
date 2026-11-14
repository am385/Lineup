using System.Reflection;
using Lineup.App.Web.Controllers;
using Lineup.Core.VirtualOutputs.Hosting;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace Lineup.App.Web.Tests.Services;

/// <summary>Verifies production endpoint roles distinguish plugin media from management and synthetic diagnostics.</summary>
public sealed class PluginEndpointRoleTests
{
    /// <summary>Verifies scoped and root HDHomeRun and Lineup Link protocol actions all require connection admission.</summary>
    [Theory]
    [InlineData(typeof(HdHomeRunProxyController))]
    [InlineData(typeof(LineupLinkController))]
    public void ProtocolController_AllHttpEndpoints_RequireAdmission(Type controller)
    {
        // Arrange
        var actions = controller.GetMethods().Where(method => method.GetCustomAttributes<HttpMethodAttribute>().Any()).ToArray();

        // Act
        var roles = actions.Select(method => method.GetCustomAttribute<PluginConnectionEndpointAttribute>() ?? controller.GetCustomAttribute<PluginConnectionEndpointAttribute>());

        // Assert
        Assert.NotEmpty(actions);
        Assert.All(roles, role => Assert.True(role?.Counts));
    }

    /// <summary>Verifies live media is counted but stops, inspection and Watch Test remain exempt.</summary>
    [Theory]
    [InlineData("/auto/v{channel}", true)]
    [InlineData("/hdhomerun/{virtualDeviceId}/auto/v{channel}", true)]
    [InlineData("cmaf/start/{channel}", true)]
    [InlineData("cmaf/{sessionId}/{filename}", true)]
    [InlineData("watch-test/{sessionId}/{filename}", false)]
    [InlineData("watch-test/start", false)]
    [InlineData("watch-test/stop/{sessionId}", false)]
    [InlineData("cmaf/stop/{sessionId}", false)]
    [InlineData("watch-test/sessions", false)]
    [InlineData("cmaf/sessions", false)]
    public void StreamController_ExplicitRoute_RetainsCorrectRole(string route, bool counted)
    {
        // Arrange
        var method = Assert.Single(typeof(StreamController).GetMethods(), action => action.GetCustomAttributes<HttpMethodAttribute>().Any(attribute => attribute.Template == route));

        // Act
        var role = method.GetCustomAttribute<PluginConnectionEndpointAttribute>();

        // Assert
        Assert.Equal(counted, role?.Counts ?? false);
    }
}
