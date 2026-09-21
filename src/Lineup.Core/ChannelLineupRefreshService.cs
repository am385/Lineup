using Lineup.HDHomeRun.Device;
using Microsoft.Extensions.Logging;

namespace Lineup.Core;

/// <summary>
/// Refreshes and persists the channel lineup independently of XMLTV guide downloads.
/// </summary>
public sealed class ChannelLineupRefreshService
{
    private readonly ILogger<ChannelLineupRefreshService> _logger;
    private readonly IChannelLineupProvider _provider;
    private readonly ChannelLineupStore _store;

    /// <summary>
    /// Initializes the channel lineup refresh service.
    /// </summary>
    public ChannelLineupRefreshService(ILogger<ChannelLineupRefreshService> logger, IChannelLineupProvider provider, ChannelLineupStore store)
    {
        _logger = logger;
        _provider = provider;
        _store = store;
    }

    /// <summary>
    /// Queries configured physical tuners and saves the combined lineup.
    /// </summary>
    /// <param name="cancellationToken">Cancels refresh before publication.</param>
    /// <returns>The persisted lineup snapshot.</returns>
    public async Task<ChannelLineupSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var channels = await _provider.FetchChannelLineupAsync(cancellationToken);
        if (channels.Count == 0)
        {
            throw new InvalidDataException("The configured HDHomeRun devices returned no channels. The previously saved lineup was preserved.");
        }

        var snapshot = await _store.StoreAsync(channels, cancellationToken);
        _logger.LogInformation(
            "Refreshed the physical HDHomeRun channel lineup with {ChannelCount} channels",
            snapshot.Channels.Count);
        return snapshot;
    }
}
