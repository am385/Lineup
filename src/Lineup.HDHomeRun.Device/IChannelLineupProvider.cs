using Lineup.HDHomeRun.Device.Models;

namespace Lineup.HDHomeRun.Device;

/// <summary>
/// Provides the channels currently available from configured physical HDHomeRun devices.
/// </summary>
public interface IChannelLineupProvider
{
    /// <summary>
    /// Fetches the current physical-device channel lineup.
    /// </summary>
    /// <param name="cancellationToken">Cancels lineup retrieval.</param>
    /// <returns>The available physical channels.</returns>
    Task<List<HDHomeRunChannel>> FetchChannelLineupAsync(CancellationToken cancellationToken = default);
}
