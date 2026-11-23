namespace Lineup.Tools.BrowserAssets;

/// <summary>Restores checksum-pinned browser dependencies into the ignored build cache.</summary>
internal static class Program
{
    /// <summary>Restores or verifies the manifest's cached assets without adding runtime download dependencies.</summary>
    /// <param name="args">The manifest path, cache root, and optional --verify switch.</param>
    /// <returns>Zero on success; otherwise one with an explicit diagnostic.</returns>
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length is not (2 or 3) || (args.Length == 3 && args[2] != "--verify"))
            {
                throw new ArgumentException("Usage: Lineup.Tools.BrowserAssets <manifest> <cache-root> [--verify]");
            }
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(1) };
            var directory = await BrowserAssetCache.PrepareAsync(args[0], args[1], client, args.Length == 3);
            Console.WriteLine($"Verified browser assets in '{directory}'.");
            return 0;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or HttpRequestException or UnauthorizedAccessException or ArgumentException
            or InvalidOperationException or System.Text.Json.JsonException or TaskCanceledException)
        {
            Console.Error.WriteLine($"Browser asset restore failed: {exception.Message}");
            return 1;
        }
    }
}
