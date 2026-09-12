using System.Net;
using Microsoft.Extensions.Logging;

namespace Lineup.HDHomeRun.Device.Protocol;

/// <summary>
/// Creates HDHomeRun HTTP controls backed by factory-managed HTTP transports.
/// </summary>
public sealed class HDHomeRunHttpControlFactory : IHDHomeRunHttpControlFactory
{
    /// <summary>
    /// The registered name of the HDHomeRun control HTTP client.
    /// </summary>
    public const string HttpClientName = "HDHomeRunControl";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HDHomeRunHttpControl> _logger;

    /// <summary>
    /// Creates a new factory.
    /// </summary>
    /// <param name="httpClientFactory">The application HTTP client factory.</param>
    /// <param name="logger">The HTTP control logger.</param>
    public HDHomeRunHttpControlFactory(IHttpClientFactory httpClientFactory, ILogger<HDHomeRunHttpControl> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public HDHomeRunHttpControl Create(IPAddress deviceAddress)
    {
        return Create(new UriBuilder(Uri.UriSchemeHttp, deviceAddress.ToString()).Uri.AbsoluteUri);
    }

    /// <inheritdoc />
    public HDHomeRunHttpControl Create(string baseUrl)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        return new HDHomeRunHttpControl(baseUrl, _logger, client);
    }
}
