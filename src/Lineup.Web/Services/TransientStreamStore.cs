using Lineup.Core;

namespace Lineup.Web.Services;

/// <summary>
/// Provides contained roots for transient stream artifacts.
/// </summary>
public sealed class TransientStreamStore
{
    /// <summary>
    /// Initializes a transient stream store.
    /// </summary>
    /// <param name="rootPath">Application-owned transient root.</param>
    public TransientStreamStore(string rootPath)
    {
        RootPath = Path.GetFullPath(rootPath);
        HlsRootPath = Path.Combine(RootPath, TransientDirectoryOwnership.HlsDirectoryName);
        SubtitleRootPath = Path.Combine(RootPath, SubtitleSidecarService.DirectoryName);
        Directory.CreateDirectory(RootPath);
    }

    /// <summary>Gets the normalized transient root.</summary>
    public string RootPath { get; }

    /// <summary>Gets the shared HLS process-directory root.</summary>
    public string HlsRootPath { get; }

    /// <summary>Gets the shared subtitle process-directory root.</summary>
    public string SubtitleRootPath { get; }

    /// <summary>
    /// Creates the configured transient stream store.
    /// </summary>
    /// <param name="configuration">Application configuration.</param>
    /// <returns>The normalized transient stream store.</returns>
    public static TransientStreamStore Create(IConfiguration configuration) =>
        new(ResolveRootPath(configuration));

    /// <summary>
    /// Creates the default transient stream store.
    /// </summary>
    /// <returns>The default transient stream store.</returns>
    internal static TransientStreamStore CreateDefault() =>
        new(AppConstants.DefaultTransientPath);

    /// <summary>
    /// Resolves the configured transient root or default path.
    /// </summary>
    /// <param name="configuration">Application configuration.</param>
    /// <returns>The configured or default transient root.</returns>
    internal static string ResolveRootPath(IConfiguration configuration)
    {
        var configuredPath = configuration[AppConstants.TransientPathConfigKey];
        return string.IsNullOrWhiteSpace(configuredPath)
            ? AppConstants.DefaultTransientPath
            : configuredPath.Trim();
    }
}
