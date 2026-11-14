using Lineup.App.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Lineup.App.Web.Tests.Services;

/// <summary>Verifies recovery preserves actual protected application state, not just key file names.</summary>
public sealed class RecoveryDataProtectionTests
{
    /// <summary>Recovers the Data Protection key ring into a different empty directory and decrypts prior protected state.</summary>
    [Fact]
    public async Task Restore_KeyRing_UnprotectsOriginalState()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-key-recovery-");
        try
        {
            var source = new AppDataStore(Path.Combine(root.FullName, "source"));
            string protectedValue;
            var services = new ServiceCollection();
            services.AddLogging();
            DataProtectionService.Configure(services, source);
            using (var provider = services.BuildServiceProvider())
            {
                protectedValue = provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("recovery-test").Protect("protected-state-sentinel");
            }
            var archive = Path.Combine(root.FullName, "backup.zip");
            var target = Path.Combine(root.FullName, "restored");

            // Act
            await new RecoveryArchiveService(source.RootPath).BackupAsync(archive, TestContext.Current.CancellationToken);
            await new RecoveryArchiveService(target).RestoreAsync(archive, TestContext.Current.CancellationToken);
            var restoredServices = new ServiceCollection();
            restoredServices.AddLogging();
            DataProtectionService.Configure(restoredServices, new AppDataStore(target));
            using var restored = restoredServices.BuildServiceProvider();
            var plaintext = restored.GetRequiredService<IDataProtectionProvider>().CreateProtector("recovery-test").Unprotect(protectedValue);

            // Assert
            Assert.Equal("protected-state-sentinel", plaintext);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
