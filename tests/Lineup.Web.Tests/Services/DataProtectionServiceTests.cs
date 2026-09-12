using Lineup.Core;
using Lineup.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Lineup.Web.Tests.Services;

/// <summary>
/// Verifies persistent ASP.NET Core Data Protection configuration.
/// </summary>
public class DataProtectionServiceTests
{
    /// <summary>
    /// Verifies protected payloads remain readable after the service provider is recreated.
    /// </summary>
    [Fact]
    public void Configure_RecreatedProvider_UnprotectsExistingPayload()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-data-protection-");
        const string purpose = "Lineup.Tests";
        const string value = "persistent payload";
        string protectedValue;
        using (var firstProvider = CreateProvider(root.FullName))
        {
            protectedValue = firstProvider.GetRequiredService<IDataProtectionProvider>()
                .CreateProtector(purpose)
                .Protect(value);
        }

        // Act
        using var secondProvider = CreateProvider(root.FullName);
        var unprotectedValue = secondProvider.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector(purpose)
            .Unprotect(protectedValue);

        // Assert
        Assert.Equal(value, unprotectedValue);
        Assert.NotEmpty(Directory.EnumerateFiles(
            Path.Combine(root.FullName, AppConstants.DataProtectionKeysDirectoryName),
            "*.xml",
            SearchOption.TopDirectoryOnly));
        root.Delete(recursive: true);
    }

    private static ServiceProvider CreateProvider(string configPath)
    {
        var services = new ServiceCollection();
        DataProtectionService.Configure(services, configPath);
        return services.BuildServiceProvider();
    }
}
