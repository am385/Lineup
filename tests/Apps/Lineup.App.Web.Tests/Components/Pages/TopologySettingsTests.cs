using Bunit;
using System.Globalization;
using System.Text.Json;
using Lineup.Plugin.GuideOutputs.Json.Plugin;
using Lineup.Plugin.GuideOutputs.Xmltv.Plugin;
using Lineup.Plugin.LineupLink.Protocol;
using Lineup.Plugin.LineupLink.Virtual;
using Lineup.App.Web.Components.Transcoding;
using Lineup.App.Web.Components.Pages;
using Lineup.App.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NSubstitute;
using Xunit;

namespace Lineup.App.Web.Tests.Components.Pages;

/// <summary>
/// Verifies topology-specific Settings presentation.
/// </summary>
public sealed class TopologySettingsTests
{
    /// <summary>Verifies shared saves cannot overwrite a pending theme owned by the independent Web settings store.</summary>
    [Theory]
    [InlineData("light", "themeLight")]
    [InlineData("auto", "themeAuto")]
    public async Task Save_ThemeChange_WithIndependentSettings_PreservesSelection(string theme, string selector)
    {
        // Arrange
        await using var context = CreateContext(out var settingsService, out var settings);
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var webSettings = context.Services.GetRequiredService<IWebSettingsService>().Settings;
        webSettings.Theme = "dark";
        var component = context.Render<Settings>();
        await component.Find($"#{selector}").ChangeAsync(true);
        await component.Find("#tab-guide").ClickAsync(new());
        await component.Find("#epgHistoryRetentionHours").ChangeAsync(48);

        // Act
        await component.InvokeAsync(() => settingsService.UpdateAsync(current => current.NextAutoFetchTime = DateTime.UtcNow.AddDays(1)));
        Assert.True(component.Find($"#{selector}").HasAttribute("checked"));
        await component.Find("button.btn-primary").ClickAsync(new());

        // Assert
        Assert.Equal(theme, webSettings.Theme);
        Assert.True(component.Find($"#{selector}").HasAttribute("checked"));
        Assert.Equal(48, settings.EpgHistoryRetentionHours);
        Assert.Contains(context.JSInterop.Invocations, invocation => invocation.Identifier == "setTheme" && Equals(invocation.Arguments[0], theme));
    }

    /// <summary>Verifies external Web theme changes update the open form without reloading unsaved shared settings.</summary>
    [Fact]
    public async Task WebSettingsChanged_ExternalThemeChange_PreservesOtherEdits()
    {
        // Arrange
        await using var context = CreateContext(out _, out var settings);
        var webSettings = context.Services.GetRequiredService<IWebSettingsService>();
        var component = context.Render<Settings>();
        await component.Find("#tab-guide").ClickAsync(new());
        await component.Find("#epgHistoryRetentionHours").ChangeAsync(48);

        // Act
        await component.InvokeAsync(() => webSettings.UpdateAsync(current => current.Theme = "dark"));

        // Assert
        Assert.True(component.Find("#themeDark").HasAttribute("checked"));
        Assert.Equal("48", component.Find("#epgHistoryRetentionHours").GetAttribute("value"));
        Assert.NotEqual(48, settings.EpgHistoryRetentionHours);
    }

    /// <summary>Verifies the theme handler is owned by, and unsubscribed from, the Web settings event.</summary>
    [Fact]
    public async Task Dispose_IndependentSettings_UnsubscribesWebThemeHandler()
    {
        // Arrange
        await using var context = CreateContext(out var settingsService, out _);
        var webSettings = context.Services.GetRequiredService<IWebSettingsService>();
        var component = context.Render<Settings>();

        // Act
        component.Instance.Dispose();

        // Assert
        webSettings.Received(1).OnSettingsChanged += Arg.Any<Action>();
        webSettings.Received(1).OnSettingsChanged -= Arg.Any<Action>();
        settingsService.DidNotReceive().OnSettingsChanged += Arg.Any<Action>();
    }

    /// <summary>Verifies theme cleanup does not resolve dependencies after the prerender request's service scope has been disposed.</summary>
    [Fact]
    public async Task Dispose_ExpiredServiceScope_UnsubscribesCapturedWebSettings()
    {
        // Arrange
        await using var context = CreateContext(out _, out _);
        var webSettings = context.Services.GetRequiredService<IWebSettingsService>();
        var component = context.Render<Settings>();
        using var expired = new ServiceCollection().BuildServiceProvider();
        var property = Assert.IsAssignableFrom<System.Reflection.PropertyInfo>(
            typeof(Settings).GetProperty("Services", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic));
        property.SetValue(component.Instance, expired);
        expired.Dispose();

        // Act
        Exception? failure;
        try
        {
            failure = Record.Exception(component.Instance.Dispose);
        }
        finally
        {
            property.SetValue(component.Instance, context.Services);
        }

        // Assert
        Assert.Null(failure);
        webSettings.Received(1).OnSettingsChanged -= Arg.Any<Action>();
    }

