using Bunit;
using System.Text.Json;
using Lineup.GuideOutputs.Json.Plugin;
using Lineup.GuideOutputs.Xmltv.Plugin;
using Lineup.LineupLink.Protocol;
using Lineup.LineupLink.Virtual;
using Lineup.Web.Components.Transcoding;
using Lineup.Web.Components.Pages;
using Lineup.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Lineup.Web.Tests.Components.Pages;

/// <summary>
/// Verifies topology-specific Settings presentation.
/// </summary>
public sealed class TopologySettingsTests
{
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
        using var context = CreateContext(out var settingsService, out var settings);
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddSingleton<IVirtualTunerDriver, LineupLinkVirtualTunerDriver>();
        var component = context.Render<Settings>();
        var initialConfiguration = settings.Streaming;
        component.Find("#tab-virtual-devices").Click();
        if (kind == "web-player")
        {
            component.Find(".virtual-device-tab").Click();
        }
        else
        {
            component.Find("#addVirtualDevice").Click();
            component.Find($"[data-virtual-device-kind='{kind}']").Click();
        }
        var index = kind == "web-player" ? 0 : 1;
        var device = component.Instance.Topology.VirtualDevices.Single(candidate => candidate.Kind == kind);
        component.Find($"#dedicatedListener-{index}").Change(true);
        component.Find($"#workerRestarts-{index}").Change(2);
        if (manualOverride)
        {
            component.Find($"#listenerPublicUriOverride-{index}").Change(true);
            component.Find($"#listenerPublicUri-{index}").Change("http://public.example:9600/");
        }

        // Act
        component.Find($"#listenerPort-{index}").Change(65_536);
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
        component.Find($"#listenerPort-{index}").Change(9800);
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
    public void Render_EmptyTopology_ShowsAddGuidance()
    {
        // Arrange
        using var context = CreateContext(out _, out _);
        var component = context.Render<Settings>();

        // Act
        component.Find("#tab-guide").Click();

        // Assert
        Assert.Contains("No guide sources are configured", component.Find("#guideSourcesEmptyState").TextContent);

        component.Find("#tab-physical-devices").Click();

        Assert.Contains("No physical devices are configured", component.Find("#physicalDevicesEmptyState").TextContent);

        component.Find("#tab-virtual-devices").Click();

        Assert.Contains("No additional virtual devices are configured", component.Find("#virtualDevicesEmptyState").TextContent);
        Assert.Contains("built-in Web Player remains available", component.Find("#virtualDevicesEmptyState").TextContent);
    }

    /// <summary>
    /// Verifies empty virtual-device guidance does not claim that a disabled Web Player is available.
    /// </summary>
    [Fact]
    public void Render_EmptyVirtualDevicesWithDisabledWebPlayer_ShowsEnableGuidance()
    {
        // Arrange
        using var context = CreateContext(out _, out var settings);
        var editor = new TopologySettingsEditor();
        var model = editor.Edit(settings.Streaming);
        model.VirtualDevices.Single(device => device.IsWebPlayer).Enabled = false;
        Assert.Empty(editor.TryBuild(model, out var configuration));
        settings.Streaming = configuration;

        // Act
        var component = context.Render<Settings>();
        component.Find("#tab-virtual-devices").Click();

        // Assert
        var message = component.Find("#virtualDevicesEmptyState").TextContent;
        Assert.Contains("Enable the built-in Web Player", message);
        Assert.DoesNotContain("Web Player remains available", message);
    }

