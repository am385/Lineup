namespace Lineup.HDHomeRun.Api;

/// <summary>
/// Provides authentication tokens for HDHomeRun API access
/// </summary>
public interface IDeviceAuthProvider
{
    /// <summary>
    /// Gets the device authentication token
    /// </summary>
    /// <returns>Device authentication token</returns>
    Task<string> GetDeviceAuthAsync();
}
