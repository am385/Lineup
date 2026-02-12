using Lineup.HDHomeRun.Api;
using Lineup.HDHomeRun.Device;

namespace Lineup.Core;

internal class HDHomeRunDeviceAuthProvider : IDeviceAuthProvider
{
    private readonly HDHomeRunDeviceClient _hdHomeRunDeviceClient;

    public HDHomeRunDeviceAuthProvider(HDHomeRunDeviceClient hdHomeRunDeviceClient)
    {
        _hdHomeRunDeviceClient = hdHomeRunDeviceClient;
    }

    public async Task<string> GetDeviceAuthAsync()
    {
        return await _hdHomeRunDeviceClient.DiscoverDeviceAuthAsync();
    }
}

