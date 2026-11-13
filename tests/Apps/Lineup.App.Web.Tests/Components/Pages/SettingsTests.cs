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
    public void Render_SettingsActions_ReservesBottomSpacing(bool isSetupComplete)
    {
        // Arrange
        using var context = new BunitContext();
        var settingsService = Substitute.For<ILineupSettingsService>();
        settingsService.Settings.Returns(new LineupSettings { IsSetupComplete = isSetupComplete });
        var timeZoneService = Substitute.For<ITimeZoneService>();
        timeZoneService.TimeZone.Returns(TimeZoneInfo.Utc);
        context.Services.AddSingleton(settingsService);
        context.Services.AddSingleton(timeZoneService);
        var component = context.Render<Settings>();

        // Act
        component.Find("#tab-virtual-devices").Click();

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
    public void Render_SettingsTabs_PlacesDevicesBeforeGuides(bool isSetupComplete)
    {
        // Arrange
        using var context = new BunitContext();
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
    public void Render_CompletedSetup_ComposesDedicatedTabComponents()
    {
        // Arrange
        using var context = new BunitContext();
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
    public void Save_ApiPrivacySettings_PersistsValues()
    {
        // Arrange
        using var context = new BunitContext();
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
        component.Find("#tab-api").Click();
        var clientRedactionDefault = component.Find("#redactApiClientAddresses").HasAttribute("checked");

        component.Find("#redactApiClientAddresses").Change(false);
        component.Find("#redactApiDeviceAddresses").Change(true);
        // Act
        component.Find("button.btn-primary").Click();

        // Assert
        Assert.True(clientRedactionDefault);
        Assert.False(settings.RedactApiClientAddresses);
        Assert.True(settings.RedactApiDeviceAddresses);
        Assert.False(settings.RedactApiUrls);
        Assert.Contains("/api/v1/status", component.Markup);
    }

    /// <summary>Verifies the API settings tab links to the generated API documentation.</summary>
    [Fact]
    public void Render_ApiSettings_ShowsDocumentationLink()
    {
        // Arrange
        using var context = new BunitContext();
        var settingsService = Substitute.For<ILineupSettingsService>();
        settingsService.Settings.Returns(new LineupSettings { IsSetupComplete = true });
        var timeZoneService = Substitute.For<ITimeZoneService>();
        timeZoneService.TimeZone.Returns(TimeZoneInfo.Utc);
        context.Services.AddSingleton(settingsService);
        context.Services.AddSingleton(timeZoneService);
        var component = context.Render<Settings>();

        // Act
        component.Find("#tab-api").Click();

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
    public void Render_GuideTab_DifferingXmltvDestination_ShowsNoticeAndResets()
    {
        // Arrange
        using var context = new BunitContext();
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
        component.Find("#tab-guide-outputs").Click();
        var outputPathBeforeReset = component.Find("#guideOutputDirectoryPath");
        var wasEditable = !outputPathBeforeReset.HasAttribute("readonly");
        var savedValue = outputPathBeforeReset.GetAttribute("value");
        var showedMismatch = component.FindAll("#xmltvPathMismatch").Count == 1;

        // Act
        component.Find("#resetXmltvOutputPath").Click();

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
    public void Render_GlobalXmltvOutput_ShowsStableApiEndpoint()
    {
        // Arrange
        using var context = new BunitContext();
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
        component.Find("#tab-guide-outputs").Click();
        component.Find(".guide-output-copy-link").Click();

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
    public void AddGuideOutput_ConfiguredXmltvDirectory_DefaultsOutputThere()
    {
        // Arrange
        using var context = new BunitContext();
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
        component.Find("#tab-guide-outputs").Click();

        // Act
        component.Find("#addGuideOutput").Click();
        component.Find(".add-guide-output-type").Click();
        var guideOutputId = component.Find(".guide-output-settings").GetAttribute("data-guide-output-id");
        var absoluteStableIdEndpoint = context.Services.GetRequiredService<NavigationManager>().ToAbsoluteUri($"/api/guide-outputs/{guideOutputId}").AbsoluteUri;
        context.JSInterop.SetupVoid("copyTextToClipboard", absoluteStableIdEndpoint).SetVoidResult();
        component.Find(".guide-output-stable-id-copy-link").Click();

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

        component.Find(".guide-output-key").Change("My XMLTV 2!");

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
    public void Save_EditedXmltvPath_PersistsValue()
    {
        // Arrange
        using var context = new BunitContext();
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
        component.Find("#tab-guide-outputs").Click();

        // Act
        component.Find("#guideOutputDirectoryPath").Change("/custom");
        component.Find("button.btn-primary").Click();

        // Assert
        Assert.Equal(Path.Combine("/custom", AppConstants.DefaultXmltvFileName), settings.XmltvOutputPath);
        settingsService.Received(1).UpdateAsync(Arg.Any<Action<LineupSettings>>());
    }

    /// <summary>
    /// Verifies that an empty XMLTV destination cannot be saved.
    /// </summary>
    [Fact]
    public void Save_EmptyXmltvPath_ShowsValidationError()
    {
        // Arrange
        using var context = new BunitContext();
        var settingsService = Substitute.For<ILineupSettingsService>();
        settingsService.Settings.Returns(new LineupSettings { IsSetupComplete = true });
        settingsService.ConfiguredXmltvOutputPath.Returns("/xmltv/epg.xml");
        var timeZoneService = Substitute.For<ITimeZoneService>();
        timeZoneService.TimeZone.Returns(TimeZoneInfo.Utc);
        context.Services.AddSingleton(settingsService);
        context.Services.AddSingleton(timeZoneService);
        var component = context.Render<Settings>();
        component.Find("#tab-guide-outputs").Click();
        component.Find("#guideOutputDirectoryPath").Change(" ");

        // Act
        component.Find("button.btn-primary").Click();

        // Assert
        var notifications = context.Services.GetRequiredService<IStatusNotificationService>();
        var notification = Assert.Single(notifications.Notifications);
        Assert.Equal("Guide output Global XMLTV requires an output destination.", notification.Message);
        Assert.True(notification.IsError);
        settingsService.DidNotReceive().UpdateAsync(Arg.Any<Action<LineupSettings>>());
    }

    /// <summary>Verifies the Virtual Devices tab exposes topology discovery without legacy proxy controls.</summary>
    [Fact]
    public void Render_VirtualDevicesTab_ShowsTopologyDiscoveryOnly()
    {
        // Arrange
        using var context = new BunitContext();
        var settingsService = Substitute.For<ILineupSettingsService>();
        settingsService.Settings.Returns(new LineupSettings { IsSetupComplete = true });
        var timeZoneService = Substitute.For<ITimeZoneService>();
        timeZoneService.TimeZone.Returns(TimeZoneInfo.Utc);
        context.Services.AddSingleton(settingsService);
        context.Services.AddSingleton(timeZoneService);
        var component = context.Render<Settings>();

        // Act
        component.Find("#tab-virtual-devices").Click();

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
    public void Render_GeneralSettings_SeparatesAppearanceAndRegionalCards()
    {
        // Arrange
        using var context = new BunitContext();
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
    public void Tabs_SwitchVisiblePaneHelpAndUrlFragment()
    {
        // Arrange
        using var context = new BunitContext();
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
        component.Find("#tab-streaming").Click();

        // Assert
        Assert.Equal("tab-dashboard", tabButtons[1].Id);
        Assert.Contains("active", component.Find("#tab-streaming").ClassList);
        Assert.DoesNotContain("d-none", component.Find("#streamingSettingsTab").ClassList);
        Assert.Contains("d-none", component.Find("#generalSettingsTab").ClassList);
        Assert.Contains("stream admission, shared-pipeline retention", component.Find("#aboutSettingsBody").TextContent);
        Assert.EndsWith("/settings#streaming", navigation.Uri, StringComparison.Ordinal);

        component.Find("#tab-dashboard").Click();

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
    public void AboutSettings_AllTabs_UsesConsistentSectionFormatting()
    {
        // Arrange
        using var context = new BunitContext();
        var settingsService = Substitute.For<ILineupSettingsService>();
        settingsService.Settings.Returns(new LineupSettings { IsSetupComplete = true });
        var timeZoneService = Substitute.For<ITimeZoneService>();
        timeZoneService.TimeZone.Returns(TimeZoneInfo.Utc);
        context.Services.AddSingleton(settingsService);
        context.Services.AddSingleton(timeZoneService);
        var component = context.Render<Settings>();

        // Act
        var generalSections = component.FindAll("#aboutSettingsBody .about-settings-section");
        component.Find("#tab-api").Click();
        var apiSections = component.FindAll("#aboutSettingsBody .about-settings-section");
        component.Find("#tab-logging").Click();
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
    public void Initialize_UsesTabFromUrlFragment()
    {
        // Arrange
        using var context = new BunitContext();
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
    public void Initialize_LegacyTranscodingFragment_UsesStreamingTab()
    {
        // Arrange
        using var context = new BunitContext();
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
    public void Render_ShowsCompleteTranscoderControlsInDeviceGlobalSettings()
    {
        // Arrange
        using var context = new BunitContext();
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

        component.Find("#physicalDefaultHevc").Change(true);
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

        component.Find("#virtualDefaultHevc").Change(true);
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
    public void Save_PersistsTranscodeSelections()
    {
        // Arrange
        using var context = new BunitContext();
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

        component.Find("#tab-physical-devices").Click();
        component.Find("#physicalDefaultHevc").Change(true);
        component.Find("#physicalDefaultDeinterlace").Change(nameof(SourceDeinterlacePolicy.SourceFrameRate));
        component.Find("#physicalDefaultAudio").Change(nameof(OutputAudioPolicy.Ac3));
        component.Find("#physicalDefaultAc4Audio").Change(nameof(OutputAudioPolicy.Eac3));
        component.Find("#physicalDefaultPreset").Change(nameof(VideoEncoderPreset.Fast));
        component.Find("#physicalDefaultQuality").Change("18");
        component.Find("#physicalDefaultBitrate").Change("16");

        component.Find("#tab-virtual-devices").Click();
        component.Find("#virtualDefaultHevc").Change(true);
        component.Find("#virtualDefaultDeinterlace").Change(nameof(SourceDeinterlacePolicy.SourceFieldRate));
        component.Find("#virtualDefaultAudio").Change(nameof(OutputAudioPolicy.Eac3));
        component.Find("#virtualDefaultAc4Audio").Change(nameof(OutputAudioPolicy.Eac3));
        component.Find("#virtualDefaultPreset").Change(nameof(VideoEncoderPreset.Faster));
        component.Find("#virtualDefaultQuality").Change("19");
        component.Find("#virtualDefaultBitrate").Change("14");

        component.Find("#tab-streaming").Click();
        component.Find("#protectedContentMode").Change(nameof(ProtectedContentMode.StreamSlate));
        component.Find("#maximumConcurrentStreams").Change("6");
        component.Find("#keepWarmPeriodSeconds").Change("15");

        component.Find("#tab-dashboard").Click();
        component.Find("#activeStreamRefreshInterval").Change("15");
        component.Find("#tab-general").Click();
        component.Find("#uiLanguage").Change("es");
        // Act
        component.Find("button.btn-primary").Click();

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
        settingsService.Received(1).UpdateAsync(Arg.Any<Action<LineupSettings>>());
        webSettingsService.Received(1).UpdateAsync(Arg.Any<Action<WebSettings>>());
    }

    /// <summary>
    /// Verifies that resetting the page restores defaults without persisting them.
    /// </summary>
    [Fact]
    public void Reset_RestoresTranscodeDefaultsWithoutSaving()
    {
        // Arrange
        using var context = new BunitContext();
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
        component.Find("#tab-reset").Click();

        // Act
        component.Find("#resetSettings").Click();
        component.Find("#tab-physical-devices").Click();

        Assert.False(component.Find("#physicalDefaultHevc").HasAttribute("checked"));
        Assert.Equal(nameof(SourceDeinterlacePolicy.Preserve), component.Find("#physicalDefaultDeinterlace").GetAttribute("value"));
        Assert.Equal(nameof(OutputAudioPolicy.Copy), component.Find("#physicalDefaultAudio").GetAttribute("value"));
        Assert.Equal(nameof(OutputAudioPolicy.Copy), component.Find("#physicalDefaultAc4Audio").GetAttribute("value"));

        component.Find("#physicalDefaultHevc").Change(true);

        Assert.Equal(nameof(VideoEncoderPreset.VeryFast), component.Find("#physicalDefaultPreset").GetAttribute("value"));
        Assert.Equal("21", component.Find("#physicalDefaultQuality").GetAttribute("value"));
        Assert.Equal("10", component.Find("#physicalDefaultBitrate").GetAttribute("value"));

        component.Find("#tab-virtual-devices").Click();
        Assert.False(component.Find("#virtualDefaultHevc").HasAttribute("checked"));
        Assert.Equal(nameof(SourceDeinterlacePolicy.Preserve), component.Find("#virtualDefaultDeinterlace").GetAttribute("value"));
        Assert.Equal(nameof(OutputAudioPolicy.Copy), component.Find("#virtualDefaultAudio").GetAttribute("value"));
        Assert.Equal(nameof(OutputAudioPolicy.Ac3), component.Find("#virtualDefaultAc4Audio").GetAttribute("value"));

        component.Find("#virtualDefaultHevc").Change(true);

        Assert.Equal(nameof(VideoEncoderPreset.VeryFast), component.Find("#virtualDefaultPreset").GetAttribute("value"));
        Assert.Equal("21", component.Find("#virtualDefaultQuality").GetAttribute("value"));
        Assert.Equal("10", component.Find("#virtualDefaultBitrate").GetAttribute("value"));

        component.Find("#tab-streaming").Click();
        component.Find("#tab-dashboard").Click();
        component.Find("#tab-logging").Click();

        // Assert
        Assert.Equal(nameof(ProtectedContentMode.ReturnError), component.Find("#protectedContentMode").GetAttribute("value"));
        Assert.Equal("0", component.Find("#maximumConcurrentStreams").GetAttribute("value"));
        Assert.Equal("0", component.Find("#keepWarmPeriodSeconds").GetAttribute("value"));
        Assert.Equal("5", component.Find("#activeStreamRefreshInterval").GetAttribute("value"));
        Assert.False(component.Find("#overrideLoggingDefaults").HasAttribute("checked"));
        Assert.Empty(component.FindAll("#applicationLogLevel"));
        settingsService.DidNotReceive().UpdateAsync(Arg.Any<Action<LineupSettings>>());
    }
}
