using System.Security.Cryptography;
using System.Text.Json;
using Lineup.Common.Configuration;
using Lineup.Common.Streaming;
using Xunit;

namespace Lineup.VirtualOutputs.Runtime.Tests;

/// <summary>Verifies declarative worker-package validation and executable provenance checks.</summary>
public sealed class VirtualOutputWorkerManifestValidatorTests
{
    /// <summary>Verifies the settings descriptor may belong to any declared adapter, using case-insensitive membership.</summary>
    [Theory]
    [InlineData("lineup-link")]
    [InlineData("web-player")]
    [InlineData("WEB-PLAYER")]
    public void Validate_MultipleAdapterKinds_DeclaredDescriptor_ReturnsNoErrors(string descriptorKind)
    {
        // Arrange
        var manifest = CreateManifest(new string('0', 64)) with
        {
            AdapterKinds = ["lineup-link", "web-player"],
            SettingsDescriptor = new AdapterSettingsDescriptor { AdapterKind = descriptorKind }
        };

        // Act
        var errors = VirtualOutputWorkerManifestValidator.Validate(manifest);

        // Assert
        Assert.Empty(errors);
    }

    /// <summary>Verifies multi-adapter manifests report undeclared descriptor membership rather than throwing.</summary>
    [Fact]
    public void Validate_MultipleAdapterKinds_UndeclaredDescriptor_ReturnsError()
    {
        // Arrange
        var manifest = CreateManifest(new string('0', 64)) with
        {
            AdapterKinds = ["lineup-link", "web-player"],
            SettingsDescriptor = new AdapterSettingsDescriptor { AdapterKind = "other-output" }
        };

        // Act
        var errors = VirtualOutputWorkerManifestValidator.Validate(manifest);

        // Assert
        Assert.Equal("The settings descriptor adapter kind must be declared by the worker manifest.", Assert.Single(errors));
    }

    /// <summary>Verifies removing singleton assumptions does not bypass validation of individual adapter identifiers.</summary>
    [Fact]
    public void Validate_MultipleAdapterKinds_InvalidIdentifier_ReturnsError()
    {
        // Arrange
        var manifest = CreateManifest(new string('0', 64)) with { AdapterKinds = ["lineup-link", "Invalid Adapter"] };

        // Act
        var errors = VirtualOutputWorkerManifestValidator.Validate(manifest);

        // Assert
        Assert.Equal("Worker manifest requires at least one valid adapter kind.", Assert.Single(errors));
    }

    /// <summary>Verifies missing adapter declarations retain both identifier and membership diagnostics.</summary>
    [Fact]
    public void Validate_EmptyAdapterKinds_ReturnsErrors()
    {
        // Arrange
        var manifest = CreateManifest(new string('0', 64)) with { AdapterKinds = [] };

        // Act
        var errors = VirtualOutputWorkerManifestValidator.Validate(manifest);

        // Assert
        Assert.Equal(
            ["Worker manifest requires at least one valid adapter kind.", "The settings descriptor adapter kind must be declared by the worker manifest."],
            errors);
    }

    /// <summary>Verifies single-adapter membership and case-insensitive matching retain existing behavior.</summary>
    [Theory]
    [InlineData("lineup-link", true)]
    [InlineData("LINEUP-LINK", true)]
    [InlineData("other-output", false)]
    public void Validate_SingleAdapterKind_PreservesDescriptorMembership(string descriptorKind, bool expectedValid)
    {
        // Arrange
        var manifest = CreateManifest(new string('0', 64)) with { SettingsDescriptor = new AdapterSettingsDescriptor { AdapterKind = descriptorKind } };

        // Act
        var errors = VirtualOutputWorkerManifestValidator.Validate(manifest);

        // Assert
        if (expectedValid)
        {
            Assert.Empty(errors);
        }
        else
        {
            Assert.Equal("The settings descriptor adapter kind must be declared by the worker manifest.", Assert.Single(errors));
        }
    }

    /// <summary>Verifies executable hash checks remain enforced for valid multi-adapter packages.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task VerifyExecutableAsync_MultipleAdapterKinds_ValidatesHash(bool matchingHash)
    {
        // Arrange
        var directory = Directory.CreateTempSubdirectory("lineup-multi-adapter-hash-");
        try
        {
            byte[] executable = [1, 2, 3, 4];
            await File.WriteAllBytesAsync(Path.Combine(directory.FullName, "worker.bin"), executable, TestContext.Current.CancellationToken);
            var hash = matchingHash ? Convert.ToHexString(SHA256.HashData(executable)).ToLowerInvariant() : new string('0', 64);
            var manifest = CreateManifest(hash) with { AdapterKinds = ["lineup-link", "web-player"] };

            // Act
            var verified = await VirtualOutputWorkerManifestValidator.VerifyExecutableAsync(manifest, directory.FullName, TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal(matchingHash, verified);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>Verifies installed multi-adapter manifests allow both declared adapters and unrelated packages to resolve.</summary>
    [Theory]
    [InlineData("lineup-link", "multi-output")]
    [InlineData("WEB-PLAYER", "multi-output")]
    [InlineData("other-output", "other-output")]
    public async Task ResolveAsync_InstalledMultiAdapterManifest_ResolvesPackage(string adapterKind, string expectedWorkerId)
    {
        // Arrange
        var directory = Directory.CreateTempSubdirectory("lineup-multi-adapter-catalog-");
        try
        {
            var multi = CreateManifest(new string('0', 64)) with
            {
                WorkerId = "multi-output",
                AdapterKinds = ["lineup-link", "web-player"],
                Platforms = []
            };
            var other = CreateManifest(new string('0', 64)) with
            {
                WorkerId = "other-output",
                AdapterKinds = ["other-output"],
                SettingsDescriptor = new AdapterSettingsDescriptor { AdapterKind = "other-output" },
                Platforms = []
            };
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "multi.virtual-output.json"), JsonSerializer.Serialize(multi, options), TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "other.virtual-output.json"), JsonSerializer.Serialize(other, options), TestContext.Current.CancellationToken);
            var catalog = new VirtualOutputWorkerCatalog(directory.FullName, new Version(3, 0, 0));

            // Act
            var resolved = await catalog.ResolveAsync(adapterKind, TestContext.Current.CancellationToken);
            var installed = await catalog.LoadAsync(TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal(expectedWorkerId, resolved.Manifest.WorkerId);
            Assert.Equal(2, installed.Count);
            Assert.Equal(directory.FullName, resolved.PackageRoot);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

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
