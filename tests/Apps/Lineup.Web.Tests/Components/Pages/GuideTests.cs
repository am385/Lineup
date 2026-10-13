using Bunit;
using Lineup.Web.Components.Pages;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Lineup.Web.Tests.Components.Pages;

/// <summary>Verifies guide page presentation behavior.</summary>
public sealed class GuideTests
{
    /// <summary>Verifies channel rows are ordered by numeric channel components instead of alphabetically.</summary>
    [Fact]
    public void ChannelRows_MixedChannelNumbers_OrdersByNumericValue()
    {
        // Arrange
        var reader = Substitute.For<ILineupGuideReader>();
        reader.GetChannelsAsync(Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>()).Returns(
        [
            new GuideChannelSegment { GuideNumber = "100.1", GuideName = "Hundred" },
            new GuideChannelSegment { GuideNumber = "10.1", GuideName = "Ten" },
            new GuideChannelSegment { GuideNumber = "7.2", GuideName = "Seven Two" },
            new GuideChannelSegment { GuideNumber = "2.2", GuideName = "Two Two" },
            new GuideChannelSegment { GuideNumber = "7.10", GuideName = "Seven Ten" },
            new GuideChannelSegment { GuideNumber = "2", GuideName = "Two" }
        ]);
        reader.GetProgramsAsync(Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>()).Returns([]);
        var timeZone = Substitute.For<ITimeZoneService>();
        timeZone.Today.Returns(new DateTime(2026, 10, 5));
        timeZone.Now.Returns(new DateTime(2026, 10, 5, 15, 0, 0));
        timeZone.ConvertToUtc(Arg.Any<DateTime>()).Returns(call => call.Arg<DateTime>());
        using var context = new BunitContext();
        context.Services.AddSingleton(reader);
        context.Services.AddSingleton(timeZone);

        // Act
        var component = context.Render<Guide>();

        // Assert
        component.WaitForAssertion(() =>
            Assert.Equal(
                ["2", "2.2", "7.10", "7.2", "10.1", "100.1"],
                component.FindAll(".channel-number").Select(element => element.TextContent.Trim())));
    }
}