    /// <summary>Verifies reset and save retain the configured built-in publication destination rather than the editor's default filename.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResetDefaults_ConfiguredXmltvPath_PreservesTopologyAndPublication(bool customPath)
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-settings-reset-");
        try
        {
            await using var context = CreateContext(out var settingsService, out var settings);
            context.JSInterop.Mode = JSRuntimeMode.Loose;
            var configuredPath = customPath ? Path.Combine(root.FullName, "configured.xml") : "xmltv.xml";
            settingsService.ConfiguredXmltvOutputPath.Returns(configuredPath);
            settings.XmltvOutputPath = Path.Combine(root.FullName, "previous.xml");
            var original = settings.Streaming;
            var component = context.Render<Settings>();
            await component.Find("#tab-reset").ClickAsync(new());

            // Act
            await component.Find("#resetSettings").ClickAsync(new());
            var edited = Assert.Single(component.Instance.Topology.GuideOutputs, output => output.IsBuiltIn);
            Assert.Equal(configuredPath, edited.OutputPath);
            Assert.Equal(configuredPath, component.Instance.XmltvOutputPath);
            Assert.Same(original, settings.Streaming);
            await component.Find("button.btn-primary").ClickAsync(new());

            // Assert
            Assert.Equal(configuredPath, settings.XmltvOutputPath);
            var builtIn = Assert.Single(settings.Streaming.GuideOutputs, output => output.IsBuiltIn);
            var destination = new XmltvGuideOutputDriver().ResolveDestination(builtIn.Adapter);
            Assert.Equal(Path.GetFullPath(configuredPath), destination.Path);
            Assert.True(builtIn.Enabled);
            Assert.Equal(new LineupSettings().AutoGenerateXmltv, settings.AutoGenerateXmltv);
            Assert.Single(settings.Streaming.VirtualDevices);
            if (customPath)
            {
                var publications = new XmltvPublicationStore();
                await publications.PublishAsync(destination.Path, async (stream, token) =>
                {
                    await using var writer = new StreamWriter(stream, leaveOpen: true);
                    await writer.WriteAsync("<tv />".AsMemory(), token);
                    await writer.FlushAsync(token);
                }, Xunit.TestContext.Current.CancellationToken);
                Assert.Equal("<tv />", await File.ReadAllTextAsync(configuredPath, Xunit.TestContext.Current.CancellationToken));
            }
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>Verifies malformed listener addresses stay editable, reject persistence, and can be corrected without losing unrelated changes.</summary>
    [Theory]
    [InlineData("bad host")]
    [InlineData("http://192.0.2.1")]
    [InlineData("192.0.2.1:9600")]
    [InlineData("[::1")]
    public async Task Save_InvalidListenerAddress_PreservesEditsAndAllowsCorrection(string address)
    {
        // Arrange
        await using var context = CreateContext(out var settingsService, out var settings);
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var original = settings.Streaming;
        var component = context.Render<Settings>();
        await component.Find("#tab-virtual-devices").ClickAsync(new());
        await component.Find(".virtual-device-tab").ClickAsync(new());
        await component.Find("#dedicatedListener-0").ChangeAsync(true);
        await component.Find("#workerRestarts-0").ChangeAsync(2);

        // Act
        await component.Find("#listenerAddresses-0").ChangeAsync(address);
        await component.Find("button.btn-primary").ClickAsync(new());

        // Assert
        Assert.Equal(address, component.Find("#listenerAddresses-0").GetAttribute("value"));
        Assert.Equal("2", component.Find("#workerRestarts-0").GetAttribute("value"));
        Assert.True(string.IsNullOrEmpty(component.Find("#listenerClientUrl-0").GetAttribute("value")));
        Assert.Same(original, settings.Streaming);
        await settingsService.DidNotReceive().UpdateAsync(Arg.Any<Action<LineupSettings>>());
        Assert.Contains(context.Services.GetRequiredService<IStatusNotificationService>().Notifications, notification => notification.IsError);

        // Act
        await component.Find("#listenerAddresses-0").ChangeAsync("192.0.2.10");
        await component.Find("button.btn-primary").ClickAsync(new());

        // Assert
        var hosting = Assert.IsType<VirtualOutputHostingDefinition>(Assert.Single(settings.Streaming.VirtualDevices).Hosting);
        Assert.Equal("192.0.2.10", Assert.Single(Assert.Single(hosting.Bindings).Addresses));
        Assert.Equal(2, Assert.Single(settings.Streaming.VirtualOutputWorkerPolicies, policy => policy.Key == hosting.WorkerPolicyKey).MaximumRestarts);
        await settingsService.Received(1).UpdateAsync(Arg.Any<Action<LineupSettings>>());
    }

    /// <summary>Verifies the deployment-wide limit is editable in Streaming settings and persists independently of listener policies.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    public async Task StreamingPluginConnectionLimit_Save_PersistsGlobalSetting(int maximum)
    {
        // Arrange
        await using var context = CreateContext(out var settingsService, out var settings);
        settings.Streaming = settings.Streaming with { MaximumPluginConnections = 4 };
        var component = context.Render<Settings>();
        await component.Find("#tab-streaming").ClickAsync(new());
        Assert.Equal("4", component.Find("#maximumPluginConnections").GetAttribute("value"));
        await component.Find("#maximumPluginConnections").ChangeAsync(maximum);

        // Act
        await component.Find("button.btn-primary").ClickAsync(new());

        // Assert
        Assert.Equal(maximum, settings.Streaming.MaximumPluginConnections);
        await settingsService.Received(1).UpdateAsync(Arg.Any<Action<LineupSettings>>());
    }

    /// <summary>Verifies exported, copied, displayed, and reloaded dedicated bundles use the same effective URL as saved listener bindings.</summary>
    [Theory]
    [InlineData(VirtualOutputTransport.Http, "192.0.2.10", null, false, "", "http://192.0.2.10:9600/")]
    [InlineData(VirtualOutputTransport.Https, "192.0.2.20", 443, false, "", "https://192.0.2.20/")]
    [InlineData(VirtualOutputTransport.Https, "192.0.2.20", 9443, false, "", "https://192.0.2.20:9443/")]
    [InlineData(VirtualOutputTransport.Http, "*", 9800, false, "", "http://localhost:9800/")]
    [InlineData(VirtualOutputTransport.Http, "2001:db8::10", null, false, "", "http://[2001:db8::10]:9600/")]
    [InlineData(VirtualOutputTransport.Http, "localhost", null, false, "http://stale.example:8600/", "http://localhost:9600/")]
    [InlineData(VirtualOutputTransport.Https, "localhost", 443, true, "https://public.example:9443/", "https://public.example/")]
    [InlineData(VirtualOutputTransport.Http, "localhost", 443, true, "https://public.example:9443/", "https://public.example/")]
    [InlineData(VirtualOutputTransport.Http, "localhost", null, true, "http://public.example:9443/", "http://public.example:9443/")]
    public async Task LineupLinkBundle_DedicatedListener_UsesEffectiveAddress(VirtualOutputTransport transport, string addresses, int? publishedPort, bool manualOverride, string manualUri, string expected)
    {
        // Arrange
        await using var context = CreateContext(out var settingsService, out var settings);
        context.Services.AddSingleton<IVirtualTunerDriver, LineupLinkVirtualTunerDriver>();
        context.JSInterop.SetupVoid("downloadTextFile", _ => true).SetVoidResult();
        context.JSInterop.SetupVoid("copyTextToClipboard", _ => true).SetVoidResult();
        var component = context.Render<Settings>();
        await component.Find("#tab-virtual-devices").ClickAsync(new());
        await component.Find("#addVirtualDevice").ClickAsync(new());
        await component.Find("[data-virtual-device-kind='lineup-link']").ClickAsync(new());
        await component.Find("#dedicatedListener-1").ChangeAsync(true);
        await component.Find("#listenerTransport-1").ChangeAsync(transport);
        await component.Find("#listenerAddresses-1").ChangeAsync(addresses);
        await component.Find("#listenerPort-1").ChangeAsync(9600);
        if (publishedPort.HasValue)
        {
            await component.Find("#publishedPort-1").ChangeAsync(publishedPort.Value);
        }
        if (transport == VirtualOutputTransport.Https)
        {
            await component.Find("#listenerCertificate-1").ChangeAsync("test-certificate");
        }
        var device = component.Instance.Topology.VirtualDevices.Single(candidate => candidate.IsLineupLink);
        device.PublicBaseUri = manualUri;
        if (manualOverride)
        {
            await component.Find("#listenerPublicUriOverride-1").ChangeAsync(true);
            await component.Find("#listenerPublicUri-1").ChangeAsync(manualUri);
        }
        var requireEncryption = expected.StartsWith("https:", StringComparison.Ordinal);
        if (requireEncryption)
        {
            await component.Find("#linkServerCertificate-1").ChangeAsync(new string('A', 64));
        }
        await component.Find("#newLineupLinkGrantName-1").ChangeAsync("Effective address");
        await component.Find("#createLineupLinkGrant-1").ClickAsync(new());

        // Act
        await component.Find(".lineup-link-access-grant button[id^='exportLineupLinkBundle-']").ClickAsync(new());
        await component.Find(".lineup-link-access-grant button[id^='copyLineupLinkBundle-']").ClickAsync(new());
        await component.Find(".lineup-link-access-grant button[id^='showLineupLinkBundle-']").ClickAsync(new());

        // Assert
        var download = Assert.Single(context.JSInterop.Invocations, invocation => invocation.Identifier == "downloadTextFile");
        var copy = Assert.Single(context.JSInterop.Invocations, invocation => invocation.Identifier == "copyTextToClipboard");
        var displayed = component.Find(".lineup-link-access-grant pre.connection-bundle-json code").TextContent;
        var expectedSecurity = requireEncryption ? LineupLinkTransportSecurity.Https : LineupLinkTransportSecurity.Http;
        foreach (var json in new[] { Assert.IsType<string>(download.Arguments[2]), Assert.IsType<string>(copy.Arguments[0]), displayed })
        {
            var bundle = LineupLinkConnectionBundleCodec.Deserialize(json);
            Assert.Equal(expected, bundle.BaseUrl);
            Assert.Equal(device.Id.Value, bundle.VirtualDeviceId);
            Assert.Equal(expectedSecurity, bundle.TransportSecurity);
            Assert.Equal(requireEncryption ? new string('A', 64) : null, bundle.CertificateSha256);
            Assert.NotEqual(LineupLinkConnectionBundleCodec.RedactedAccessToken, bundle.AccessToken);
        }
        Assert.Equal(expected, component.Find("#listenerClientUrl-1").GetAttribute("value"));

        // Act
        await component.Find("button.btn-primary").ClickAsync(new());
        var reloaded = context.Render<Settings>();
        await reloaded.Find("#tab-virtual-devices").ClickAsync(new());
        await reloaded.FindAll(".virtual-device-tab").Single(tab => tab.TextContent.Contains("Lineup Link", StringComparison.Ordinal)).ClickAsync(new());
        await reloaded.Find(".lineup-link-access-grant button[id^='showLineupLinkBundle-']").ClickAsync(new());

        // Assert
        await settingsService.Received(1).UpdateAsync(Arg.Any<Action<LineupSettings>>());
        var saved = settings.Streaming.VirtualDevices.Single(candidate => candidate.Id == device.Id);
        Assert.Equal(expected, Assert.Single(Assert.IsType<VirtualOutputHostingDefinition>(saved.Hosting).Bindings).PublicBaseUri);
        using var redacted = JsonDocument.Parse(reloaded.Find(".lineup-link-access-grant pre.connection-bundle-json code").TextContent);
        Assert.Equal(expected, redacted.RootElement.GetProperty("baseUrl").GetString());
        Assert.Equal(LineupLinkConnectionBundleCodec.RedactedAccessToken, redacted.RootElement.GetProperty("accessToken").GetString());
        Assert.True(reloaded.Find(".lineup-link-access-grant button[id^='copyLineupLinkBundle-']").HasAttribute("disabled"));
        Assert.True(reloaded.Find(".lineup-link-access-grant button[id^='exportLineupLinkBundle-']").HasAttribute("disabled"));
    }

    /// <summary>Verifies invalid listener-port edits remain renderable, fail Save without persistence, and can be corrected without losing other edits.</summary>
    [Theory]
    [InlineData("web-player", false)]
    [InlineData("web-player", true)]
    [InlineData("hdhomerun", false)]
    [InlineData("hdhomerun", true)]
    [InlineData("lineup-link", false)]
    [InlineData("lineup-link", true)]
    public async Task Save_ListenerPortEditing_InvalidPort_PreservesEditsAndAllowsCorrection(string kind, bool manualOverride)
    {
        // Arrange
        await using var context = CreateContext(out var settingsService, out var settings);
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddSingleton<IVirtualTunerDriver, LineupLinkVirtualTunerDriver>();
        var component = context.Render<Settings>();
        var initialConfiguration = settings.Streaming;
        await component.Find("#tab-virtual-devices").ClickAsync(new());
        if (kind == "web-player")
        {
            await component.Find(".virtual-device-tab").ClickAsync(new());
        }
        else
        {
            await component.Find("#addVirtualDevice").ClickAsync(new());
            await component.Find($"[data-virtual-device-kind='{kind}']").ClickAsync(new());
        }
        var index = kind == "web-player" ? 0 : 1;
        var device = component.Instance.Topology.VirtualDevices.Single(candidate => candidate.Kind == kind);
        await component.Find($"#dedicatedListener-{index}").ChangeAsync(true);
        await component.Find($"#workerRestarts-{index}").ChangeAsync(2);
        if (manualOverride)
        {
            await component.Find($"#listenerPublicUriOverride-{index}").ChangeAsync(true);
            await component.Find($"#listenerPublicUri-{index}").ChangeAsync("http://public.example:9600/");
        }

        // Act
        await component.Find($"#listenerPort-{index}").ChangeAsync(65_536);
        await component.Find("button.btn-primary").ClickAsync(new());

        // Assert
        Assert.Equal("65536", component.Find($"#listenerPort-{index}").GetAttribute("value"));
        Assert.Equal("2", component.Find($"#workerRestarts-{index}").GetAttribute("value"));
        Assert.Equal(manualOverride ? "http://public.example:9600/" : string.Empty, component.Find($"#listenerClientUrl-{index}").GetAttribute("value") ?? string.Empty);
        Assert.Equal(manualOverride, device.OverridePublicBaseUri);
        if (manualOverride)
        {
            Assert.Equal("http://public.example:9600/", component.Find($"#listenerPublicUri-{index}").GetAttribute("value"));
            Assert.Equal(string.Empty, component.Find($"#listenerPublicUri-{index}").GetAttribute("placeholder") ?? string.Empty);
        }
        Assert.Same(initialConfiguration, settings.Streaming);
        await settingsService.DidNotReceive().UpdateAsync(Arg.Any<Action<LineupSettings>>());
        var notifications = context.Services.GetRequiredService<IStatusNotificationService>();
        var error = Assert.Single(notifications.Notifications);
        Assert.True(error.IsError);
        Assert.Contains($"Virtual device {device.Id} output binding port must be between 1 and 65535.", error.Message, StringComparison.Ordinal);

        // Act
        await component.Find($"#listenerPort-{index}").ChangeAsync(9800);
        var expectedUri = manualOverride ? "http://public.example:9600/" : "http://localhost:9800/";
        Assert.Equal(expectedUri, component.Find($"#listenerClientUrl-{index}").GetAttribute("value"));
        await component.Find("button.btn-primary").ClickAsync(new());

        // Assert
        var saved = settings.Streaming.VirtualDevices.Single(candidate => candidate.Id == device.Id);
        var hosting = Assert.IsType<VirtualOutputHostingDefinition>(saved.Hosting);
        var binding = Assert.Single(hosting.Bindings);
        Assert.Equal(9800, binding.Port);
        Assert.Equal(expectedUri, binding.PublicBaseUri);
        Assert.Equal(2, Assert.Single(settings.Streaming.VirtualOutputWorkerPolicies, policy => policy.Key == hosting.WorkerPolicyKey).MaximumRestarts);
        Assert.Contains(notifications.Notifications, notification => !notification.IsError);
        await settingsService.Received(1).UpdateAsync(Arg.Any<Action<LineupSettings>>());
    }

    /// <summary>
    /// Verifies empty topology collections direct the user to the corresponding add action.
    /// </summary>
    [Fact]
    public async Task Render_EmptyTopology_ShowsAddGuidance()
    {
        // Arrange
        await using var context = CreateContext(out _, out _);
        var component = context.Render<Settings>();

        // Act
        await component.Find("#tab-guide").ClickAsync(new());

        // Assert
        Assert.Contains("No guide sources are configured", component.Find("#guideSourcesEmptyState").TextContent);

        await component.Find("#tab-physical-devices").ClickAsync(new());

        Assert.Contains("No physical devices are configured", component.Find("#physicalDevicesEmptyState").TextContent);

        await component.Find("#tab-virtual-devices").ClickAsync(new());

        Assert.Contains("No additional virtual devices are configured", component.Find("#virtualDevicesEmptyState").TextContent);
        Assert.Contains("built-in Web Player remains available", component.Find("#virtualDevicesEmptyState").TextContent);
    }

    /// <summary>
    /// Verifies empty virtual-device guidance does not claim that a disabled Web Player is available.
    /// </summary>
    [Fact]
    public async Task Render_EmptyVirtualDevicesWithDisabledWebPlayer_ShowsEnableGuidance()
    {
        // Arrange
        await using var context = CreateContext(out _, out var settings);
        var editor = new TopologySettingsEditor();
        var model = editor.Edit(settings.Streaming);
        model.VirtualDevices.Single(device => device.IsWebPlayer).Enabled = false;
        Assert.Empty(editor.TryBuild(model, out var configuration));
        settings.Streaming = configuration;

        // Act
        var component = context.Render<Settings>();
        await component.Find("#tab-virtual-devices").ClickAsync(new());

        // Assert
        var message = component.Find("#virtualDevicesEmptyState").TextContent;
        Assert.Contains("Enable the built-in Web Player", message);
        Assert.DoesNotContain("Web Player remains available", message);
    }

    /// <summary>
    /// Verifies global physical settings appear first and inherited values remain visible on each device.
    /// </summary>
    [Fact]
    public async Task Render_Topology_ShowsBuiltInPlayerAndInheritedPhysicalValue()
    {
        // Arrange
        await using var context = CreateContext(out _, out var settings);
        var editor = new TopologySettingsEditor();
        var model = editor.Edit(settings.Streaming);
        model.PhysicalDefaults.ConvertHevcToH264 = true;
        model.VirtualDefaults.ConvertHevcToH264 = true;
        model.PhysicalDefaults.MaximumVideoBitRateMbps = 17;
        editor.AddPhysicalDevice(model, new TestPhysicalTunerDriver());
        model.PhysicalDevices[0].Name = "Living room";
        model.PhysicalDevices[0].Address = "tuner.local";
        Assert.Empty(editor.TryBuild(model, out var configuration));
        settings.Streaming = configuration with
        {
            VirtualDevices = [configuration.VirtualDevices.Single() with { Name = WebPlayerVirtualTunerDriver.BuiltInName }]
        };

        // Act
        var component = context.Render<Settings>();
        await component.Find("#tab-physical-devices").ClickAsync(new());

        // Assert
        Assert.Equal(2, component.FindComponents<GlobalTranscoderSettings>().Count);
        Assert.Empty(component.FindComponents<DeviceTranscoderSettings>());
        Assert.Contains("active", component.Find("#physicalSubtabGlobal").ClassList);
        Assert.Single(component.FindAll(".physical-device-tab"));
        Assert.Contains("Living room", component.Find(".physical-device-tab").TextContent);
        Assert.Equal(2, component.FindAll("#physicalGlobalSettings > .card").Count);
        Assert.Contains("Refresh Settings", component.Find("#physicalDeviceRefreshSettings").TextContent);
        Assert.Contains("Transcoder Settings", component.Find("#physicalTranscoderSettings").TextContent);
        Assert.Contains("Video", component.Find(".physical-global-video-settings").TextContent);
        Assert.Contains("Audio", component.Find(".physical-global-audio-settings").TextContent);
        Assert.NotNull(component.Find("#physicalDefaultHevc"));
        Assert.NotNull(component.Find("#physicalDefaultDeinterlace"));
        Assert.NotNull(component.Find("#physicalDefaultAudio"));
        Assert.NotNull(component.Find("#physicalDefaultAc4Audio"));
        Assert.NotNull(component.Find("#physicalDefaultPreset"));
        Assert.NotNull(component.Find("#physicalDefaultQuality"));
        Assert.NotNull(component.Find("#physicalDefaultBitrate"));
        Assert.Contains("Source field rate", component.Find("#physicalTranscoderSettings").TextContent);
        Assert.Contains("Copy preserves the original codec and quality", component.Find("#physicalTranscoderSettings").TextContent);
        Assert.Empty(component.FindAll("#deviceAddress"));
        Assert.DoesNotContain("Device Settings", component.Find("#physicalDevicesSettingsTab").TextContent);
        Assert.DoesNotContain("d-none", component.Find("#physicalDevicesSettingsTab").ClassList);
        Assert.Contains("d-none", component.Find("#virtualDevicesSettingsTab").ClassList);

        await component.Find(".physical-device-tab").ClickAsync(new());

        Assert.Single(component.FindComponents<GlobalTranscoderSettings>());
        Assert.Single(component.FindComponents<DeviceTranscoderSettings>());
        Assert.Equal(2, component.FindAll(".physical-device > .card").Count);
        Assert.Contains("Device Settings", component.Find(".physical-device-settings").TextContent);
        Assert.Contains("Transcoder Settings", component.Find(".physical-device-transcoder-settings").TextContent);
        Assert.Contains("inherits 17 Mbps", component.Find(".physical-device").TextContent);
        Assert.Contains("Uses Global Settings unless an override is enabled.", component.Find(".physical-device").TextContent);
        Assert.Contains("Video", component.Find(".physical-device-video-settings").TextContent);
        Assert.Contains("Audio", component.Find(".physical-device-audio-settings").TextContent);
        Assert.NotNull(component.Find("#physicalOverrideHevc-0"));
        Assert.NotNull(component.Find("#physicalOverrideDeinterlace-0"));
        Assert.NotNull(component.Find("#physicalOverrideAudio-0"));
        Assert.NotNull(component.Find("#physicalOverrideAc4Audio-0"));
        Assert.NotNull(component.Find("#physicalOverridePreset-0"));
        Assert.NotNull(component.Find("#physicalOverrideQuality-0"));
        Assert.NotNull(component.Find("#physicalOverrideBitrate-0"));
        Assert.Contains("Enable the override to choose whether this device converts", component.Find(".physical-device-transcoder-settings").TextContent);
        Assert.Contains("Enable the override to handle AC-4 sources independently", component.Find(".physical-device-transcoder-settings").TextContent);
        Assert.Contains("HDHomeRun", component.Find("#aboutSettingsBody").TextContent);
        Assert.Contains("must be reachable", component.Find("#aboutSettingsBody").TextContent);
        Assert.Contains("Transcoder Settings", component.Find("#aboutSettingsBody").TextContent);
        Assert.Contains("continue to inherit", component.Find("#aboutSettingsBody").TextContent);

        await component.Find("#tab-virtual-devices").ClickAsync(new());

        Assert.Contains("d-none", component.Find("#physicalDevicesSettingsTab").ClassList);
        Assert.DoesNotContain("d-none", component.Find("#virtualDevicesSettingsTab").ClassList);
        Assert.Contains("active", component.Find("#virtualSubtabGlobal").ClassList);
        Assert.Single(component.FindAll(".virtual-device-tab"));
        Assert.Contains("Web Player", component.Find(".virtual-device-tab").TextContent);
        Assert.Equal(2, component.FindAll("#virtualGlobalSettings > .card").Count);
        Assert.Contains("Transcoder Settings", component.Find("#virtualTranscoderSettings").TextContent);
        Assert.Contains("Virtual Tuner Discovery", component.Find("#networkDiscoverySettings").TextContent);
        Assert.NotNull(component.Find("#virtualDefaultHevc"));
        Assert.NotNull(component.Find("#virtualDefaultDeinterlace"));
        Assert.NotNull(component.Find("#virtualDefaultAudio"));
        Assert.NotNull(component.Find("#virtualDefaultAc4Audio"));
        Assert.NotNull(component.Find("#virtualDefaultPreset"));
        Assert.NotNull(component.Find("#virtualDefaultQuality"));
        Assert.NotNull(component.Find("#virtualDefaultBitrate"));
        Assert.Contains("Source field rate", component.Find("#virtualTranscoderSettings").TextContent);
        Assert.Contains("AC-3 is the broadest compatibility option", component.Find("#virtualTranscoderSettings").TextContent);

        await component.Find(".virtual-device-tab").ClickAsync(new());

        Assert.NotNull(component.Find("#virtualOverrideHevc-0"));
        Assert.NotNull(component.Find("#virtualOverrideDeinterlace-0"));
        Assert.NotNull(component.Find("#virtualOverrideAudio-0"));
        Assert.NotNull(component.Find("#virtualOverrideAc4Audio-0"));
        Assert.NotNull(component.Find("#virtualOverridePreset-0"));
        Assert.NotNull(component.Find("#virtualOverrideQuality-0"));
        Assert.NotNull(component.Find("#virtualOverrideBitrate-0"));
        Assert.Empty(component.FindAll("[data-device-kind='web-player'] .virtual-name"));
        Assert.Equal(3, component.FindAll(".virtual-device > .card").Count);
        Assert.Contains("Output Hosting", component.Find("[data-device-kind='web-player']").TextContent);
        Assert.Contains("Web Player", component.Find("[data-device-kind='web-player'] .text-body-secondary").TextContent);
        Assert.Empty(component.FindAll("[data-device-kind='web-player'] .remove-virtual-device"));
        Assert.Empty(component.FindAll("#streamingSettingsTab #virtualGlobalSettings"));
        Assert.Contains("built-in Web Player", component.Find("#aboutSettingsBody").TextContent);
        Assert.Contains("Watch page", component.Find("#aboutSettingsBody").TextContent);
        Assert.EndsWith("/settings#virtual-devices", context.Services.GetRequiredService<NavigationManager>().Uri, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies the add action discovers registered user-creatable virtual tuner drivers.
    /// </summary>
    [Fact]
    public async Task AddVirtualDevice_RegisteredDrivers_PromptsForTypeAndAddsSelection()
    {
        // Arrange
        await using var context = CreateContext(out _, out _);
        context.Services.AddSingleton<IVirtualTunerDriver, M3uTestVirtualTunerDriver>();
        var component = context.Render<Settings>();
        await component.Find("#tab-virtual-devices").ClickAsync(new());

        // Act
        await component.Find("#addVirtualDevice").ClickAsync(new());

        // Assert
        var picker = component.Find("#virtualDeviceTypePicker");
        var options = picker.QuerySelectorAll(".virtual-device-type");
        Assert.Equal(2, options.Length);
        Assert.Contains(options, option => option.GetAttribute("data-virtual-device-kind") == "hdhomerun");
        var option = Assert.Single(options, option => option.GetAttribute("data-virtual-device-kind") == "m3u");
        Assert.Contains("M3U Provider", option.TextContent);

        await option.ClickAsync(new());

        Assert.Empty(component.FindAll("#virtualDeviceTypePicker"));
        Assert.Single(component.FindAll("[data-device-kind='m3u']"));
        Assert.Single(component.FindAll("[data-device-kind='m3u'] .virtual-name"));
        Assert.Empty(component.FindAll("#virtualDevicesEmptyState"));
    }

    /// <summary>
    /// Verifies exporting a Lineup Link connection bundle downloads the generated JSON.
    /// </summary>
    [Fact]
    public async Task ExportLineupLinkBundle_AvailableToken_DownloadsJsonFile()
    {
        // Arrange
        await using var context = CreateContext(out _, out _);
        context.Services.AddSingleton<IVirtualTunerDriver, LineupLinkVirtualTunerDriver>();
        context.JSInterop.SetupVoid("downloadTextFile", _ => true).SetVoidResult();
        var component = context.Render<Settings>();
        await component.Find("#tab-virtual-devices").ClickAsync(new());
        await component.Find("#addVirtualDevice").ClickAsync(new());
        await component.Find("[data-virtual-device-kind='lineup-link']").ClickAsync(new());
        await component.Find("#linkPublicUrl-1").ChangeAsync("https://lineup.example:8443");
        await component.Find("#newLineupLinkGrantName-1").ChangeAsync("Export access");
        await component.Find("#createLineupLinkGrant-1").ClickAsync(new());

        // Act
        await component.Find(".lineup-link-access-grant button[id^='exportLineupLinkBundle-']").ClickAsync(new());

        // Assert
        component.WaitForAssertion(() =>
        {
            var invocation = Assert.Single(context.JSInterop.Invocations, item => item.Identifier == "downloadTextFile");
            Assert.Matches(@"^lineup-link-[0-9a-f-]{36}-[0-9a-f-]{36}\.json$", Assert.IsType<string>(invocation.Arguments[0]));
            Assert.Equal("application/json", invocation.Arguments[1]);
            using var json = JsonDocument.Parse(Assert.IsType<string>(invocation.Arguments[2]));
            Assert.Equal(1, json.RootElement.GetProperty("bundleVersion").GetInt32());
            Assert.Equal(2, json.RootElement.GetProperty("protocolVersion").GetInt32());
            Assert.Equal("https://lineup.example:8443", json.RootElement.GetProperty("baseUrl").GetString());
            Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("accessToken").GetString()));
        }, timeout: LineupWebBunitContext.WaitTimeout);
        Assert.Empty(component.FindAll(".connection-bundle-json"));
        var showButton = component.Find(".lineup-link-access-grant button[id^='showLineupLinkBundle-']");
        Assert.Equal("false", showButton.GetAttribute("aria-expanded"));
        Assert.Contains("Show bundle", showButton.TextContent);
    }

    /// <summary>Verifies a connection bundle can be exported before a public Lineup URL is assigned.</summary>
    [Fact]
    public async Task ExportLineupLinkBundle_WithoutPublicUrl_UsesNullAddress()
    {
        // Arrange
        await using var context = CreateContext(out _, out _);
        context.Services.AddSingleton<IVirtualTunerDriver, LineupLinkVirtualTunerDriver>();
        context.JSInterop.SetupVoid("downloadTextFile", _ => true).SetVoidResult();
        var component = context.Render<Settings>();
        await component.Find("#tab-virtual-devices").ClickAsync(new());
        await component.Find("#addVirtualDevice").ClickAsync(new());
        await component.Find("[data-virtual-device-kind='lineup-link']").ClickAsync(new());
        await component.Find("#newLineupLinkGrantName-1").ChangeAsync("Pending address");
        await component.Find("#createLineupLinkGrant-1").ClickAsync(new());

        // Act
        await component.Find(".lineup-link-access-grant button[id^='exportLineupLinkBundle-']").ClickAsync(new());

        // Assert
        component.WaitForAssertion(() =>
        {
            var invocation = Assert.Single(context.JSInterop.Invocations, item => item.Identifier == "downloadTextFile");
            using var json = JsonDocument.Parse(Assert.IsType<string>(invocation.Arguments[2]));
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("baseUrl").ValueKind);
        }, timeout: LineupWebBunitContext.WaitTimeout);
    }

