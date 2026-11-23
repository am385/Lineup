using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Lineup.Tools.BrowserAssets;

/// <summary>Maintains manifest-keyed, hash-verified assets shared by builds and publishes.</summary>
internal static class BrowserAssetCache
{
    /// <summary>Reuses valid local assets or atomically replaces missing or corrupt assets with verified downloads.</summary>
    /// <param name="manifestPath">The reviewed source, transformation, and checksum manifest.</param>
    /// <param name="cacheRoot">The ignored root beneath which each manifest has an independent cache.</param>
    /// <param name="client">The HTTP client used only for assets requiring restoration.</param>
    /// <param name="verifyOnly">Whether missing or corrupt files must fail without attempting network access.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The directory containing this manifest's complete asset set.</returns>
    public static async Task<string> PrepareAsync(string manifestPath, string cacheRoot, HttpClient client, bool verifyOnly = false, CancellationToken cancellationToken = default)
    {
        var bytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken);
        var directory = Path.GetFullPath(Path.Combine(cacheRoot, Convert.ToHexString(SHA256.HashData(bytes))));
        using var manifest = JsonDocument.Parse(bytes);
        if (!manifest.RootElement.TryGetProperty("assets", out var entries) || entries.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("The browser asset manifest requires an 'assets' array.");
        }
        var assets = entries.EnumerateArray().ToArray();
        if (assets.Length == 0)
        {
            throw new InvalidDataException("The browser asset manifest must contain at least one asset.");
        }
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in assets)
        {
            var path = GetAssetPath(directory, RequiredString(asset, "path"));
            if (!paths.Add(path))
            {
                throw new InvalidDataException($"Duplicate browser asset path: {path}");
            }
            _ = RequiredHash(asset, "sha256");
            if (asset.TryGetProperty("sourceSha256", out _))
            {
                _ = RequiredHash(asset, "sourceSha256");
            }
            if (!Uri.TryCreate(RequiredString(asset, "url"), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                throw new InvalidDataException($"Browser asset sources must use HTTPS: {path}");
            }
        }

        Directory.CreateDirectory(directory);
        await using var cacheLock = await AcquireLockAsync(Path.Combine(directory, ".restore.lock"), cancellationToken);
        foreach (var asset in assets)
        {
            var path = GetAssetPath(directory, RequiredString(asset, "path"));
            var expected = RequiredHash(asset, "sha256");
            if (await HasHashAsync(path, expected, cancellationToken))
            {
                continue;
            }
            if (verifyOnly)
            {
                throw new InvalidDataException($"Browser asset is missing or corrupt: {path}. Run a build with network access to restore it.");
            }
            Console.WriteLine($"Restoring missing or corrupt browser asset '{RequiredString(asset, "path")}'.");
            await RestoreAssetAsync(asset, path, client, expected, cancellationToken);
        }
        if (!verifyOnly)
        {
            await File.WriteAllLinesAsync(Path.Combine(directory, "assets.files"), assets.Select(asset => RequiredString(asset, "path")), cancellationToken);
        }
        return directory;
    }

    private static async Task RestoreAssetAsync(JsonElement asset, string path, HttpClient client, string expected, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var response = await client.GetAsync(RequiredString(asset, "url"), HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                response.EnsureSuccessStatusCode();
                await using var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
                await response.Content.CopyToAsync(file, cancellationToken);
            }
            var sourceHash = asset.TryGetProperty("sourceSha256", out _) ? RequiredHash(asset, "sourceSha256") : expected;
            if (!await HasHashAsync(temporary, sourceHash, cancellationToken))
            {
                throw new InvalidDataException($"Downloaded browser asset checksum mismatch: {path}");
            }
            if (asset.TryGetProperty("replacements", out var replacements))
            {
                var content = await File.ReadAllTextAsync(temporary, cancellationToken);
                foreach (var replacement in replacements.EnumerateArray())
                {
                    var from = RequiredString(replacement, "from");
                    var to = RequiredString(replacement, "to");
                    var first = content.IndexOf(from, StringComparison.Ordinal);
                    if (first < 0 || first != content.LastIndexOf(from, StringComparison.Ordinal))
                    {
                        throw new InvalidDataException($"Expected exactly one stylesheet replacement in {path}. Review the pinned upstream file.");
                    }
                    content = content.Replace(from, to, StringComparison.Ordinal);
                }
                await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(false), cancellationToken);
            }
            if (!await HasHashAsync(temporary, expected, cancellationToken))
            {
                throw new InvalidDataException($"Prepared browser asset checksum mismatch: {path}");
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static string RequiredString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException($"Browser asset manifest requires a nonempty '{property}' string.");
        }
        return value.GetString()!;
    }

    private static string RequiredHash(JsonElement asset, string property)
    {
        var hash = RequiredString(asset, property);
        if (hash.Length != 64 || !hash.All(char.IsAsciiHexDigit))
        {
            throw new InvalidDataException($"Invalid SHA-256 checksum in browser asset property '{property}'.");
        }
        return hash;
    }

    private static string GetAssetPath(string directory, string relative)
    {
        var path = Path.GetFullPath(relative.Replace('/', Path.DirectorySeparatorChar), directory);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (Path.IsPathRooted(relative) || !path.StartsWith(directory + Path.DirectorySeparatorChar, comparison) || relative.Contains('\\'))
        {
            throw new InvalidDataException($"Browser asset path must remain inside the cache: {relative}");
        }
        return path;
    }

    private static async Task<bool> HasHashAsync(string path, string expected, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return false;
        }
        await using var file = File.OpenRead(path);
        return string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken)), expected, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<FileStream> AcquireLockAsync(string path, CancellationToken cancellationToken)
    {
        var waiting = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException exception) when ((exception.HResult & 0xffff) is 11 or 32 or 33)
            {
                if (waiting.Elapsed > TimeSpan.FromMinutes(10))
                {
                    throw new IOException($"Timed out waiting for the browser asset cache lock '{path}'.", exception);
                }
                await Task.Delay(100, cancellationToken);
            }
        }
    }
}
