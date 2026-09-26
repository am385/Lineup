using Bunit;
using Lineup.Web.Components;
using Lineup.Web.Components.Layout;
using Lineup.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Lineup.Web.Tests.Components;

/// <summary>
/// Verifies the shared status notification presentation and lifecycle.
/// </summary>
public class StatusNotificationTests
{
    /// <summary>
    /// Verifies error notifications use the uniform floating presentation and can be dismissed manually.
    /// </summary>
    [Fact]
    public void ErrorNotification_DismissButton_HidesNotification()
    {
        // Arrange
        using var context = new BunitContext();
        var notifications = new StatusNotificationService();
        var settings = Substitute.For<IAppSettingsService>();
        settings.Settings.Returns(new AppSettings { IsSetupComplete = true });
        context.Services.AddSingleton<IStatusNotificationService>(notifications);
        context.Services.AddSingleton(settings);
        var component = context.Render<MainLayout>(parameters => parameters
            .Add(item => item.Body, builder => builder.AddContent(0, "Page content")));

        // Act
        notifications.ShowError("Something failed.");

        // Assert
        var notification = component.Find(".status-notification");
        Assert.Contains("alert-danger", notification.ClassList);
        Assert.Equal("5s", notification.QuerySelector(".status-notification-timer")?.TextContent.Trim());

        // Act
        component.Find(".btn-close").Click();

        // Assert
        Assert.Empty(notifications.Notifications);
        Assert.Empty(component.FindAll(".status-notification"));
    }

    /// <summary>
    /// Verifies notifications count down and dismiss themselves when their configured duration expires.
    /// </summary>
    [Fact]
    public void Notification_DurationExpires_HidesNotification()
    {
        // Arrange
        using var context = new BunitContext();
        var notifications = new StatusNotificationService();
        context.Services.AddSingleton<IStatusNotificationService>(notifications);
        var component = context.Render<StatusNotification>(parameters => parameters
            .Add(item => item.DurationSeconds, 1));

        // Act
        notifications.ShowSuccess("Saved.");
        component.WaitForAssertion(
            () => Assert.Empty(notifications.Notifications),
            TimeSpan.FromSeconds(2));

        // Assert
        Assert.Empty(component.FindAll(".status-notification"));
    }

    /// <summary>
    /// Verifies navigating to another page clears a notification from the shared layout host.
    /// </summary>
    [Fact]
    public void Notification_NavigationOccurs_HidesNotification()
    {
        // Arrange
        using var context = new BunitContext();
        var notifications = new StatusNotificationService();
        context.Services.AddSingleton<IStatusNotificationService>(notifications);
        var component = context.Render<StatusNotification>();
        notifications.ShowSuccess("Saved.");
        var navigation = context.Services.GetRequiredService<NavigationManager>();

        // Act
        navigation.NavigateTo("/next");

        // Assert
        Assert.Empty(notifications.Notifications);
        Assert.Empty(component.FindAll(".status-notification"));
    }

    /// <summary>
    /// Verifies publishing more than five notifications evicts only the oldest notification.
    /// </summary>
    [Fact]
    public void Notifications_CapacityExceeded_EvictsOldestNotification()
    {
        // Arrange
        using var context = new BunitContext();
        var notifications = new StatusNotificationService();
        context.Services.AddSingleton<IStatusNotificationService>(notifications);
        var component = context.Render<StatusNotification>();

        // Act
        for (var index = 1; index <= 6; index++)
        {
            notifications.ShowSuccess($"Message {index}");
        }

        // Assert
        Assert.Equal(5, notifications.Notifications.Count);
        Assert.Equal(5, component.FindAll(".status-notification").Count);
        Assert.DoesNotContain(notifications.Notifications, notification => notification.Message == "Message 1");
        Assert.Equal("Message 6", notifications.Notifications[^1].Message);
    }

    /// <summary>
    /// Verifies a later notification keeps its full lifetime when an older notification expires.
    /// </summary>
    [Fact]
    public async Task Notifications_PublishedAtDifferentTimes_ExpireIndependently()
    {
        // Arrange
        using var context = new BunitContext();
        var notifications = new StatusNotificationService();
        context.Services.AddSingleton<IStatusNotificationService>(notifications);
        var component = context.Render<StatusNotification>(parameters => parameters
            .Add(item => item.DurationSeconds, 2));

        // Act
        notifications.ShowSuccess("First");
        await Task.Delay(TimeSpan.FromMilliseconds(1100), Xunit.TestContext.Current.CancellationToken);
        notifications.ShowSuccess("Second");

        // Assert
        component.WaitForAssertion(() =>
        {
            var notification = Assert.Single(notifications.Notifications);
            Assert.Equal("Second", notification.Message);
            Assert.Single(component.FindAll(".status-notification"));
        }, TimeSpan.FromSeconds(2));
    }
}
