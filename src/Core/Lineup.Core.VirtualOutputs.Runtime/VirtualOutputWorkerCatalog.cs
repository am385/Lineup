using System.Text.Json;
using System.Runtime.InteropServices;
using Lineup.Core.Common.Streaming;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lineup.Core.VirtualOutputs.Runtime;

/// <summary>One validated installed worker package.</summary>
public sealed record VirtualOutputWorkerPackage
{
    /// <summary>Gets the package root.</summary>
    public required string PackageRoot { get; init; }

    /// <summary>Gets the validated declarative manifest.</summary>
    public required VirtualOutputWorkerManifest Manifest { get; init; }
}

/// <summary>Discovers declarative worker packages without loading their executable assemblies.</summary>
public interface IVirtualOutputWorkerCatalog
{
    /// <summary>Loads and validates all installed worker packages, failing explicitly if any package is invalid or incompatible.</summary>
    Task<IReadOnlyList<VirtualOutputWorkerPackage>> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Resolves exactly one valid compatible worker for an adapter kind, logging rejected packages without blocking independent workers.</summary>
    Task<VirtualOutputWorkerPackage> ResolveAsync(string adapterKind, CancellationToken cancellationToken = default);
}

/// <summary>Filesystem-backed declarative virtual-output worker catalog.</summary>
public sealed class VirtualOutputWorkerCatalog : IVirtualOutputWorkerCatalog
{
    private const int MaximumManifestBytes = 256 * 1024;
    private readonly string _packageRoot;
    private readonly Version _lineupVersion;
    private readonly ILogger<VirtualOutputWorkerCatalog> _logger;

    /// <summary>Initializes a worker catalog rooted at one trusted installation directory.</summary>
    public VirtualOutputWorkerCatalog(string packageRoot, Version? lineupVersion = null, ILogger<VirtualOutputWorkerCatalog>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageRoot);
        _packageRoot = Path.GetFullPath(packageRoot);
        _lineupVersion = lineupVersion ?? typeof(VirtualOutputWorkerCatalog).Assembly.GetName().Version
            ?? throw new InvalidOperationException("The running Lineup version is unavailable.");
        _logger = logger ?? NullLogger<VirtualOutputWorkerCatalog>.Instance;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<VirtualOutputWorkerPackage>> LoadAsync(CancellationToken cancellationToken = default) =>
        LoadPackagesAsync(null, cancellationToken);

    private async Task<IReadOnlyList<VirtualOutputWorkerPackage>> LoadPackagesAsync(List<string>? rejectedPackages, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(_packageRoot))
        {
            return [];
        }

        var packages = new List<VirtualOutputWorkerPackage>();
        foreach (var manifestPath in Directory.EnumerateFiles(_packageRoot, "*.virtual-output.json", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                packages.Add(await LoadPackageAsync(manifestPath, cancellationToken));
            }
            catch (Exception ex) when (rejectedPackages is not null && ex is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Rejecting installed virtual-output worker manifest {ManifestPath}", manifestPath);
                rejectedPackages.Add($"Worker manifest '{manifestPath}' could not be loaded: {ex.Message}");
            }
        }
        return packages;
    }

    private async Task<VirtualOutputWorkerPackage> LoadPackageAsync(string manifestPath, CancellationToken cancellationToken)
    {
        var info = new FileInfo(manifestPath);
        if (info.Length is < 1 or > MaximumManifestBytes)
        {
            throw new InvalidDataException($"Worker manifest '{manifestPath}' has an invalid size.");
        }
        await using var stream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65_536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var manifest = await JsonSerializer.DeserializeAsync<VirtualOutputWorkerManifest>(
            stream,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { RespectNullableAnnotations = true },
            cancellationToken)
            ?? throw new InvalidDataException($"Worker manifest '{manifestPath}' is empty.");
        var errors = VirtualOutputWorkerManifestValidator.Validate(manifest);
        if (errors.Count > 0)
        {
            throw new InvalidDataException($"Worker manifest '{manifestPath}' is invalid: {string.Join(" ", errors)}");
        }
        if (!VirtualOutputWorkerManifestValidator.IsCompatibleWithLineupVersion(manifest, _lineupVersion))
        {
            throw new InvalidDataException(
                $"Worker manifest '{manifestPath}' does not support Lineup version {_lineupVersion}.");
        }
        return new VirtualOutputWorkerPackage
        {
            PackageRoot = Path.GetDirectoryName(manifestPath)!,
            Manifest = manifest
        };
    }

    /// <inheritdoc />
    public async Task<VirtualOutputWorkerPackage> ResolveAsync(string adapterKind, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterKind);
        var rejectedPackages = new List<string>();
        var adapterMatches = (await LoadPackagesAsync(rejectedPackages, cancellationToken))
            .Where(package => package.Manifest.AdapterKinds.Contains(adapterKind, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        var matches = adapterMatches.Where(package => SupportsCurrentPlatform(package.Manifest)).ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 when adapterMatches.Length > 0 => throw new PlatformNotSupportedException(
                $"Installed virtual-output workers for adapter kind '{adapterKind}' do not support this operating system and architecture."),
            0 when rejectedPackages.Count > 0 => throw new InvalidDataException(
                $"No valid installed virtual-output worker supports adapter kind '{adapterKind}'. Rejected packages: {string.Join(" ", rejectedPackages)}"),
            0 => throw new KeyNotFoundException($"No installed virtual-output worker supports adapter kind '{adapterKind}'."),
            _ => throw new InvalidOperationException($"Multiple installed virtual-output workers support adapter kind '{adapterKind}'.")
        };
    }

    private static bool SupportsCurrentPlatform(VirtualOutputWorkerManifest manifest)
    {
        if (manifest.Platforms.Count == 0)
        {
            return true;
        }
        var operatingSystem = OperatingSystem.IsWindows()
            ? "windows"
            : OperatingSystem.IsLinux()
                ? "linux"
                : OperatingSystem.IsMacOS()
                    ? "macos"
                    : "unknown";
        var architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        return manifest.Platforms.Any(platform =>
            string.Equals(platform.OperatingSystem, operatingSystem, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(platform.Architecture, architecture, StringComparison.OrdinalIgnoreCase));
    }
}