    /// <summary>Verifies dedicated Lineup Link outputs derive HTTP or HTTPS from the listener instead of presenting a second transport switch.</summary>
    [Fact]
    public async Task ConfigureDedicatedLineupLink_ListenerOwnsTransportSelection()
    {
        // Arrange
        await using var context = CreateContext(out _, out _);
        context.Services.AddSingleton<IVirtualTunerDriver, LineupLinkVirtualTunerDriver>();
        context.JSInterop.SetupVoid("downloadTextFile", _ => true).SetVoidResult();
        var component = context.Render<Settings>();
        await component.Find("#tab-virtual-devices").ClickAsync(new());
        await component.Find("#addVirtualDevice").ClickAsync(new());
        await component.Find("[data-virtual-device-kind='lineup-link']").ClickAsync(new());

        // Act
        Assert.NotNull(component.Find("#linkEncryption-1"));
        await component.Find("#dedicatedListener-1").ChangeAsync(true);

        // Assert
        Assert.Empty(component.FindAll("#linkEncryption-1"));
        Assert.Contains("HTTP", component.Find(".lineup-link-listener-transport").TextContent);
        Assert.Empty(component.FindAll("#linkServerCertificate-1"));
        var hostingText = component.Find(".virtual-output-hosting-settings").TextContent;
        Assert.DoesNotContain("}", hostingText, StringComparison.Ordinal);
        Assert.Contains("Managed processes are launched and supervised by Lineup", hostingText);
        Assert.Contains("Port opened by the worker", hostingText);
        Assert.DoesNotContain("Concurrent client limit", hostingText);
        Assert.Contains("forwarded client addresses and schemes", hostingText);
        Assert.Contains("Failed worker restarts allowed", hostingText);
        Assert.Contains("active requests to finish", hostingText);
        Assert.Contains("authenticated host lease", hostingText);
        Assert.Contains("simultaneous capability requests", hostingText);
        Assert.Contains("Worker memory ceiling", hostingText);
        Assert.Contains("Worker CPU ceiling", hostingText);
        var advancedNetworking = component.Find(".virtual-output-advanced-networking");
        Assert.NotNull(advancedNetworking.QuerySelector("#listenerNetworks-1"));
        Assert.Null(advancedNetworking.QuerySelector("#listenerConnections-1"));
        Assert.NotNull(advancedNetworking.QuerySelector("#listenerForwarded-1"));
        Assert.NotNull(advancedNetworking.QuerySelector("#workerRestarts-1"));
        Assert.NotNull(advancedNetworking.QuerySelector("#workerLease-1"));
        Assert.NotNull(advancedNetworking.QuerySelector("#workerBrokerCalls-1"));
        Assert.NotNull(advancedNetworking.QuerySelector("#workerMemory-1"));
        Assert.NotNull(advancedNetworking.QuerySelector("#workerCpu-1"));
        Assert.Empty(component.FindAll("#listenerPublicUri-1"));
        Assert.Equal(
            component.Instance.Topology.VirtualDevices.Single(device => device.IsLineupLink).EffectivePublicBaseUri,
            component.Find("#listenerClientUrl-1").GetAttribute("value"));

        // Act
        await component.Find("#listenerTransport-1").ChangeAsync(VirtualOutputTransport.Https);

        // Assert
        Assert.Contains("HTTPS", component.Find(".lineup-link-listener-transport").TextContent);
        Assert.NotNull(component.Find("#linkServerCertificate-1"));
        Assert.Contains("Connection bundle certificate pin", component.Find("label[for='linkServerCertificate-1']").TextContent);
        await component.Find("#listenerPublicUriOverride-1").ChangeAsync(true);
        await component.Find("#listenerPublicUri-1").ChangeAsync("https://lineup.example:8600/");
        await component.Find("#newLineupLinkGrantName-1").ChangeAsync("Dedicated access");
        await component.Find("#createLineupLinkGrant-1").ClickAsync(new());

        // Act
        await component.Find(".lineup-link-access-grant button[id^='exportLineupLinkBundle-']").ClickAsync(new());

        // Assert
        component.WaitForAssertion(() =>
        {
            var invocation = Assert.Single(context.JSInterop.Invocations, item => item.Identifier == "downloadTextFile");
            var bundle = LineupLinkConnectionBundleCodec.Deserialize(Assert.IsType<string>(invocation.Arguments[2]));
            Assert.Equal("https://lineup.example:8600/", bundle.BaseUrl);
            Assert.Equal(LineupLinkTransportSecurity.Https, bundle.TransportSecurity);
        }, timeout: LineupWebBunitContext.WaitTimeout);
    }

