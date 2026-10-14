using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Lineup.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Lineup.Web.Tests.Services;

/// <summary>Verifies Web UI culture configuration and request application.</summary>
public sealed class UiLocalizationTests
{
    /// <summary>Verifies supported cultures normalize consistently.</summary>
    [Theory]
    [InlineData("EN", "en")]
    [InlineData("ES", "es")]
    [InlineData("DE", "de")]
    [InlineData("FR", "fr")]
    [InlineData("PT-br", "pt-BR")]
    [InlineData("IT", "it")]
    [InlineData("NL", "nl")]
    [InlineData("PL", "pl")]
    [InlineData("ZH-hans", "zh-Hans")]
    [InlineData("JA", "ja")]
    public void WebSettings_UiCulture_NormalizesSupportedCultures(string configuredCulture, string expectedCulture)
    {
        // Arrange
        var settings = new WebSettings();

        // Act
        settings.UiCulture = configuredCulture;

        // Assert
        Assert.Equal(expectedCulture, settings.UiCulture);
    }

    /// <summary>Verifies unsupported cultures safely use English.</summary>
    [Fact]
    public void WebSettings_UiCulture_UnsupportedCulture_UsesEnglish()
    {
        // Arrange
        var settings = new WebSettings();

        // Act
        settings.UiCulture = "unsupported";

        // Assert
        Assert.Equal("en", settings.UiCulture);
    }

    /// <summary>Verifies existing settings documents acquire the default UI culture without a schema reset.</summary>
    [Fact]
    public void WebSettingsService_ExistingDocumentWithoutUiCulture_DefaultsToEnglish()
    {
        // Arrange
        var testDirectory = CreateTestDirectory();
        var store = new AppDataStore(testDirectory);
        File.WriteAllText(store.GetPath("web-settings.json"), """{"SchemaVersion":1,"Theme":"dark"}""");

        // Act
        var service = new WebSettingsService(store, NullLogger<WebSettingsService>.Instance);

        // Assert
        Assert.Equal("dark", service.Settings.Theme);
        Assert.Equal("en", service.Settings.UiCulture);
        Directory.Delete(testDirectory, recursive: true);
    }

