using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace Lineup.App.Web.Tests.Components;

/// <summary>
/// Verifies browser playback configuration embedded in the application shell.
/// </summary>
public sealed class AppPlaybackConfigurationTests
{
    /// <summary>
    /// Verifies the application shell composes focused browser scripts instead of embedding playback implementation.
    /// </summary>
    [Fact]
    public void App_LoadsFocusedBrowserScripts()
    {
        // Arrange
        var repositoryRoot = FindRepositoryRoot();
        var appPath = Path.Combine(repositoryRoot, "src", "Apps", "Lineup.App.Web", "Components", "App.razor");

        // Act
        var source = File.ReadAllText(appPath);

        // Assert
        Assert.Contains("js/browser-utilities.js", source, StringComparison.Ordinal);
        Assert.Contains("js/runtime-watch.js", source, StringComparison.Ordinal);
        Assert.Contains("js/cmaf-playback.js", source, StringComparison.Ordinal);
        Assert.Contains("js/guide-viewport.js", source, StringComparison.Ordinal);
        Assert.Contains("js/theme.js", source, StringComparison.Ordinal);
        Assert.DoesNotContain("function initCmafPlayer", source, StringComparison.Ordinal);
        Assert.Contains("CultureInfo.CurrentUICulture.TwoLetterISOLanguageName", source, StringComparison.Ordinal);
        Assert.DoesNotContain("<html lang=\"en\">", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies a fresh browser can load the player and icon libraries without contacting a CDN.
    /// </summary>
    [Fact]
    public void App_BrowserLibraries_LoadFromLocalStaticAssets()
    {
        // Arrange
        var appPath = Path.Combine(FindRepositoryRoot(), "src", "Apps", "Lineup.App.Web", "Components", "App.razor");

        // Act
        var source = File.ReadAllText(appPath);

        // Assert
        Assert.Contains("@Assets[\"lib/shaka-player/shaka-player.ui.js\"]", source, StringComparison.Ordinal);
        Assert.Contains("@Assets[\"lib/shaka-player/controls.css\"]", source, StringComparison.Ordinal);
        Assert.Contains("@Assets[\"lib/bootstrap-icons/bootstrap-icons.min.css\"]", source, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex("""(?:src|href)\s*=\s*["']\s*(?:https?:)?//""", RegexOptions.IgnoreCase), source);
        Assert.True(source.IndexOf("lib/shaka-player/shaka-player.ui.js", StringComparison.Ordinal) < source.IndexOf("js/cmaf-playback.js", StringComparison.Ordinal));
    }

    /// <summary>
    /// Verifies the build-restored runtime assets and redistribution notices match the reviewed dependency lock.
    /// </summary>
    [Fact]
    public void App_CachedBrowserAssets_MatchPinnedChecksums()
    {
        // Arrange
        var repositoryRoot = FindRepositoryRoot();
        var libraryRoot = FindBrowserAssetCache(repositoryRoot);
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(repositoryRoot, "build", "browser-assets.json")));

        // Act
        var assets = manifest.RootElement.GetProperty("assets").EnumerateArray().ToArray();

        // Assert
        Assert.NotEmpty(assets);
        foreach (var asset in assets)
        {
            var relativePath = Assert.IsType<string>(asset.GetProperty("path").GetString());
            var assetPath = Path.Combine(libraryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(assetPath), $"Missing build-restored browser asset: {relativePath}");
            var actualHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(assetPath)));
            Assert.Equal(asset.GetProperty("sha256").GetString(), actualHash);
        }
    }

    /// <summary>
    /// Verifies browser styles resolve their transitive font and image dependencies from the local library tree.
    /// </summary>
    [Fact]
    public void App_CachedStyles_ResolveDependenciesLocally()
    {
        // Arrange
        var libraryRoot = FindBrowserAssetCache(FindRepositoryRoot());
        var dependencyPattern = new Regex("""url\(\s*["']?(?<path>[^)"'\s]+)["']?\s*\)|@import\s+["'](?<path>[^"']+)["']""", RegexOptions.IgnoreCase);

        // Act
        var dependencies = Directory.EnumerateFiles(libraryRoot, "*.css", SearchOption.AllDirectories)
            .SelectMany(path => dependencyPattern.Matches(File.ReadAllText(path)).Select(match => (Stylesheet: new Uri(path), Reference: match.Groups["path"].Value)))
            .Where(dependency => !dependency.Reference.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        // Assert
        Assert.NotEmpty(dependencies);
        foreach (var dependency in dependencies)
        {
            Assert.False(dependency.Reference.StartsWith("//", StringComparison.Ordinal), $"Remote dependency in {dependency.Stylesheet}: {dependency.Reference}");
            var resolved = new Uri(dependency.Stylesheet, dependency.Reference.Split('?', '#')[0]);
            Assert.True(resolved.IsFile, $"Remote dependency in {dependency.Stylesheet}: {dependency.Reference}");
            Assert.StartsWith(libraryRoot + Path.DirectorySeparatorChar, resolved.LocalPath, StringComparison.Ordinal);
            Assert.True(File.Exists(resolved.LocalPath), $"Missing stylesheet dependency: {resolved.LocalPath}");
        }
    }

    /// <summary>
    /// Verifies Watch and compatibility-test DASH playback use sequence mode to keep independent CMAF tracks synchronized.
    /// </summary>
    [Fact]
    public void App_DashPlayers_EnableSequenceMode()
    {
        // Arrange
        var repositoryRoot = FindRepositoryRoot();
        var playerScriptPath = Path.Combine(repositoryRoot, "src", "Apps", "Lineup.App.Web", "wwwroot", "js", "cmaf-playback.js");

        // Act
        var source = File.ReadAllText(playerScriptPath);
        var sequenceModeConfigurationCount = source.Split("sequenceMode: true", StringSplitOptions.None).Length - 1;

        // Assert
        Assert.Equal(2, sequenceModeConfigurationCount);
    }

    /// <summary>
    /// Verifies runtime audio fallback matching strips the shared source label without encoding-dependent text.
    /// </summary>
    [Fact]
    public void App_AudioFallbackMatching_UsesStableMiddleDotEscape()
    {
        // Arrange
        var repositoryRoot = FindRepositoryRoot();
        var playerScriptPath = Path.Combine(repositoryRoot, "src", "Apps", "Lineup.App.Web", "wwwroot", "js", "cmaf-playback.js");

        // Act
        var source = File.ReadAllText(playerScriptPath);

        // Assert
        Assert.Contains(@".replace(/\s+\u00b7\s+(Source|Fallback.*)$/i, '')", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Ã", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Â", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies stale player surfaces cannot stop a replacement that reuses the same video element identifier.
    /// </summary>
    [Fact]
    public void App_PlayerCleanup_RequiresMatchingSurfaceOwner()
    {
        // Arrange
        var repositoryRoot = FindRepositoryRoot();
        var playerScriptPath = Path.Combine(repositoryRoot, "src", "Apps", "Lineup.App.Web", "wwwroot", "js", "cmaf-playback.js");

        // Act
        var source = File.ReadAllText(playerScriptPath);

        // Assert
        Assert.Contains("window.lineupMediaPlayerOwners.set(videoElementId, playerOwnerId);", source, StringComparison.Ordinal);
        Assert.Contains("window.lineupMediaPlayerOwners?.get(videoElementId) !== playerOwnerId", source, StringComparison.Ordinal);
    }

    private static string FindBrowserAssetCache(string repositoryRoot)
    {
        var manifest = File.ReadAllBytes(Path.Combine(repositoryRoot, "build", "browser-assets.json"));
        return Path.Combine(repositoryRoot, "out", "browser-assets", Convert.ToHexString(SHA256.HashData(manifest)));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Lineup.slnx")))
        {
            directory = directory.Parent;
        }

        return Assert.IsType<DirectoryInfo>(directory).FullName;
    }
}
