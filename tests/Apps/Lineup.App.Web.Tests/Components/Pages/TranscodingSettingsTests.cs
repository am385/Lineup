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
    /// <summary>Verifies experimental compensation is initially off and editing it notifies the form without changing codec policies.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GlobalAudio_Ac4TimestampCompensation_IsIndependentOptIn(bool physical)
    {
        // Arrange
        await using var context = new BunitContext();
        ITranscodeSettingsModel settings = physical ? new PhysicalTranscodeSettingsModel() : new VirtualTranscodeSettingsModel();
        settings.Ac4Audio = OutputAudioPolicy.Copy;
        var notifications = 0;
        var component = context.Render<GlobalTranscoderSettings>(parameters => parameters
            .Add(editor => editor.Settings, settings)
            .Add(editor => editor.IdPrefix, "global")
            .Add(editor => editor.CssPrefix, "global")
            .Add(editor => editor.StageName, physical ? "Physical" : "Virtual")
            .Add(editor => editor.SettingsChanged, () => notifications++));
        var initiallyChecked = component.Find("#globalAc4TimestampCompensation").HasAttribute("checked");

        // Act
        await component.Find("#globalAc4TimestampCompensation").ChangeAsync(true);

        // Assert
        Assert.False(initiallyChecked);
        Assert.True(settings.Ac4TimestampCompensation);
        Assert.Equal(1, notifications);
        Assert.Equal(OutputAudioPolicy.Copy, settings.Ac4Audio);
        Assert.Equal(OutputAudioPolicy.Copy, settings.Audio);
        Assert.Contains("experimental", component.Markup, StringComparison.Ordinal);
        Assert.Contains("48 kHz with this option on or off", component.Markup, StringComparison.Ordinal);
        Assert.Empty(component.FindAll("[role='alert']"));
    }

    /// <summary>Verifies a device can disable globally enabled compensation and return to inheritance without overriding AC-4 codec selection.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeviceAudio_Ac4TimestampCompensation_CanOverrideAndInherit(bool physical)
    {
        // Arrange
        await using var context = new BunitContext();
        ITranscodeSettingsModel defaults = physical ? new PhysicalTranscodeSettingsModel() : new VirtualTranscodeSettingsModel();
        ITranscodeOverrideSettingsModel settings = physical ? new PhysicalDeviceSettingsModel() : new VirtualDeviceSettingsModel();
        defaults.Ac4TimestampCompensation = true;
        defaults.Ac4Audio = OutputAudioPolicy.Copy;
        settings.Ac4TimestampCompensation = true;
        var component = context.Render<DeviceTranscoderSettings>(parameters => parameters
            .Add(editor => editor.Settings, settings)
            .Add(editor => editor.Defaults, defaults)
            .Add(editor => editor.IdPrefix, "device")
            .Add(editor => editor.CssPrefix, "device")
            .Add(editor => editor.StageName, physical ? "Physical" : "Virtual"));

        // Act
        await component.Find("#deviceOverrideAc4TimestampCompensation-0").ChangeAsync(true);
        await component.Find("#deviceAc4TimestampCompensation-0").ChangeAsync(false);

        // Assert
        Assert.True(settings.OverrideAc4TimestampCompensation);
        Assert.False(settings.Ac4TimestampCompensation);
        Assert.False(settings.OverrideAc4Audio);
        Assert.Empty(component.FindAll("[role='alert']"));

        // Act
        await component.Find("#deviceOverrideAc4TimestampCompensation-0").ChangeAsync(false);

        // Assert
        Assert.False(settings.OverrideAc4TimestampCompensation);
        Assert.Empty(component.FindAll("#deviceAc4TimestampCompensation-0"));
        Assert.Contains("inherits True", component.Find("label[for='deviceOverrideAc4TimestampCompensation-0']").TextContent, StringComparison.Ordinal);
    }

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

    /// <summary>Verifies named candidates write their exact backend IDs while Default and Custom retain existing editing behavior.</summary>
    [Theory]
    [InlineData("3")]
    [InlineData("/dev/dri/renderD130")]
    public async Task HardwareDevicePicker_SelectingDevices_PreservesNativeIdsAndManualEntry(string device)
    {
        // Arrange
        await using var context = new BunitContext();
        var discovery = Substitute.For<IHardwareEncoderDeviceDiscovery>();
        discovery.GetDevicesAsync(HardwareVideoEncoder.Qsv).Returns(new HardwareEncoderDeviceSnapshot([new(device, "Detected GPU")]));
        context.Services.AddSingleton(discovery);
        var settings = new PhysicalTranscodeSettingsModel { HardwareEncoding = new() { Encoder = HardwareVideoEncoder.Qsv, Device = "7" } };
        var component = context.Render<GlobalTranscoderSettings>(parameters => parameters
            .Add(editor => editor.Settings, settings)
            .Add(editor => editor.IdPrefix, "picker")
            .Add(editor => editor.CssPrefix, "picker")
            .Add(editor => editor.StageName, "Physical"));

        // Act
        var savedDevice = component.Find("#pickerCustomDevice").GetAttribute("value");
        await component.Find("#pickerDevice").ChangeAsync(device);

        // Assert
        Assert.Equal("7", savedDevice);
        Assert.Equal(device, settings.HardwareEncoding.Device);
        Assert.Empty(component.FindAll("#pickerCustomDevice"));
        Assert.Contains("Detected GPU", component.Find("#pickerDevice").TextContent, StringComparison.Ordinal);

        // Act
        await component.Find("#pickerDevice").ChangeAsync("custom");
        await component.Find("#pickerCustomDevice").ChangeAsync("9");

        // Assert
        Assert.Equal("9", settings.HardwareEncoding.Device);

        // Act
        await component.Find("#pickerDevice").ChangeAsync(string.Empty);

        // Assert
        Assert.Empty(settings.HardwareEncoding.Device);
        Assert.Empty(component.FindAll("#pickerCustomDevice"));
    }

    /// <summary>Verifies empty discovery and explicit failures never erase a saved device or prevent custom editing.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HardwareDevicePicker_UnavailableCandidates_PreservesSavedConfiguration(bool discoveryFailed)
    {
        // Arrange
        await using var context = new BunitContext();
        var discovery = Substitute.For<IHardwareEncoderDeviceDiscovery>();
        discovery.GetDevicesAsync(HardwareVideoEncoder.Amf).Returns(new HardwareEncoderDeviceSnapshot([], discoveryFailed));
        context.Services.AddSingleton(discovery);
        HardwareEncodingSettings? changed = null;
        var component = context.Render<HardwareEncoderSettings>(parameters => parameters
            .Add(editor => editor.IdPrefix, "picker")
            .Add(editor => editor.Value, new() { Encoder = HardwareVideoEncoder.Amf, Device = "8" })
            .Add(editor => editor.ValueChanged, value => changed = value));

        // Act
        var savedDevice = component.Find("#pickerCustomDevice").GetAttribute("value");
        var status = component.Find("[role='status']").TextContent;
        await component.Find("#pickerCustomDevice").ChangeAsync("9");

        // Assert
        Assert.Equal("8", savedDevice);
        Assert.Contains(discoveryFailed ? "GPU discovery failed" : "No matching GPU", status, StringComparison.Ordinal);
        Assert.Equal("9", Assert.IsType<HardwareEncodingSettings>(changed).Device);
        Assert.Equal(2, component.FindAll("#pickerDevice option").Count);
    }

    /// <summary>Verifies a late response from an earlier backend cannot overwrite the currently selected backend's candidates.</summary>
    [Fact]
    public async Task HardwareDevicePicker_BackendChanges_IgnoresPreviousDiscovery()
    {
        // Arrange
        await using var context = new BunitContext();
        var first = new TaskCompletionSource<HardwareEncoderDeviceSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<HardwareEncoderDeviceSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var discovery = Substitute.For<IHardwareEncoderDeviceDiscovery>();
        discovery.GetDevicesAsync(HardwareVideoEncoder.Nvenc).Returns(first.Task);
        discovery.GetDevicesAsync(HardwareVideoEncoder.Amf).Returns(second.Task);
        context.Services.AddSingleton(discovery);
        var component = context.Render<HardwareEncoderSettings>(parameters => parameters
            .Add(editor => editor.IdPrefix, "picker")
            .Add(editor => editor.Value, new() { Encoder = HardwareVideoEncoder.Nvenc }));
        try
        {
            // Act
            component.Render(parameters => parameters.Add(editor => editor.Value, new() { Encoder = HardwareVideoEncoder.Amf }));
            second.SetResult(new([new("4", "Current AMD")]));
            await component.WaitForAssertionAsync(() => Assert.Contains("Current AMD", component.Markup, StringComparison.Ordinal), BunitContext.WaitTimeout);
            var renders = component.RenderCount;
            first.SetResult(new([new("0", "Previous NVIDIA")]));
            await component.WaitForAssertionAsync(() => Assert.True(component.RenderCount > renders), BunitContext.WaitTimeout);

            // Assert
            Assert.Contains("Current AMD", component.Markup, StringComparison.Ordinal);
            Assert.DoesNotContain("Previous NVIDIA", component.Markup, StringComparison.Ordinal);
            Assert.False(component.Find("#pickerDevice").HasAttribute("disabled"));
        }
        finally
        {
            first.TrySetResult(new([]));
            second.TrySetResult(new([]));
        }
    }

    /// <summary>Verifies VideoToolbox uses the system device rather than offering meaningless GPU indices.</summary>
    [Fact]
    public async Task HardwareDevicePicker_VideoToolbox_OffersOnlySystemDefault()
    {
        // Arrange
        await using var context = new BunitContext();

        // Act
        var component = context.Render<HardwareEncoderSettings>(parameters => parameters
            .Add(editor => editor.IdPrefix, "picker")
            .Add(editor => editor.Value, new() { Encoder = HardwareVideoEncoder.VideoToolbox }));

        // Assert
        Assert.Single(component.FindAll("#pickerDevice option"));
        Assert.Empty(component.FindAll("#pickerCustomDevice"));
        Assert.Empty(component.FindAll("[role='status']"));
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