    /// <summary>Verifies request localization changes UI culture without changing formatting culture.</summary>
    [Fact]
    public async Task InvokeAsync_ConfiguredCulture_PreservesFormattingCulture()
    {
        // Arrange
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        var formattingCulture = CultureInfo.GetCultureInfo("fr-FR");
        CultureInfo.CurrentCulture = formattingCulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de-DE");
        var webSettings = Substitute.For<IWebSettingsService>();
        webSettings.Settings.Returns(new WebSettings { UiCulture = "es" });
        var services = new ServiceCollection()
            .AddSingleton(webSettings)
            .BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        string? observedFormattingCulture = null;
        string? observedUiCulture = null;

        try
        {
            // Act
            await UiLocalization.InvokeAsync(
                context,
                _ =>
                {
                    observedFormattingCulture = CultureInfo.CurrentCulture.Name;
                    observedUiCulture = CultureInfo.CurrentUICulture.Name;
                    return Task.CompletedTask;
                });

            // Assert
            Assert.Equal("fr-FR", observedFormattingCulture);
            Assert.Equal("es", observedUiCulture);
            Assert.Equal("fr-FR", CultureInfo.CurrentCulture.Name);
            Assert.Equal("de-DE", CultureInfo.CurrentUICulture.Name);
        }

        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
            await services.DisposeAsync();
        }
    }

    /// <summary>Verifies the neutral resource catalog returns the current English UI text.</summary>
    [Fact]
    public void ResourceCatalog_EnglishKey_ReturnsCurrentText()
    {
        // Arrange
        using var services = new ServiceCollection()
            .AddLogging()
            .AddLocalization(options => options.ResourcesPath = "Resources")
            .BuildServiceProvider();
        var localizer = services.GetRequiredService<IStringLocalizer<UiStrings>>();

        // Act
        var value = localizer["General_RegionalHeading"];

        // Assert
        Assert.False(value.ResourceNotFound);
        Assert.Equal("Regional Settings", value.Value);
    }

    /// <summary>Verifies every satellite catalog returns translated UI text.</summary>
    [Theory]
    [MemberData(nameof(SatelliteCultures))]
    public void ResourceCatalog_SatelliteKey_ReturnsTranslatedText(string culture)
    {
        // Arrange
        using var services = new ServiceCollection()
            .AddLogging()
            .AddLocalization(options => options.ResourcesPath = "Resources")
            .BuildServiceProvider();
        var localizer = services.GetRequiredService<IStringLocalizer<UiStrings>>();
        var originalUiCulture = CultureInfo.CurrentUICulture;

        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);

            // Act
            var value = localizer["General_RegionalHeading"];

            // Assert
            Assert.False(value.ResourceNotFound);
            Assert.NotEqual("Regional Settings", value.Value);
        }
        finally
        {
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    /// <summary>Verifies every statically referenced shared UI resource key exists and has English text.</summary>
    [Fact]
    public void ResourceCatalog_StaticReferences_HaveEnglishValues()
    {
        // Arrange
        var repositoryRoot = FindRepositoryRoot();
        var webRoot = Path.Combine(repositoryRoot, "src", "Apps", "Lineup.Web");
        var resourcePath = Path.Combine(webRoot, "Resources", "UiStrings.resx");
        var resourceDocument = XDocument.Load(resourcePath);
        var resources = resourceDocument.Root!
            .Elements("data")
            .ToDictionary(
                element => element.Attribute("name")!.Value,
                element => element.Element("value")?.Value,
                StringComparer.Ordinal);
        var referencePattern = new Regex("Text\\[\\s*\"([A-Za-z0-9_]+)", RegexOptions.CultureInvariant);
        var placeholderPattern = new Regex(@"\{\d+[^{}]*\}", RegexOptions.CultureInvariant);
        var referencedKeys = Directory
            .EnumerateFiles(webRoot, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .SelectMany(path => referencePattern.Matches(File.ReadAllText(path)).Select(match => match.Groups[1].Value))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        // Act
        var invalidKeys = referencedKeys
            .Where(key => !resources.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            .ToArray();
        var invalidSatelliteKeys = new List<string>();
        var placeholderMismatches = new List<string>();
        foreach (var culture in UiLocalization.SupportedCultures.Skip(1))
        {
            var satelliteResourcePath = Path.Combine(webRoot, "Resources", $"UiStrings.{culture}.resx");
            var satelliteResources = XDocument.Load(satelliteResourcePath).Root!
                .Elements("data")
                .ToDictionary(
                    element => element.Attribute("name")!.Value,
                    element => element.Element("value")?.Value,
                    StringComparer.Ordinal);
            invalidSatelliteKeys.AddRange(resources.Keys
                .Where(key => !satelliteResources.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
                .Concat(satelliteResources.Keys.Where(key => !resources.ContainsKey(key)))
                .Select(key => $"{culture}:{key}"));
            placeholderMismatches.AddRange(resources.Keys
                .Where(key => satelliteResources.TryGetValue(key, out var satelliteValue)
                    &&
                    !placeholderPattern.Matches(resources[key]!).Select(match => match.Value).Order(StringComparer.Ordinal)
                        .SequenceEqual(placeholderPattern.Matches(satelliteValue!).Select(match => match.Value).Order(StringComparer.Ordinal), StringComparer.Ordinal))
                .Select(key => $"{culture}:{key}"));
        }

        // Assert
        Assert.Empty(invalidKeys);
        Assert.Empty(invalidSatelliteKeys);
        Assert.Empty(placeholderMismatches);
    }

    /// <summary>Verifies resource keys assembled from runtime status values remain available.</summary>
    [Theory]
    [InlineData("Dashboard_FetchStatusCompleted")]
    [InlineData("Dashboard_FetchStatusFailed")]
    [InlineData("Dashboard_FetchStatusFetching")]
    [InlineData("Dashboard_FetchStatusInitializing")]
    [InlineData("Dashboard_FetchStatusStoring")]
    [InlineData("Logs_LevelDebug")]
    [InlineData("Logs_LevelError")]
    [InlineData("Logs_LevelFatal")]
    [InlineData("Logs_LevelInformation")]
    [InlineData("Logs_LevelTrace")]
    [InlineData("Logs_LevelWarning")]
    [InlineData("Streaming_MediaTrackAudio")]
    [InlineData("Streaming_MediaTrackSubtitle")]
    [InlineData("Streaming_MediaTrackVideo")]
    public void ResourceCatalog_DynamicReference_HasEnglishValue(string key)
    {
        // Arrange
        using var services = new ServiceCollection()
            .AddLogging()
            .AddLocalization(options => options.ResourcesPath = "Resources")
            .BuildServiceProvider();
        var localizer = services.GetRequiredService<IStringLocalizer<UiStrings>>();

        // Act
        var value = localizer[key];

        // Assert
        Assert.False(value.ResourceNotFound);
        Assert.False(string.IsNullOrWhiteSpace(value.Value));
    }

    /// <summary>Gets the supported satellite cultures used by localization tests.</summary>
    public static TheoryData<string> SatelliteCultures =>
    [
        UiLocalization.SpanishCulture,
        UiLocalization.GermanCulture,
        UiLocalization.FrenchCulture,
        UiLocalization.BrazilianPortugueseCulture,
        UiLocalization.ItalianCulture,
        UiLocalization.DutchCulture,
        UiLocalization.PolishCulture,
        UiLocalization.SimplifiedChineseCulture,
        UiLocalization.JapaneseCulture
    ];

    private static string CreateTestDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lineup-ui-localization-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Lineup.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Unable to locate the Lineup repository root.");
    }
}
