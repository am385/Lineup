using Bunit;
using Lineup.App.Web.Components.Pages;
using Lineup.App.Web.Components.Settings;
using Lineup.App.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Lineup.App.Web.Tests.Components.Pages;

/// <summary>
/// Verifies transcode settings behavior on the Settings page.
/// </summary>
public class SettingsTests
{
    /// <summary>Verifies settings actions retain bottom spacing on long tabs during setup and normal use.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Render_SettingsActions_ReservesBottomSpacing(bool isSetupComplete)
    {
        // Arrange
        await using var context = new BunitContext();
        var settingsService = Substitute.For<ILineupSettingsService>();
        settingsService.Settings.Returns(new LineupSettings { IsSetupComplete = isSetupComplete });
        var timeZoneService = Substitute.For<ITimeZoneService>();
        timeZoneService.TimeZone.Returns(TimeZoneInfo.Utc);
        context.Services.AddSingleton(settingsService);
        context.Services.AddSingleton(timeZoneService);
        var component = context.Render<Settings>();

        // Act
        await component.Find("#tab-virtual-devices").ClickAsync(new());

        // Assert
        var actions = component.Find("#settingsContent fieldset > .d-flex");
        Assert.Contains("pb-4", actions.ClassList);
        Assert.Equal(3, actions.QuerySelectorAll("button").Length);
        Assert.NotNull(actions.QuerySelector("button.btn-primary"));
        Assert.NotNull(actions.QuerySelector("#undoSettings"));
    }

    /// <summary>Verifies device tabs precede guide tabs during setup and normal use.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Render_SettingsTabs_PlacesDevicesBeforeGuides(bool isSetupComplete)
    {
        // Arrange
        await using var context = new BunitContext();
        var settingsService = Substitute.For<ILineupSettingsService>();
        settingsService.Settings.Returns(new LineupSettings { IsSetupComplete = isSetupComplete });
        var timeZoneService = Substitute.For<ITimeZoneService>();
        timeZoneService.TimeZone.Returns(TimeZoneInfo.Utc);
        context.Services.AddSingleton(settingsService);
        context.Services.AddSingleton(timeZoneService);
        string[] expectedTabIds = isSetupComplete
            ? ["tab-general", "tab-dashboard", "tab-physical-devices", "tab-virtual-devices", "tab-guide", "tab-guide-outputs", "tab-streaming", "tab-api", "tab-logging", "tab-reset"]
            : ["tab-general", "tab-dashboard", "tab-physical-devices", "tab-virtual-devices", "tab-guide", "tab-guide-outputs", "tab-streaming"];

        // Act
        var component = context.Render<Settings>();

        // Assert
        Assert.Equal(expectedTabIds, component.FindAll("#settingsCategoryTabs [role='tab']").Select(tab => tab.Id));
    }

    /// <summary>
    /// Verifies every top-level settings category is rendered through its dedicated tab component.
    /// </summary>
    [Fact]
    public async Task Render_CompletedSetup_ComposesDedicatedTabComponents()
    {
        // Arrange
        await using var context = new BunitContext();
        var settingsService = Substitute.For<ILineupSettingsService>();
        settingsService.Settings.Returns(new LineupSettings { IsSetupComplete = true });
        var timeZoneService = Substitute.For<ITimeZoneService>();
        timeZoneService.TimeZone.Returns(TimeZoneInfo.Utc);
        context.Services.AddSingleton(settingsService);
        context.Services.AddSingleton(timeZoneService);

        // Act
        var component = context.Render<Settings>();

        // Assert
        Assert.Single(component.FindComponents<GeneralSettingsTab>());
        Assert.Single(component.FindComponents<GuideSourcesSettingsTab>());
        Assert.Single(component.FindComponents<GuideOutputsSettingsTab>());
        Assert.Single(component.FindComponents<PhysicalDevicesSettingsTab>());
        Assert.Single(component.FindComponents<VirtualDevicesSettingsTab>());
        Assert.Single(component.FindComponents<StreamingSettingsTab>());
        Assert.Single(component.FindComponents<DashboardSettingsTab>());
        Assert.Single(component.FindComponents<ApiSettingsTab>());
        Assert.Single(component.FindComponents<LoggingSettingsTab>());
        Assert.Single(component.FindComponents<ResetSettingsTab>());
    }

    /// <summary>
    /// Verifies API privacy settings render with safe defaults and persist edits.
    /// </summary>
    [Fact]
    public async Task Save_ApiPrivacySettings_PersistsValues()
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
        await component.Find("#tab-api").ClickAsync(new());
        var clientRedactionDefault = component.Find("#redactApiClientAddresses").HasAttribute("checked");

        await component.Find("#redactApiClientAddresses").ChangeAsync(false);
        await component.Find("#redactApiDeviceAddresses").ChangeAsync(true);
        // Act
        await component.Find("button.btn-primary").ClickAsync(new());

