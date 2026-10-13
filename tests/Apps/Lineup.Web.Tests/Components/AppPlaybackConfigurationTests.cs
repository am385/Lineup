using Xunit;

namespace Lineup.Web.Tests.Components;

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
        var appPath = Path.Combine(repositoryRoot, "src", "Apps", "Lineup.Web", "Components", "App.razor");

        // Act
        var source = File.ReadAllText(appPath);

        // Assert
        Assert.Contains("js/browser-utilities.js", source, StringComparison.Ordinal);
        Assert.Contains("js/runtime-watch.js", source, StringComparison.Ordinal);
        Assert.Contains("js/cmaf-playback.js", source, StringComparison.Ordinal);
        Assert.Contains("js/guide-viewport.js", source, StringComparison.Ordinal);
        Assert.Contains("js/theme.js", source, StringComparison.Ordinal);
        Assert.DoesNotContain("function initCmafPlayer", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies Watch and compatibility-test DASH playback use sequence mode to keep independent CMAF tracks synchronized.
    /// </summary>
    [Fact]
    public void App_DashPlayers_EnableSequenceMode()
    {
        // Arrange
        var repositoryRoot = FindRepositoryRoot();
        var playerScriptPath = Path.Combine(repositoryRoot, "src", "Apps", "Lineup.Web", "wwwroot", "js", "cmaf-playback.js");

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
        var playerScriptPath = Path.Combine(repositoryRoot, "src", "Apps", "Lineup.Web", "wwwroot", "js", "cmaf-playback.js");

        // Act
        var source = File.ReadAllText(playerScriptPath);

        // Assert
        Assert.Contains(@".replace(/\s+\u00b7\s+(Source|Fallback.*)$/i, '')", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Ã", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Â", source, StringComparison.Ordinal);
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
