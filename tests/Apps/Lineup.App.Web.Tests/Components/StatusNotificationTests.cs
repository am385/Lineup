using Bunit;
using Lineup.App.Web.Components;
using Lineup.App.Web.Components.Layout;
using Lineup.App.Web.Services;
using Lineup.TestSupport;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Lineup.App.Web.Tests.Components;

/// <summary>
/// Verifies the shared status notification presentation and lifecycle.
/// </summary>
public class StatusNotificationTests
{
    /// <summary>
    /// Verifies error notifications use the uniform floating presentation and can be dismissed manually.
    /// </summary>
    [Fact]
    public async Task ErrorNotification_DismissButton_HidesNotification()
    {
        // Arrange
        await using var context = new BunitContext();
        var notifications = new StatusNotificationService();
        var settings = Substitute.For<ILineupSettingsService>();
        settings.Settings.Returns(new LineupSettings { IsSetupComplete = true });
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
        await component.Find(".btn-close").ClickAsync(new());

        // Assert
        Assert.Empty(notifications.Notifications);
        Assert.Empty(component.FindAll(".status-notification"));
    }

    /// <summary>
    /// Verifies notifications count down and dismiss themselves when their configured duration expires.
    /// </summary>
    [Fact]
    public async Task Notification_DurationExpires_HidesNotification()
    {
        // Arrange
        await using var context = new BunitContext();
        var clock = new ControlledTimeProvider();
        context.Services.AddSingleton<TimeProvider>(clock);
        var notifications = new StatusNotificationService();
        context.Services.AddSingleton<IStatusNotificationService>(notifications);
        var component = context.Render<StatusNotification>(parameters => parameters
            .Add(item => item.DurationSeconds, 1));

        // Act
        notifications.ShowSuccess("Saved.");
        await clock.WaitForTimerAsync(TimeSpan.FromSeconds(1), Xunit.TestContext.Current.CancellationToken);
        await component.InvokeAsync(() => clock.Advance(TimeSpan.FromSeconds(1)));

        // Assert
        Assert.Empty(notifications.Notifications);
        Assert.Empty(component.FindAll(".status-notification"));
    }

    /// <summary>
    /// Verifies navigating to another page clears a notification from the shared layout host.
    /// </summary>
    [Fact]
    public async Task Notification_NavigationOccurs_HidesNotification()
    {
        // Arrange
        await using var context = new BunitContext();
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
    public async Task Notifications_CapacityExceeded_EvictsOldestNotification()
    {
        // Arrange
        await using var context = new BunitContext();
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
        await using var context = new BunitContext();
        var clock = new ControlledTimeProvider();
        context.Services.AddSingleton<TimeProvider>(clock);
        var notifications = new StatusNotificationService();
        context.Services.AddSingleton<IStatusNotificationService>(notifications);
        var component = context.Render<StatusNotification>(parameters => parameters
            .Add(item => item.DurationSeconds, 2));

        // Act
        notifications.ShowSuccess("First");
        await clock.WaitForTimerAsync(TimeSpan.FromSeconds(1), Xunit.TestContext.Current.CancellationToken);
        await component.InvokeAsync(() => clock.Advance(TimeSpan.FromSeconds(1)));
        notifications.ShowSuccess("Second");
        await clock.WaitForTimerAsync(TimeSpan.FromSeconds(1), Xunit.TestContext.Current.CancellationToken);
        await component.InvokeAsync(() => clock.Advance(TimeSpan.FromSeconds(1)));

        // Assert
        var notification = Assert.Single(notifications.Notifications);
        Assert.Equal("Second", notification.Message);
        Assert.Single(component.FindAll(".status-notification"));

        // Act
        await component.InvokeAsync(() => clock.Advance(TimeSpan.FromSeconds(1)));

        // Assert
        Assert.Empty(notifications.Notifications);
        Assert.Empty(component.FindAll(".status-notification"));
    }
}