    /// <summary>
    /// Verifies global physical settings appear first and inherited values remain visible on each device.
    /// </summary>
    [Fact]
    public void Render_Topology_ShowsBuiltInPlayerAndInheritedPhysicalValue()
    {
        // Arrange
        using var context = CreateContext(out _, out var settings);
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
        component.Find("#tab-physical-devices").Click();

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

        component.Find(".physical-device-tab").Click();

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

        component.Find("#tab-virtual-devices").Click();

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

        component.Find(".virtual-device-tab").Click();

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
    public void AddVirtualDevice_RegisteredDrivers_PromptsForTypeAndAddsSelection()
    {
        // Arrange
        using var context = CreateContext(out _, out _);
        context.Services.AddSingleton<IVirtualTunerDriver, M3uTestVirtualTunerDriver>();
        var component = context.Render<Settings>();
        component.Find("#tab-virtual-devices").Click();

        // Act
        component.Find("#addVirtualDevice").Click();

        // Assert
        var picker = component.Find("#virtualDeviceTypePicker");
        var options = picker.QuerySelectorAll(".virtual-device-type");
        Assert.Equal(2, options.Length);
        Assert.Contains(options, option => option.GetAttribute("data-virtual-device-kind") == "hdhomerun");
        var option = Assert.Single(options, option => option.GetAttribute("data-virtual-device-kind") == "m3u");
        Assert.Contains("M3U Provider", option.TextContent);

        option.Click();

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
        using var context = CreateContext(out _, out _);
        context.Services.AddSingleton<IVirtualTunerDriver, LineupLinkVirtualTunerDriver>();
        context.JSInterop.SetupVoid("downloadTextFile", _ => true).SetVoidResult();
        var component = context.Render<Settings>();
        component.Find("#tab-virtual-devices").Click();
        component.Find("#addVirtualDevice").Click();
        component.Find("[data-virtual-device-kind='lineup-link']").Click();
        component.Find("#linkPublicUrl-1").Change("https://lineup.example:8443");
        component.Find("#newLineupLinkGrantName-1").Change("Export access");
        component.Find("#createLineupLinkGrant-1").Click();

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
        });
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
        using var context = CreateContext(out _, out _);
        context.Services.AddSingleton<IVirtualTunerDriver, LineupLinkVirtualTunerDriver>();
        context.JSInterop.SetupVoid("downloadTextFile", _ => true).SetVoidResult();
        var component = context.Render<Settings>();
        component.Find("#tab-virtual-devices").Click();
        component.Find("#addVirtualDevice").Click();
        component.Find("[data-virtual-device-kind='lineup-link']").Click();
        component.Find("#newLineupLinkGrantName-1").Change("Pending address");
        component.Find("#createLineupLinkGrant-1").Click();

        // Act
        await component.Find(".lineup-link-access-grant button[id^='exportLineupLinkBundle-']").ClickAsync(new());

