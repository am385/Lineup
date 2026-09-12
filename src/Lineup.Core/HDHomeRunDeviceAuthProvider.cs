using Lineup.HDHomeRun.Api;
using Lineup.HDHomeRun.Device;

namespace Lineup.Core;

/// <summary>
/// Represents hd home run device auth provider.
/// </summary>
internal class HDHomeRunDeviceAuthProvider : IDeviceAuthProvider
{
    private readonly HDHomeRunDeviceClient _hdHomeRunDeviceClient;

    /// <summary>
    /// Initializes a new instance of the <see cref="HDHomeRunDeviceAuthProvider"/> class.
    /// </summary>
    public HDHomeRunDeviceAuthProvider(HDHomeRunDeviceClient hdHomeRunDeviceClient)
    {
        _hdHomeRunDeviceClient = hdHomeRunDeviceClient;
    }

    /// <summary>
    /// Performs the get device auth operation.
    /// </summary>
    public async Task<string> GetDeviceAuthAsync()
    {
        return await _hdHomeRunDeviceClient.DiscoverDeviceAuthAsync();
    }
}