    /// <summary>Verifies a selected access-grant expiration survives subsequent component rerenders.</summary>
    [Fact]
    public async Task SetLineupLinkGrantExpiration_DateTimePicker_PersistsSelection()
    {
        // Arrange
        await using var context = CreateContext(out _, out _);
        context.Services.AddSingleton<IVirtualTunerDriver, LineupLinkVirtualTunerDriver>();
        var component = context.Render<Settings>();
        await component.Find("#tab-virtual-devices").ClickAsync(new());
        await component.Find("#addVirtualDevice").ClickAsync(new());
        await component.Find("[data-virtual-device-kind='lineup-link']").ClickAsync(new());
        var expiration = new DateTime(2030, 1, 2, 3, 4, 0, DateTimeKind.Unspecified);

        // Act
        component.Find("#newLineupLinkGrantExpiration-1").Input("2030-01-02T03:04");
        await component.Find("#newLineupLinkGrantName-1").ChangeAsync("Expiration test");

        // Assert
        var device = component.Instance.Topology.VirtualDevices.Single(item => item.IsLineupLink);
        Assert.Equal(expiration, device.LinkNewAccessGrantExpiresAtLocal);
        Assert.Equal("2030-01-02T03:04", component.Find("#newLineupLinkGrantExpiration-1").GetAttribute("value"));
    }

