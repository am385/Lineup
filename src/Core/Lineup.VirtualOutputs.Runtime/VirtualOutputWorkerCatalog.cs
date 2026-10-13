using System.Text.Json;
using System.Runtime.InteropServices;
using Lineup.Common.Streaming;

namespace Lineup.VirtualOutputs.Runtime;

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
    /// <summary>Loads and validates installed worker packages.</summary>
    Task<IReadOnlyList<VirtualOutputWorkerPackage>> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Resolves exactly one worker package for an adapter kind.</summary>
    Task<VirtualOutputWorkerPackage> ResolveAsync(string adapterKind, CancellationToken cancellationToken = default);
}

/// <summary>Filesystem-backed declarative virtual-output worker catalog.</summary>
public sealed class VirtualOutputWorkerCatalog : IVirtualOutputWorkerCatalog
{
    private const int MaximumManifestBytes = 256 * 1024;
    private readonly string _packageRoot;
    private readonly Version _lineupVersion;

    /// <summary>Initializes a worker catalog rooted at one trusted installation directory.</summary>
    public VirtualOutputWorkerCatalog(string packageRoot, Version? lineupVersion = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageRoot);
        _packageRoot = Path.GetFullPath(packageRoot);
        _lineupVersion = lineupVersion ?? typeof(VirtualOutputWorkerCatalog).Assembly.GetName().Version
            ?? throw new InvalidOperationException("The running Lineup version is unavailable.");
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<VirtualOutputWorkerPackage>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_packageRoot))
        {
            return [];
        }

        var packages = new List<VirtualOutputWorkerPackage>();
        foreach (var manifestPath in Directory.EnumerateFiles(_packageRoot, "*.virtual-output.json", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(manifestPath);
            if (info.Length is < 1 or > MaximumManifestBytes)
            {
                throw new InvalidDataException($"Worker manifest '{manifestPath}' has an invalid size.");
            }
            await using var stream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65_536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var manifest = await JsonSerializer.DeserializeAsync<VirtualOutputWorkerManifest>(
                stream,
                new JsonSerializerOptions(JsonSerializerDefaults.Web),
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
            packages.Add(new VirtualOutputWorkerPackage
            {
                PackageRoot = Path.GetDirectoryName(manifestPath)!,
                Manifest = manifest
            });
        }
        return packages;
    }

    /// <inheritdoc />
    public async Task<VirtualOutputWorkerPackage> ResolveAsync(string adapterKind, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterKind);
        var adapterMatches = (await LoadAsync(cancellationToken))
            .Where(package => package.Manifest.AdapterKinds.Contains(adapterKind, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        var matches = adapterMatches.Where(package => SupportsCurrentPlatform(package.Manifest)).ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 when adapterMatches.Length > 0 => throw new PlatformNotSupportedException(
                $"Installed virtual-output workers for adapter kind '{adapterKind}' do not support this operating system and architecture."),
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
