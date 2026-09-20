using System.Text.Json;
using Lineup.HDHomeRun.Device.Models;

namespace Lineup.Core;

/// <summary>
/// Persists the most recently refreshed physical tuner channel lineup.
/// </summary>
public sealed class ChannelLineupStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;

    /// <summary>
    /// Initializes a channel lineup store at the specified path.
    /// </summary>
    /// <param name="path">Persistent snapshot path.</param>
    public ChannelLineupStore(string path)
    {
        _path = Path.GetFullPath(path);
    }

    /// <summary>
    /// Reads the last successfully refreshed tuner lineup.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The saved snapshot, or <see langword="null"/> when channels have not been refreshed.</returns>
    public async Task<ChannelLineupSnapshot?> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await JsonSerializer.DeserializeAsync<ChannelLineupSnapshot>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("The saved HDHomeRun channel lineup is invalid.");
    }

    /// <summary>
    /// Atomically replaces the saved tuner lineup.
    /// </summary>
    /// <param name="channels">Combined channels from configured physical tuners.</param>
    /// <param name="cancellationToken">Cancels staging before publication.</param>
    /// <returns>The saved snapshot.</returns>
    public async Task<ChannelLineupSnapshot> StoreAsync(IEnumerable<HDHomeRunChannel> channels, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channels);

        var snapshot = new ChannelLineupSnapshot(
            DateTime.UtcNow,
            channels
                .Where(channel => !string.IsNullOrWhiteSpace(channel.GuideNumber))
                .DistinctBy(channel => channel.GuideNumber.Trim(), StringComparer.OrdinalIgnoreCase)
                .OrderBy(channel => channel.GuideNumber, ChannelNumberComparer.Instance)
                .ToArray());
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, snapshot, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, _path, overwrite: true);
            return snapshot;
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }
}

/// <summary>
/// Represents a persisted physical tuner channel lineup.
/// </summary>
/// <param name="RefreshedAtUtc">When the tuner lineup was successfully refreshed.</param>
/// <param name="Channels">The combined unique physical tuner channels.</param>
public sealed record ChannelLineupSnapshot(DateTime RefreshedAtUtc, IReadOnlyList<HDHomeRunChannel> Channels);