    /// <summary>
    /// Verifies generated Lineup Link bundles remain collapsed until shown and can be copied.
    /// </summary>
    [Fact]
    public async Task ShowAndCopyLineupLinkBundle_GeneratedBundle_CopiesExactJson()
    {
        // Arrange
        await using var context = CreateContext(out _, out _);
        context.Services.AddSingleton<IVirtualTunerDriver, LineupLinkVirtualTunerDriver>();
        context.JSInterop.SetupVoid("copyTextToClipboard", _ => true).SetVoidResult();
        var component = context.Render<Settings>();
        await component.Find("#tab-virtual-devices").ClickAsync(new());
        await component.Find("#addVirtualDevice").ClickAsync(new());
        await component.Find("[data-virtual-device-kind='lineup-link']").ClickAsync(new());
        await component.Find("#linkPublicUrl-1").ChangeAsync("https://lineup.example:8443");
        await component.Find("#newLineupLinkGrantName-1").ChangeAsync("Copy access");
        await component.Find("#createLineupLinkGrant-1").ClickAsync(new());
        await component.Find(".lineup-link-access-grant button[id^='rotateLineupLinkGrant-']").ClickAsync(new());

        // Act
        await component.Find(".lineup-link-access-grant button[id^='copyLineupLinkBundle-']").ClickAsync(new());

        // Assert
        var copyButton = component.Find(".lineup-link-access-grant button[id^='copyLineupLinkBundle-']");
        var showButton = component.Find(".lineup-link-access-grant button[id^='showLineupLinkBundle-']");
        Assert.Contains("Copy", copyButton.TextContent);
        Assert.Equal("true", showButton.GetAttribute("aria-expanded"));
        Assert.Contains("Hide bundle", showButton.TextContent);
        var code = component.Find(".lineup-link-access-grant pre.connection-bundle-json code.language-json");
        Assert.NotEmpty(code.QuerySelectorAll(".json-property"));
        Assert.NotEmpty(code.QuerySelectorAll(".json-string"));
        Assert.NotEmpty(code.QuerySelectorAll(".json-number"));
        using var highlightedJson = JsonDocument.Parse(code.TextContent);
        Assert.Equal(1, highlightedJson.RootElement.GetProperty("bundleVersion").GetInt32());
        Assert.Equal(2, highlightedJson.RootElement.GetProperty("protocolVersion").GetInt32());
        component.WaitForAssertion(() =>
        {
            var invocation = Assert.Single(context.JSInterop.Invocations, item => item.Identifier == "copyTextToClipboard");
            using var json = JsonDocument.Parse(Assert.IsType<string>(invocation.Arguments[0]));
            Assert.Equal(1, json.RootElement.GetProperty("bundleVersion").GetInt32());
            Assert.Equal(2, json.RootElement.GetProperty("protocolVersion").GetInt32());
            Assert.Equal("https://lineup.example:8443", json.RootElement.GetProperty("baseUrl").GetString());
        }, timeout: LineupWebBunitContext.WaitTimeout);
    }

    /// <summary>
    /// Verifies persisted Lineup Link devices can always show a bundle with the unavailable token redacted.
    /// </summary>
    [Fact]
    public async Task ShowLineupLinkBundle_PersistedTokenHash_ShowsRedactedJson()
    {
        // Arrange
        await using var context = CreateContext(out _, out var settings);
        var driver = new LineupLinkVirtualTunerDriver();
        context.Services.AddSingleton<IVirtualTunerDriver>(driver);
        var editor = new TopologySettingsEditor();
        var model = editor.Edit(settings.Streaming);
        var lineupLink = editor.AddVirtualDevice(model, driver);
        lineupLink.LinkPublicBaseUrl = "https://lineup.example:8443";
        var mutation = LineupLinkAccessGrantManager.Create(
            new LineupLinkVirtualConfiguration { InstanceId = lineupLink.LinkInstanceId },
            "Persisted access",
            null,
            DateTime.UtcNow);
        lineupLink.LinkAccessGrants.Add(LineupLinkAccessGrantSettingsModel.Create(Assert.Single(mutation.Configuration!.AccessGrants)));
        Assert.Empty(editor.TryBuild(model, out var configuration));
        settings.Streaming = configuration;
        var component = context.Render<Settings>();
        await component.Find("#tab-virtual-devices").ClickAsync(new());
        await component.FindAll(".virtual-device-tab").Single(tab => tab.TextContent.Contains("Lineup Link", StringComparison.Ordinal)).ClickAsync(new());

        // Act
        await component.Find(".lineup-link-access-grant button[id^='showLineupLinkBundle-']").ClickAsync(new());

        // Assert
        Assert.True(component.Find(".lineup-link-access-grant button[id^='exportLineupLinkBundle-']").HasAttribute("disabled"));
        Assert.True(component.Find(".lineup-link-access-grant button[id^='copyLineupLinkBundle-']").HasAttribute("disabled"));
        Assert.Equal("true", component.Find(".lineup-link-access-grant button[id^='showLineupLinkBundle-']").GetAttribute("aria-expanded"));
        Assert.Contains(LineupLinkConnectionBundleCodec.RedactedAccessToken, component.Markup);
        Assert.Contains("The token is redacted.", component.Find(".lineup-link-access-grant").TextContent);
        await component.Find(".lineup-link-access-grant button[id^='showLineupLinkBundle-']").ClickAsync(new());
        Assert.Equal("false", component.Find(".lineup-link-access-grant button[id^='showLineupLinkBundle-']").GetAttribute("aria-expanded"));
        Assert.Empty(component.FindAll(".lineup-link-access-grant pre.connection-bundle-json"));
    }

    /// <summary>Verifies a final revoked grant can be permanently purged to disable remote access.</summary>
    [Fact]
    public async Task ManageLineupLinkAccessGrants_PurgeFinalGrant_DisablesRemoteAccess()
    {
        // Arrange
        await using var context = CreateContext(out _, out _);
        context.Services.AddSingleton<IVirtualTunerDriver, LineupLinkVirtualTunerDriver>();
        var component = context.Render<Settings>();
        await component.Find("#tab-virtual-devices").ClickAsync(new());
        await component.Find("#addVirtualDevice").ClickAsync(new());
        await component.Find("[data-virtual-device-kind='lineup-link']").ClickAsync(new());
        await component.Find("#linkPublicUrl-1").ChangeAsync("https://lineup.example:8443");
        await component.Find("#newLineupLinkGrantName-1").ChangeAsync("Bob");

        // Act
        await component.Find("#createLineupLinkGrant-1").ClickAsync(new());
        await component.Find(".lineup-link-access-grant button[id^='revokeLineupLinkGrant-']").ClickAsync(new());
        var revokedGrant = component.Find(".lineup-link-access-grant");
        await revokedGrant.QuerySelector("button[id^='purgeLineupLinkGrant-']")!.ClickAsync(new());
        await component.Find("button[id^='confirmPurgeLineupLinkGrant-']").ClickAsync(new());
        var grants = component.FindAll(".lineup-link-access-grant");

        // Assert
        Assert.Empty(grants);
        Assert.Contains("Remote access is disabled", component.Markup);
        var model = component.Instance.Topology.VirtualDevices.Single(device => device.IsLineupLink);
        Assert.Empty(model.LinkAccessGrants);
    }

    /// <summary>
    /// Verifies the built-in Web Player remains the first device tab regardless of persisted virtual-device order.
    /// </summary>
    [Fact]
    public async Task Render_VirtualDeviceTabs_PinsWebPlayerBeforeConfiguredDevices()
    {
        // Arrange
        await using var context = CreateContext(out _, out var settings);
        var driver = new LineupLinkVirtualTunerDriver();
        context.Services.AddSingleton<IVirtualTunerDriver>(driver);
        var editor = new TopologySettingsEditor();
        var model = editor.Edit(settings.Streaming);
        var lineupLink = editor.AddVirtualDevice(model, driver);
        lineupLink.LinkPublicBaseUrl = "https://lineup.example:8443";
        model.VirtualDevices.Remove(lineupLink);
        model.VirtualDevices.Insert(0, lineupLink);
        Assert.Empty(editor.TryBuild(model, out var configuration));
        settings.Streaming = configuration;
        var component = context.Render<Settings>();

        // Act
        await component.Find("#tab-virtual-devices").ClickAsync(new());

        // Assert
        var tabs = component.FindAll("#virtualTopologySettings .nav-tabs .nav-link");
        Assert.Collection(
            tabs,
            tab => Assert.Contains("Global Settings", tab.TextContent),
            tab => Assert.Contains("Web Player", tab.TextContent),
            tab => Assert.Contains("Lineup Link", tab.TextContent));
    }

    /// <summary>
    /// Verifies the add action discovers registered user-creatable physical tuner drivers.
    /// </summary>
    [Fact]
    public async Task AddPhysicalDevice_RegisteredDrivers_PromptsForTypeAndAddsSelection()
    {
        // Arrange
        await using var context = CreateContext(out _, out _);
        context.Services.AddSingleton<IPhysicalTunerDriver>(new TestPhysicalTunerDriver("m3u", "M3U Source", ""));
        var component = context.Render<Settings>();
        await component.Find("#tab-physical-devices").ClickAsync(new());

        // Act
        await component.Find("#addPhysicalDevice").ClickAsync(new());

        // Assert
        var picker = component.Find("#physicalDeviceTypePicker");
        var options = picker.QuerySelectorAll(".physical-device-type");
        Assert.Equal(2, options.Length);
        Assert.Contains(options, option => option.GetAttribute("data-physical-device-kind") == "hdhomerun");
        var option = Assert.Single(options, option => option.GetAttribute("data-physical-device-kind") == "m3u");
        Assert.Contains("M3U Source", option.TextContent);

        await option.ClickAsync(new());

        Assert.Empty(component.FindAll("#physicalDeviceTypePicker"));
        Assert.Single(component.FindAll("[data-device-kind='m3u']"));
        Assert.Single(component.FindAll("[data-device-kind='m3u'] .physical-name"));
        Assert.Empty(component.FindAll("[data-device-kind='m3u'] .physical-address"));
        Assert.Empty(component.FindAll("#physicalDevicesEmptyState"));
    }

