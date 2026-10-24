using Lineup.Core;
using Lineup.Common.Guides;
using Lineup.Extensibility.Guides;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Lineup.SiliconDust.GuideSource.Tests;

/// <summary>
/// Verifies SiliconDust guide-source dependency injection.
/// </summary>
public sealed class SiliconDustGuideSourceServiceExtensionsTests
{
    /// <summary>
    /// Verifies that the guide driver is available through the provider-neutral contract.
    /// </summary>
    [Fact]
    public void AddSiliconDustGuideSource_RegistersDriver()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEpgCore(Path.Combine(Path.GetTempPath(), $"lineup-silicondust-services-{Guid.NewGuid():N}.db"));

        // Act
        services.AddSiliconDustGuideSource();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
        var driver = provider.GetRequiredService<IGuideSourceDriver>();

        // Assert
        Assert.IsType<SiliconDustGuideSourceDriver>(driver);
        Assert.Equal(SiliconDustGuideSourceDriver.DriverKind, driver.Kind);
    }
}
