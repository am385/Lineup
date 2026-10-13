using System.Security.Cryptography;
using Lineup.Common.Configuration;
using Lineup.Common.Streaming;
using Xunit;

namespace Lineup.VirtualOutputs.Runtime.Tests;

/// <summary>Verifies declarative worker-package validation and executable provenance checks.</summary>
public sealed class VirtualOutputWorkerManifestValidatorTests
{
    /// <summary>Verifies a matching executable hash is accepted without loading the executable assembly.</summary>
    [Fact]
    public async Task VerifyExecutableAsync_MatchingHash_ReturnsTrue()
    {
        // Arrange
        var directory = Directory.CreateTempSubdirectory("lineup-worker-manifest-");
        var executablePath = Path.Combine(directory.FullName, "worker.bin");
        await File.WriteAllBytesAsync(executablePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);
        var hash = Convert.ToHexString(SHA256.HashData([1, 2, 3, 4])).ToLowerInvariant();
        var manifest = CreateManifest(hash);

        // Act
        var verified = await VirtualOutputWorkerManifestValidator.VerifyExecutableAsync(manifest, directory.FullName, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(verified);
        directory.Delete(recursive: true);
    }

    /// <summary>Verifies path traversal and incompatible protocol declarations are rejected.</summary>
    [Fact]
    public void Validate_UnsafeManifest_ReturnsErrors()
    {
        // Arrange
        var manifest = CreateManifest(new string('0', 64)) with
        {
            ExecutablePath = Path.Combine("..", "worker.exe"),
            MinimumProtocolVersion = 2,
            MaximumProtocolVersion = 1,
            Platforms =
            [
                new VirtualOutputWorkerPlatform
                {
                    OperatingSystem = "Windows Server",
                    Architecture = "x64"
                }
            ]
        };

        // Act
        var errors = VirtualOutputWorkerManifestValidator.Validate(manifest);

        // Assert
        Assert.Contains(errors, error => error.Contains("package-relative", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("compatible IPC protocol", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("platform", StringComparison.Ordinal));
    }

    /// <summary>Verifies package versions may use semantic-version prerelease suffixes.</summary>
    [Fact]
    public void Validate_PrereleaseVersions_ReturnsNoVersionErrors()
    {
        // Arrange
        var manifest = CreateManifest(new string('0', 64)) with
        {
            Version = "3.0.0-beta.1",
            MinimumLineupVersion = "3.0.0-beta"
        };

        // Act
        var errors = VirtualOutputWorkerManifestValidator.Validate(manifest);

        // Assert
        Assert.DoesNotContain(errors, error => error.Contains("version", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Verifies the running Lineup version must fall within the package compatibility range.</summary>
    [Fact]
    public void IsCompatibleWithLineupVersion_OutsideDeclaredRange_ReturnsFalse()
    {
        // Arrange
        var manifest = CreateManifest(new string('0', 64)) with
        {
            MinimumLineupVersion = "3.1.0",
            MaximumLineupVersion = "3.9.0"
        };

        // Act
        var compatible = VirtualOutputWorkerManifestValidator.IsCompatibleWithLineupVersion(
            manifest,
            new Version(3, 0, 0));

        // Assert
        Assert.False(compatible);
    }

    private static VirtualOutputWorkerManifest CreateManifest(string hash) =>
        new()
        {
            WorkerId = "lineup-link",
            Version = "1.0.0",
            Publisher = "Lineup",
            ExecutablePath = "worker.bin",
            ExecutableSha256 = hash,
            AdapterKinds = ["lineup-link"],
            Platforms = [new VirtualOutputWorkerPlatform { OperatingSystem = "windows", Architecture = "x64" }],
            MinimumProtocolVersion = 1,
            MaximumProtocolVersion = 1,
            MinimumLineupVersion = "3.0.0",
            SettingsDescriptor = new AdapterSettingsDescriptor { AdapterKind = "lineup-link" }
        };
}