        // Assert
        Assert.True(clientRedactionDefault);
        Assert.False(settings.RedactApiClientAddresses);
        Assert.True(settings.RedactApiDeviceAddresses);
        Assert.False(settings.RedactApiUrls);
        Assert.Contains("/api/v1/status", component.Markup);
    }

    /// <summary>Verifies the API settings tab links to the generated API documentation.</summary>
    [Fact]
    public async Task Render_ApiSettings_ShowsDocumentationLink()
    {
        // Arrange
        await using var context = new BunitContext();
        var settingsService = Substitute.For<ILineupSettingsService>();
        settingsService.Settings.Returns(new LineupSettings { IsSetupComplete = true });
        var timeZoneService = Substitute.For<ITimeZoneService>();
        timeZoneService.TimeZone.Returns(TimeZoneInfo.Utc);
        context.Services.AddSingleton(settingsService);
        context.Services.AddSingleton(timeZoneService);
        var component = context.Render<Settings>();

        // Act
        await component.Find("#tab-api").ClickAsync(new());

        // Assert
        var link = component.Find(".api-documentation-link");
        Assert.Equal("/api-docs", link.GetAttribute("href"));
        Assert.Equal("_blank", link.GetAttribute("target"));
        Assert.Contains("API Documentation", component.Find(".api-documentation-card").TextContent, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that a differing saved XMLTV destination is editable and can be reset to startup configuration.
    /// </summary>
    [Fact]
    public async Task Render_GuideTab_DifferingXmltvDestination_ShowsNoticeAndResets()
    {
        // Arrange
        await using var context = new BunitContext();
        var settingsService = Substitute.For<ILineupSettingsService>();
        settingsService.Settings.Returns(new LineupSettings
        {
            IsSetupComplete = true,
            XmltvOutputPath = "untrusted-user-value.xml"
        });
        settingsService.ConfiguredXmltvOutputPath.Returns("/xmltv/epg.xml");
        var timeZoneService = Substitute.For<ITimeZoneService>();
        timeZoneService.TimeZone.Returns(TimeZoneInfo.Utc);
        context.Services.AddSingleton(settingsService);
        context.Services.AddSingleton(timeZoneService);
        var component = context.Render<Settings>();
        await component.Find("#tab-guide-outputs").ClickAsync(new());
        var outputPathBeforeReset = component.Find("#guideOutputDirectoryPath");
        var wasEditable = !outputPathBeforeReset.HasAttribute("readonly");
        var savedValue = outputPathBeforeReset.GetAttribute("value");
        var showedMismatch = component.FindAll("#xmltvPathMismatch").Count == 1;

        // Act
        await component.Find("#resetXmltvOutputPath").ClickAsync(new());

        // Assert
        Assert.True(wasEditable);
        Assert.Equal(Path.GetDirectoryName(Path.GetFullPath("untrusted-user-value.xml")), savedValue);
        Assert.True(showedMismatch);
        Assert.Equal(
            Path.GetDirectoryName(Path.GetFullPath("/xmltv/epg.xml")),
            component.Find("#guideOutputDirectoryPath").GetAttribute("value"));
        Assert.Empty(component.FindAll("#xmltvPathMismatch"));
    }

    /// <summary>Verifies the global XMLTV output advertises its stable endpoint without a guide output identifier.</summary>
    [Fact]
    public async Task Render_GlobalXmltvOutput_ShowsStableApiEndpoint()
    {
        // Arrange
        await using var context = new BunitContext();
        var settingsService = Substitute.For<ILineupSettingsService>();
        settingsService.Settings.Returns(new LineupSettings { IsSetupComplete = true });
        var timeZoneService = Substitute.For<ITimeZoneService>();
        timeZoneService.TimeZone.Returns(TimeZoneInfo.Utc);
        context.Services.AddSingleton(settingsService);
        context.Services.AddSingleton(timeZoneService);
        var absoluteEndpoint = context.Services.GetRequiredService<NavigationManager>().ToAbsoluteUri("/api/xmltv").AbsoluteUri;
        context.JSInterop.SetupVoid("copyTextToClipboard", absoluteEndpoint).SetVoidResult();
        var component = context.Render<Settings>();

        // Act
        await component.Find("#tab-guide-outputs").ClickAsync(new());
        await component.Find(".guide-output-copy-link").ClickAsync(new());

        // Assert
        Assert.Single(component.FindAll(".guide-output-endpoints"));
        Assert.Single(component.FindAll(".guide-output-endpoint-row"));
        Assert.Equal(absoluteEndpoint, component.Find(".guide-output-api-endpoint").TextContent);
        var viewLink = component.Find(".guide-output-view-link");
        Assert.Equal("/api/xmltv?download=false", viewLink.GetAttribute("href"));
        Assert.Equal("_blank", viewLink.GetAttribute("target"));
        Assert.Equal("/api/xmltv?download=true", component.Find(".guide-output-download-link").GetAttribute("href"));
        Assert.Contains(context.JSInterop.Invocations, invocation =>
            invocation.Identifier == "copyTextToClipboard" &&
            Assert.IsType<string>(invocation.Arguments[0]) == absoluteEndpoint);
    }

    /// <summary>Verifies additional XMLTV outputs default beside the configured global XMLTV file.</summary>
    [Fact]
    public async Task AddGuideOutput_ConfiguredXmltvDirectory_DefaultsOutputThere()
    {
        // Arrange
        await using var context = new BunitContext();
        var directory = Path.GetFullPath(Path.Combine("configured-guide-outputs"));
        var settingsService = Substitute.For<ILineupSettingsService>();
        settingsService.Settings.Returns(new LineupSettings
        {
            IsSetupComplete = true,
            XmltvOutputPath = Path.Combine(directory, AppConstants.DefaultXmltvFileName)
        });
        var timeZoneService = Substitute.For<ITimeZoneService>();
        timeZoneService.TimeZone.Returns(TimeZoneInfo.Utc);
        context.Services.AddSingleton(settingsService);
        context.Services.AddSingleton(timeZoneService);
        var component = context.Render<Settings>();
        await component.Find("#tab-guide-outputs").ClickAsync(new());

        // Act
        await component.Find("#addGuideOutput").ClickAsync(new());
        await component.Find(".add-guide-output-type").ClickAsync(new());
        var guideOutputId = component.Find(".guide-output-settings").GetAttribute("data-guide-output-id");
        var absoluteStableIdEndpoint = context.Services.GetRequiredService<NavigationManager>().ToAbsoluteUri($"/api/guide-outputs/{guideOutputId}").AbsoluteUri;
        context.JSInterop.SetupVoid("copyTextToClipboard", absoluteStableIdEndpoint).SetVoidResult();
        await component.Find(".guide-output-stable-id-copy-link").ClickAsync(new());

        // Assert
        Assert.Single(component.FindAll(".guide-output-endpoints"));
        Assert.Equal(2, component.FindAll(".guide-output-endpoint-row").Count);
        Assert.Equal(directory, component.Find("#guideOutputDirectoryPath").GetAttribute("value"));
        Assert.Equal("xmltv_2", component.Find(".guide-output-key").GetAttribute("value"));
        var absoluteKeyEndpoint = context.Services.GetRequiredService<NavigationManager>().ToAbsoluteUri("/api/guide-outputs/xmltv_2").AbsoluteUri;
        Assert.Equal(absoluteKeyEndpoint, component.Find(".guide-output-api-endpoint").TextContent);
        Assert.Equal("/api/guide-outputs/xmltv_2?download=false", component.Find(".guide-output-view-link").GetAttribute("href"));
        Assert.Equal("/api/guide-outputs/xmltv_2?download=true", component.Find(".guide-output-download-link").GetAttribute("href"));
        Assert.Equal(absoluteStableIdEndpoint, component.Find(".guide-output-stable-id-endpoint").TextContent);
        Assert.Equal($"/api/guide-outputs/{guideOutputId}?download=false", component.Find(".guide-output-stable-id-view-link").GetAttribute("href"));
        Assert.Equal($"/api/guide-outputs/{guideOutputId}?download=true", component.Find(".guide-output-stable-id-download-link").GetAttribute("href"));
        Assert.Contains(context.JSInterop.Invocations, invocation =>
            invocation.Identifier == "copyTextToClipboard" &&
            Assert.IsType<string>(invocation.Arguments[0]) == absoluteStableIdEndpoint);
        Assert.Contains("/api/guide-outputs/", component.Find(".guide-output-settings").TextContent, StringComparison.Ordinal);
        Assert.Contains("lineup-xmltv_2.xml", component.Find("#resolvedXmltvOutputPath").TextContent, StringComparison.Ordinal);

        await component.Find(".guide-output-key").ChangeAsync("My XMLTV 2!");

        Assert.Equal("my_xmltv_2", component.Find(".guide-output-key").GetAttribute("value"));
        var updatedAbsoluteKeyEndpoint = context.Services.GetRequiredService<NavigationManager>().ToAbsoluteUri("/api/guide-outputs/my_xmltv_2").AbsoluteUri;
        Assert.Equal(updatedAbsoluteKeyEndpoint, component.Find(".guide-output-api-endpoint").TextContent);
        Assert.Equal("/api/guide-outputs/my_xmltv_2?download=false", component.Find(".guide-output-view-link").GetAttribute("href"));
        Assert.Equal("/api/guide-outputs/my_xmltv_2?download=true", component.Find(".guide-output-download-link").GetAttribute("href"));
        Assert.Equal(absoluteStableIdEndpoint, component.Find(".guide-output-stable-id-endpoint").TextContent);
        Assert.Equal($"/api/guide-outputs/{guideOutputId}?download=false", component.Find(".guide-output-stable-id-view-link").GetAttribute("href"));
        Assert.Equal($"/api/guide-outputs/{guideOutputId}?download=true", component.Find(".guide-output-stable-id-download-link").GetAttribute("href"));
        Assert.Contains("lineup-my_xmltv_2.xml", component.Find("#resolvedXmltvOutputPath").TextContent, StringComparison.Ordinal);
        Assert.Contains("Existing clients", component.Find(".guide-output-key-warning").TextContent, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that an edited XMLTV destination is persisted with the other settings.
    /// </summary>
    [Fact]
    public async Task Save_EditedXmltvPath_PersistsValue()
    {
        // Arrange
        await using var context = new BunitContext();
        var settings = new LineupSettings
        {
            IsSetupComplete = true,
            XmltvOutputPath = "/xmltv/epg.xml"
        };
        var settingsService = Substitute.For<ILineupSettingsService>();
        settingsService.Settings.Returns(settings);
        settingsService.ConfiguredXmltvOutputPath.Returns("/xmltv/epg.xml");
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
        await component.Find("#tab-guide-outputs").ClickAsync(new());

        // Act
        await component.Find("#guideOutputDirectoryPath").ChangeAsync("/custom");
        await component.Find("button.btn-primary").ClickAsync(new());

        // Assert
        Assert.Equal(Path.Combine("/custom", AppConstants.DefaultXmltvFileName), settings.XmltvOutputPath);
        await settingsService.Received(1).UpdateAsync(Arg.Any<Action<LineupSettings>>());
    }

    /// <summary>
    /// Verifies that an empty XMLTV destination cannot be saved.
    /// </summary>
    [Fact]
    public async Task Save_EmptyXmltvPath_ShowsValidationError()
    {
        // Arrange
        await using var context = new BunitContext();
        var settingsService = Substitute.For<ILineupSettingsService>();
        settingsService.Settings.Returns(new LineupSettings { IsSetupComplete = true });
        settingsService.ConfiguredXmltvOutputPath.Returns("/xmltv/epg.xml");
        var timeZoneService = Substitute.For<ITimeZoneService>();
        timeZoneService.TimeZone.Returns(TimeZoneInfo.Utc);
        context.Services.AddSingleton(settingsService);
        context.Services.AddSingleton(timeZoneService);
        var component = context.Render<Settings>();
        await component.Find("#tab-guide-outputs").ClickAsync(new());
        await component.Find("#guideOutputDirectoryPath").ChangeAsync(" ");

        // Act
        await component.Find("button.btn-primary").ClickAsync(new());

        // Assert
        var notifications = context.Services.GetRequiredService<IStatusNotificationService>();
        var notification = Assert.Single(notifications.Notifications);
        Assert.Equal("Guide output Global XMLTV requires an output destination.", notification.Message);
        Assert.True(notification.IsError);
        await settingsService.DidNotReceive().UpdateAsync(Arg.Any<Action<LineupSettings>>());
    }

    /// <summary>Verifies the Virtual Devices tab exposes topology discovery without legacy proxy controls.</summary>
    [Fact]
    public async Task Render_VirtualDevicesTab_ShowsTopologyDiscoveryOnly()
    {
        // Arrange
        await using var context = new BunitContext();
        var settingsService = Substitute.For<ILineupSettingsService>();
        settingsService.Settings.Returns(new LineupSettings { IsSetupComplete = true });
        var timeZoneService = Substitute.For<ITimeZoneService>();
        timeZoneService.TimeZone.Returns(TimeZoneInfo.Utc);
        context.Services.AddSingleton(settingsService);
        context.Services.AddSingleton(timeZoneService);
        var component = context.Render<Settings>();

        // Act
        await component.Find("#tab-virtual-devices").ClickAsync(new());

        // Assert
        Assert.NotNull(component.Find("#networkDiscoverySettings"));
        Assert.NotNull(component.Find("#enableNetworkDiscovery"));
        Assert.Empty(component.FindAll("#enableHdHomeRunProxy"));
        Assert.Empty(component.FindAll(".proxy-profile"));
    }

    /// <summary>
    /// Verifies the General tab separates visual and regional preferences.
    /// </summary>
    [Fact]
    public async Task Render_GeneralSettings_SeparatesAppearanceAndRegionalCards()
    {
        // Arrange
        await using var context = new BunitContext();
        var settingsService = Substitute.For<ILineupSettingsService>();
        settingsService.Settings.Returns(new LineupSettings { IsSetupComplete = true });
        var timeZoneService = Substitute.For<ITimeZoneService>();
        timeZoneService.TimeZone.Returns(TimeZoneInfo.Utc);
        context.Services.AddSingleton(settingsService);
        context.Services.AddSingleton(timeZoneService);

        // Act
        var component = context.Render<Settings>();

        // Assert
        Assert.NotNull(component.Find("#appearanceSettings #themeAuto"));
        Assert.Empty(component.FindAll("#appearanceSettings #timeZone"));
        Assert.Contains("Regional Settings", component.Find("#regionalSettings").TextContent);
        Assert.NotNull(component.Find("#regionalSettings #timeZone"));
        Assert.Equal("en", component.Find("#regionalSettings #uiLanguage").GetAttribute("value"));
        Assert.Contains("English", component.Find("#regionalSettings #uiLanguage").TextContent);
        Assert.Contains("español", component.Find("#regionalSettings #uiLanguage").TextContent);
        Assert.Contains("Deutsch", component.Find("#regionalSettings #uiLanguage").TextContent);
        Assert.Contains("português (Brasil)", component.Find("#regionalSettings #uiLanguage").TextContent);
        Assert.Equal(10, component.FindAll("#regionalSettings #uiLanguage option").Count);
        Assert.Equal("127.0.0.1\n172.18.0.0/16", component.Find("#knownProxies").GetAttribute("placeholder"));
    }

    /// <summary>
    /// Verifies that category tabs switch visible content, contextual help, and the URL fragment.
    /// </summary>
    [Fact]
    public async Task Tabs_SwitchVisiblePaneHelpAndUrlFragment()
    {
        // Arrange
        await using var context = new BunitContext();
        var settingsService = Substitute.For<ILineupSettingsService>();
        settingsService.Settings.Returns(new LineupSettings { IsSetupComplete = true });
        var timeZoneService = Substitute.For<ITimeZoneService>();
        timeZoneService.TimeZone.Returns(TimeZoneInfo.Utc);
        context.Services.AddSingleton(settingsService);
        context.Services.AddSingleton(timeZoneService);
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        var component = context.Render<Settings>();
        var tabButtons = component.FindAll("ul.nav-tabs > li > button");

        // Act
        await component.Find("#tab-streaming").ClickAsync(new());

        // Assert
        Assert.Equal("tab-dashboard", tabButtons[1].Id);
        Assert.Contains("active", component.Find("#tab-streaming").ClassList);
        Assert.DoesNotContain("d-none", component.Find("#streamingSettingsTab").ClassList);
        Assert.Contains("d-none", component.Find("#generalSettingsTab").ClassList);
        Assert.Contains("stream admission, shared-pipeline retention", component.Find("#aboutSettingsBody").TextContent);
        Assert.EndsWith("/settings#streaming", navigation.Uri, StringComparison.Ordinal);

        await component.Find("#tab-dashboard").ClickAsync(new());

        Assert.Contains("active", component.Find("#tab-dashboard").ClassList);
        Assert.DoesNotContain("d-none", component.Find("#dashboardSettingsTab").ClassList);
        Assert.Contains("d-none", component.Find("#streamingSettingsTab").ClassList);
        Assert.Contains("Dashboard refreshes its cached view", component.Find("#aboutSettingsBody").TextContent);
        Assert.EndsWith("/settings#dashboard", navigation.Uri, StringComparison.Ordinal);
        Assert.NotNull(component.Find("button.btn-primary"));
    }

    /// <summary>
    /// Verifies every About Settings entry uses the same heading and description structure.
    /// </summary>
    [Fact]
    public async Task AboutSettings_AllTabs_UsesConsistentSectionFormatting()
    {
        // Arrange
        await using var context = new BunitContext();
        var settingsService = Substitute.For<ILineupSettingsService>();
        settingsService.Settings.Returns(new LineupSettings { IsSetupComplete = true });
        var timeZoneService = Substitute.For<ITimeZoneService>();
        timeZoneService.TimeZone.Returns(TimeZoneInfo.Utc);
        context.Services.AddSingleton(settingsService);
        context.Services.AddSingleton(timeZoneService);
        var component = context.Render<Settings>();

        // Act
        var generalSections = component.FindAll("#aboutSettingsBody .about-settings-section");
        await component.Find("#tab-api").ClickAsync(new());
        var apiSections = component.FindAll("#aboutSettingsBody .about-settings-section");
        await component.Find("#tab-logging").ClickAsync(new());
        var loggingSections = component.FindAll("#aboutSettingsBody .about-settings-section");

        // Assert
        Assert.Equal(3, generalSections.Count);
        Assert.Contains(generalSections, section => section.TextContent.Contains("Network Settings", StringComparison.Ordinal));
        Assert.Equal(2, apiSections.Count);
        Assert.Contains(apiSections, section => section.TextContent.Contains("API Documentation", StringComparison.Ordinal));
        Assert.Equal(3, loggingSections.Count);
        Assert.All(generalSections.Concat(apiSections).Concat(loggingSections), section =>
        {
            Assert.Equal(2, section.Children.Length);
            Assert.Equal("H6", section.Children[0].TagName);
            Assert.Contains("fw-semibold", section.Children[0].ClassList);
            Assert.Contains("mb-1", section.Children[0].ClassList);
            Assert.Equal("DIV", section.Children[1].TagName);
            Assert.Contains("mb-0", section.Children[1].ClassList);
            Assert.Empty(section.QuerySelectorAll("strong"));
        });
    }

    /// <summary>
    /// Verifies that a settings URL fragment restores the selected category.
    /// </summary>
    [Fact]
    public async Task Initialize_UsesTabFromUrlFragment()
    {
        // Arrange
        await using var context = new BunitContext();
        var settingsService = Substitute.For<ILineupSettingsService>();
        settingsService.Settings.Returns(new LineupSettings { IsSetupComplete = true });
        var timeZoneService = Substitute.For<ITimeZoneService>();
        timeZoneService.TimeZone.Returns(TimeZoneInfo.Utc);
        context.Services.AddSingleton(settingsService);
        context.Services.AddSingleton(timeZoneService);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/settings#physical-devices");

        // Act
        var component = context.Render<Settings>();

        // Assert
        Assert.Contains("active", component.Find("#tab-physical-devices").ClassList);
        Assert.DoesNotContain("d-none", component.Find("#physicalDevicesSettingsTab").ClassList);
        Assert.Equal(2, component.FindAll("#physicalGlobalSettings > .card").Count);
    }

    /// <summary>
    /// Verifies the legacy transcoding fragment opens the renamed Streaming tab.
    /// </summary>
    [Fact]
    public async Task Initialize_LegacyTranscodingFragment_UsesStreamingTab()
    {
        // Arrange
        await using var context = new BunitContext();
        var settingsService = Substitute.For<ILineupSettingsService>();
        settingsService.Settings.Returns(new LineupSettings { IsSetupComplete = true });
        var timeZoneService = Substitute.For<ITimeZoneService>();
        timeZoneService.TimeZone.Returns(TimeZoneInfo.Utc);
        context.Services.AddSingleton(settingsService);
        context.Services.AddSingleton(timeZoneService);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/settings#transcoding");

        // Act
        var component = context.Render<Settings>();

        // Assert
        Assert.Contains("active", component.Find("#tab-streaming").ClassList);
        Assert.DoesNotContain("d-none", component.Find("#streamingSettingsTab").ClassList);
    }

    /// <summary>
    /// Verifies complete transcoder controls render in the physical and virtual Global Settings cards.
    /// </summary>
    [Fact]
    public async Task Render_ShowsCompleteTranscoderControlsInDeviceGlobalSettings()
    {
        // Arrange
        await using var context = new BunitContext();
        var settingsService = Substitute.For<ILineupSettingsService>();
        settingsService.Settings.Returns(new LineupSettings { IsSetupComplete = true });
        var timeZoneService = Substitute.For<ITimeZoneService>();
        timeZoneService.TimeZone.Returns(TimeZoneInfo.Utc);
        context.Services.AddSingleton(settingsService);
        context.Services.AddSingleton(timeZoneService);

        // Act
        var component = context.Render<Settings>();

        // Assert
        var physical = component.Find("#physicalGlobalSettings");
        Assert.NotNull(physical.QuerySelector("#physicalDefaultHevc"));
        Assert.NotNull(physical.QuerySelector("#physicalDefaultDeinterlace"));
        Assert.NotNull(physical.QuerySelector("#physicalDefaultAudio"));
        Assert.NotNull(physical.QuerySelector("#physicalDefaultAc4Audio"));
        Assert.Null(physical.QuerySelector("#physicalDefaultPreset"));
        Assert.Null(physical.QuerySelector("#physicalDefaultQuality"));
        Assert.Null(physical.QuerySelector("#physicalDefaultBitrate"));

        await component.Find("#physicalDefaultHevc").ChangeAsync(true);
        physical = component.Find("#physicalGlobalSettings");

        Assert.NotNull(physical.QuerySelector("#physicalDefaultPreset"));
        Assert.NotNull(physical.QuerySelector("#physicalDefaultQuality"));
        Assert.NotNull(physical.QuerySelector("#physicalDefaultBitrate"));

        var virtualSettings = component.Find("#virtualGlobalSettings");
        Assert.NotNull(virtualSettings.QuerySelector("#virtualDefaultHevc"));
        Assert.NotNull(virtualSettings.QuerySelector("#virtualDefaultDeinterlace"));
        Assert.NotNull(virtualSettings.QuerySelector("#virtualDefaultAudio"));
        Assert.NotNull(virtualSettings.QuerySelector("#virtualDefaultAc4Audio"));
        Assert.Null(virtualSettings.QuerySelector("#virtualDefaultPreset"));
        Assert.Null(virtualSettings.QuerySelector("#virtualDefaultQuality"));
        Assert.Null(virtualSettings.QuerySelector("#virtualDefaultBitrate"));

        await component.Find("#virtualDefaultHevc").ChangeAsync(true);
        virtualSettings = component.Find("#virtualGlobalSettings");

        Assert.NotNull(virtualSettings.QuerySelector("#virtualDefaultPreset"));
        Assert.NotNull(virtualSettings.QuerySelector("#virtualDefaultQuality"));
        Assert.NotNull(virtualSettings.QuerySelector("#virtualDefaultBitrate"));

        Assert.Empty(component.FindAll("#audioTranscodeMode, #ac4TranscodeTarget, #virtualTunerVideoMode, #sourceDeinterlaceMode, #webVideoPreset, #webVideoQuality"));
        Assert.Equal("settingsContent", component.Find("ul.nav-tabs").NextElementSibling?.Id);
    }

    /// <summary>
    /// Verifies that saving the page persists both transcode selections.
    /// </summary>
    [Fact]
    public async Task Save_PersistsTranscodeSelections()
    {
        // Arrange
        await using var context = new BunitContext();
        var settings = new LineupSettings { IsSetupComplete = true };
        var settingsService = Substitute.For<ILineupSettingsService>();
        settingsService.Settings.Returns(settings);
        settingsService.UpdateAsync(Arg.Any<Action<LineupSettings>>())
            .Returns(callInfo =>
            {
                callInfo.Arg<Action<LineupSettings>>()(settings);
                return Task.CompletedTask;
            });
        var webSettings = new WebSettings();
        var webSettingsService = Substitute.For<IWebSettingsService>();
        webSettingsService.Settings.Returns(webSettings);
        webSettingsService.UpdateAsync(Arg.Any<Action<WebSettings>>())
            .Returns(callInfo =>
            {
                callInfo.Arg<Action<WebSettings>>()(webSettings);
                return Task.CompletedTask;
            });

        var timeZoneService = Substitute.For<ITimeZoneService>();
        timeZoneService.TimeZone.Returns(TimeZoneInfo.Utc);
        context.Services.AddSingleton(settingsService);
        context.Services.AddSingleton(webSettingsService);
        context.Services.AddSingleton(timeZoneService);
        context.JSInterop.Mode = JSRuntimeMode.Loose;

        var component = context.Render<Settings>();

        await component.Find("#tab-physical-devices").ClickAsync(new());
        await component.Find("#physicalDefaultHevc").ChangeAsync(true);
        await component.Find("#physicalDefaultDeinterlace").ChangeAsync(nameof(SourceDeinterlacePolicy.SourceFrameRate));
        await component.Find("#physicalDefaultAudio").ChangeAsync(nameof(OutputAudioPolicy.Ac3));
        await component.Find("#physicalDefaultAc4Audio").ChangeAsync(nameof(OutputAudioPolicy.Eac3));
        await component.Find("#physicalDefaultPreset").ChangeAsync(nameof(VideoEncoderPreset.Fast));
        await component.Find("#physicalDefaultQuality").ChangeAsync("18");
        await component.Find("#physicalDefaultBitrate").ChangeAsync("16");

        await component.Find("#tab-virtual-devices").ClickAsync(new());
        await component.Find("#virtualDefaultHevc").ChangeAsync(true);
        await component.Find("#virtualDefaultDeinterlace").ChangeAsync(nameof(SourceDeinterlacePolicy.SourceFieldRate));
        await component.Find("#virtualDefaultAudio").ChangeAsync(nameof(OutputAudioPolicy.Eac3));
        await component.Find("#virtualDefaultAc4Audio").ChangeAsync(nameof(OutputAudioPolicy.Eac3));
        await component.Find("#virtualDefaultPreset").ChangeAsync(nameof(VideoEncoderPreset.Faster));
        await component.Find("#virtualDefaultQuality").ChangeAsync("19");
        await component.Find("#virtualDefaultBitrate").ChangeAsync("14");

        await component.Find("#tab-streaming").ClickAsync(new());
        await component.Find("#protectedContentMode").ChangeAsync(nameof(ProtectedContentMode.StreamSlate));
        await component.Find("#maximumConcurrentStreams").ChangeAsync("6");
        await component.Find("#keepWarmPeriodSeconds").ChangeAsync("15");

        await component.Find("#tab-dashboard").ClickAsync(new());
        await component.Find("#activeStreamRefreshInterval").ChangeAsync("15");
        await component.Find("#tab-general").ClickAsync(new());
        await component.Find("#uiLanguage").ChangeAsync("es");
        // Act
        await component.Find("button.btn-primary").ClickAsync(new());

        // Assert
        Assert.Equal(ProtectedContentMode.StreamSlate, settings.ProtectedContentMode);
        Assert.True(settings.Streaming.PhysicalDefaults.ConvertHevcToH264);
        Assert.Equal(SourceDeinterlacePolicy.SourceFrameRate, settings.Streaming.PhysicalDefaults.Deinterlace);
        Assert.Equal(OutputAudioPolicy.Ac3, settings.Streaming.PhysicalDefaults.Audio);
        Assert.Equal(OutputAudioPolicy.Eac3, settings.Streaming.PhysicalDefaults.Ac4Audio);
        Assert.Equal(VideoEncoderPreset.Fast, settings.Streaming.PhysicalDefaults.VideoPreset);
        Assert.Equal(18, settings.Streaming.PhysicalDefaults.VideoQuality);
        Assert.Equal(16, settings.Streaming.PhysicalDefaults.MaximumVideoBitRateMbps);
        Assert.True(settings.Streaming.VirtualDefaults.ConvertHevcToH264);
        Assert.Equal(SourceDeinterlacePolicy.SourceFieldRate, settings.Streaming.VirtualDefaults.Deinterlace);
        Assert.Equal(OutputAudioPolicy.Eac3, settings.Streaming.VirtualDefaults.Audio);
        Assert.Equal(OutputAudioPolicy.Eac3, settings.Streaming.VirtualDefaults.Ac4Audio);
        Assert.Equal(VideoEncoderPreset.Faster, settings.Streaming.VirtualDefaults.VideoPreset);
        Assert.Equal(19, settings.Streaming.VirtualDefaults.VideoQuality);
        Assert.Equal(14, settings.Streaming.VirtualDefaults.MaximumVideoBitRateMbps);
        Assert.Equal(6, settings.MaximumConcurrentStreams);
        Assert.Equal(15, settings.Streaming.KeepWarmPeriodSeconds);
        Assert.Equal(15, webSettings.ActiveStreamRefreshIntervalSeconds);
        Assert.Equal("es", webSettings.UiCulture);
        await settingsService.Received(1).UpdateAsync(Arg.Any<Action<LineupSettings>>());
        await webSettingsService.Received(1).UpdateAsync(Arg.Any<Action<WebSettings>>());
    }

    /// <summary>
    /// Verifies that resetting the page restores defaults without persisting them.
    /// </summary>
    [Fact]
    public async Task Reset_RestoresTranscodeDefaultsWithoutSaving()
    {
        // Arrange
        await using var context = new BunitContext();
        var settings = new LineupSettings
        {
            IsSetupComplete = true,
            ProtectedContentMode = ProtectedContentMode.StreamSlate,
            MaximumConcurrentStreams = 8,
            ActiveStreamRefreshIntervalSeconds = 30,
            OverrideLoggingDefaults = true,
            ApplicationLogLevel = "Debug",
            LogCategoryOverrides = [new LogCategoryLevelSetting { Category = "Lineup.Noisy", Level = "None" }]
        };
        var settingsService = Substitute.For<ILineupSettingsService>();
        settingsService.Settings.Returns(settings);
        var timeZoneService = Substitute.For<ITimeZoneService>();
        timeZoneService.TimeZone.Returns(TimeZoneInfo.Utc);
        context.Services.AddSingleton(settingsService);
        context.Services.AddSingleton(timeZoneService);

        var component = context.Render<Settings>();
        await component.Find("#tab-reset").ClickAsync(new());

        // Act
        await component.Find("#resetSettings").ClickAsync(new());
        await component.Find("#tab-physical-devices").ClickAsync(new());

        Assert.False(component.Find("#physicalDefaultHevc").HasAttribute("checked"));
        Assert.Equal(nameof(SourceDeinterlacePolicy.Preserve), component.Find("#physicalDefaultDeinterlace").GetAttribute("value"));
        Assert.Equal(nameof(OutputAudioPolicy.Copy), component.Find("#physicalDefaultAudio").GetAttribute("value"));
        Assert.Equal(nameof(OutputAudioPolicy.Copy), component.Find("#physicalDefaultAc4Audio").GetAttribute("value"));

        await component.Find("#physicalDefaultHevc").ChangeAsync(true);

        Assert.Equal(nameof(VideoEncoderPreset.VeryFast), component.Find("#physicalDefaultPreset").GetAttribute("value"));
        Assert.Equal("21", component.Find("#physicalDefaultQuality").GetAttribute("value"));
        Assert.Equal("10", component.Find("#physicalDefaultBitrate").GetAttribute("value"));

        await component.Find("#tab-virtual-devices").ClickAsync(new());
        Assert.False(component.Find("#virtualDefaultHevc").HasAttribute("checked"));
        Assert.Equal(nameof(SourceDeinterlacePolicy.Preserve), component.Find("#virtualDefaultDeinterlace").GetAttribute("value"));
        Assert.Equal(nameof(OutputAudioPolicy.Copy), component.Find("#virtualDefaultAudio").GetAttribute("value"));
        Assert.Equal(nameof(OutputAudioPolicy.Ac3), component.Find("#virtualDefaultAc4Audio").GetAttribute("value"));

        await component.Find("#virtualDefaultHevc").ChangeAsync(true);

        Assert.Equal(nameof(VideoEncoderPreset.VeryFast), component.Find("#virtualDefaultPreset").GetAttribute("value"));
        Assert.Equal("21", component.Find("#virtualDefaultQuality").GetAttribute("value"));
        Assert.Equal("10", component.Find("#virtualDefaultBitrate").GetAttribute("value"));

        await component.Find("#tab-streaming").ClickAsync(new());
        await component.Find("#tab-dashboard").ClickAsync(new());
        await component.Find("#tab-logging").ClickAsync(new());

        // Assert
        Assert.Equal(nameof(ProtectedContentMode.ReturnError), component.Find("#protectedContentMode").GetAttribute("value"));
        Assert.Equal("0", component.Find("#maximumConcurrentStreams").GetAttribute("value"));
        Assert.Equal("0", component.Find("#keepWarmPeriodSeconds").GetAttribute("value"));
        Assert.Equal("5", component.Find("#activeStreamRefreshInterval").GetAttribute("value"));
        Assert.False(component.Find("#overrideLoggingDefaults").HasAttribute("checked"));
        Assert.Empty(component.FindAll("#applicationLogLevel"));
        await settingsService.DidNotReceive().UpdateAsync(Arg.Any<Action<LineupSettings>>());
    }
}
