using Bunit;
using Lineup.Core.Storage;
using Lineup.HDHomeRun.Api.Models;
using Lineup.Web.Components.Pages;
using Lineup.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Lineup.Web.Tests.Components.Pages;

/// <summary>
/// Verifies Guide channel metadata markers.
/// </summary>
public class GuideTests
{
    /// <summary>
    /// Verifies that cached DRM channels display an accessible protection marker.
    /// </summary>
    [Fact]
    public void DrmChannel_DisplaysProtectionMarker()
    {
        // Arrange
        using var context = new BunitContext();
        var repository = Substitute.For<IEpgRepository>();
        repository.GetChannelsAsync().Returns(
        [
            new HDHomeRunChannelEpgSegment { GuideNumber = "117.1", GuideName = "Protected", DRM = true }
        ]);
        repository.GetProgramsAsync(Arg.Any<DateTime?>(), Arg.Any<DateTime?>()).Returns([]);
        var timeZoneService = Substitute.For<ITimeZoneService>();
        timeZoneService.Today.Returns(DateTime.Today);
        timeZoneService.Now.Returns(DateTime.Now);
        context.Services.AddSingleton(repository);
        context.Services.AddSingleton(timeZoneService);
        context.JSInterop.Mode = JSRuntimeMode.Loose;

        // Act
        var component = context.Render<Guide>();

        // Assert
        component.WaitForAssertion(() =>
        {
            var marker = component.Find("[aria-label='DRM-protected channel']");
            Assert.Contains("bi-shield-lock-fill", marker.ClassList);
        });
    }
}
