using Bunit;
using Lineup.Web.Components.Playback;
using Microsoft.JSInterop;
using Xunit;

namespace Lineup.Web.Tests.Components;

/// <summary>
/// Verifies the shared CMAF player surface presentation and browser lifecycle.
/// </summary>
public sealed class CmafPlayerSurfaceTests
{
    /// <summary>
    /// Verifies live playback renders the stable Shaka container, video element, and loading contract.
    /// </summary>
    [Fact]
    public void LiveSurface_RendersShakaPlayerAndLoadingState()
    {
        // Arrange
        using var context = new BunitContext();
        context.JSInterop.SetupVoid("stopMediaPlayer", "player").SetVoidResult();

        // Act
        var component = context.Render<CmafPlayerSurface>(parameters => parameters
            .Add(item => item.ElementId, "player")
            .Add(item => item.UseShakaUi, true)
            .Add(item => item.IsLoading, true));

        // Assert
        Assert.NotNull(component.Find("[data-lineup-cmaf-container]"));
        Assert.Contains("shaka-video-container", component.Find(".ratio").ClassList);
        Assert.Contains("shaka-video", component.Find("#player").ClassList);
        Assert.Equal("Setting up stream...", component.Find("#cmafStreamLoading p").TextContent);
    }

    /// <summary>
    /// Verifies live initialization projects the surface element identifier through the typed interop boundary.
    /// </summary>
    [Fact]
    public async Task InitializeLiveAsync_UsesSurfaceElementIdentifier()
    {
        // Arrange
        using var context = new BunitContext();
        context.JSInterop.SetupVoid("stopMediaPlayer", "player").SetVoidResult();
        context.JSInterop.Setup<PlayerResult>("initCmafPlayer", _ => true).SetResult(new PlayerResult(true));
        var component = context.Render<CmafPlayerSurface>(parameters => parameters
            .Add(item => item.ElementId, "player")
            .Add(item => item.UseShakaUi, true));
        using var callbackReference = DotNetObjectReference.Create(new CallbackHost());

        // Act
        var result = await component.Instance.InitializeLiveAsync<PlayerResult, CallbackHost>(
            "/manifest.mpd",
            "/manifest.m3u8",
            "Source",
            "Source",
            true,
            "hvc1",
            "ac-3",
            Array.Empty<object>(),
            callbackReference,
            "session");

        // Assert
        Assert.True(result.Success);
        var invocation = Assert.Single(context.JSInterop.Invocations, item => item.Identifier == "initCmafPlayer");
        Assert.Equal("player", invocation.Arguments[0]);
    }

    private sealed record PlayerResult(bool Success);

    private sealed class CallbackHost;
}
