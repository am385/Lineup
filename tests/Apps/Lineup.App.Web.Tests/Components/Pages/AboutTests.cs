using Bunit;
using Lineup.App.Web.Components.Pages;
using Lineup.App.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Xunit;

namespace Lineup.App.Web.Tests.Components.Pages;

/// <summary>Verifies recoverable About-page clipboard behavior.</summary>
public sealed class AboutTests
{
    /// <summary>Verifies copy rejection reports an error without success feedback and leaves the component usable for a later copy.</summary>
    /// <param name="disconnected">Whether interop fails because the browser disconnected.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CopyXmltvUrl_ClipboardRejected_ReportsFailureAndAllowsLaterCopy(bool disconnected)
    {
        // Arrange
        await using var context = new BunitContext();
        var copy = context.JSInterop.SetupVoid("copyTextToClipboard", _ => true);
        copy.SetException<Exception>(disconnected ? new JSDisconnectedException("Browser disconnected.") : new JSException("Clipboard denied."));
        var component = context.Render<About>();
        var notifications = context.Services.GetRequiredService<IStatusNotificationService>();

        // Act
        await component.Find("button").ClickAsync(new());

        // Assert
        var failure = Assert.Single(notifications.Notifications);
        Assert.True(failure.IsError);
        Assert.Contains(disconnected ? "disconnected" : "could not copy", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(component.FindAll(".text-success"));
        Assert.False(context.Renderer.UnhandledException.IsCompleted);
        copy.SetVoidResult();
        var successfulCopy = component.Find("button").ClickAsync(new());
        try
        {
            component.WaitForAssertion(() => Assert.Contains("Copied to clipboard!", component.Markup, StringComparison.Ordinal), BunitContext.WaitTimeout);
        }
        finally
        {
            await successfulCopy;
        }
    }
}