        // Assert
        component.WaitForAssertion(() =>
        {
            var invocation = Assert.Single(context.JSInterop.Invocations, item => item.Identifier == "downloadTextFile");
            using var json = JsonDocument.Parse(Assert.IsType<string>(invocation.Arguments[2]));
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("baseUrl").ValueKind);
        });
    }

    /// <summary>Verifies dedicated Lineup Link outputs derive HTTP or HTTPS from the listener instead of presenting a second transport switch.</summary>
    [Fact]
    public async Task ConfigureDedicatedLineupLink_ListenerOwnsTransportSelection()
    {
        // Arrange
        using var context = CreateContext(out _, out _);
        context.Services.AddSingleton<IVirtualTunerDriver, LineupLinkVirtualTunerDriver>();
        context.JSInterop.SetupVoid("downloadTextFile", _ => true).SetVoidResult();
        var component = context.Render<Settings>();
        component.Find("#tab-virtual-devices").Click();
        component.Find("#addVirtualDevice").Click();
        component.Find("[data-virtual-device-kind='lineup-link']").Click();

        // Act
        Assert.NotNull(component.Find("#linkEncryption-1"));
        component.Find("#dedicatedListener-1").Change(true);

        // Assert
        Assert.Empty(component.FindAll("#linkEncryption-1"));
        Assert.Contains("HTTP", component.Find(".lineup-link-listener-transport").TextContent);
        Assert.Empty(component.FindAll("#linkServerCertificate-1"));
        var hostingText = component.Find(".virtual-output-hosting-settings").TextContent;
        Assert.DoesNotContain("}", hostingText, StringComparison.Ordinal);
        Assert.Contains("Managed processes are launched and supervised by Lineup", hostingText);
        Assert.Contains("Port opened by the worker", hostingText);
        Assert.Contains("Concurrent client limit", hostingText);
        Assert.Contains("forwarded client addresses and schemes", hostingText);
        Assert.Contains("Failed worker restarts allowed", hostingText);
        Assert.Contains("active requests to finish", hostingText);
        Assert.Contains("authenticated host lease", hostingText);
        Assert.Contains("simultaneous capability requests", hostingText);
        Assert.Contains("Worker memory ceiling", hostingText);
        Assert.Contains("Worker CPU ceiling", hostingText);
        var advancedNetworking = component.Find(".virtual-output-advanced-networking");
        Assert.NotNull(advancedNetworking.QuerySelector("#listenerNetworks-1"));
        Assert.NotNull(advancedNetworking.QuerySelector("#listenerConnections-1"));
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
        component.Find("#listenerTransport-1").Change(VirtualOutputTransport.Https);

        // Assert
        Assert.Contains("HTTPS", component.Find(".lineup-link-listener-transport").TextContent);
        Assert.NotNull(component.Find("#linkServerCertificate-1"));
        Assert.Contains("Connection bundle certificate pin", component.Find("label[for='linkServerCertificate-1']").TextContent);
        component.Find("#listenerPublicUriOverride-1").Change(true);
        component.Find("#listenerPublicUri-1").Change("https://lineup.example:8600/");
        component.Find("#newLineupLinkGrantName-1").Change("Dedicated access");
        component.Find("#createLineupLinkGrant-1").Click();

        // Act
        await component.Find(".lineup-link-access-grant button[id^='exportLineupLinkBundle-']").ClickAsync(new());

        // Assert
        component.WaitForAssertion(() =>
        {
            var invocation = Assert.Single(context.JSInterop.Invocations, item => item.Identifier == "downloadTextFile");
            var bundle = LineupLinkConnectionBundleCodec.Deserialize(Assert.IsType<string>(invocation.Arguments[2]));
            Assert.Equal("https://lineup.example:8600/", bundle.BaseUrl);
            Assert.Equal(LineupLinkTransportSecurity.Https, bundle.TransportSecurity);
        });
    }

    /// <summary>Verifies a selected access-grant expiration survives subsequent component rerenders.</summary>
    [Fact]
    public void SetLineupLinkGrantExpiration_DateTimePicker_PersistsSelection()
    {
        // Arrange
        using var context = CreateContext(out _, out _);
        context.Services.AddSingleton<IVirtualTunerDriver, LineupLinkVirtualTunerDriver>();
        var component = context.Render<Settings>();
        component.Find("#tab-virtual-devices").Click();
        component.Find("#addVirtualDevice").Click();
        component.Find("[data-virtual-device-kind='lineup-link']").Click();
        var expiration = new DateTime(2030, 1, 2, 3, 4, 0, DateTimeKind.Unspecified);

        // Act
        component.Find("#newLineupLinkGrantExpiration-1").Input("2030-01-02T03:04");
        component.Find("#newLineupLinkGrantName-1").Change("Expiration test");

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
        using var context = CreateContext(out _, out _);
        context.Services.AddSingleton<IVirtualTunerDriver, LineupLinkVirtualTunerDriver>();
        context.JSInterop.SetupVoid("copyTextToClipboard", _ => true).SetVoidResult();
        var component = context.Render<Settings>();
        component.Find("#tab-virtual-devices").Click();
        component.Find("#addVirtualDevice").Click();
        component.Find("[data-virtual-device-kind='lineup-link']").Click();
        component.Find("#linkPublicUrl-1").Change("https://lineup.example:8443");
        component.Find("#newLineupLinkGrantName-1").Change("Copy access");
        component.Find("#createLineupLinkGrant-1").Click();
        component.Find(".lineup-link-access-grant button[id^='rotateLineupLinkGrant-']").Click();

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
        });
    }

    /// <summary>
    /// Verifies persisted Lineup Link devices can always show a bundle with the unavailable token redacted.
    /// </summary>
    [Fact]
    public void ShowLineupLinkBundle_PersistedTokenHash_ShowsRedactedJson()
    {
        // Arrange
        using var context = CreateContext(out _, out var settings);
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
        component.Find("#tab-virtual-devices").Click();
        component.FindAll(".virtual-device-tab").Single(tab => tab.TextContent.Contains("Lineup Link", StringComparison.Ordinal)).Click();

        // Act
        component.Find(".lineup-link-access-grant button[id^='showLineupLinkBundle-']").Click();

        // Assert
        Assert.True(component.Find(".lineup-link-access-grant button[id^='exportLineupLinkBundle-']").HasAttribute("disabled"));
        Assert.True(component.Find(".lineup-link-access-grant button[id^='copyLineupLinkBundle-']").HasAttribute("disabled"));
        Assert.Equal("true", component.Find(".lineup-link-access-grant button[id^='showLineupLinkBundle-']").GetAttribute("aria-expanded"));
        Assert.Contains(LineupLinkConnectionBundleCodec.RedactedAccessToken, component.Markup);
        Assert.Contains("The token is redacted.", component.Find(".lineup-link-access-grant").TextContent);
        component.Find(".lineup-link-access-grant button[id^='showLineupLinkBundle-']").Click();
        Assert.Equal("false", component.Find(".lineup-link-access-grant button[id^='showLineupLinkBundle-']").GetAttribute("aria-expanded"));
        Assert.Empty(component.FindAll(".lineup-link-access-grant pre.connection-bundle-json"));
    }

    /// <summary>Verifies a final revoked grant can be permanently purged to disable remote access.</summary>
    [Fact]
    public void ManageLineupLinkAccessGrants_PurgeFinalGrant_DisablesRemoteAccess()
    {
        // Arrange
        using var context = CreateContext(out _, out _);
        context.Services.AddSingleton<IVirtualTunerDriver, LineupLinkVirtualTunerDriver>();
        var component = context.Render<Settings>();
        component.Find("#tab-virtual-devices").Click();
        component.Find("#addVirtualDevice").Click();
        component.Find("[data-virtual-device-kind='lineup-link']").Click();
        component.Find("#linkPublicUrl-1").Change("https://lineup.example:8443");
        component.Find("#newLineupLinkGrantName-1").Change("Bob");

        // Act
        component.Find("#createLineupLinkGrant-1").Click();
        component.Find(".lineup-link-access-grant button[id^='revokeLineupLinkGrant-']").Click();
        var revokedGrant = component.Find(".lineup-link-access-grant");
        revokedGrant.QuerySelector("button[id^='purgeLineupLinkGrant-']")!.Click();
        component.Find("button[id^='confirmPurgeLineupLinkGrant-']").Click();
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
    public void Render_VirtualDeviceTabs_PinsWebPlayerBeforeConfiguredDevices()
    {
        // Arrange
        using var context = CreateContext(out _, out var settings);
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
        component.Find("#tab-virtual-devices").Click();

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
    public void AddPhysicalDevice_RegisteredDrivers_PromptsForTypeAndAddsSelection()
    {
        // Arrange
        using var context = CreateContext(out _, out _);
        context.Services.AddSingleton<IPhysicalTunerDriver>(new TestPhysicalTunerDriver("m3u", "M3U Source", ""));
        var component = context.Render<Settings>();
        component.Find("#tab-physical-devices").Click();

        // Act
        component.Find("#addPhysicalDevice").Click();

        // Assert
        var picker = component.Find("#physicalDeviceTypePicker");
        var options = picker.QuerySelectorAll(".physical-device-type");
        Assert.Equal(2, options.Length);
        Assert.Contains(options, option => option.GetAttribute("data-physical-device-kind") == "hdhomerun");
        var option = Assert.Single(options, option => option.GetAttribute("data-physical-device-kind") == "m3u");
        Assert.Contains("M3U Source", option.TextContent);

        option.Click();

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
    public void GuideSources_AddSource_DefaultsToAllAndSupportsSelectedDevices()
    {
        // Arrange
        using var context = CreateContext(out _, out var settings);
        var editor = new TopologySettingsEditor();
        var model = editor.Edit(settings.Streaming);
        editor.AddPhysicalDevice(model, new TestPhysicalTunerDriver());
        model.PhysicalDevices[0].Name = "Roof Antenna";
        model.PhysicalDevices[0].Address = "tuner.local";
        Assert.Empty(editor.TryBuild(model, out var configuration));
        settings.Streaming = configuration;
        var component = context.Render<Settings>();
        component.Find("#tab-guide").Click();

        // Act
        component.Find("#addGuideSource").Click();
        component.Find(".add-guide-source-type").Click();

        // Assert
        Assert.Empty(component.FindAll("#guideSourceTypePicker"));
        Assert.Single(component.FindAll(".guide-source-tab"));
        Assert.Single(component.FindAll(".guide-source"));
        Assert.True(component.Find(".guide-source-all").HasAttribute("checked"));
        Assert.Contains("SiliconDust 1", component.Find(".guide-source-tab").TextContent);
        Assert.Empty(component.FindAll("#guideSourcesEmptyState"));

        // Act
        component.Find(".guide-source-selected").Change(true);

        // Assert
        var physical = component.Find(".guide-source-physical");
        Assert.Contains("Roof Antenna", physical.ParentElement!.TextContent);
        Assert.False(physical.HasAttribute("checked"));
    }

    /// <summary>
    /// Verifies About Settings follows global and instance subtabs and identifies dynamically registered adapter types.
    /// </summary>
    [Fact]
    public void AboutSettings_TopologySubtabs_ShowsSelectedAdapterContext()
    {
        // Arrange
        using var context = CreateContext(out _, out _);
        context.Services.AddSingleton<IPhysicalTunerDriver>(new TestPhysicalTunerDriver("m3u", "M3U Source", ""));
        context.Services.AddSingleton<IVirtualTunerDriver, M3uTestVirtualTunerDriver>();
        context.Services.AddSingleton<IGuideSourceDriver, XmltvTestGuideSourceDriver>();
        var component = context.Render<Settings>();

        // Act
        component.Find("#tab-physical-devices").Click();

        // Assert
        Assert.Contains("Refresh Settings", component.Find("#aboutSettingsBody").TextContent);
        Assert.Contains("source-stage defaults", component.Find("#aboutSettingsBody").TextContent);
        Assert.DoesNotContain("Video Quality (CRF)", component.Find("#aboutSettingsBody").TextContent);
        Assert.DoesNotContain("Non-AC-4 Audio", component.Find("#aboutSettingsBody").TextContent);

        component.Find("#addPhysicalDevice").Click();
        component.Find("[data-physical-device-kind='m3u']").Click();
        component.Find("#physicalName-0").Change("Playlist Antenna");

        Assert.Contains("Playlist Antenna", component.Find("#aboutSettingsBody").TextContent);
        Assert.Contains("M3U Source", component.Find("#aboutSettingsBody").TextContent);
        Assert.Contains("Plugin-provided connection and source options", component.Find("#aboutSettingsBody").TextContent);
        Assert.Contains("Transcoder Settings", component.Find("#aboutSettingsBody").TextContent);

        component.Find("#tab-guide").Click();

        Assert.Contains("EPG Fetch Settings", component.Find("#aboutSettingsBody").TextContent);
        Assert.DoesNotContain("XMLTV Output Settings", component.Find("#aboutSettingsBody").TextContent);

        component.Find("#addGuideSource").Click();
        component.Find("[data-guide-source-kind='xmltv']").Click();
        component.Find("#guideSourceName-0").Change("Regional XMLTV");

        Assert.Contains("Regional XMLTV", component.Find("#aboutSettingsBody").TextContent);
        Assert.Contains("XMLTV Provider", component.Find("#aboutSettingsBody").TextContent);
        Assert.DoesNotContain("Physical Device Subscription", component.Find("#aboutSettingsBody").TextContent);
        Assert.DoesNotContain("All compatible enabled devices", component.Find("#aboutSettingsBody").TextContent);

        component.Find("#tab-guide-outputs").Click();

        Assert.Contains("Guide Outputs", component.Find("#aboutSettingsBody").TextContent);
        Assert.DoesNotContain("Reset Settings", component.Find("#aboutSettingsBody").TextContent);

        component.Find("#tab-virtual-devices").Click();

        Assert.Contains("Transcoder Settings", component.Find("#aboutSettingsBody").TextContent);
        Assert.Contains("Virtual Tuner Discovery", component.Find("#aboutSettingsBody").TextContent);
        Assert.DoesNotContain("Video Encoder Preset", component.Find("#aboutSettingsBody").TextContent);
        Assert.DoesNotContain("AC-4 Audio", component.Find("#aboutSettingsBody").TextContent);

        component.Find("#addVirtualDevice").Click();
        component.Find("[data-virtual-device-kind='m3u']").Click();
        component.Find("#virtualName-1").Change("Living Room Playlist");

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
    public void PhysicalDeviceTabs_DeviceLifecycle_TracksSelectionAndLabel()
    {
        // Arrange
        using var context = CreateContext(out _, out _);
        var component = context.Render<Settings>();
        component.Find("#tab-physical-devices").Click();

        // Act
        component.Find("#addPhysicalDevice").Click();
        component.Find(".physical-device-type").Click();

        // Assert
        var deviceTab = component.Find(".physical-device-tab");
        Assert.Contains("active", deviceTab.ClassList);
        Assert.Empty(component.FindAll("#physicalGlobalSettings"));
        Assert.Single(component.FindAll(".physical-device"));

        component.Find("#physicalName-0").Change("Roof Antenna");

        Assert.Contains("Roof Antenna", component.Find(".physical-device-tab").TextContent);

        component.Find("#physicalEnabled-0").Change(false);

        Assert.Contains("Disabled", component.Find(".physical-device-tab").TextContent);

        component.Find(".remove-physical-device").Click();

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
        using var context = CreateContext(out var settingsService, out var settings);
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
        component.Find("#tab-physical-devices").Click();
        component.Find("#addPhysicalDevice").Click();
        component.Find(".physical-device-type").Click();
        component.Find("#physicalAddress-0").Change("unsaved.local");

        // Act
        await component.Find("#testPhysicalDevice-0").ClickAsync(new());

        // Assert
        component.WaitForAssertion(() =>
        {
            Assert.Contains("Connection successful", component.Find(".physical-device-probe-success").TextContent);
            Assert.Contains("ABCDEF01", component.Find(".physical-device-probe-success").TextContent);
            Assert.Contains("unsaved.local", component.Find(".physical-device-probe-success").TextContent);
        });
        Assert.Equal("unsaved.local", driver.ProbedConfiguration?.Configuration.GetProperty("Address").GetString());
        Assert.Equal("HDHomeRun 1", component.Find("#physicalName-0").GetAttribute("value"));
        await settingsService.DidNotReceive().UpdateAsync(Arg.Any<Action<LineupSettings>>());
        Assert.Empty(settings.Streaming.PhysicalDevices);

        component.Find(".use-discovered-name").Click();

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
        using var context = CreateContext(out _, out _);
        var driver = Assert.IsType<TestPhysicalTunerDriver>(context.Services.GetServices<IPhysicalTunerDriver>().Single(candidate => candidate.Kind == "hdhomerun"));
        driver.Probe = (_, _) => ValueTask.FromException<PhysicalDeviceProbeResult>(new HttpRequestException("Device refused the connection."));
        var component = context.Render<Settings>();
        component.Find("#tab-physical-devices").Click();
        component.Find("#addPhysicalDevice").Click();
        component.Find(".physical-device-type").Click();

        // Act
        await component.Find("#testPhysicalDevice-0").ClickAsync(new());

        // Assert
        component.WaitForAssertion(() => Assert.Contains("Device refused the connection.", component.Find(".physical-device-probe-error").TextContent));
        Assert.Contains("Test & Identify", component.Find("#aboutSettingsBody").TextContent);

        driver.Probe = (_, _) => ValueTask.FromResult(new PhysicalDeviceProbeResult
        {
            Details = [new PhysicalDeviceProbeDetail { Label = "Device ID", Value = "ABCDEF01" }]
        });
        await component.Find("#testPhysicalDevice-0").ClickAsync(new());
        component.WaitForAssertion(() => Assert.NotNull(component.Find(".physical-device-probe-success")));

        component.Find("#physicalAddress-0").Change("changed.local");

        Assert.Empty(component.FindAll(".physical-device-probe-success"));
        Assert.Empty(component.FindAll(".physical-device-probe-error"));
    }

    /// <summary>
    /// Verifies global and effective per-device settings identify configurations that request transcoding.
    /// </summary>
    [Fact]
    public void Render_TranscodingConfiguration_ShowsGlobalAndDeviceWarnings()
    {
        // Arrange
        using var context = CreateContext(out _, out var settings);
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
        component.Find("#tab-physical-devices").Click();

        // Assert
        Assert.Empty(component.FindAll(".physical-global-transcode-warning"));

        component.Find("#physicalDefaultHevc").Change(true);

        Assert.Single(component.FindAll(".physical-global-transcode-warning"));

        component.Find(".physical-device-tab").Click();

        Assert.Single(component.FindAll(".physical-device-transcode-warning"));

        component.Find("#tab-virtual-devices").Click();

        Assert.Single(component.FindAll(".virtual-global-transcode-warning"));

        component.Find(".virtual-device-tab").Click();

        Assert.Single(component.FindAll(".virtual-device-transcode-warning"));

        component.Find("#virtualSubtabGlobal").Click();
        component.Find("#virtualDefaultHevc").Change(true);

        Assert.Single(component.FindAll(".virtual-global-transcode-warning"));
    }

    /// <summary>
    /// Verifies physical encoder controls appear only when the effective profile requests video transcoding.
    /// </summary>
    [Fact]
    public void Render_PhysicalVideoTranscoding_ShowsEncoderControlsOnlyWhenApplicable()
    {
        // Arrange
        using var context = CreateContext(out _, out _);
        var component = context.Render<Settings>();
        component.Find("#tab-physical-devices").Click();

        Assert.Empty(component.FindAll(".physical-global-video-encoder-settings"));
        Assert.NotNull(component.Find("#physicalDefaultAudio"));

        component.Find("#addPhysicalDevice").Click();
        component.Find(".physical-device-type").Click();

        // Act and Assert
        Assert.Empty(component.FindAll("#physicalOverridePreset-0"));
        Assert.NotNull(component.Find("#physicalOverrideAudio-0"));

        component.Find("#physicalSubtabGlobal").Click();
        component.Find("#physicalDefaultHevc").Change(true);

        Assert.Single(component.FindAll(".physical-global-video-encoder-settings"));
        Assert.NotNull(component.Find("#physicalDefaultPreset"));
        Assert.NotNull(component.Find("#physicalDefaultQuality"));
        Assert.NotNull(component.Find("#physicalDefaultBitrate"));

        component.Find(".physical-device-tab").Click();

        Assert.NotNull(component.Find("#physicalOverridePreset-0"));
        Assert.NotNull(component.Find("#physicalOverrideQuality-0"));
        Assert.NotNull(component.Find("#physicalOverrideBitrate-0"));

        component.Find("#physicalOverrideHevc-0").Change(true);

        Assert.Empty(component.FindAll("#physicalOverridePreset-0"));

        component.Find("#physicalOverrideDeinterlace-0").Change(true);
        component.Find(".physical-deinterlace").Change(nameof(SourceDeinterlacePolicy.SourceFrameRate));

        Assert.NotNull(component.Find("#physicalOverridePreset-0"));
    }

    /// <summary>
    /// Verifies disabled physical and virtual devices collapse while their header toggles remain available.
    /// </summary>
    [Fact]
    public void DisableDevice_CollapsesDetailsAndKeepsHeaderToggle()
    {
        // Arrange
        using var context = CreateContext(out _, out _);
        var component = context.Render<Settings>();
        component.Find("#tab-physical-devices").Click();
        component.Find("#addPhysicalDevice").Click();
        component.Find(".physical-device-type").Click();

        // Act
        component.Find("#physicalEnabled-0").Change(false);

        // Assert
        Assert.NotNull(component.Find(".physical-device .card-header #physicalEnabled-0"));
        Assert.Empty(component.FindAll(".physical-device-details"));
        Assert.Empty(component.FindAll("#physicalName-0"));

        component.Find("#tab-virtual-devices").Click();
        component.Find(".virtual-device-tab").Click();
        component.Find("#virtualEnabled-0").Change(false);

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
        using var context = CreateContext(out var settingsService, out var settings);
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var component = context.Render<Settings>();
        component.Find("#tab-physical-devices").Click();
        component.Find("#addPhysicalDevice").Click();
        component.Find(".physical-device-type").Click();
        component.Find("#physicalName-0").Change("Antenna");
        component.Find("#physicalAddress-0").Change("antenna.local");
        component.Find("#tab-virtual-devices").Click();
        component.Find(".virtual-device-tab").Click();
        component.Find("#virtualSubscription-0").Change(nameof(PhysicalDeviceSubscriptionMode.Selected));
        component.Find(".physical-subscription-selection").Change(true);

        // Act
        await component.Find("button.btn-primary").ClickAsync(new());

        // Assert
        var player = Assert.Single(settings.Streaming.VirtualDevices);
        Assert.Equal(PhysicalDeviceSubscriptionMode.Selected, player.Subscription.Mode);
        Assert.Equal(settings.Streaming.PhysicalDevices[0].Id, Assert.Single(player.Subscription.SelectedDeviceIds));
        await settingsService.Received(1).UpdateAsync(Arg.Any<Action<LineupSettings>>());
    }

    /// <summary>Verifies a non-XMLTV Guide Output renders and persists its plugin-owned portable settings.</summary>
    [Fact]
    public async Task Save_NonXmltvGuideOutput_PersistsPortableSettings()
    {
        // Arrange
        using var context = CreateContext(out var settingsService, out var settings);
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var component = context.Render<Settings>();
        component.Find("#tab-guide-outputs").Click();
        component.Find("#addGuideOutput").Click();
        component.Find($"[data-guide-output-kind='{JsonGuideOutputDriver.AdapterKind}']").Click();
        var outputDirectory = Path.GetFullPath("json-guide-output");

        // Act
        component.Find("[data-setting-key='outputDirectoryPath'] input").Change(outputDirectory);
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
        using var context = CreateContext(out var settingsService, out var settings);
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
        component.Find("#tab-guide-outputs").Click();
        if (existingOutput)
        {
            var output = settings.Streaming.GuideOutputs.Single(candidate => candidate.Adapter.Kind == driver.Kind);
            component.Find($".guide-output-tab[data-guide-output-id='{output.Id}']").Click();
        }
        else
        {
            component.Find("#addGuideOutput").Click();
            component.Find($"[data-guide-output-kind='{driver.Kind}']").Click();
        }
        var outputDirectory = Path.GetFullPath("changed-json-guide-output");
        component.Find("[data-setting-key='outputDirectoryPath'] input").Change(outputDirectory);

        // Act
        if (failFirstSave)
        {
            await component.Find("button.btn-primary").ClickAsync(new());
            Assert.Same(initialConfiguration, settings.Streaming);
            Assert.Equal(outputDirectory, component.Find("[data-setting-key='outputDirectoryPath'] input").GetAttribute("value"));
        }
        await component.Find("button.btn-primary").ClickAsync(new());
        var firstOutput = settings.Streaming.GuideOutputs.Single(candidate => candidate.Adapter.Kind == driver.Kind);
        component.Find("#tab-guide").Click();
        component.Find("#epgHistoryRetentionHours").Change(48);
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
        using var context = CreateContext(out var settingsService, out var settings);
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var component = context.Render<Settings>();
        component.Find("#tab-physical-devices").Click();
        component.Find("#addPhysicalDevice").Click();
        component.Find(".physical-device-type").Click();
        component.Find("#physicalAddress-0").Change("antenna.local");
        component.Find("#tab-virtual-devices").Click();
        component.Find(".virtual-device-tab").Click();
        component.Find("#virtualSubscription-0").Change(nameof(PhysicalDeviceSubscriptionMode.Selected));
        component.Find(".physical-subscription-selection").Change(true);
        await component.Find("button.btn-primary").ClickAsync(new());
        var savedConfiguration = settings.Streaming;
        var savedPlayer = Assert.Single(savedConfiguration.VirtualDevices);
        var physicalId = Assert.Single(savedConfiguration.PhysicalDevices).Id;

        // Act
        component.Find(".physical-subscription-selection").Change(false);
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
        settings = new LineupSettings { IsSetupComplete = true };
        settingsService = Substitute.For<ILineupSettingsService>();
        settingsService.Settings.Returns(settings);
        var capturedSettings = settings;
        settingsService.UpdateAsync(Arg.Any<Action<LineupSettings>>()).Returns(callInfo =>
        {
            callInfo.Arg<Action<LineupSettings>>()(capturedSettings);
            return Task.CompletedTask;
        });
        var timeZoneService = Substitute.For<ITimeZoneService>();
        timeZoneService.TimeZone.Returns(TimeZoneInfo.Utc);
        context.Services.AddSingleton(settingsService);
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

        ValueTask<IGuideSource> IGuideSourceDriver.CreateAsync(
            GuideSourceId sourceId,
            AdapterConfiguration configuration,
            IReadOnlyList<PhysicalDeviceId> physicalDeviceIds,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<IGuideSource>(new NotSupportedException("Web tests do not create guide source runtimes."));
    }
}
