using Microsoft.AspNetCore.DataProtection;

namespace Lineup.Web.Services;

/// <summary>
/// Configures persistent ASP.NET Core Data Protection for Lineup.
/// </summary>
internal static class DataProtectionService
{
    /// <summary>
    /// Stable Data Protection application discriminator.
    /// </summary>
    internal const string ApplicationName = "Lineup";

    /// <summary>
    /// Configures Data Protection to persist keys in the application-data store.
    /// </summary>
    internal static string Configure(IServiceCollection services, AppDataStore appDataStore)
    {
        var keyDirectory = appDataStore.DataProtectionKeysPath;
        appDataStore.EnsureDirectory(keyDirectory);
        services.AddDataProtection()
            .SetApplicationName(ApplicationName)
            .PersistKeysToFileSystem(new DirectoryInfo(keyDirectory));
        return keyDirectory;
    }

    /// <summary>
    /// Configures Data Protection from an application-data root path.
    /// </summary>
    internal static string Configure(IServiceCollection services, string appDataPath) =>
        Configure(services, new AppDataStore(appDataPath));
}