    /// <summary>
    /// Verifies guide sources use global and per-source tabs with compatible physical-device subscriptions.
    /// </summary>
    [Fact]
    public async Task GuideSources_AddSource_DefaultsToAllAndSupportsSelectedDevices()
    {
        // Arrange
        await using var context = CreateContext(out _, out var settings);
        var editor = new TopologySettingsEditor();
        var model = editor.Edit(settings.Streaming);
        editor.AddPhysicalDevice(model, new TestPhysicalTunerDriver());
        model.PhysicalDevices[0].Name = "Roof Antenna";
        model.PhysicalDevices[0].Address = "tuner.local";
        Assert.Empty(editor.TryBuild(model, out var configuration));
        settings.Streaming = configuration;
        var component = context.Render<Settings>();
        await component.Find("#tab-guide").ClickAsync(new());

        // Act
        await component.Find("#addGuideSource").ClickAsync(new());
        await component.Find(".add-guide-source-type").ClickAsync(new());

        // Assert
        Assert.Empty(component.FindAll("#guideSourceTypePicker"));
        Assert.Single(component.FindAll(".guide-source-tab"));
        Assert.Single(component.FindAll(".guide-source"));
        Assert.True(component.Find(".guide-source-all").HasAttribute("checked"));
        Assert.Contains("SiliconDust 1", component.Find(".guide-source-tab").TextContent);
        Assert.Empty(component.FindAll("#guideSourcesEmptyState"));

        // Act
        await component.Find(".guide-source-selected").ChangeAsync(true);

        // Assert
        var physical = component.Find(".guide-source-physical");
        Assert.Contains("Roof Antenna", physical.ParentElement!.TextContent);
        Assert.False(physical.HasAttribute("checked"));
    }

    /// <summary>
    /// Verifies About Settings follows global and instance subtabs and identifies dynamically registered adapter types.
    /// </summary>
    [Fact]
    public async Task AboutSettings_TopologySubtabs_ShowsSelectedAdapterContext()
    {
        // Arrange
        await using var context = CreateContext(out _, out _);
        context.Services.AddSingleton<IPhysicalTunerDriver>(new TestPhysicalTunerDriver("m3u", "M3U Source", ""));
        context.Services.AddSingleton<IVirtualTunerDriver, M3uTestVirtualTunerDriver>();
        context.Services.AddSingleton<IGuideSourceDriver, XmltvTestGuideSourceDriver>();
        var component = context.Render<Settings>();

        // Act
        await component.Find("#tab-physical-devices").ClickAsync(new());

        // Assert
        Assert.Contains("Refresh Settings", component.Find("#aboutSettingsBody").TextContent);
        Assert.Contains("source-stage defaults", component.Find("#aboutSettingsBody").TextContent);
        Assert.DoesNotContain("Video Quality (CRF)", component.Find("#aboutSettingsBody").TextContent);
        Assert.DoesNotContain("Non-AC-4 Audio", component.Find("#aboutSettingsBody").TextContent);

        await component.Find("#addPhysicalDevice").ClickAsync(new());
        await component.Find("[data-physical-device-kind='m3u']").ClickAsync(new());
        await component.Find("#physicalName-0").ChangeAsync("Playlist Antenna");

        Assert.Contains("Playlist Antenna", component.Find("#aboutSettingsBody").TextContent);
        Assert.Contains("M3U Source", component.Find("#aboutSettingsBody").TextContent);
        Assert.Contains("Plugin-provided connection and source options", component.Find("#aboutSettingsBody").TextContent);
        Assert.Contains("Transcoder Settings", component.Find("#aboutSettingsBody").TextContent);

        await component.Find("#tab-guide").ClickAsync(new());

        Assert.Contains("EPG Fetch Settings", component.Find("#aboutSettingsBody").TextContent);
        Assert.DoesNotContain("XMLTV Output Settings", component.Find("#aboutSettingsBody").TextContent);

        await component.Find("#addGuideSource").ClickAsync(new());
        await component.Find("[data-guide-source-kind='xmltv']").ClickAsync(new());
        await component.Find("#guideSourceName-0").ChangeAsync("Regional XMLTV");

        Assert.Contains("Regional XMLTV", component.Find("#aboutSettingsBody").TextContent);
        Assert.Contains("XMLTV Provider", component.Find("#aboutSettingsBody").TextContent);
        Assert.DoesNotContain("Physical Device Subscription", component.Find("#aboutSettingsBody").TextContent);
        Assert.DoesNotContain("All compatible enabled devices", component.Find("#aboutSettingsBody").TextContent);

        await component.Find("#tab-guide-outputs").ClickAsync(new());

        Assert.Contains("Guide Outputs", component.Find("#aboutSettingsBody").TextContent);
        Assert.DoesNotContain("Reset Settings", component.Find("#aboutSettingsBody").TextContent);

        await component.Find("#tab-virtual-devices").ClickAsync(new());

        Assert.Contains("Transcoder Settings", component.Find("#aboutSettingsBody").TextContent);
        Assert.Contains("Virtual Tuner Discovery", component.Find("#aboutSettingsBody").TextContent);
        Assert.DoesNotContain("Video Encoder Preset", component.Find("#aboutSettingsBody").TextContent);
        Assert.DoesNotContain("AC-4 Audio", component.Find("#aboutSettingsBody").TextContent);

        await component.Find("#addVirtualDevice").ClickAsync(new());
        await component.Find("[data-virtual-device-kind='m3u']").ClickAsync(new());
        await component.Find("#virtualName-1").ChangeAsync("Living Room Playlist");

        Assert.Contains("Living Room Playlist", component.Find("#aboutSettingsBody").TextContent);
        Assert.Contains("M3U Provider", component.Find("#aboutSettingsBody").TextContent);
        Assert.Contains("physical-device subscription", component.Find("#aboutSettingsBody").TextContent);
        Assert.Contains("Output Hosting", component.Find("#aboutSettingsBody").TextContent);
        Assert.Contains("Transcoder Settings", component.Find("#aboutSettingsBody").TextContent);
    }

    /// <summary>
    /// Verifies physical device tabs track creation, editing, disablement, and removal.
    /// </summary>
    [Fact]
    public async Task PhysicalDeviceTabs_DeviceLifecycle_TracksSelectionAndLabel()
    {
        // Arrange
        await using var context = CreateContext(out _, out _);
        var component = context.Render<Settings>();
        await component.Find("#tab-physical-devices").ClickAsync(new());

        // Act
        await component.Find("#addPhysicalDevice").ClickAsync(new());
        await component.Find(".physical-device-type").ClickAsync(new());

        // Assert
        var deviceTab = component.Find(".physical-device-tab");
        Assert.Contains("active", deviceTab.ClassList);
        Assert.Empty(component.FindAll("#physicalGlobalSettings"));
        Assert.Single(component.FindAll(".physical-device"));

        await component.Find("#physicalName-0").ChangeAsync("Roof Antenna");

        Assert.Contains("Roof Antenna", component.Find(".physical-device-tab").TextContent);

        await component.Find("#physicalEnabled-0").ChangeAsync(false);

        Assert.Contains("Disabled", component.Find(".physical-device-tab").TextContent);

        await component.Find(".remove-physical-device").ClickAsync(new());

        Assert.Contains("active", component.Find("#physicalSubtabGlobal").ClassList);
        Assert.NotNull(component.Find("#physicalGlobalSettings"));
        Assert.Empty(component.FindAll(".physical-device-tab"));
    }

    /// <summary>
    /// Verifies Test &amp; Identify uses unsaved values and applies a discovered name only when requested.
    /// </summary>
    [Fact]
    public async Task TestPhysicalDevice_UnsavedAddress_ShowsIdentityAndOffersName()
    {
        // Arrange
        await using var context = CreateContext(out var settingsService, out var settings);
        var driver = Assert.IsType<TestPhysicalTunerDriver>(context.Services.GetServices<IPhysicalTunerDriver>().Single(candidate => candidate.Kind == "hdhomerun"));
        driver.Probe = (configuration, _) =>
        {
            var address = configuration.Configuration.GetProperty("Address").GetString();
            return ValueTask.FromResult(new PhysicalDeviceProbeResult
            {
                SuggestedName = "Living Room HDHomeRun (ABCDEF01)",
                Details =
                [
                    new PhysicalDeviceProbeDetail { Label = "Device ID", Value = "ABCDEF01" },
                    new PhysicalDeviceProbeDetail { Label = "Address", Value = address! }
                ]
            });
        };
        var component = context.Render<Settings>();
        await component.Find("#tab-physical-devices").ClickAsync(new());
        await component.Find("#addPhysicalDevice").ClickAsync(new());
        await component.Find(".physical-device-type").ClickAsync(new());
        await component.Find("#physicalAddress-0").ChangeAsync("unsaved.local");

        // Act
        await component.Find("#testPhysicalDevice-0").ClickAsync(new());

        // Assert
        component.WaitForAssertion(() =>
        {
            Assert.Contains("Connection successful", component.Find(".physical-device-probe-success").TextContent);
            Assert.Contains("ABCDEF01", component.Find(".physical-device-probe-success").TextContent);
            Assert.Contains("unsaved.local", component.Find(".physical-device-probe-success").TextContent);
        }, timeout: LineupWebBunitContext.WaitTimeout);
        Assert.Equal("unsaved.local", driver.ProbedConfiguration?.Configuration.GetProperty("Address").GetString());
        Assert.Equal("HDHomeRun 1", component.Find("#physicalName-0").GetAttribute("value"));
        await settingsService.DidNotReceive().UpdateAsync(Arg.Any<Action<LineupSettings>>());
        Assert.Empty(settings.Streaming.PhysicalDevices);

        await component.Find(".use-discovered-name").ClickAsync(new());

        Assert.Equal("Living Room HDHomeRun (ABCDEF01)", component.Find("#physicalName-0").GetAttribute("value"));
        await settingsService.DidNotReceive().UpdateAsync(Arg.Any<Action<LineupSettings>>());
    }

