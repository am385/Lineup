using System.Text.Json;
using System.Text.Json.Nodes;
using Lineup.Core.Common.Configuration;
using Lineup.Core.Common.Streaming;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Lineup.Core.VirtualOutputs.Runtime.Tests;

/// <summary>Verifies package failures remain diagnosable without blocking independent healthy workers.</summary>
public sealed class VirtualOutputWorkerCatalogTests
{
    /// <summary>Verifies mixed catalogs isolate malformed, invalid and incompatible packages while reporting each rejection.</summary>
    [Theory]
    [InlineData("healthy")]
    [InlineData("HEALTHY")]
    public async Task ResolveAsync_MixedCatalog_ResolvesHealthyWorkerAndLogsRejectedPackages(string adapterKind)
    {
        // Arrange
        var directory = Directory.CreateTempSubdirectory("lineup-mixed-catalog-");
        try
        {
            await WriteMixedCatalogAsync(directory.FullName);
            var logger = Substitute.For<ILogger<VirtualOutputWorkerCatalog>>();
            var catalog = new VirtualOutputWorkerCatalog(directory.FullName, new Version(3, 0, 0), logger);

            // Act
            var package = await catalog.ResolveAsync(adapterKind, TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal("healthy", package.Manifest.WorkerId);
            Assert.Equal(directory.FullName, package.PackageRoot);
            var warnings = logger.ReceivedCalls().Where(call => call.GetMethodInfo().Name == nameof(ILogger.Log)).Select(call => call.GetArguments()).ToArray();
            Assert.Equal(3, warnings.Length);
            Assert.All(warnings, arguments =>
            {
                Assert.Equal(LogLevel.Warning, arguments[0]);
                Assert.IsAssignableFrom<Exception>(arguments[3]);
            });
            foreach (var name in new[] { "malformed", "invalid", "newer" })
            {
                Assert.Contains(warnings, arguments => arguments[2]?.ToString()?.Contains($"{name}.virtual-output.json", StringComparison.Ordinal) == true);
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>Verifies invalid, incompatible and unidentified malformed workers fail explicitly with package-specific diagnostics.</summary>
    [Theory]
    [InlineData("invalid")]
    [InlineData("newer")]
    [InlineData("malformed")]
    public async Task ResolveAsync_RejectedAdapter_FailsWithActionablePackageDiagnostics(string adapterKind)
    {
        // Arrange
        var directory = Directory.CreateTempSubdirectory("lineup-rejected-catalog-");
        try
        {
            await WriteMixedCatalogAsync(directory.FullName);
            var catalog = new VirtualOutputWorkerCatalog(directory.FullName, new Version(3, 0, 0));

            // Act
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => catalog.ResolveAsync(adapterKind, TestContext.Current.CancellationToken));

            // Assert
            Assert.Contains($"adapter kind '{adapterKind}'", error.Message, StringComparison.Ordinal);
            Assert.Contains($"{adapterKind}.virtual-output.json", error.Message, StringComparison.Ordinal);
            Assert.Contains("schema 2", error.Message, StringComparison.Ordinal);
            Assert.Contains("does not support Lineup version", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>Verifies invalid neighbors do not hide duplicate valid worker matches or unsupported execution platforms.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ResolveAsync_MixedCatalog_PreservesAmbiguityAndPlatformRejection(bool duplicate)
    {
        // Arrange
        var directory = Directory.CreateTempSubdirectory("lineup-ambiguous-catalog-");
        try
        {
            await WriteMixedCatalogAsync(directory.FullName);
            var manifest = CreateManifest(duplicate ? "duplicate" : "unsupported") with
            {
                AdapterKinds = [duplicate ? "healthy" : "unsupported"],
                SettingsDescriptor = new AdapterSettingsDescriptor { AdapterKind = duplicate ? "healthy" : "unsupported" },
                Platforms = duplicate ? [] : [new VirtualOutputWorkerPlatform { OperatingSystem = "unsupported", Architecture = "unsupported" }]
            };
            await WriteManifestAsync(directory.FullName, manifest);
            var catalog = new VirtualOutputWorkerCatalog(directory.FullName, new Version(3, 0, 0));

            // Act
            Exception error = duplicate
                ? await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.ResolveAsync("healthy", TestContext.Current.CancellationToken))
                : await Assert.ThrowsAsync<PlatformNotSupportedException>(() => catalog.ResolveAsync("unsupported", TestContext.Current.CancellationToken));

            // Assert
            Assert.Contains(duplicate ? "Multiple installed" : "operating system and architecture", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>Verifies strict inventory validation remains available even when independent resolution can succeed.</summary>
    [Fact]
    public async Task LoadAsync_MalformedManifest_StillFailsExplicitly()
    {
        // Arrange
        var directory = Directory.CreateTempSubdirectory("lineup-strict-catalog-");
        try
        {
            await WriteManifestAsync(directory.FullName, CreateManifest("healthy"));
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "malformed.virtual-output.json"), "{", TestContext.Current.CancellationToken);
            var catalog = new VirtualOutputWorkerCatalog(directory.FullName, new Version(3, 0, 0));

            // Act
            var error = await Assert.ThrowsAsync<JsonException>(() => catalog.LoadAsync(TestContext.Current.CancellationToken));

            // Assert
            Assert.NotEmpty(error.Message);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>Verifies JSON nulls and null collection members are explicit rejections rather than unhandled validator failures.</summary>
    [Theory]
    [InlineData("executableSha256", true)]
    [InlineData("adapterKinds", true)]
    [InlineData("platforms", true)]
    [InlineData("settingsDescriptor", true)]
    [InlineData("adapter-element", false)]
    [InlineData("platform-element", false)]
    public async Task ResolveAsync_NullManifestValues_IsolatesInvalidPackage(string field, bool jsonError)
    {
        // Arrange
        var directory = Directory.CreateTempSubdirectory("lineup-null-catalog-");
        try
        {
            await WriteManifestAsync(directory.FullName, CreateManifest("healthy"));
            var node = Assert.IsType<JsonObject>(JsonSerializer.SerializeToNode(CreateManifest("invalid"), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            if (field == "adapter-element")
            {
                node["adapterKinds"] = JsonNode.Parse("[null]");
            }
            else if (field == "platform-element")
            {
                node["platforms"] = JsonNode.Parse("[null]");
            }
            else
            {
                node[field] = null;
            }
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "invalid.virtual-output.json"), node.ToJsonString(), TestContext.Current.CancellationToken);
            var catalog = new VirtualOutputWorkerCatalog(directory.FullName, new Version(3, 0, 0));

            // Act
            var package = await catalog.ResolveAsync("healthy", TestContext.Current.CancellationToken);
            var rejected = await Assert.ThrowsAsync<InvalidDataException>(() => catalog.ResolveAsync("invalid", TestContext.Current.CancellationToken));
            Exception strictError = jsonError
                ? await Assert.ThrowsAsync<JsonException>(() => catalog.LoadAsync(TestContext.Current.CancellationToken))
                : await Assert.ThrowsAsync<InvalidDataException>(() => catalog.LoadAsync(TestContext.Current.CancellationToken));

            // Assert
            Assert.Equal("healthy", package.Manifest.WorkerId);
            Assert.Contains("invalid.virtual-output.json", rejected.Message, StringComparison.Ordinal);
            Assert.NotEmpty(strictError.Message);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>Verifies manifest size limits still reject empty and oversized files without blocking healthy packages.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ResolveAsync_InvalidManifestSize_IsolatesRejectedPackage(bool empty)
    {
        // Arrange
        var directory = Directory.CreateTempSubdirectory("lineup-size-catalog-");
        try
        {
            await WriteManifestAsync(directory.FullName, CreateManifest("healthy"));
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "invalid.virtual-output.json"), empty ? string.Empty : new string(' ', 256 * 1024 + 1),
                TestContext.Current.CancellationToken);
            var catalog = new VirtualOutputWorkerCatalog(directory.FullName, new Version(3, 0, 0));

            // Act
            var package = await catalog.ResolveAsync("healthy", TestContext.Current.CancellationToken);
            var rejected = await Assert.ThrowsAsync<InvalidDataException>(() => catalog.ResolveAsync("invalid", TestContext.Current.CancellationToken));

            // Assert
            Assert.Equal("healthy", package.Manifest.WorkerId);
            Assert.Contains("invalid size", rejected.Message, StringComparison.Ordinal);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>Verifies cancellation is not treated as a rejected package or missing-worker fallback.</summary>
    [Fact]
    public async Task ResolveAsync_CanceledToken_PropagatesCancellation()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var catalog = new VirtualOutputWorkerCatalog(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), new Version(3, 0, 0));

        // Act
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => catalog.ResolveAsync("healthy", cancellation.Token));

        // Assert
        Assert.Equal(cancellation.Token, error.CancellationToken);
    }

    private static async Task WriteMixedCatalogAsync(string root)
    {
        await WriteManifestAsync(root, CreateManifest("healthy"));
        await File.WriteAllTextAsync(Path.Combine(root, "malformed.virtual-output.json"), "{", TestContext.Current.CancellationToken);
        await WriteManifestAsync(root, CreateManifest("invalid") with { SchemaVersion = 2 });
        await WriteManifestAsync(root, CreateManifest("newer") with { MinimumLineupVersion = "4.0.0" });
    }

    private static Task WriteManifestAsync(string root, VirtualOutputWorkerManifest manifest) =>
        File.WriteAllTextAsync(Path.Combine(root, $"{manifest.WorkerId}.virtual-output.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)), TestContext.Current.CancellationToken);

    private static VirtualOutputWorkerManifest CreateManifest(string adapterKind) =>
        new()
        {
            WorkerId = adapterKind,
            Version = "1.0.0",
            Publisher = "Lineup",
            ExecutablePath = "worker.bin",
            ExecutableSha256 = new string('0', 64),
            AdapterKinds = [adapterKind],
            MinimumProtocolVersion = 1,
            MaximumProtocolVersion = 1,
            MinimumLineupVersion = "3.0.0",
            SettingsDescriptor = new AdapterSettingsDescriptor { AdapterKind = adapterKind }
        };
}
