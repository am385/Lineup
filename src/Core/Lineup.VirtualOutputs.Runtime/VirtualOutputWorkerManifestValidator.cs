using System.Globalization;
using System.Security.Cryptography;
using Lineup.Common.Streaming;
using Lineup.VirtualOutputs.Protocol;

namespace Lineup.VirtualOutputs.Runtime;

/// <summary>Validates declarative worker manifests and installed executable provenance without loading worker assemblies.</summary>
public static class VirtualOutputWorkerManifestValidator
{
    /// <summary>Returns every validation error for one untrusted manifest.</summary>
    public static IReadOnlyList<string> Validate(VirtualOutputWorkerManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var errors = new List<string>();
        if (manifest.SchemaVersion != 1)
        {
            errors.Add($"Worker manifest schema {manifest.SchemaVersion} is not supported.");
        }
        if (!IsIdentifierValid(manifest.WorkerId))
        {
            errors.Add("Worker ID must start with a lowercase letter and contain only lowercase letters, digits, hyphens, or periods.");
        }
        if (!TryParseVersion(manifest.Version, out _))
        {
            errors.Add("Worker package version must be a valid version.");
        }
        if (string.IsNullOrWhiteSpace(manifest.Publisher))
        {
            errors.Add("Worker publisher is required.");
        }
        if (string.IsNullOrWhiteSpace(manifest.ExecutablePath) ||
            Path.IsPathRooted(manifest.ExecutablePath) ||
            manifest.ExecutablePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(segment => segment == ".."))
        {
            errors.Add("Worker executable path must be a safe package-relative path.");
        }
        if (manifest.ExecutableSha256.Length != 64 || !manifest.ExecutableSha256.All(Uri.IsHexDigit))
        {
            errors.Add("Worker executable SHA-256 must contain 64 hexadecimal characters.");
        }
        if (manifest.AdapterKinds.Count == 0 || manifest.AdapterKinds.Any(kind => !IsIdentifierValid(kind)))
        {
            errors.Add("Worker manifest requires at least one valid adapter kind.");
        }
        if (manifest.Platforms.Any(platform =>
            !IsIdentifierValid(platform.OperatingSystem) ||
            !IsIdentifierValid(platform.Architecture)))
        {
            errors.Add("Worker manifest platforms must use normalized operating-system and architecture identifiers.");
        }
        if (manifest.MinimumProtocolVersion < 1 ||
            manifest.MaximumProtocolVersion < manifest.MinimumProtocolVersion ||
            VirtualOutputProtocol.Negotiate(manifest.MinimumProtocolVersion, manifest.MaximumProtocolVersion) is null)
        {
            errors.Add("Worker manifest does not support a compatible IPC protocol version.");
        }
        if (!TryParseVersion(manifest.MinimumLineupVersion, out var minimumLineupVersion))
        {
            errors.Add("Minimum Lineup version must be a valid version.");
        }
        if (manifest.MaximumLineupVersion is { } maximum &&
            (!TryParseVersion(maximum, out var maximumLineupVersion) ||
             minimumLineupVersion is not null && maximumLineupVersion < minimumLineupVersion))
        {
            errors.Add("Maximum Lineup version must be valid and not less than the minimum version.");
        }
        if (!string.Equals(manifest.SettingsDescriptor.AdapterKind, manifest.AdapterKinds.SingleOrDefault(), StringComparison.OrdinalIgnoreCase) &&
            !manifest.AdapterKinds.Contains(manifest.SettingsDescriptor.AdapterKind, StringComparer.OrdinalIgnoreCase))
        {
            errors.Add("The settings descriptor adapter kind must be declared by the worker manifest.");
        }
        return errors;
    }

    /// <summary>Verifies that an installed worker executable matches its declarative hash.</summary>
    public static async Task<bool> VerifyExecutableAsync(VirtualOutputWorkerManifest manifest, string packageRoot, CancellationToken cancellationToken = default)
    {
        var errors = Validate(manifest);
        if (errors.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", errors), nameof(manifest));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(packageRoot);
        var root = Path.GetFullPath(packageRoot);
        var executablePath = Path.GetFullPath(Path.Combine(root, manifest.ExecutablePath));
        if (!IsWithinRoot(root, executablePath) || !File.Exists(executablePath))
        {
            return false;
        }

        await using var stream = new FileStream(executablePath, FileMode.Open, FileAccess.Read, FileShare.Read, 131_072, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        var expected = Convert.FromHexString(manifest.ExecutableSha256);
        return CryptographicOperations.FixedTimeEquals(hash, expected);
    }

    /// <summary>Determines whether a validated worker package supports the running Lineup version.</summary>
    public static bool IsCompatibleWithLineupVersion(VirtualOutputWorkerManifest manifest, Version lineupVersion)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(lineupVersion);
        var normalizedLineupVersion = NormalizeVersion(lineupVersion);
        return TryParseVersion(manifest.MinimumLineupVersion, out var minimum) &&
            normalizedLineupVersion >= NormalizeVersion(minimum!) &&
            (manifest.MaximumLineupVersion is null ||
             TryParseVersion(manifest.MaximumLineupVersion, out var maximum) &&
             normalizedLineupVersion <= NormalizeVersion(maximum!));
    }

    private static bool IsIdentifierValid(string value) =>
        !string.IsNullOrEmpty(value) &&
        value[0] is >= 'a' and <= 'z' &&
        value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '.');

    private static bool TryParseVersion(string value, out Version? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }
        var coreLength = value.IndexOfAny('-', '+');
        var core = coreLength < 0 ? value : value[..coreLength];
        return Version.TryParse(core, out version);
    }

    private static Version NormalizeVersion(Version version) =>
        new(version.Major, version.Minor, Math.Max(0, version.Build));

    private static bool IsWithinRoot(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative != ".." &&
            !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
            !Path.IsPathRooted(relative);
    }
}
