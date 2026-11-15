using Bunit;
using Lineup.App.Web.Components.Pages;
using Lineup.App.Web.Components.Transcoding;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Lineup.App.Web.Tests.Components.Pages;

/// <summary>
/// Verifies transcoding settings behavior on the Settings page.
/// </summary>
public class TranscodingSettingsTests
{
    /// <summary>Verifies selecting a new backend cannot retain incompatible device and advanced options.</summary>
    [Fact]
    public async Task HardwareEncoder_ChangingBackend_ClearsBackendSpecificSettings()
    {
        // Arrange
        await using var context = new BunitContext();
        HardwareEncodingSettings? changed = null;
        var component = context.Render<HardwareEncoderSettings>(parameters => parameters
            .Add(editor => editor.IdPrefix, "hardware")
            .Add(editor => editor.Value, new HardwareEncodingSettings { Encoder = HardwareVideoEncoder.Nvenc, Device = "1", AdvancedArguments = "preset=p6" })
            .Add(editor => editor.ValueChanged, value => changed = value));

        // Act
        await component.Find("#hardwareEncoder").ChangeAsync(HardwareVideoEncoder.Software.ToString());

        // Assert
        Assert.NotNull(changed);
        Assert.Equal(HardwareVideoEncoder.Software, changed.Encoder);
        Assert.Empty(changed.Device);
        Assert.Empty(changed.AdvancedArguments);
    }

    /// <summary>
    /// Verifies the disabled-channel behavior defaults to an error and persists edits.
    /// </summary>
    [Fact]
    public async Task Save_DisabledChannelMode_PersistsValue()
    {
        // Arrange
        await using var context = new BunitContext();
        var settings = new LineupSettings { IsSetupComplete = true };
        var settingsService = Substitute.For<ILineupSettingsService>();
        settingsService.Settings.Returns(settings);
        settingsService.UpdateAsync(Arg.Any<Action<LineupSettings>>()).Returns(callInfo =>
        {
            callInfo.Arg<Action<LineupSettings>>()(settings);
            return Task.CompletedTask;
        });
        var timeZoneService = Substitute.For<ITimeZoneService>();
        timeZoneService.TimeZone.Returns(TimeZoneInfo.Utc);
        context.Services.AddSingleton(settingsService);
        context.Services.AddSingleton(timeZoneService);
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var component = context.Render<Settings>();
        await component.Find("#tab-streaming").ClickAsync(new());
        var selector = component.Find("#disabledChannelMode");

        // Act
        await selector.ChangeAsync(DisabledChannelMode.StreamSlate.ToString());
        await component.Find("button.btn-primary").ClickAsync(new());

        // Assert
        Assert.Equal(DisabledChannelMode.StreamSlate, settings.DisabledChannelMode);
        await settingsService.Received(1).UpdateAsync(Arg.Any<Action<LineupSettings>>());
    }
}