    /// <summary>
    /// Verifies failed physical-device probes remain inline and successful results are cleared by connection edits.
    /// </summary>
    [Fact]
    public async Task TestPhysicalDevice_FailureAndConnectionEdit_UpdatesInlineState()
    {
        // Arrange
        await using var context = CreateContext(out _, out _);
        var driver = Assert.IsType<TestPhysicalTunerDriver>(context.Services.GetServices<IPhysicalTunerDriver>().Single(candidate => candidate.Kind == "hdhomerun"));
        driver.Probe = (_, _) => ValueTask.FromException<PhysicalDeviceProbeResult>(new HttpRequestException("Device refused the connection."));
        var component = context.Render<Settings>();
        await component.Find("#tab-physical-devices").ClickAsync(new());
        await component.Find("#addPhysicalDevice").ClickAsync(new());
        await component.Find(".physical-device-type").ClickAsync(new());

        // Act
        await component.Find("#testPhysicalDevice-0").ClickAsync(new());

        // Assert
        component.WaitForAssertion(() => Assert.Contains("Device refused the connection.", component.Find(".physical-device-probe-error").TextContent), timeout: LineupWebBunitContext.WaitTimeout);
        Assert.Contains("Test & Identify", component.Find("#aboutSettingsBody").TextContent);

        driver.Probe = (_, _) => ValueTask.FromResult(new PhysicalDeviceProbeResult
        {
            Details = [new PhysicalDeviceProbeDetail { Label = "Device ID", Value = "ABCDEF01" }]
        });
        await component.Find("#testPhysicalDevice-0").ClickAsync(new());
        component.WaitForAssertion(() => Assert.NotNull(component.Find(".physical-device-probe-success")), timeout: LineupWebBunitContext.WaitTimeout);

        await component.Find("#physicalAddress-0").ChangeAsync("changed.local");

        Assert.Empty(component.FindAll(".physical-device-probe-success"));
        Assert.Empty(component.FindAll(".physical-device-probe-error"));
    }

    /// <summary>
    /// Verifies global and effective per-device settings identify configurations that request transcoding.
    /// </summary>
    [Fact]
    public async Task Render_TranscodingConfiguration_ShowsGlobalAndDeviceWarnings()
    {
        // Arrange
        await using var context = CreateContext(out _, out var settings);
        var editor = new TopologySettingsEditor();
        var model = editor.Edit(settings.Streaming);
        editor.AddPhysicalDevice(model, new TestPhysicalTunerDriver());
        model.PhysicalDevices[0].Name = "Antenna";
        model.PhysicalDevices[0].Address = "tuner.local";
        model.PhysicalDevices[0].OverrideAudio = true;
        model.PhysicalDevices[0].Audio = OutputAudioPolicy.Ac3;
        model.VirtualDevices[0].OverrideConvertHevcToH264 = true;
        model.VirtualDevices[0].ConvertHevcToH264 = true;
        Assert.Empty(editor.TryBuild(model, out var configuration));
        settings.Streaming = configuration;

        // Act
        var component = context.Render<Settings>();
        await component.Find("#tab-physical-devices").ClickAsync(new());

        // Assert
        Assert.Empty(component.FindAll(".physical-global-transcode-warning"));

        await component.Find("#physicalDefaultHevc").ChangeAsync(true);

        Assert.Single(component.FindAll(".physical-global-transcode-warning"));

        await component.Find(".physical-device-tab").ClickAsync(new());

        Assert.Single(component.FindAll(".physical-device-transcode-warning"));

        await component.Find("#tab-virtual-devices").ClickAsync(new());

        Assert.Single(component.FindAll(".virtual-global-transcode-warning"));

        await component.Find(".virtual-device-tab").ClickAsync(new());

        Assert.Single(component.FindAll(".virtual-device-transcode-warning"));

        await component.Find("#virtualSubtabGlobal").ClickAsync(new());
        await component.Find("#virtualDefaultHevc").ChangeAsync(true);

        Assert.Single(component.FindAll(".virtual-global-transcode-warning"));
    }

    /// <summary>
    /// Verifies physical encoder controls appear only when the effective profile requests video transcoding.
    /// </summary>
    [Fact]
    public async Task Render_PhysicalVideoTranscoding_ShowsEncoderControlsOnlyWhenApplicable()
    {
        // Arrange
        await using var context = CreateContext(out _, out _);
        var component = context.Render<Settings>();

        // Act
        await component.Find("#tab-physical-devices").ClickAsync(new());

        // Assert
        Assert.Empty(component.FindAll(".physical-global-video-encoder-settings"));
        Assert.NotNull(component.Find("#physicalDefaultAudio"));

        await component.Find("#addPhysicalDevice").ClickAsync(new());
        await component.Find(".physical-device-type").ClickAsync(new());

        Assert.Empty(component.FindAll("#physicalOverridePreset-0"));
        Assert.NotNull(component.Find("#physicalOverrideAudio-0"));

        await component.Find("#physicalSubtabGlobal").ClickAsync(new());
        await component.Find("#physicalDefaultHevc").ChangeAsync(true);

        Assert.Single(component.FindAll(".physical-global-video-encoder-settings"));
        Assert.NotNull(component.Find("#physicalDefaultPreset"));
        Assert.NotNull(component.Find("#physicalDefaultQuality"));
        Assert.NotNull(component.Find("#physicalDefaultBitrate"));

        await component.Find(".physical-device-tab").ClickAsync(new());

        Assert.NotNull(component.Find("#physicalOverridePreset-0"));
        Assert.NotNull(component.Find("#physicalOverrideQuality-0"));
        Assert.NotNull(component.Find("#physicalOverrideBitrate-0"));

        await component.Find("#physicalOverrideHevc-0").ChangeAsync(true);

        Assert.Empty(component.FindAll("#physicalOverridePreset-0"));

        await component.Find("#physicalOverrideDeinterlace-0").ChangeAsync(true);
        await component.Find(".physical-deinterlace").ChangeAsync(nameof(SourceDeinterlacePolicy.SourceFrameRate));

        Assert.NotNull(component.Find("#physicalOverridePreset-0"));
    }

    /// <summary>
    /// Verifies disabled physical and virtual devices collapse while their header toggles remain available.
    /// </summary>
    [Fact]
    public async Task DisableDevice_CollapsesDetailsAndKeepsHeaderToggle()
    {
        // Arrange
        await using var context = CreateContext(out _, out _);
        var component = context.Render<Settings>();
        await component.Find("#tab-physical-devices").ClickAsync(new());
        await component.Find("#addPhysicalDevice").ClickAsync(new());
        await component.Find(".physical-device-type").ClickAsync(new());

        // Act
        await component.Find("#physicalEnabled-0").ChangeAsync(false);

        // Assert
        Assert.NotNull(component.Find(".physical-device .card-header #physicalEnabled-0"));
        Assert.Empty(component.FindAll(".physical-device-details"));
        Assert.Empty(component.FindAll("#physicalName-0"));

        await component.Find("#tab-virtual-devices").ClickAsync(new());
        await component.Find(".virtual-device-tab").ClickAsync(new());
        await component.Find("#virtualEnabled-0").ChangeAsync(false);

        Assert.NotNull(component.Find(".virtual-device .card-header #virtualEnabled-0"));
        Assert.Empty(component.FindAll(".virtual-device-details"));
        Assert.Empty(component.FindAll("#virtualName-0"));
        Assert.Contains("Web Player", component.Find(".virtual-device .card-header").TextContent);
    }

    /// <summary>
    /// Verifies Selected subscriptions persist only checked physical devices.
    /// </summary>
    [Fact]
    public async Task Save_SelectedSubscription_PersistsSelection()
    {
        // Arrange
        await using var context = CreateContext(out var settingsService, out var settings);
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var component = context.Render<Settings>();
        await component.Find("#tab-physical-devices").ClickAsync(new());
        await component.Find("#addPhysicalDevice").ClickAsync(new());
        await component.Find(".physical-device-type").ClickAsync(new());
        await component.Find("#physicalName-0").ChangeAsync("Antenna");
        await component.Find("#physicalAddress-0").ChangeAsync("antenna.local");
        await component.Find("#tab-virtual-devices").ClickAsync(new());
        await component.Find(".virtual-device-tab").ClickAsync(new());
        await component.Find("#virtualSubscription-0").ChangeAsync(nameof(PhysicalDeviceSubscriptionMode.Selected));
        await component.Find(".physical-subscription-selection").ChangeAsync(true);

        // Act
        await component.Find("button.btn-primary").ClickAsync(new());

        // Assert
        var player = Assert.Single(settings.Streaming.VirtualDevices);
        Assert.Equal(PhysicalDeviceSubscriptionMode.Selected, player.Subscription.Mode);
        Assert.Equal(settings.Streaming.PhysicalDevices[0].Id, Assert.Single(player.Subscription.SelectedDeviceIds));
        await settingsService.Received(1).UpdateAsync(Arg.Any<Action<LineupSettings>>());
    }

