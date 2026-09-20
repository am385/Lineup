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
    private readonly SemaphoreSlim _gate = new(1, 1);

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
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await ReadSnapshotAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
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

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var previous = await ReadSnapshotAsync(cancellationToken);
            var refreshedChannels = channels
                .Where(channel => !string.IsNullOrWhiteSpace(channel.GuideNumber))
                .DistinctBy(channel => channel.GuideNumber.Trim(), StringComparer.OrdinalIgnoreCase)
                .OrderBy(channel => channel.GuideNumber, ChannelNumberComparer.Instance)
                .ToArray();
            var refreshedNumbers = refreshedChannels
                .Select(channel => channel.GuideNumber.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var disabledGuideNumbers = previous?.DisabledGuideNumbers
                .Where(refreshedNumbers.Contains)
                .OrderBy(guideNumber => guideNumber, ChannelNumberComparer.Instance)
                .ToArray() ?? [];
            var snapshot = new ChannelLineupSnapshot(DateTime.UtcNow, refreshedChannels)
            {
                DisabledGuideNumbers = disabledGuideNumbers
            };
            await WriteSnapshotAsync(snapshot, cancellationToken);
            return snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Enables or disables one saved channel without changing its tuner metadata.
    /// </summary>
    /// <param name="guideNumber">Logical channel number to update.</param>
    /// <param name="enabled">Whether the channel should be exposed publicly.</param>
    /// <param name="cancellationToken">Cancels the update before publication.</param>
    /// <returns>The updated saved snapshot.</returns>
    public async Task<ChannelLineupSnapshot> SetChannelEnabledAsync(string guideNumber, bool enabled, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(guideNumber);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var snapshot = await ReadSnapshotAsync(cancellationToken)
                ?? throw new InvalidOperationException("No HDHomeRun channel lineup has been saved.");
            var normalizedGuideNumber = guideNumber.Trim();
            if (!snapshot.Channels.Any(channel => string.Equals(channel.GuideNumber.Trim(), normalizedGuideNumber, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"Channel {normalizedGuideNumber} is not present in the saved HDHomeRun lineup.");
            }

            var disabledGuideNumbers = snapshot.DisabledGuideNumbers.ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (enabled)
            {
                disabledGuideNumbers.Remove(normalizedGuideNumber);
            }
            else
            {
                disabledGuideNumbers.Add(normalizedGuideNumber);
            }

            var updated = snapshot with
            {
                DisabledGuideNumbers = disabledGuideNumbers
                    .OrderBy(number => number, ChannelNumberComparer.Instance)
                    .ToArray()
            };
            await WriteSnapshotAsync(updated, cancellationToken);
            return updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ChannelLineupSnapshot?> ReadSnapshotAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var snapshot = await JsonSerializer.DeserializeAsync<ChannelLineupSnapshot>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("The saved HDHomeRun channel lineup is invalid.");
        var channelNumbers = snapshot.Channels
            .Select(channel => channel.GuideNumber.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return snapshot with
        {
            DisabledGuideNumbers = snapshot.DisabledGuideNumbers
                .Where(channelNumbers.Contains)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(number => number, ChannelNumberComparer.Instance)
                .ToArray()
        };
    }

    private async Task WriteSnapshotAsync(ChannelLineupSnapshot snapshot, CancellationToken cancellationToken)
    {
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
public sealed record ChannelLineupSnapshot(DateTime RefreshedAtUtc, IReadOnlyList<HDHomeRunChannel> Channels)
{
    /// <summary>
    /// Logical channel numbers explicitly hidden from public lineups, guides, and streams.
    /// </summary>
    public IReadOnlyList<string> DisabledGuideNumbers { get; init; } = [];

    /// <summary>
    /// Gets the number of channels exposed publicly.
    /// </summary>
    public int EnabledChannelCount => Channels.Count - DisabledGuideNumbers.Count;

    /// <summary>
    /// Gets whether a logical channel is enabled.
    /// </summary>
    /// <param name="guideNumber">Logical channel number.</param>
    /// <returns><see langword="false"/> only when the channel was explicitly disabled.</returns>
    public bool IsChannelEnabled(string guideNumber)
    {
        return !DisabledGuideNumbers.Contains(guideNumber.Trim(), StringComparer.OrdinalIgnoreCase);
    }
}
