using Bunit;
using Lineup.App.Web.Components.Pages;
using Lineup.App.Web.Components.Transcoding;
using Lineup.App.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Lineup.App.Web.Tests.Components.Pages;

/// <summary>
/// Verifies transcoding settings behavior on the Settings page.
/// </summary>
public class TranscodingSettingsTests
{
    /// <summary>Verifies forced video encoding reveals global encoder controls without enabling other conversion policies.</summary>
    [Theory]
    [InlineData(HardwareVideoEncoder.Software)]
    [InlineData(HardwareVideoEncoder.Qsv)]
    public async Task GlobalEncoderControls_ForcedVideo_RevealsApplicableSettings(HardwareVideoEncoder encoder)
    {
        // Arrange
        await using var context = new BunitContext();
        var settings = new PhysicalTranscodeSettingsModel
        {
            HardwareEncoding = new() { Encoder = encoder },
            Deinterlace = SourceDeinterlacePolicy.Preserve,
            Audio = OutputAudioPolicy.Copy,
            Ac4Audio = OutputAudioPolicy.Copy
        };
        var component = context.Render<GlobalTranscoderSettings>(parameters => parameters
            .Add(editor => editor.Settings, settings)
            .Add(editor => editor.IdPrefix, "global")
            .Add(editor => editor.CssPrefix, "global")
            .Add(editor => editor.StageName, "Physical"));

        // Assert
        Assert.Empty(component.FindAll("#globalPreset, #globalQuality, #globalBitrate"));

        // Act
        await component.Find("#globalAlwaysVideo").ChangeAsync(true);
        await component.Find("#globalPreset").ChangeAsync(nameof(VideoEncoderPreset.Fast));
        await component.Find("#globalQuality").ChangeAsync(22);
        await component.Find("#globalBitrate").ChangeAsync(21);

        // Assert
        Assert.Equal(VideoEncoderPreset.Fast, settings.VideoPreset);
        Assert.Equal(22, settings.VideoQuality);
        Assert.Equal(21, settings.MaximumVideoBitRateMbps);
        Assert.True(settings.AlwaysTranscodeVideo);
        Assert.False(settings.ConvertHevcToH264);
        Assert.Equal(SourceDeinterlacePolicy.Preserve, settings.Deinterlace);
        Assert.Single(component.FindAll("[role='alert']"));

        // Act
        await component.Find("#globalAlwaysVideo").ChangeAsync(false);

        // Assert
        Assert.Empty(component.FindAll("#globalPreset, #globalQuality, #globalBitrate"));
        Assert.Equal(21, settings.MaximumVideoBitRateMbps);
    }

    /// <summary>Verifies device forced encoding honors inheritance and preserves encoder overrides while controls are hidden.</summary>
    [Theory]
    [InlineData(HardwareVideoEncoder.Software, false)]
    [InlineData(HardwareVideoEncoder.Qsv, false)]
    [InlineData(HardwareVideoEncoder.Qsv, true)]
    public async Task DeviceEncoderControls_ForcedVideo_PreservesIndependentOverrides(HardwareVideoEncoder encoder, bool overrideHardware)
    {
        // Arrange
        await using var context = new BunitContext();
        var defaults = new VirtualTranscodeSettingsModel
        {
            HardwareEncoding = new() { Encoder = overrideHardware ? HardwareVideoEncoder.Software : encoder },
            Deinterlace = SourceDeinterlacePolicy.Preserve,
            Audio = OutputAudioPolicy.Copy,
            Ac4Audio = OutputAudioPolicy.Copy,
            MaximumVideoBitRateMbps = 17
        };
        var settings = new VirtualDeviceSettingsModel
        {
            OverrideHardwareEncoding = overrideHardware,
            HardwareEncoding = new() { Encoder = encoder }
        };
        var component = context.Render<DeviceTranscoderSettings>(parameters => parameters
            .Add(editor => editor.Settings, settings)
            .Add(editor => editor.Defaults, defaults)
            .Add(editor => editor.IdPrefix, "device")
            .Add(editor => editor.CssPrefix, "device")
            .Add(editor => editor.StageName, "Virtual"));

        // Assert
        Assert.Empty(component.FindAll("#deviceOverridePreset-0, #deviceOverrideQuality-0, #deviceOverrideBitrate-0"));
        Assert.Empty(component.FindAll(".device-video-preset, .device-video-quality, .device-bitrate"));

        // Act
        await component.Find("#deviceOverrideAlwaysVideo-0").ChangeAsync(true);
        await component.Find("#deviceAlwaysVideo-0").ChangeAsync(true);
        await component.Find("#deviceOverridePreset-0").ChangeAsync(true);
        await component.Find(".device-video-preset").ChangeAsync(nameof(VideoEncoderPreset.Fast));
        await component.Find("#deviceOverrideQuality-0").ChangeAsync(true);
        await component.Find(".device-video-quality").ChangeAsync(22);
        await component.Find("#deviceOverrideBitrate-0").ChangeAsync(true);
        await component.Find(".device-bitrate").ChangeAsync(21);

        // Assert
        Assert.True(settings.OverrideVideoPreset);
        Assert.Equal(VideoEncoderPreset.Fast, settings.VideoPreset);
        Assert.True(settings.OverrideVideoQuality);
        Assert.Equal(22, settings.VideoQuality);
        Assert.True(settings.OverrideMaximumVideoBitRate);
        Assert.Equal(21, settings.MaximumVideoBitRateMbps);
        Assert.False(settings.OverrideConvertHevcToH264);
        Assert.False(settings.OverrideDeinterlace);
        Assert.Single(component.FindAll("[role='alert']"));

        // Act
        await component.Find("#deviceOverrideBitrate-0").ChangeAsync(false);

        // Assert
        Assert.False(settings.OverrideMaximumVideoBitRate);
        Assert.Empty(component.FindAll(".device-bitrate"));
        Assert.Contains("inherits 17 Mbps", component.Markup, StringComparison.Ordinal);
        Assert.NotNull(component.Find(".device-video-preset"));
        Assert.NotNull(component.Find(".device-video-quality"));

        // Act
        defaults.AlwaysTranscodeVideo = true;
        await component.Find("#deviceAlwaysVideo-0").ChangeAsync(false);

        // Assert
        Assert.Empty(component.FindAll("#deviceOverridePreset-0, #deviceOverrideQuality-0, #deviceOverrideBitrate-0"));
        Assert.True(settings.OverrideVideoPreset);
        Assert.True(settings.OverrideVideoQuality);

        // Act
        await component.Find("#deviceOverrideAlwaysVideo-0").ChangeAsync(false);

        // Assert
        Assert.NotNull(component.Find(".device-video-preset"));
        Assert.NotNull(component.Find(".device-video-quality"));
        Assert.Contains("inherits 17 Mbps", component.Markup, StringComparison.Ordinal);
    }

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
