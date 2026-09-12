using Lineup.HDHomeRun.Device.Models;

namespace Lineup.Web.Services;

/// <summary>
/// Performs physical-device HTTP requests scoped to a resolved proxy profile.
/// </summary>
public interface IHdHomeRunProxyDeviceClient
{
    /// <summary>
    /// Fetches the channel lineup from a profile's physical device.
    /// </summary>
    /// <param name="profile">Resolved proxy profile.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>The physical device lineup.</returns>
    Task<List<HDHomeRunChannel>> FetchLineupAsync(HdHomeRunProxyProfileSnapshot profile, CancellationToken cancellationToken = default);
}

/// <summary>
/// Uses <see cref="IHttpClientFactory"/> for isolated physical-device lineup access.
/// </summary>
public sealed class HdHomeRunProxyDeviceClient : IHdHomeRunProxyDeviceClient
{
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>
    /// Initializes the device-scoped client.
    /// </summary>
    /// <param name="httpClientFactory">Factory for physical-device HTTP clients.</param>
    public HdHomeRunProxyDeviceClient(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    /// <inheritdoc />
    public async Task<List<HDHomeRunChannel>> FetchLineupAsync(HdHomeRunProxyProfileSnapshot profile, CancellationToken cancellationToken = default)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("HdHomeRunProxyDevice");
            return await client.GetFromJsonAsync<List<HDHomeRunChannel>>(
                new Uri(profile.PhysicalBaseUri, "lineup.json"),
                cancellationToken) ?? throw new InvalidOperationException("The physical HDHomeRun returned no lineup.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException("The physical HDHomeRun lineup is unavailable.", ex);
        }
    }
}