    /// <summary>Verifies the XMLTV placeholder label and help use localized Web resources instead of plugin defaults.</summary>
    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task Render_XmltvPlaceholderPolicy_UsesLocalizedResources(string culture)
    {
        // Arrange
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            await using var context = CreateContext(out _, out _);
            context.JSInterop.Mode = JSRuntimeMode.Loose;
            var localizer = context.Services.GetRequiredService<IStringLocalizer<UiStrings>>();

            // Act
            var component = context.Render<Settings>();
            await component.Find("#tab-guide-outputs").ClickAsync(new());
            var field = component.Find("[data-setting-key='includeUnavailablePrograms']");

            // Assert
            Assert.Equal(localizer["Settings_GuideOutputIncludeUnavailableProgramsLabel"].Value, field.QuerySelector("label")!.TextContent);
            Assert.Equal(localizer["Settings_GuideOutputIncludeUnavailableProgramsHelp"].Value, field.QuerySelector(".form-text")!.TextContent);
            if (culture == "de-DE")
            {
                var descriptor = new XmltvGuideOutputDriver().SettingsDescriptor.Fields.Single(setting => setting.Key == "includeUnavailablePrograms");
                Assert.NotEqual(descriptor.Label, field.QuerySelector("label")!.TextContent);
                Assert.NotEqual(descriptor.Description, field.QuerySelector(".form-text")!.TextContent);
            }
        }
        finally
        {
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    /// <summary>Verifies XMLTV exposes and retains its placeholder switch alongside the custom directory editor.</summary>
    [Fact]
    public async Task Save_XmltvGuideOutput_PersistsPlaceholderPolicy()
    {
        // Arrange
        await using var context = CreateContext(out var settingsService, out var settings);
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var component = context.Render<Settings>();
        await component.Find("#tab-guide-outputs").ClickAsync(new());
        const string selector = "[data-setting-key='includeUnavailablePrograms'] input";
        Assert.False(component.Find(selector).HasAttribute("checked"));
        var directory = Path.GetFullPath("xmltv-guide-output");

        // Act
        await component.Find(selector).ChangeAsync(true);
        await component.Find("#guideOutputDirectoryPath").ChangeAsync(directory);
        await component.Find("button.btn-primary").ClickAsync(new());
        await component.Find("button.btn-primary").ClickAsync(new());

        // Assert
        var output = Assert.Single(settings.Streaming.GuideOutputs, candidate => candidate.IsBuiltIn);
        Assert.True(output.Adapter.Configuration.GetProperty("IncludeUnavailablePrograms").GetBoolean());
        Assert.Equal(directory, Path.GetDirectoryName(new XmltvGuideOutputDriver().ResolveDestination(output.Adapter).Path));
        Assert.Empty(component.FindAll("[data-setting-key='outputDirectoryPath']"));
        await settingsService.Received(2).UpdateAsync(Arg.Any<Action<LineupSettings>>());
    }

    /// <summary>Verifies a non-XMLTV Guide Output renders and persists its plugin-owned portable settings.</summary>
    [Fact]
    public async Task Save_NonXmltvGuideOutput_PersistsPortableSettings()
    {
        // Arrange
        await using var context = CreateContext(out var settingsService, out var settings);
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var component = context.Render<Settings>();
        await component.Find("#tab-guide-outputs").ClickAsync(new());
        await component.Find("#addGuideOutput").ClickAsync(new());
        await component.Find($"[data-guide-output-kind='{JsonGuideOutputDriver.AdapterKind}']").ClickAsync(new());
        var outputDirectory = Path.GetFullPath("json-guide-output");

        // Act
        await component.Find("[data-setting-key='outputDirectoryPath'] input").ChangeAsync(outputDirectory);
        await component.Find("button.btn-primary").ClickAsync(new());

        // Assert
        Assert.Empty(component.FindAll("#guideOutputDirectoryPath"));
        var output = settings.Streaming.GuideOutputs.Single(candidate => candidate.Adapter.Kind == JsonGuideOutputDriver.AdapterKind);
        Assert.Equal(
            Path.Combine(outputDirectory, "lineup-json_1.json"),
            new JsonGuideOutputDriver().ResolveDestination(output.Adapter).Path);
        await settingsService.Received(1).UpdateAsync(Arg.Any<Action<LineupSettings>>());
    }

    /// <summary>Verifies unrelated saves retain the latest portable guide-output settings for new and existing outputs.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Save_NonXmltvGuideOutput_RepeatedSavesPreservePortableSettings(bool existingOutput, bool failFirstSave)
    {
        // Arrange
        await using var context = CreateContext(out var settingsService, out var settings);
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var driver = new JsonGuideOutputDriver();
        if (existingOutput)
        {
            var editor = new TopologySettingsEditor();
            var model = editor.Edit(settings.Streaming, guideOutputDrivers: [new XmltvGuideOutputDriver(), driver]);
            editor.AddGuideOutput(model, driver, Path.GetFullPath("original-json-guide-output"));
            Assert.Empty(editor.TryBuild(model, out var configuration, guideOutputDrivers: [new XmltvGuideOutputDriver(), driver]));
            settings.Streaming = configuration;
        }
        var initialConfiguration = settings.Streaming;
        var saveAttempts = 0;
        settingsService.UpdateAsync(Arg.Any<Action<LineupSettings>>()).Returns(callInfo =>
        {
            if (failFirstSave && saveAttempts++ == 0)
            {
                return Task.FromException(new IOException("Simulated settings persistence failure."));
            }
            callInfo.Arg<Action<LineupSettings>>()(settings);
            return Task.CompletedTask;
        });
        var component = context.Render<Settings>();
        await component.Find("#tab-guide-outputs").ClickAsync(new());
        if (existingOutput)
        {
            var output = settings.Streaming.GuideOutputs.Single(candidate => candidate.Adapter.Kind == driver.Kind);
            await component.Find($".guide-output-tab[data-guide-output-id='{output.Id}']").ClickAsync(new());
        }
        else
        {
            await component.Find("#addGuideOutput").ClickAsync(new());
            await component.Find($"[data-guide-output-kind='{driver.Kind}']").ClickAsync(new());
        }
        var outputDirectory = Path.GetFullPath("changed-json-guide-output");
        await component.Find("[data-setting-key='outputDirectoryPath'] input").ChangeAsync(outputDirectory);

        // Act
        if (failFirstSave)
        {
            await component.Find("button.btn-primary").ClickAsync(new());
            Assert.Same(initialConfiguration, settings.Streaming);
            Assert.Equal(outputDirectory, component.Find("[data-setting-key='outputDirectoryPath'] input").GetAttribute("value"));
        }
        await component.Find("button.btn-primary").ClickAsync(new());
        var firstOutput = settings.Streaming.GuideOutputs.Single(candidate => candidate.Adapter.Kind == driver.Kind);
        await component.Find("#tab-guide").ClickAsync(new());
        await component.Find("#epgHistoryRetentionHours").ChangeAsync(48);
        await component.Find("button.btn-primary").ClickAsync(new());

        // Assert
        var secondOutput = settings.Streaming.GuideOutputs.Single(candidate => candidate.Id == firstOutput.Id);
        var expectedPath = Path.Combine(outputDirectory, "lineup-json_1.json");
        Assert.Equal(expectedPath, driver.ResolveDestination(firstOutput.Adapter).Path);
        Assert.Equal(expectedPath, driver.ResolveDestination(secondOutput.Adapter).Path);
        Assert.Equal(48, settings.EpgHistoryRetentionHours);
        var notifications = context.Services.GetRequiredService<IStatusNotificationService>().Notifications;
        Assert.Equal(failFirstSave ? 1 : 0, notifications.Count(notification => notification.IsError));
        Assert.Equal(2, notifications.Count(notification => !notification.IsError));
        await settingsService.Received(failFirstSave ? 3 : 2).UpdateAsync(Arg.Any<Action<LineupSettings>>());
    }

    /// <summary>Verifies unsaved checkbox changes do not alter the live saved subscription until the next save.</summary>
    [Fact]
    public async Task Save_SelectedSubscription_UnsavedEditsDoNotChangeLiveConfiguration()
    {
        // Arrange
        await using var context = CreateContext(out var settingsService, out var settings);
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var component = context.Render<Settings>();
        await component.Find("#tab-physical-devices").ClickAsync(new());
        await component.Find("#addPhysicalDevice").ClickAsync(new());
        await component.Find(".physical-device-type").ClickAsync(new());
        await component.Find("#physicalAddress-0").ChangeAsync("antenna.local");
        await component.Find("#tab-virtual-devices").ClickAsync(new());
        await component.Find(".virtual-device-tab").ClickAsync(new());
        await component.Find("#virtualSubscription-0").ChangeAsync(nameof(PhysicalDeviceSubscriptionMode.Selected));
        await component.Find(".physical-subscription-selection").ChangeAsync(true);
        await component.Find("button.btn-primary").ClickAsync(new());
        var savedConfiguration = settings.Streaming;
        var savedPlayer = Assert.Single(savedConfiguration.VirtualDevices);
        var physicalId = Assert.Single(savedConfiguration.PhysicalDevices).Id;

        // Act
        await component.Find(".physical-subscription-selection").ChangeAsync(false);
        var selectionBeforeSave = savedPlayer.Subscription.SelectedDeviceIds.ToArray();
        await component.Find("button.btn-primary").ClickAsync(new());

        // Assert
        Assert.Equal(physicalId, Assert.Single(selectionBeforeSave));
        Assert.Equal(physicalId, Assert.Single(savedPlayer.Subscription.SelectedDeviceIds));
        Assert.Empty(Assert.Single(settings.Streaming.VirtualDevices).Subscription.SelectedDeviceIds);
        await settingsService.Received(2).UpdateAsync(Arg.Any<Action<LineupSettings>>());
    }

    private static BunitContext CreateContext(out ILineupSettingsService settingsService, out LineupSettings settings)
    {
        var context = new BunitContext();
        settings = new LineupSettings();
        settingsService = Substitute.For<ILineupSettingsService>();
        settingsService.Settings.Returns(settings);
        var capturedSettings = settings;
        var capturedService = settingsService;
        settingsService.ConfiguredXmltvOutputPath.Returns(_ => capturedSettings.XmltvOutputPath);
        settingsService.UpdateAsync(Arg.Any<Action<LineupSettings>>()).Returns(callInfo =>
        {
            callInfo.Arg<Action<LineupSettings>>()(capturedSettings);
            capturedService.OnSettingsChanged += Raise.Event<Action>();
            return Task.CompletedTask;
        });
        var webSettings = new WebSettings { IsSetupComplete = true };
        var webSettingsService = Substitute.For<IWebSettingsService>();
        webSettingsService.Settings.Returns(webSettings);
        webSettingsService.UpdateAsync(Arg.Any<Action<WebSettings>>()).Returns(callInfo =>
        {
            callInfo.Arg<Action<WebSettings>>()(webSettings);
            webSettingsService.OnSettingsChanged += Raise.Event<Action>();
            return Task.CompletedTask;
        });
        var timeZoneService = Substitute.For<ITimeZoneService>();
        timeZoneService.TimeZone.Returns(TimeZoneInfo.Utc);
        context.Services.AddSingleton(settingsService);
        context.Services.AddSingleton(webSettingsService);
        context.Services.AddSingleton(timeZoneService);
        return context;
    }

    private sealed class M3uTestVirtualTunerDriver : IVirtualTunerDriver
    {
        string IVirtualTunerDriver.Kind => "m3u";

        string IVirtualTunerDriver.DisplayName => "M3U Provider";

        bool IVirtualTunerDriver.SupportsUserCreation => true;

        AdapterConfiguration IVirtualTunerDriver.CreateDefaultConfiguration(VirtualDeviceId deviceId) =>
            new("m3u", JsonSerializer.SerializeToElement(new { PlaylistName = "Lineup" }));

        IReadOnlyList<string> IVirtualTunerDriver.Validate(AdapterConfiguration configuration) => [];
    }

    private sealed class XmltvTestGuideSourceDriver : IGuideSourceDriver
    {
        string IGuideSourceDriver.Kind => "xmltv";

        string IGuideSourceDriver.DisplayName => "XMLTV Provider";

        bool IGuideSourceDriver.SupportsUserCreation => true;

        AdapterConfiguration IGuideSourceDriver.CreateDefaultConfiguration(GuideSourceId sourceId) =>
            new("xmltv", JsonSerializer.SerializeToElement(new { }));

        bool IGuideSourceDriver.SupportsPhysicalAdapter(string physicalAdapterKind) =>
            string.Equals(physicalAdapterKind, "m3u", StringComparison.OrdinalIgnoreCase);

        IReadOnlyList<string> IGuideSourceDriver.Validate(AdapterConfiguration configuration) => [];

        ValueTask<IGuideSource> IGuideSourceDriver.CreateAsync(GuideSourceId sourceId, AdapterConfiguration configuration, IReadOnlyList<PhysicalDeviceId> physicalDeviceIds, CancellationToken cancellationToken) =>
            ValueTask.FromException<IGuideSource>(new NotSupportedException("Web tests do not create guide source runtimes."));
    }
}
